using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// The Saved shelf, as a queue rather than a bookmark folder.
///
/// <para><b>Why this endpoint changed shape.</b> It used to return titles ordered by save time with
/// a predicted rating read from <see cref="TitleScore"/> joined to the Active artifact — and nothing
/// else. Saved titles are excluded from eligibility, so they are never rescored by a feed build;
/// after any retrain a saved title has no score under the new artifact and rendered with no "for
/// you" number at all. On the founder's own database that was not an edge case but the MODAL state:
/// 104 live saves against 470 scores under the Active artifact. The product made its loudest promise
/// on the feed and then dropped it at the exact moment of highest intent.</para>
///
/// <para><b>The backfill and its budget.</b> Missing scores are filled by <see cref="OnDemandScorer"/>
/// — the same mechanism the weekly digest uses, with the same per-request budget — and persisted, so
/// the work is done once per artifact rather than once per page view. A queue longer than the budget
/// converges over consecutive requests instead of in one; <c>unscoredRemaining</c> reports exactly how
/// many are still missing so the client can decide whether to come back for them, rather than the
/// server silently blocking on an unbounded scoring job inside a GET.</para>
///
/// <para>Both handler bodies are public static methods rather than inline lambdas, following
/// <see cref="ReactionEndpoints"/>, so integration tests exercise the exact endpoint semantics —
/// ordering, backfill budget and the unrated guard — without an HTTP host.</para>
/// </summary>
public static class SavedEndpoints
{
    /// <summary>
    /// On-demand scoring budget per request, matching <c>BuildDigestJobHandler.OnDemandScoreBudget</c>.
    /// The number is a latency ceiling on a synchronous GET, not a statement about how many saves
    /// deserve a score — everything above it is picked up by the next request.
    /// </summary>
    public const int OnDemandScoreBudget = 30;

    /// <summary>How far back the post-watch prompt looks. Beyond two weeks "what did you think" is archaeology.</summary>
    public const int RecentlyWatchedWindowDays = 14;

    /// <summary>At most this many loop-closing prompts are offered at once; the strip shows one at a time.</summary>
    public const int RecentlyWatchedMax = 10;

    /// <summary>One card in the queue. Serialised camelCase by the Web JSON defaults, like every other DTO here.</summary>
    /// <param name="PredictedRating">Never null once the backfill has reached this title; see <c>UnscoredRemaining</c>.</param>
    /// <param name="AvailableNow">On a flatrate provider in the account's region right now.</param>
    /// <param name="DaysSaved">Whole days since the save — "saved 41 days ago" is the queue's own nag.</param>
    /// <param name="WatchTonight">True on exactly one card at most: the pinned pick.</param>
    /// <param name="WatchTonightReason">Why that card won, in the user's terms. Null on every other card.</param>
    public sealed record SavedQueueItem(
        Guid TitleId,
        string MediaType,
        long? TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        int? RuntimeMinutes,
        decimal? PredictedRating,
        decimal? CrowdRating,
        int? CrowdVotes,
        IReadOnlyList<FeedEndpoints.CardProvider> Providers,
        bool AvailableNow,
        DateTime SavedAt,
        int DaysSaved,
        bool FitsEveningBudget,
        bool OnManagedList,
        bool WatchTonight,
        string? WatchTonightReason,
        // Trailing and optional: the lens's one-line hook where copy exists for this account and
        // title, and null everywhere it does not. The queue is where deliberation happens, so it is
        // where the richest copy belongs — but a saved card has to read perfectly well without it.
        string? Hook = null,
        // Where the card stands against the managed Trakt list: "pending" (a ListAdd write-back is
        // still waiting — the only state that may say "syncing"), "onList" or "offList". See
        // SavedListSync. OnManagedList stays for older clients and is true only for "onList".
        string ListSync = SavedListSync.OffList);

    /// <param name="ExpectedAbsoluteError">What a displayed prediction is typically off by. Null until the account's closed loop has pairs — an unmeasured band is not a band.</param>
    /// <param name="UnscoredRemaining">Saves still without a prediction after this request's budget was spent.</param>
    public sealed record SavedQueueResponse(
        string? ManagedListUrl,
        string Sort,
        int EveningBudgetMinutes,
        double? ExpectedAbsoluteError,
        int UnscoredRemaining,
        IReadOnlyList<SavedQueueItem> Items);

    /// <param name="PredictedRating">What Reel committed to BEFORE the watch — the number the prompt settles.</param>
    public sealed record RecentlyWatchedItem(
        Guid TitleId,
        string MediaType,
        long? TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        decimal PredictedRating,
        DateTime WatchedAt);

    public sealed record RecentlyWatchedResponse(
        double? ExpectedAbsoluteError,
        IReadOnlyList<RecentlyWatchedItem> Items);

    public static void MapSavedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/saved").RequireAccount();

