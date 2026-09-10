using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
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
/// every category refetched regardless of last_activities) AND a Train when
/// <see cref="RetrainGate"/> says one is owed — ≥ RETRAIN_MIN_NEW_RATINGS new ratings since it last
/// trained, an artifact older than 14 days, or a CALIBRATION BREACH on the account's own closed loop
/// (a fresh, well-fed model can still be badly mis-levelled, and the staleness rules cannot see it).
/// Train chains Evaluate → BuildFeed. Gate: Background:NightlyReconcile.
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
            await SweepEnrichmentBacklogAsync(stoppingToken);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                var managedList = scope.ServiceProvider.GetRequiredService<ManagedListReconciler>();
                // The gate is shared with DeltaSyncJobHandler's post-sync message on purpose: the
                // pill promises "retraining tonight" from exactly the rules that decide it here.
                var retrainGate = scope.ServiceProvider.GetRequiredService<RetrainGate>();
                var personAffinities = scope.ServiceProvider.GetRequiredService<PersonAffinityWriter>();
                var featureBuilder = scope.ServiceProvider.GetRequiredService<FeatureVectorBuilder>();
                var now = DateTime.UtcNow;

                var accountIds = await db.TraktConnections
                    .Where(c => c.Status == ConnectionStatus.Active)
                    .Select(c => c.AccountId)
                    .ToListAsync(stoppingToken);

                var enqueued = 0;
                var retrains = 0;
                var listRemovals = 0;
                var affinityBackfills = 0;
                var narratives = 0;
                var lensPasses = 0;
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

                    // Retrain when the model has drifted — ≥N ratings since it last trained, or it's
                    // older than two weeks — OR when the account's own closed loop says it is
                    // measurably WRONG (|bias| > 0.75 or MAE > 2.0 on ≥30 pairs the current artifact
                    // itself produced), which the staleness rules cannot see at all: a model can be
                    // fresh, well-fed and badly mis-levelled. The breach trigger carries a one-day
                    // floor because it reads the same pairs the last train was fitted against, so
                    // without one a genuinely hard account would retrain every night on numbers that
                    // have not moved. The nightly cadence itself is unchanged. Train chains
                    // Evaluate → BuildFeed, so the feed refreshes.
                    var trainInFlight = await db.SyncJobs.AnyAsync(
                        j => j.AccountId == accountId && (j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                        stoppingToken);
                    if (!trainInFlight
                        && await retrainGate.EvaluateAsync(accountId, now, stoppingToken) is { } trigger)
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
                        logger.LogInformation(
                            "Nightly reconcile queued a retrain for {AccountId} ({Trigger}).", accountId, trigger);
                    }

                    // Person-affinity backfill, ONCE, for an account that has none. The steady-state
                    // writer is TrainJobHandler — affinities are refreshed with the model they are
                    // served alongside — so this exists only to close the gap for accounts that were
                    // trained before the writer existed, or that are not due a retrain tonight. It is
                    // deliberately conditional on the table being empty rather than on age: a nightly
                    // unconditional rewrite would build a full taste state per account for a cache
                    // that a retrain is about to rebuild anyway.
                    if (await personAffinities.IsEmptyAsync(accountId, stoppingToken))
                    {
                        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, stoppingToken);
                        var written = await personAffinities.RefreshAsync(accountId, taste, now, stoppingToken);
                        if (written.Total > 0)
                        {
                            affinityBackfills++;
                        }
                    }

                    // The weekly taste narrative. Sunday, and only when the account has not been
                    // written one inside the last week — the handler ALSO exits early when the
                    // account's taste fingerprint has not moved, so the cadence here is a ceiling on
                    // how often it may be asked, not a promise that it will spend. A build that sees
                    // the fingerprint move queues it sooner; this is the floor under that.
                    if (now.DayOfWeek == DayOfWeek.Sunday)
                    {
                        var writtenRecently = await db.AccountTasteNarratives.AnyAsync(
                            n => n.AccountId == accountId && n.GeneratedAt >= now.AddDays(-6), stoppingToken);
                        var narrativeInFlight = await db.SyncJobs.AnyAsync(
                            j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative
                                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                            stoppingToken);
                        if (!writtenRecently && !narrativeInFlight)
                        {
                            db.SyncJobs.Add(new SyncJob
                            {
                                Id = Guid.NewGuid(),
                                AccountId = accountId,
                                Kind = JobKind.BuildTasteNarrative,
                                Priority = 4,
                                EnqueuedAt = now,
                                TriggerReason = "weekly-narrative",
                            });
                            narratives++;
                        }
                    }

                    // The Saved prefetch: lens copy for the queue's top twenty by prediction. The
                    // queue is where deliberation happens, so it is where the richest copy belongs —
                    // and the nightly pass is when it can be written without anyone waiting. The
                    // account's own daily ceiling still binds, so this cannot run away.
                    var lensInFlight = await db.SyncJobs.AnyAsync(
                        j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles
                            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                        stoppingToken);
                    if (!lensInFlight)
                    {
                        db.SyncJobs.Add(new SyncJob
                        {
                            Id = Guid.NewGuid(),
                            AccountId = accountId,
                            Kind = JobKind.PersonaliseTitles,
                            Priority = 4,
                            EnqueuedAt = now,
                            TriggerReason = "nightly-saved-prefetch",
                            CursorJson = Application.Jobs.PersonaliseTitlesJobHandler.SavedCursor,
                        });
                        lensPasses++;
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
                    "Nightly reconcile: {Sync} delta sync(s), {Retrain} retrain(s), {ListRemove} managed-list removal(s), "
                    + "{AffinityBackfill} person-affinity backfill(s), {Narrative} taste narrative(s), "
                    + "{Lens} lens pass(es).",
                    enqueued, retrains, listRemovals, affinityBackfills, narratives, lensPasses);
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

    /// <summary>
    /// The nightly ENRICHMENT pass — two steps, both bounded by the same per-run cap the handler
    /// uses, and deliberately kept out of the account loop above so that a failure here can never
    /// cost the reconcile its delta syncs or its retrains.
    /// <para>
    /// <b>Step 1 — ExtractorVersion invalidation.</b> The exact analogue of the feature-schema sweep
    /// above, one layer down: an artifact trained against a superseded <c>FeatureSchema</c> misreads
    /// its own inputs, and an attribute row written by a superseded PROMPT misreports its own title.
    /// The failure mode is worse here because it is invisible — the row looks Done, and a Tone that
    /// predates the closed vocabulary is silently dropped by every reader that verifies it. Rows
    /// below the running version go back to Pending with a fresh attempt budget.
    /// </para>
    /// <para>
    /// <b>Step 2 — one EnrichCatalog per active account.</b> This is the floor under the whole
    /// pipeline: builds enqueue enrichment only when they upserted something, so an account whose
    /// discovery is dry would otherwise never enrich the backlog it already has.
    /// </para>
    /// </summary>
    private async Task SweepEnrichmentBacklogAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var extractor = scope.ServiceProvider.GetRequiredService<ITitleAttributeExtractor>();
            var cap = EnrichCatalogJobHandler.MaxTitlesPerRun(configuration);

            // Committed on its own, before the enqueues: SaveToleratingUniqueRaceAsync DISCARDS the
            // whole unit of work when a concurrent enqueue wins the job race, and losing a batch of
            // version resets to an unrelated job collision would silently stall the drain.
            var requeued = await EnrichmentScheduler.RequeueStaleExtractorVersionsAsync(
                db, extractor.Version, cap, ct);
            if (requeued > 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Extractor-version sweep re-queued {Count} attribute row(s) written before prompt v{Version}; "
                    + "the backlog drains at {Cap} titles per enrich run.",
                    requeued, extractor.Version, cap);
            }

            var accountIds = await db.TraktConnections
                .Where(c => c.Status == ConnectionStatus.Active)
                .Select(c => c.AccountId)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            var enqueued = 0;
            foreach (var accountId in accountIds)
            {
                if (await EnrichmentScheduler.EnqueueAsync(db, accountId, now, ct))
                {
                    enqueued++;
                }
            }

            if (enqueued > 0 && !await SaveToleratingUniqueRaceAsync(db, ct))
            {
                logger.LogInformation("Nightly enrichment: a concurrent enqueue won the race; this pass's duplicates were dropped.");
                return;
            }

            logger.LogInformation("Nightly enrichment: {Enqueued} enrich job(s) queued, {Requeued} row(s) invalidated.",
                enqueued, requeued);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never take the reconcile down over enrichment — the backlog is a quality problem, not
            // a correctness one, and the next nightly pass retries from wherever this one stopped.
            logger.LogError(ex, "Nightly enrichment sweep failed; the next pass continues the drain.");
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

    /// <summary>
    /// Time until the next 03:00 UTC pass. Delegates to <see cref="RetrainPolicy.NextNightlyRunUtc"/>
    /// so the schedule and the "retraining tonight at 03:00 UTC" the post-sync pill promises cannot
    /// drift apart — the hour is stated once, in the policy.
    /// </summary>
    internal static TimeSpan DelayUntilNextRun(DateTime utcNow) =>
        RetrainPolicy.NextNightlyRunUtc(utcNow) - utcNow;
}
