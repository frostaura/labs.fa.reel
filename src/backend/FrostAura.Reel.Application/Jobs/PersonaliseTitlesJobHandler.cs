using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Writes "through your lens" copy for the titles that carry the day — hero, the leaders of every
/// rail, and the week's top digest pick.
///
/// <para><b>Why this is a job and not part of the build.</b> The feed's why pass is already
/// time-boxed at 20s soft / 60s hard for a ≤160-character sentence per card; a lens is five to ten
/// times the output tokens. Folding generation into the build turns a background job into a
/// timeout and takes the feed down with it. So it runs afterwards, at priority 3, against the
/// snapshot that was just written — by which time the page is already serving, with templater
/// sentences, and the copy upgrades underneath it.</para>
///
/// <para><b>The volume basis is per FRESH build, not per build.</b> Snapshots are written per
/// build and the founder produced eight in nine minutes; most of those are Preserve and write
/// nothing at all. This job is enqueued only where a new snapshot was actually minted, and is
/// capped at <see cref="MaxTitlesPerRun"/> on top of that, under the account's own daily ceiling.</para>
/// </summary>
public sealed class PersonaliseTitlesJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    TasteFactsBuilder facts,
    LensService lens,
    ILogger<PersonaliseTitlesJobHandler> logger,
    // Optional so a hand-built handler (a test) needs no breaker; DI always supplies it.
    Llm.IProviderHealth? health = null) : IJobHandler
{
    /// <summary>
    /// The hard cap per run: hero (5) + rank 0–2 of each rail (up to 9) + the digest's top pick.
    /// Fifteen is what "the titles that carry the day" actually amounts to, and everything past it
    /// is copy for a card nobody reached.
    /// </summary>
    public const int MaxTitlesPerRun = 15;

    /// <summary>How deep into each rail the copy goes. Rank 3 down is a scroll, not a headline.</summary>
    public const int RailDepth = 3;

    /// <summary>Saved titles pre-fetched on the nightly pass — the queue is where deliberation happens.</summary>
    public const int SavedPrefetch = 20;

    /// <summary>Set on the cursor by the nightly pass to add the Saved prefetch to the selection.</summary>
    public const string SavedCursor = """{"Saved":true}""";

    public JobKind Kind => JobKind.PersonaliseTitles;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("PersonaliseTitles requires an account.");
        if (health?.OpenBreakerFor(Domain.Sync.ApiProvider.OpenRouter, Domain.Sync.ApiUsagePurpose.Lens) is { } open)
        {
            // Paused, not failed: the provider breaker is open, every call would be refused before
            // it was sent, and a run that "wrote" nothing must not read as one that tried.
            job.ProgressMessage = $"lens paused ({open.ReasonCode})";
            await db.SaveChangesAsync(ct);
            return;
        }

        var includeSaved = job.CursorJson is { Length: > 0 } cursor
            && cursor.Contains("\"Saved\":true", StringComparison.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        var built = await facts.BuildAsync(accountId, taste, now, ct);

        var titleIds = await SelectAsync(accountId, includeSaved, ct);
        if (titleIds.Count == 0)
        {
            job.ProgressMessage = "nothing to personalise";
            await db.SaveChangesAsync(ct);
            return;
        }

        job.ProgressMessage = $"Writing your lens on {titleIds.Count} titles";
        await db.SaveChangesAsync(ct);

        var outcome = await lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, titleIds, ct);

        job.ProgressMessage = outcome.Written > 0
            ? $"Lens written for {outcome.Written} title(s)"
            : $"no lens written ({outcome.Reason})";
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Lens pass for {AccountId}: {Written} written, {Rejected} rejected by guards, {Skipped} skipped ({Reason}).",
            accountId, outcome.Written, outcome.Rejected, outcome.Skipped, outcome.Reason);
    }

    /// <summary>
    /// The titles that carry the day, in the order they earn copy: hero first (it is the answer to
    /// the only question the home page asks), then each rail's leaders, then the digest's top pick,
    /// then — on the nightly pass — the Saved queue's best predictions. Deduplicated and capped, so
    /// a title on both the hero and a rail is written once.
    /// </summary>
    internal async Task<List<Guid>> SelectAsync(Guid accountId, bool includeSaved, CancellationToken ct)
    {
        var ordered = new List<Guid>();
        var seen = new HashSet<Guid>();

        void Add(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                if (ordered.Count < MaxTitlesPerRun && seen.Add(id))
                {
                    ordered.Add(id);
                }
            }
        }

        var snapshotId = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .OrderByDescending(s => s.GeneratedAt)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshotId is { } id)
        {
            var items = await db.FeedItems
                .Where(i => i.FeedSnapshotId == id)
                .Select(i => new { i.Row, i.AnchorTitleId, i.Rank, i.TitleId })
                .ToListAsync(ct);

            Add(items
                .Where(i => i.Row == FeedRowKind.Hero)
                .OrderBy(i => i.Rank)
                .Select(i => i.TitleId));

            Add(items
                .Where(i => i.Row != FeedRowKind.Hero && i.Rank < RailDepth)
                // Rank-major across rails, so every rail gets its leader before any rail gets its
                // third card — a cap that fell inside one rail would leave the others bare.
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Row)
                .ThenBy(i => i.AnchorTitleId)
                .Select(i => i.TitleId));
        }

        var weekStart = DigestWeek.MondayOf(DateTime.UtcNow);
        var digest = await db.AccountDigests
            .Where(d => d.AccountId == accountId && d.WeekStart == weekStart)
            .Select(d => d.ItemsJson)
            .FirstOrDefaultAsync(ct);
        if (digest is { Length: > 0 })
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<List<DigestItem>>(digest) ?? [];
            Add(items
                .OrderByDescending(i => i.PredictedRating)
                .Take(1)
                .Select(i => i.TitleId));
        }

        if (includeSaved)
        {
            // The Saved queue's best predictions. Reads live (unrevoked) saves and joins the
            // account's own scores — a saved card with no prediction is not a deliberation the
            // copy can help with.
            var saved = await db.UserTitleReactions
                .Where(r => r.AccountId == accountId
                    && r.Kind == Domain.Library.ReactionKind.SaveForLater
                    && r.RevokedAt == null)
                .Join(
                    db.TitleScores.Where(s => s.AccountId == accountId),
                    r => r.TitleId,
                    s => s.TitleId,
                    (r, s) => new { r.TitleId, s.PredictedRating })
                .OrderByDescending(x => x.PredictedRating)
                .Take(SavedPrefetch)
                .Select(x => x.TitleId)
                .ToListAsync(ct);
            Add(saved);
        }

        return ordered;
    }
}