        group.MapGet("/", async (
            string? sort,
            IReelDbContext db,
            OnDemandScorer scorer,
            RatingCalibrationService calibration,
            LensService lens,
            TasteFactsBuilder tasteFacts,
            HttpContext http,
            CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var now = DateTime.UtcNow;
            var queue = await QueueAsync(sort, account, db, scorer, calibration, now, ct);

            // Read-only, like the feed: a page load must never be able to start a generation. The
            // nightly pass pre-fetches the queue's top twenty, so in steady state most of these are
            // already warm and the rest simply render without a hook.
            var notes = await lens.ResolveManyAsync(
                account.Id,
                queue.Items.Select(i => i.TitleId).ToList(),
                await tasteFacts.CurrentHashAsync(account.Id, now, ct),
                ct);

            return Results.Ok(queue with
            {
                Items = queue.Items
                    .Select(i => i with { Hook = notes.GetValueOrDefault(i.TitleId)?.Note?.Hook })
                    .ToList(),
            });
        });

        group.MapGet("/recently-watched", async (
            IReelDbContext db,
            IAccountContext accountContext,
            RatingCalibrationService calibration,
            CancellationToken ct) =>
            Results.Ok(await RecentlyWatchedAsync(
                accountContext.AccountId!.Value, db, calibration, DateTime.UtcNow, ct)));
    }

    /// <summary>
    /// The queue: every live save, scored (backfilling what the Active artifact is missing, within
    /// budget), priced for availability in the account's region, ordered by the requested sort, with
    /// at most one card pinned as tonight's pick.
    /// </summary>
    public static async Task<SavedQueueResponse> QueueAsync(
        string? sort,
        Account account,
        IReelDbContext db,
        OnDemandScorer scorer,
        RatingCalibrationService calibration,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var accountId = account.Id;
        var requestedSort = SavedQueue.ParseSort(sort);
        var eveningBudget = SavedQueue.EffectiveEveningBudget(account.Settings.EveningBudgetMinutes);

        // Consumed saves — fully watched, or rated — never render, even before the reconciler or the
        // one-off repair has revoked them: the read is the guarantee, the revocation is hygiene.
        var saved = await SaveConsumption.LiveSaves(db, accountId)
            .Unconsumed(db)
            .Join(db.Titles, r => r.TitleId, t => t.Id, (r, t) => new { Reaction = r, Title = t })
            .ToListAsync(ct);

        var titleIds = saved.Select(s => s.Title.Id).ToList();
        var scores = await ActiveArtifactScoresAsync(db, accountId, titleIds, ct);

        // Fill the gaps, newest saves first: a title saved this week is the one the user is most
        // likely to act on before the next request tops the rest up.
        var unscored = saved
            .Where(s => !scores.ContainsKey(s.Title.Id))
            .OrderByDescending(s => s.Reaction.CreatedAt)
            .Select(s => s.Title.Id)
            .ToList();
        if (unscored.Count > 0)
        {
            var fresh = await scorer.ScoreAsync(accountId, unscored.Take(OnDemandScoreBudget).ToList(), nowUtc, ct);
            foreach (var (titleId, predicted) in fresh)
            {
                scores[titleId] = predicted;
            }
        }

        // A title can stay unscored after the budget is spent, and stays unscored indefinitely if it
        // has no feature vector (an un-enriched catalogue row). Reporting the count is the honest
        // option: the client tops up while the number is falling and gives up when it stops, rather
        // than polling a queue that will never complete.
        var unscoredRemaining = titleIds.Count(id => !scores.ContainsKey(id));

        var providers = await FeedEndpoints.ProvidersForAsync(db, account.Region, titleIds, nowUtc, ct);
        var listSync = await SavedListSync.ForAsync(db, accountId, titleIds, ct);

        var expectedAbsoluteError = await ExpectedAbsoluteErrorAsync(calibration, accountId, ct);

        var entries = saved
            .Select(s => new SavedQueueEntry(
                s.Title.Id,
                scores.TryGetValue(s.Title.Id, out var predicted) ? predicted : null,
                s.Title.RuntimeMinutes,
                providers.TryGetValue(s.Title.Id, out var list) && list.Count > 0,
                s.Reaction.CreatedAt))
            .ToList();

        var ordered = SavedQueue.Order(entries, requestedSort);
        var pinned = SavedQueue.PickWatchTonight(entries, eveningBudget);
        var byId = saved.ToDictionary(s => s.Title.Id);
        var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.AccountId == accountId, ct);

        var items = ordered.Select(entry =>
        {
            var title = byId[entry.TitleId].Title;
            var titleProviders = providers.GetValueOrDefault(entry.TitleId, []);
            var isPinned = pinned?.TitleId == entry.TitleId;
            return new SavedQueueItem(
                entry.TitleId,
                title.MediaType.ToString(),
                title.TmdbId,
                title.Name,
                title.Year,
                title.PosterPath,
                title.RuntimeMinutes,
                // 1dp at the response edge, matching the feed and title detail.
                entry.PredictedRating is { } p ? Math.Round(p, 1) : null,
                CrowdRating.For(title),
                CrowdRating.VotesFor(title),
                titleProviders,
                entry.AvailableNow,
                entry.SavedAt,
                Math.Max(0, (int)(nowUtc - entry.SavedAt).TotalDays),
                title.RuntimeMinutes is { } runtime && runtime <= eveningBudget,
                // True only when the title is confirmed on the list. An older client renders
                // "syncing…" for false, so a pending card now says so honestly; a dead-lettered or
                // removed-on-Trakt card still does until the client reads ListSync (CR-2).
                listSync.GetValueOrDefault(entry.TitleId) == SavedListSync.OnList,
                isPinned,
                isPinned
                    ? SavedQueue.WatchTonightReason(
                        entry.PredictedRating!.Value,
                        expectedAbsoluteError,
                        entry.RuntimeMinutes!.Value,
                        eveningBudget,
                        titleProviders.FirstOrDefault()?.Name)
                    : null,
                ListSync: listSync.GetValueOrDefault(entry.TitleId, SavedListSync.OffList));
        }).ToList();

        return new SavedQueueResponse(
            connection?.ManagedListTraktId is { } listId ? $"https://trakt.tv/lists/{listId}" : null,
            requestedSort.ToString().ToLowerInvariant(),
            eveningBudget,
            expectedAbsoluteError,
            unscoredRemaining,
            items);
    }

    /// <summary>
    /// The loop-closing prompt's feed: titles that left the managed list because they were watched,
    /// inside the trailing window, that the user has NOT rated.
    ///
    /// <para>The unrated guard is the whole design. On the founder's own history all 73 conversions
    /// were already rated on Trakt before Reel could ask, because the same delta sync that detects
    /// the watch ingests the rating; without the guard this strip would fire almost exclusively on
    /// titles it already has an answer for.</para>
    ///
    /// <para>What it is worth, stated honestly: this is NOT a label-acquisition mechanism. For a user
    /// who rates on Trakt it will yield close to zero incremental labels. Its value is that the
    /// product settles the promise it made out loud ("8.4 for you") out loud — the one thing the
    /// founder's first observation says it has never done.</para>
    /// </summary>
    public static async Task<RecentlyWatchedResponse> RecentlyWatchedAsync(
        Guid accountId,
        IReelDbContext db,
        RatingCalibrationService calibration,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-RecentlyWatchedWindowDays);

        var rows = await db.ManagedListItems
            .Where(m => m.AccountId == accountId
                && m.RemovalReason == ListRemovalReason.Watched
                && m.RemovedAt != null
                && m.RemovedAt >= cutoff)
            .Where(m => !db.UserRatings.Any(r => r.AccountId == accountId
                && r.TitleId == m.TitleId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)))
            .Join(db.Titles, m => m.TitleId, t => t.Id, (m, t) => new { Item = m, Title = t })
            .Select(x => new
            {
                x.Title.Id,
                x.Title.MediaType,
                x.Title.TmdbId,
                x.Title.Name,
                x.Title.Year,
                x.Title.PosterPath,
                WatchedAt = x.Item.RemovedAt,
                // The number Reel actually committed to BEFORE the watch — the same strictly-before
                // pairing the calibrator uses, so the delta this strip shows is the delta the model
                // is graded on. A score written afterwards is hindsight, not a prediction.
                Predicted = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == x.Item.TitleId && s.ScoredAt < x.Item.RemovedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
            })
            // No prediction means no promise was made, so there is nothing to settle and the strip
            // has nothing honest to say. Silence beats "Reel guessed — what did you think?".
            .Where(x => x.Predicted != null)
            .OrderByDescending(x => x.WatchedAt)
            .Take(RecentlyWatchedMax)
            .ToListAsync(ct);

        return new RecentlyWatchedResponse(
            await ExpectedAbsoluteErrorAsync(calibration, accountId, ct),
            rows.Select(x => new RecentlyWatchedItem(
                x.Id,
                x.MediaType.ToString(),
                x.TmdbId,
                x.Name,
                x.Year,
                x.PosterPath,
                Math.Round(x.Predicted!.Value, 1),
                x.WatchedAt!.Value)).ToList());
    }

    /// <summary>Predicted ratings for these titles under the account's Active artifact only.</summary>
    private static async Task<Dictionary<Guid, decimal>> ActiveArtifactScoresAsync(
        IReelDbContext db, Guid accountId, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return [];
        }

        return await db.TitleScores
            .Where(s => s.AccountId == accountId && titleIds.Contains(s.TitleId))
            .Join(
                db.ModelArtifacts.Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active),
                s => s.ModelArtifactId,
                a => a.Id,
                (s, _) => s)
            .ToDictionaryAsync(s => s.TitleId, s => s.PredictedRating, ct);
    }

    /// <summary>
    /// The account's measured "typically off by" band, or null when its closed loop has no pairs to
    /// measure. A calibrator with nothing to fit reports zero error, and printing "±0.0" beside a
    /// prediction would be the most dishonest number on the screen.
    /// </summary>
    private static async Task<double?> ExpectedAbsoluteErrorAsync(
        RatingCalibrationService calibration, Guid accountId, CancellationToken ct)
    {
        var calibrator = await calibration.GetAsync(accountId, ct);
        return calibrator.PairCount == 0 ? null : Math.Round(calibrator.ExpectedAbsoluteError, 1);
    }
}
