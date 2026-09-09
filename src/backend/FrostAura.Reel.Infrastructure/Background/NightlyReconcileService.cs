using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// 03:00 UTC drift repair: per active account, enqueues a forced DeltaSync ({"force":true} →
/// every category refetched regardless of last_activities) AND a Train when the model has drifted
/// (≥ RETRAIN_MIN_NEW_RATINGS new ratings since it last trained, or >14 days old — Train chains
/// Evaluate → BuildFeed). Gate: Background:NightlyReconcile.
/// <para>
/// It also runs the FEATURE-SCHEMA SWEEP, once at startup and again on every nightly pass. That one
/// is NOT gated: an artifact trained against a superseded <see cref="FeatureSchema"/> version is
/// unscoreable by construction, and leaving it Active would keep the account modelless and silent.
/// </para>
/// </summary>
public sealed class NightlyReconcileService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<NightlyReconcileService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before anything else, and before the config gate: a feature-schema version can only go
        // stale across a DEPLOY (it is a compile-time constant), so sweeping here — synchronously,
        // during host start-up — closes the window in which a request could load one.
        await SweepStaleSchemaArtifactsAsync(stoppingToken);

        if (!configuration.GetValue("Background:NightlyReconcile", true))
        {
            logger.LogInformation("NightlyReconcile disabled via Background:NightlyReconcile=false.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(DateTime.UtcNow);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SweepStaleSchemaArtifactsAsync(stoppingToken);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                var managedList = scope.ServiceProvider.GetRequiredService<ManagedListReconciler>();
                var now = DateTime.UtcNow;

                var accountIds = await db.TraktConnections
                    .Where(c => c.Status == ConnectionStatus.Active)
                    .Select(c => c.AccountId)
                    .ToListAsync(stoppingToken);

                var retrainThreshold = configuration.GetValue("RETRAIN_MIN_NEW_RATINGS", 10);
                var enqueued = 0;
                var retrains = 0;
                var listRemovals = 0;
                foreach (var accountId in accountIds)
                {
                    // Managed-list auto-remove backstop: anything in "Reel — Up Next" detected watched
                    // (via sync, outside the app) leaves the live queue — the stated product promise.
                    // The reactive DeltaSync path already does this within minutes; this nightly pass is
                    // the drift repair via the same shared reconciler.
                    listRemovals += (await managedList.RemoveWatchedAsync(accountId, now, stoppingToken)).Count;

                    var deltaInFlight = await db.SyncJobs.AnyAsync(
                        j => j.AccountId == accountId && j.Kind == JobKind.DeltaSync
                            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                        stoppingToken);
                    if (!deltaInFlight)
                    {
                        db.SyncJobs.Add(new SyncJob
                        {
                            Id = Guid.NewGuid(),
                            AccountId = accountId,
                            Kind = JobKind.DeltaSync,
                            Priority = 2,
                            EnqueuedAt = now,
                            CursorJson = """{"Force":true}""",
                        });
                        enqueued++;
                    }

                    // Retrain when the model has drifted: ≥N ratings since it last trained, or it's
                    // older than two weeks. Train chains Evaluate → BuildFeed, so the feed refreshes.
                    var trainInFlight = await db.SyncJobs.AnyAsync(
                        j => j.AccountId == accountId && (j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                        stoppingToken);
                    if (!trainInFlight)
                    {
                        var trainedAt = await db.ModelArtifacts
                            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
                            .Select(a => (DateTime?)a.TrainedAt)
                            .FirstOrDefaultAsync(stoppingToken);
                        if (trainedAt is { } since)
                        {
                            var newRatings = await db.UserRatings
                                .CountAsync(r => r.AccountId == accountId && r.RatedAt > since, stoppingToken);
                            if (newRatings >= retrainThreshold || (now - since).TotalDays > 14)
                            {
                                db.SyncJobs.Add(new SyncJob
                                {
                                    Id = Guid.NewGuid(),
                                    AccountId = accountId,
                                    Kind = JobKind.Train,
                                    Priority = 3,
                                    EnqueuedAt = now,
                                });
                                retrains++;
                            }
                        }
                    }
                }

                // A concurrent enqueue (EnrichCatalogJobHandler's chain, another instance's nightly
                // pass) can win the check-then-insert race above; the partial unique index on
                // SyncJobs then rejects OUR duplicate with 23505. That is the index working, not a
                // reconcile failure — absorb it rather than losing the whole batch to a rollback.
                if (!await SaveToleratingUniqueRaceAsync(db, stoppingToken))
                {
                    logger.LogInformation("Nightly reconcile: a concurrent enqueue won the race; this pass's duplicates were dropped.");
                    continue;
                }

                logger.LogInformation(
                    "Nightly reconcile: {Sync} delta sync(s), {Retrain} retrain(s), {ListRemove} managed-list removal(s).",
                    enqueued, retrains, listRemovals);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Nightly reconcile pass failed; next run continues the schedule.");
            }
        }
    }

    /// <summary>
    /// Demotes every Active artifact whose recorded <see cref="FeatureSchema"/> version is not the
    /// running one, and queues a retrain for its account.
    /// <para>
    /// <c>TrainJobHandler</c> writes the version into <c>FeatureSchemaJson</c> but, until this sweep
    /// existed, nothing ever read it back — and the engine validates vector WIDTH only, never column
    /// ORDER, which is precisely the silent failure FeatureSchema's doc-comment warns about. The
    /// query filter in <see cref="ReelDbContext"/> already makes such an artifact invisible to every
    /// scoring path (so it is never used); this closes the loop by getting the account a model it
    /// CAN use, and by saying so loudly in the log rather than degrading in silence.
    /// </para>
    /// </summary>
    private async Task SweepStaleSchemaArtifactsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

            // IgnoreQueryFilters — the mismatched rows are exactly the ones the filter hides.
            var stale = await db.ModelArtifacts
                .IgnoreQueryFilters()
                .Where(a => a.Status == ArtifactStatus.Active
                    && EF.Property<int?>(a, ReelDbContext.FeatureSchemaVersionProperty) != FeatureSchema.Version)
                .ToListAsync(ct);
            if (stale.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var artifact in stale)
            {
                logger.LogError(
                    "Model artifact {ArtifactId} (account {AccountId}, v{Version}) was trained against feature schema "
                    + "{StoredSchema} but this build runs schema {CurrentSchema}. Scoring with it would silently misread "
                    + "its own inputs, so it is being superseded and the account queued for a retrain.",
                    artifact.Id, artifact.AccountId, artifact.Version,
                    db.Entry(artifact).Property<int?>(ReelDbContext.FeatureSchemaVersionProperty).CurrentValue,
                    FeatureSchema.Version);
                artifact.Status = ArtifactStatus.Superseded;
            }

            foreach (var accountId in stale.Select(a => a.AccountId).Distinct())
            {
                var inFlight = await db.SyncJobs.AnyAsync(
                    j => j.AccountId == accountId && j.Kind == JobKind.Train
                        && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
                if (inFlight)
                {
                    continue;
                }

                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Kind = JobKind.Train,
                    Priority = 1,
                    EnqueuedAt = now,
                });
            }

            await SaveToleratingUniqueRaceAsync(db, ct);
            logger.LogWarning(
                "Feature-schema sweep superseded {Count} artifact(s) trained against an older schema than v{Version}.",
                stale.Count, FeatureSchema.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never take the host down over the sweep — the query filter already prevents the stale
            // artifact from being scored with, so the worst case is a delayed retrain.
            logger.LogError(ex, "Feature-schema artifact sweep failed; stale artifacts stay hidden until the next pass.");
        }
    }

    private static async Task<bool> SaveToleratingUniqueRaceAsync(ReelDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    internal static TimeSpan DelayUntilNextRun(DateTime utcNow)
    {
        var todayRun = utcNow.Date.AddHours(3);
        var nextRun = utcNow < todayRun ? todayRun : todayRun.AddDays(1);
        return nextRun - utcNow;
    }
}
