using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class SyncEndpoints
{
    public record HistoryCleanupRequest(string MediaType, long TmdbId, DateTime StartAt, DateTime EndAt, bool DryRun = true);

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").RequireAccount();

        group.MapGet("/status", async (IReelDbContext db, IAccountContext accountContext, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.AccountId == account.Id, ct);
            var jobs = await db.SyncJobs
                .Where(j => j.AccountId == account.Id)
                .OrderByDescending(j => j.EnqueuedAt)
                .Take(6)
                .ToListAsync(ct);

            var outboxPending = await db.TraktOutbox.CountAsync(o => o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed, ct);
            var outboxDead = await db.TraktOutbox.CountAsync(o => o.Status == OutboxStatus.DeadLetter, ct);

            // List/write-back health for the header indicator: when Trakt last accepted a write, and
            // the most recent failure text (so a stuck list surfaces the actual reason on hover).
            var lastListSyncAt = await db.TraktOutbox
                .Where(o => o.Status == OutboxStatus.Sent && o.SentAt != null)
                .MaxAsync(o => (DateTime?)o.SentAt, ct);
            var outboxLastError = await db.TraktOutbox
                .Where(o => (o.Status == OutboxStatus.Failed || o.Status == OutboxStatus.DeadLetter) && o.Error != null)
                .OrderByDescending(o => o.NextAttemptAt)
                .Select(o => o.Error)
                .FirstOrDefaultAsync(ct);

            static object ToJobSummary(SyncJob job) => new
            {
                kind = job.Kind.ToString(),
                status = job.Status.ToString(),
                progressPct = job.ProgressPct,
                progressMessage = job.ProgressMessage,
                enqueuedAt = job.EnqueuedAt,
                completedAt = job.CompletedAt,
            };

            // The pill should surface the job the user cares about. A primary pipeline job
            // (onboarding/enrichment/training) outranks a background Trakt poll (delta/reconcile)
            // that fires every few minutes — otherwise a transient DeltaSync, enqueued after a
            // long EnrichCatalog, would hijack the pill and read as a generic "Syncing".
            static bool IsBackground(JobKind k) =>
                k is JobKind.DeltaSync or JobKind.FullReconcile or JobKind.RefreshAvailability;

            var active = jobs.Where(j => j.Status == JobStatus.Running)
                    .OrderBy(j => IsBackground(j.Kind) ? 1 : 0)
                    .ThenByDescending(j => j.EnqueuedAt)
                    .FirstOrDefault()
                ?? jobs.Where(j => j.Status == JobStatus.Pending)
                    .OrderBy(j => IsBackground(j.Kind) ? 1 : 0)
                    .ThenByDescending(j => j.EnqueuedAt)
                    .FirstOrDefault();

            return Results.Ok(new
            {
                pipelineStage = account.PipelineStage.ToString(),
                connectionStatus = connection?.Status.ToString() ?? "Revoked",
                lastDeltaSyncAt = connection?.LastDeltaSyncAt,
                lastFullReconcileAt = connection?.LastFullReconcileAt,
                activeJob = active is null ? null : ToJobSummary(active),
                recentJobs = jobs.Select(ToJobSummary),
                outboxPending,
                outboxDeadLetters = outboxDead,
                lastListSyncAt,
                outboxLastError,
            });
        });

        group.MapPost("/now", async (IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var inFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == accountId
                    && (j.Kind == JobKind.DeltaSync || j.Kind == JobKind.FullIngest)
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);

            if (!inFlight)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Kind = JobKind.DeltaSync,
                    Priority = 0, // user asked — interactive priority
                    EnqueuedAt = DateTime.UtcNow,
                    // Force a full refetch, not an activity-diffed delta. A user hitting "sync now"
                    // wants the authoritative state — and Trakt does not bump last_activities
                    // watched_at when history is *removed*, so a diffed delta would silently skip a
                    // removal and the change would never land. Mirrors the nightly reconcile.
                    CursorJson = """{"Force":true}""",
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.Accepted();
        });

        // ── Founder-only surgical history repair ────────────────────────────────────────
        // Removes the Trakt history events for ONE title inside an explicit watched_at window —
        // the recovery tool for corruption like the show-object sync/history incident, where one
        // rating fabricated a play for every aired episode. DryRun (the default) only reports
        // what would be removed.
        group.MapPost("/history-cleanup", async (
            HistoryCleanupRequest request,
            IReelDbContext db, IAccountContext accountContext, HttpContext http,
            TraktTokenStore tokenStore, ITraktClient trakt, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null || account.Tier != AccountTier.Founder)
            {
                return Results.Forbid();
            }

            var title = await TitleEndpoints.ResolveTitleAsync(db, request.MediaType, request.TmdbId, ct);
            if (title is null)
            {
                return Results.NotFound();
            }

            var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.AccountId == account.Id, ct);
            if (connection is null || connection.Status == ConnectionStatus.Revoked)
            {
                return Results.BadRequest(new { error = "trakt connection unavailable" });
            }

            var token = await tokenStore.GetValidAccessTokenAsync(connection, ct);
            var traktId = title.TraktId;
            if (traktId is null)
            {
                traktId = await trakt.ResolveTraktIdByTmdbAsync(
                    token, request.TmdbId, title.MediaType == MediaType.Movie, RatePriority.Interactive, ct);
                if (traktId is null)
                {
                    return Results.BadRequest(new { error = "trakt id unresolved" });
                }

                title.TraktId = traktId; // resolved once, cached forever on the catalog row
            }

            var type = title.MediaType == MediaType.Movie ? "movies" : "shows";
            var history = await trakt.GetHistoryAsync(
                token, type, traktId.Value, request.StartAt, request.EndAt, RatePriority.Interactive, ct);

            if (request.DryRun)
            {
                await db.SaveChangesAsync(ct); // keep a freshly resolved Trakt id even on dry runs
                return Results.Ok(new
                {
                    dryRun = true,
                    found = history.Count,
                    // Capped for response size — `found` carries the full count.
                    events = history.Take(50).Select(h => new { id = h.Id, watchedAt = h.WatchedAt, type = h.Type }),
                });
            }

            // /sync/history/remove caps at 100 ids per call — chunk to stay under it.
            foreach (var chunk in history.Select(h => h.Id).Chunk(100))
            {
                await trakt.PostSyncBatchAsync(token, "sync/history/remove", new { ids = chunk }, RatePriority.Interactive, ct);
            }

            // Local watched state for the title is now stale — drop it and let a forced delta
            // resync rebuild the truth from Trakt (mirrors /api/sync/now).
            var watched = await db.WatchedTitles
                .Where(w => w.AccountId == account.Id && w.TitleId == title.Id)
                .ToListAsync(ct);
            db.WatchedTitles.RemoveRange(watched);
            if (title.MediaType == MediaType.Show)
            {
                var progress = await db.ShowWatchProgresses
                    .Where(p => p.AccountId == account.Id && p.TitleId == title.Id)
                    .ToListAsync(ct);
                db.ShowWatchProgresses.RemoveRange(progress);
            }

            var resyncInFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == account.Id
                    && (j.Kind == JobKind.DeltaSync || j.Kind == JobKind.FullIngest)
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
            if (!resyncInFlight)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    Kind = JobKind.DeltaSync,
                    Priority = 0, // founder asked — interactive priority
                    EnqueuedAt = DateTime.UtcNow,
                    // Force a full refetch: Trakt does not bump last_activities on history
                    // *removal*, so a diffed delta would skip the very change just made.
                    CursorJson = """{"Force":true}""",
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { dryRun = false, removed = history.Count, resyncQueued = true });
        });
    }
}
