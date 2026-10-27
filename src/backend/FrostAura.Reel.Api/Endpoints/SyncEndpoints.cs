using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class SyncEndpoints
{
    public record HistoryCleanupRequest(string MediaType, long TmdbId, DateTime StartAt, DateTime EndAt, bool DryRun = true);

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").RequireAccount();

        // Contract v2 — the shape lives in Application/Pipeline/SyncStatusContract.cs and the rules
        // in SyncStatusQuery. Typed, so the build-time OpenAPI document carries the full schema
        // and the generated frontend types follow it.
        group.MapGet("/status", async (
            IReelDbContext db, SyncStatusQuery statusQuery, IConfiguration configuration,
            HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            // Progress toward the next retrain, so a screen that asks for ratings can say what they
            // BUY: "7 more ratings and Reel retrains tonight". The same threshold the nightly
            // reconcile's gate uses.
            var retrainThreshold = configuration.GetValue("RETRAIN_MIN_NEW_RATINGS", 10);
            var status = await statusQuery.GetAsync(account, retrainThreshold, DateTime.UtcNow, ct);
            return Results.Ok(status);
        })
        .Produces<SyncStatusResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

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
                    // The one DeltaSync the header shows: the user asked for it and is watching.
                    TriggerReason = JobVisibilityPolicy.SyncNowReason,
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
        // what would be removed. Founder-only through the shared route gate, never an inline tier
        // comparison (J-11): the gate is what the authorisation matrix sees.
        group.MapPost("/history-cleanup", async (
            HistoryCleanupRequest request,
            IReelDbContext db, HttpContext http,
            TraktTokenStore tokenStore, ITraktClient trakt, CancellationToken ct) =>
        {
            var account = http.GatedAccount();

            // The record's MediaType is non-nullable in C# only; a body without it binds to null.
            if (string.IsNullOrWhiteSpace(request.MediaType))
            {
                return Results.BadRequest(new { error = "mediaType required" });
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
        })
        .RequireFounder();
    }
}
