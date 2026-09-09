using System.Net;
using System.Text.Json;
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
    IPipelineEventHub events,
    ILogger<DeltaSyncJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.DeltaSync;

    private record Cursor(bool Force);

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
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

        if (changes.Movies)
        {
            await ReportAsync(12, "Pulling your watched movies…");
            var movies = await ingestor.IngestMoviesAsync(accountId, token, RatePriority.DeltaPoll, ct);
            await ReportAsync(32, $"Watched movies · {movies:N0}", movies, movies);
        }

        if (changes.Shows)
        {
            await ReportAsync(38, "Pulling your watched shows…");
            var (shows, episodes) = await ingestor.IngestShowsAsync(accountId, token, RatePriority.DeltaPoll, ct);
            await ReportAsync(60, $"Watched shows · {shows:N0} · {episodes:N0} episodes", shows, shows);

            // Continue-watching enrichment is a per-show Trakt call, so it carries a genuine x / y.
            await ingestor.EnrichNextEpisodesAsync(accountId, token, budget: 15, ct,
                onProgress: (done, total) => ReportAsync(
                    60 + (total == 0 ? 18 : 18 * done / total),
                    $"Finding your next episodes · {done:N0}/{total:N0}", done, total));
        }

        if (changes.Ratings)
        {
            await ReportAsync(82, "Pulling your ratings…");
            var ratings = await ingestor.IngestRatingsAsync(accountId, token, RatePriority.DeltaPoll, ct);
            await ReportAsync(94, $"Ratings · {ratings:N0}", ratings, ratings);
        }

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
        if (changes.Movies || changes.Shows)
        {
            var removed = await managedList.RemoveWatchedAsync(accountId, DateTime.UtcNow, ct);
            if (removed.Count > 0)
            {
                logger.LogInformation(
                    "DeltaSync for {AccountId}: removed {Count} watched item(s) from the managed list ({TitleIds}).",
                    accountId, removed.Count, string.Join(", ", removed));
            }
        }

        // Passive watches nudge the residual head too — but ONLY on a normal delta, never on a force/
        // full reconcile (that replays the whole library and would drown the head in one pass). Bounded
        // to the 20 most-recently-first-synced watched titles the user hasn't rated (rated titles teach
        // the head via the rating path), with a soft positive pseudo-label — a watch is weak "I chose
        // this" signal, not an explicit score. The Take(20) cap keeps a normal delta cheap.
        if (!force && (changes.Movies || changes.Shows))
        {
            var recentUnratedWatched = await db.WatchedTitles
                .Where(w => w.AccountId == accountId
                    && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == w.TitleId
                        && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)))
                .OrderByDescending(w => w.FirstSyncedAt)
                .Take(20)
                .Select(w => w.TitleId)
                .ToListAsync(ct);
            foreach (var titleId in recentUnratedWatched)
            {
                await residual.ObserveAsync(accountId, titleId, 6.8, 0.2, ct);
            }
        }

        // New library data landed → regenerate the feed now, reusing the Active model (the seconds-long
        // serving pass, not a retrain), so fresh, still-eligible picks lazy-load in as the sync arrives
        // instead of waiting for the nightly build. Coalesced + committed in this job's transaction.
        if (changes.Any)
        {
            await feedRefresh.RequestRebuildAsync(accountId, force ? "reconcile" : "delta-sync", ct);
        }

        job.ProgressPct = 100;
        job.ProgressMessage = changes.Any
            ? $"synced: {(changes.Movies ? "movies " : "")}{(changes.Shows ? "shows " : "")}{(changes.Ratings ? "ratings" : "")}".Trim()
            : "Already up to date";
        await db.SaveChangesAsync(ct);

        if (changes.Any)
        {
            events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?>
            {
                ["kind"] = force ? "reconcile" : "delta",
            });
        }

        logger.LogInformation("DeltaSync for {AccountId}: movies={Movies} shows={Shows} ratings={Ratings} (force={Force}).",
            accountId, changes.Movies, changes.Shows, changes.Ratings, force);
    }

    public record ChangedCategories(bool Movies, bool Shows, bool Ratings)
    {
        public bool Any => Movies || Shows || Ratings;
    }

    /// <summary>Compares category timestamps between two /sync/last_activities snapshots.</summary>
    public static ChangedCategories DiffActivities(string? previousJson, string currentJson, bool force)
    {
        if (force || previousJson is null)
        {
            return new ChangedCategories(true, true, true);
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

        return new ChangedCategories(movies, shows, ratings);
    }

    private static string? GetTimestamp(JsonElement root, string category, string field) =>
        root.TryGetProperty(category, out var cat) && cat.ValueKind == JsonValueKind.Object
            && cat.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
