using System.Globalization;
using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Incremental sync. One cheap /sync/last_activities call gates everything: only categories
/// whose timestamps moved since the stored snapshot are refetched (Trakt has no webhooks —
/// this diff is what keeps Reel inside the rate budget at multi-tenant scale). The nightly
/// reconcile enqueues this with {"force":true}, refetching every category as a drift repair.
/// </summary>
public class DeltaSyncJobHandler(
    IReelDbContext db,
    ITraktClient trakt,
    TraktTokenStore tokenStore,
    TraktLibraryIngestor ingestor,
    FeedRefreshCoordinator feedRefresh,
    ManagedListReconciler managedList,
    OnlineResidualModel residual,
    RetrainGate retrainGate,
    IPipelineEventHub events,
    IConfiguration configuration,
    ILogger<DeltaSyncJobHandler> logger) : IJobHandler
{
    /// <summary>Hard cap on how many passive watches one delta may teach the head, however many landed.</summary>
    private const int PassiveWatchBudget = 20;

    /// <summary>A watch is weak "I chose this" signal, not a score — soft positive target, low weight.</summary>
    private const double PassiveWatchTarget = 6.8;

    /// <inheritdoc cref="PassiveWatchTarget"/>
    private const double PassiveWatchWeight = 0.2;

    /// <summary>Progress calls a quiet delta may spend on the next-episode air-date back-fill.</summary>
    internal const int NextEpisodeBackfillPerDelta = 5;

    public JobKind Kind => JobKind.DeltaSync;

    private record Cursor(bool Force);

    /// <summary>
    /// Which passive watches this delta may teach the online residual head — the whole rule in one
    /// place, so every clause of it is directly assertable. Extracted from the job body on
    /// 2026-09-10 for exactly that reason: the bound had no test at all, and it is the mechanism
    /// behind a runaway bias that was expensive to diagnose.
    ///
    /// <para>Three ways this returns nothing, each load-bearing:</para>
    /// <list type="bullet">
    /// <item><description><paramref name="force"/> — a forced reconcile replays the whole library
    /// and would drown the head in one pass.</description></item>
    /// <item><description>Neither watch category moved — nothing landed to learn from.</description></item>
    /// <item><description><paramref name="previousDeltaSyncAt"/> is null — this is the first delta
    /// after the full ingest, where EVERY row was "first synced" by that ingest. There is no new
    /// information in the backlog.</description></item>
    /// </list>
    ///
    /// <para>
    /// And the window is <c>FirstSyncedAt &gt; since</c>, not "the most recent N unrated watches".
    /// The old bound was the latter, and with a delta poll running every few minutes against a
    /// library that changes far more slowly it re-taught the head the SAME rows over and over —
    /// 224 updates on the founder's account, dominated by repeats of a handful of titles, which is
    /// a large part of how its bias ran away. <see cref="PassiveWatchBudget"/> then caps a single
    /// unusually large delta on top of that.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<Guid>> PassiveWatchesToObserveAsync(
        IReelDbContext db,
        Guid accountId,
        bool force,
        bool watchCategoriesChanged,
        DateTime? previousDeltaSyncAt,
        CancellationToken ct)
    {
        if (force || !watchCategoriesChanged || previousDeltaSyncAt is not { } since)
        {
            return [];
        }

        return await db.WatchedTitles
            .Where(w => w.AccountId == accountId
                && w.FirstSyncedAt > since
                && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == w.TitleId
                    && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)))
            .OrderByDescending(w => w.FirstSyncedAt)
            .Take(PassiveWatchBudget)
            .Select(w => w.TitleId)
            .ToListAsync(ct);
    }

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        try
        {
            await RunAsync(job, ct);
        }
        catch (TraktTokenUnreadableException ex)
        {
            // A token no key ring here can decrypt fails identically on every attempt; the token
            // store has already marked the connection Revoked, so the poll stops re-enqueuing it and
            // the user is asked to re-link. Fail once (R1-27).
            throw new NonRetryableJobException(ex.Message, ex);
        }
    }

    private async Task RunAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("DeltaSync requires an account.");
        var connection = await db.TraktConnections.FirstAsync(c => c.AccountId == accountId, ct);
        var token = await tokenStore.GetValidAccessTokenAsync(connection, ct);
        var force = job.CursorJson is not null
            && (JsonSerializer.Deserialize<Cursor>(job.CursorJson)?.Force ?? false);

        // Live, staged progress for the header pill. The poll fallback reads the persisted
        // ProgressMessage/Pct; the SSE frame pushes the same in real time (with raw current/total
        // so the bar and "x / y" counter render without re-parsing the copy). Kind stays "delta"
        // even on a forced refresh — to the user this is "syncing with Trakt", whatever triggered it.
        async Task ReportAsync(int pct, string message, int? current = null, int? total = null)
        {
            job.ProgressPct = pct;
            job.ProgressMessage = message;
            await db.SaveChangesAsync(ct);
            events.Publish(accountId, PipelineEventTypes.JobProgress, new Dictionary<string, object?>
            {
                ["kind"] = "delta",
                ["pct"] = pct,
                ["message"] = message,
                ["current"] = current,
                ["total"] = total,
            });
        }

        await ReportAsync(4, "Checking Trakt for changes…");
        string activitiesRaw;
        try
        {
            activitiesRaw = await trakt.GetLastActivitiesRawAsync(token, RatePriority.DeltaPoll, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The stored access token died before its tracked expiry (revoked server-side, or rotated
            // out-of-band). Reactively rotate it via the refresh token — the same self-heal the
            // write-back path uses — then retry. One refresh on demand, not a refresh on every sync.
            // The fresh token then flows into the ingest calls below.
            logger.LogInformation("Trakt returned 401 during DeltaSync for {AccountId}; reactively refreshing the token.", accountId);
            token = await tokenStore.ForceRefreshAsync(connection, token, ct);
            activitiesRaw = await trakt.GetLastActivitiesRawAsync(token, RatePriority.DeltaPoll, ct);
        }
        var changes = DiffActivities(connection.LastActivitiesJson, activitiesRaw, force);

        var watchedAdded = 0;
        if (changes.Movies)
        {
            await ReportAsync(12, "Pulling your watched movies…");
            var movies = await ingestor.IngestMoviesAsync(accountId, token, RatePriority.DeltaPoll, ct);
            watchedAdded += movies.Added;
            await ReportAsync(
                32,
                string.Create(CultureInfo.InvariantCulture, $"Watched movies · {movies.Count:N0}"),
                movies.Count,
                movies.Count);
        }

        if (changes.Shows)
        {
            await ReportAsync(38, "Pulling your watched shows…");
            var showResult = await ingestor.IngestShowsAsync(accountId, token, RatePriority.DeltaPoll, ct);
            var (shows, episodes) = (showResult.Shows, showResult.Episodes);
            watchedAdded += showResult.Added;
            await ReportAsync(
                60,
                string.Create(CultureInfo.InvariantCulture, $"Watched shows · {shows:N0} · {episodes:N0} episodes"),
                shows,
                shows);

            // Continue-watching enrichment is a per-show Trakt call, so it carries a genuine x / y.
            await ingestor.EnrichNextEpisodesAsync(accountId, token, budget: 15, ct,
                onProgress: (done, total) => ReportAsync(
                    60 + (total == 0 ? 18 : 18 * done / total),
                    string.Create(CultureInfo.InvariantCulture, $"Finding your next episodes · {done:N0}/{total:N0}"),
                    done,
                    total));
        }
        else if (!force)
        {
            // The next-episode air-date back-fill (N+5), riding the poll rather than a one-off job:
            // a handful of progress calls per delta for rows that know their next episode but not
            // when it aired, and none at all once that list is empty. Idempotent by construction —
            // a filled row leaves the list — and rate-gated on the Backfill lane like every other
            // progress call.
            await ingestor.EnrichNextEpisodesAsync(accountId, token, budget: NextEpisodeBackfillPerDelta, ct, backfillOnly: true);
        }

        IReadOnlyList<TraktLibraryIngestor.IngestedRating> ratings = [];
        var ratingsAdded = 0;
        var ratingsChanged = 0;
        if (changes.Ratings)
        {
            await ReportAsync(82, "Pulling your ratings…");
            var result = await ingestor.IngestRatingsAsync(accountId, token, RatePriority.DeltaPoll, ct);
            ratings = result.Observed;
            ratingsAdded = result.Added;
            ratingsChanged = result.Updated;
            await ReportAsync(
                94,
                string.Create(CultureInfo.InvariantCulture, $"Ratings · {result.Count:N0}"),
                result.Count,
                result.Count);
        }

        // The Trakt watchlist into Saved (N-5), after ratings so a title rated in this same sync is
        // already consumed and is not saved. Read-only towards Trakt; see WatchlistIntake.
        var watchlistSaved = 0;
        if (changes.Watchlist)
        {
            await ReportAsync(95, "Reading your Trakt watchlist…");
            watchlistSaved = (await ingestor.IngestWatchlistAsync(accountId, token, RatePriority.DeltaPoll, ct)).Saved;
        }

        // Hidden and dropped items are recorded, not applied (FD-17).
        if (changes.Hidden)
        {
            await ingestor.IngestHiddenAsync(connection, token, RatePriority.Backfill, ct);
        }

        // The cursor BEFORE this sync moved it — everything the passive-watch observe below is
        // allowed to consider must be newer than this instant.
        var previousDeltaSyncAt = connection.LastDeltaSyncAt;
        connection.LastActivitiesJson = activitiesRaw;
        connection.LastDeltaSyncAt = DateTime.UtcNow;
        if (force)
        {
            connection.LastFullReconcileAt = DateTime.UtcNow;
        }

        // A clean sync proves the Trakt connection is healthy again. Release any write-backs that
        // exponentially backed off during an outage (token expiry / rate-limit) so the user's
        // saved, rated and list items reach Trakt on the next dispatch tick instead of waiting out
        // an hours-long delay. DeadLetter entries stay parked — they've genuinely given up.
        await db.TraktOutbox
            .Where(o => o.Status == OutboxStatus.Failed && o.NextAttemptAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow), ct);

        // Managed-list hygiene, moved off the nightly-only path: a title just detected watched (movies
        // or shows category moved) leaves "Reel — Up Next" now — within minutes of the play, not up to
        // 24h later. The Trakt ListRemove enqueue rides the same tracked context and commits below.
        // Ratings count too: a title rated on Trakt is a title seen, and its save is consumed — the
        // reconciler revokes it in this same transaction rather than leaving it on the Saved shelf.
        if (changes.Movies || changes.Shows || changes.Ratings)
        {
            var removed = await managedList.RemoveWatchedAsync(accountId, DateTime.UtcNow, ct);
            if (removed.Count > 0)
            {
                logger.LogInformation(
                    "DeltaSync for {AccountId}: removed {Count} watched or rated item(s) from the managed list ({TitleIds}).",
                    accountId, removed.Count, string.Join(", ", removed));
            }
        }

        // Passive watches nudge the residual head too, under the bound documented and asserted on
        // PassiveWatchesToObserveAsync — normal deltas only, rows first seen since the previous one
        // only, capped at PassiveWatchBudget.
        var newlyWatched = await PassiveWatchesToObserveAsync(
            db, accountId, force, changes.Movies || changes.Shows, previousDeltaSyncAt, ct);
        if (newlyWatched.Count > 0)
        {
            await residual.ObserveBatchAsync(
                accountId,
                newlyWatched
                    .Select(id => new OnlineResidualModel.ObservedEvent(id, PassiveWatchTarget, PassiveWatchWeight))
                    .ToList(),
                ct);
        }

        // Trakt-synced ratings are ~98% of the founder's ratings and have never reached the head —
        // only the 2% made in-app ever did. Feeding them is the obvious fix and is deliberately OFF
        // by default: the head was saturated by exactly this kind of bulk teaching once already, and
        // the plan requires an offline replay dry run (see the /api/dev/residual-replay tool) before
        // anyone turns it on. Target = the rating itself, weight 1.0 — the same contract as an in-app
        // rating, because it IS the same event, observed later.
        if (!force && ratings.Count > 0 && FeatureFlags.IsEnabled(configuration, FeatureFlags.ResidualObserveTraktRatings, fallback: false))
        {
            await residual.ObserveBatchAsync(
                accountId,
                ratings
                    .Select(r => new OnlineResidualModel.ObservedEvent(r.TitleId, r.Rating, 1.0))
                    .ToList(),
                ct);
        }

        // New library data landed → regenerate the feed now, reusing the Active model (the seconds-long
        // serving pass, not a retrain), so fresh, still-eligible picks lazy-load in as the sync arrives
        // instead of waiting for the nightly build. Coalesced + committed in this job's transaction.
        // A watchlist entry that became a save is an exclusion the page must drop, so it rebuilds too.
        if (changes.Any || watchlistSaved > 0)
        {
            await feedRefresh.RequestRebuildAsync(accountId, force ? "reconcile" : "delta-sync", ct);
        }

        // What the sync actually did, in the user's terms. `picksChanged` is deliberately null here:
        // the rebuild above was ENQUEUED, not awaited (a sync that blocked on a feed build would
        // stall the 5-minute poll behind a seconds-to-minutes job), so the number arrives later on
        // the build's own feed-ready frame and the client joins the two.
        var (retrainQueued, retrainAt) = await retrainGate.OutlookAsync(accountId, DateTime.UtcNow, ct);
        var summary = new SyncSummary(
            RatingsAdded: ratingsAdded,
            RatingsChanged: ratingsChanged,
            WatchedAdded: watchedAdded,
            RetrainQueued: retrainQueued,
            RetrainAt: retrainAt,
            PicksChanged: null);

        job.ProgressPct = 100;
        // The persisted message is the SSE frame's sentence, not a different one: the header pill
        // polls this every 5 seconds as its fallback, and two renderers meant the copy changed under
        // the user whenever the poll overtook the frame.
        job.ProgressMessage = SyncSummaryCopy.Describe(summary);
        await db.SaveChangesAsync(ct);

        if (changes.Any)
        {
            events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?>
            {
                ["kind"] = force ? "reconcile" : "delta",
                ["ratingsAdded"] = summary.RatingsAdded,
                ["ratingsChanged"] = summary.RatingsChanged,
                ["watchedAdded"] = summary.WatchedAdded,
                ["retrainQueued"] = summary.RetrainQueued,
                ["retrainAt"] = summary.RetrainAt,
                ["picksChanged"] = summary.PicksChanged,
            });
        }

        logger.LogInformation("DeltaSync for {AccountId}: movies={Movies} shows={Shows} ratings={Ratings} (force={Force}).",
            accountId, changes.Movies, changes.Shows, changes.Ratings, force);
    }

    /// <summary>
    /// Which categories moved. <see cref="Any"/> is the library (watches and ratings) only: a moved
    /// watchlist rebuilds the page only when it actually produced a save, and hidden items are
    /// recorded without being applied, so neither alone is a reason to rebuild.
    /// </summary>
    public record ChangedCategories(bool Movies, bool Shows, bool Ratings, bool Watchlist = false, bool Hidden = false)
    {
        public bool Any => Movies || Shows || Ratings;
    }

    /// <summary>Compares category timestamps between two /sync/last_activities snapshots.</summary>
    public static ChangedCategories DiffActivities(string? previousJson, string currentJson, bool force)
    {
        if (force || previousJson is null)
        {
            return new ChangedCategories(true, true, true, Watchlist: true, Hidden: true);
        }

        using var previous = JsonDocument.Parse(previousJson);
        using var current = JsonDocument.Parse(currentJson);

        bool Changed(string category, string field)
        {
            var prev = GetTimestamp(previous.RootElement, category, field);
            var curr = GetTimestamp(current.RootElement, category, field);
            return prev != curr;
        }

        var movies = Changed("movies", "watched_at");
        var shows = Changed("episodes", "watched_at");
        var ratings = Changed("movies", "rated_at") || Changed("shows", "rated_at")
            || Changed("seasons", "rated_at") || Changed("episodes", "rated_at");

        // The watchlist moves on any add or remove, Reel's own write-backs included — the ingest
        // tells those apart, the gate only decides whether to look.
        var watchlist = Changed("watchlist", "updated_at")
            || Changed("movies", "watchlisted_at") || Changed("shows", "watchlisted_at");
        var hidden = Changed("movies", "hidden_at") || Changed("shows", "hidden_at")
            || Changed("seasons", "hidden_at") || Changed("shows", "dropped_at");

        return new ChangedCategories(movies, shows, ratings, watchlist, hidden);
    }

    private static string? GetTimestamp(JsonElement root, string category, string field) =>
        root.TryGetProperty(category, out var cat) && cat.ValueKind == JsonValueKind.Object
            && cat.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
