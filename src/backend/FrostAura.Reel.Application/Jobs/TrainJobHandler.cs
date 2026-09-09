using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Trains the SERVING model on all ratings as-of now and persists the versioned artifact
/// (exactly one Active per account). Chains to Evaluate, which validates the same config on
/// the leakage-clean time split — serving and gate always measure the same engine.
/// </summary>
public class TrainJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    IPipelineEventHub events,
    ILogger<TrainJobHandler> logger) : IJobHandler
{
    public const string DefaultHyperparams = """{"Trees":200,"Leaves":20,"MinExamplesPerLeaf":10,"LearningRate":0.2}""";

    /// <summary>
    /// Fewest labelled rows worth fitting a tree on. Below this, ML.NET FastTree throws
    /// "All instances skipped due to missing features" (a zero-rating account is the common case —
    /// plenty of real Trakt users watch without ever rating), the job fails, retries three identical
    /// times and dies, and because the in-flight guard only looks at Pending/Running the next sync
    /// enqueues another guaranteed failure. The account is parked instead, with a message the UI can
    /// actually render. Matches the trainer's own MinExamplesPerLeaf default.
    /// </summary>
    public const int MinTrainingRows = 10;

    public JobKind Kind => JobKind.Train;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("Train requires an account.");
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var hyperparams = job.CursorJson is { } cursor && JsonSerializer.Deserialize<JsonElement>(cursor).TryGetProperty("hyperparams", out var hp)
            ? hp.GetRawText()
            : DefaultHyperparams;

        var now = DateTime.UtcNow;
        var labels = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .GroupBy(r => r.TitleId)
            .Select(g => new { TitleId = g.Key, Rating = g.Max(r => r.Rating) })
            .ToListAsync(ct);

        // Guard BEFORE the stage is advanced: the old code moved the account to Training and only
        // then hit the trainer's exception, leaving it parked mid-pipeline behind a permanent spinner.
        if (labels.Count < MinTrainingRows)
        {
            await ParkForInsufficientRatingsAsync(job, account, labels.Count, ct);
            return;
        }

        if (account.PipelineStage is PipelineStage.Extracting or PipelineStage.Linked or PipelineStage.Ingesting
            || (account.PipelineStage is PipelineStage.Degraded && await ConnectionIsHealthyAsync(accountId, ct)))
        {
            account.PipelineStage = PipelineStage.Training;
            account.PipelineStageChangedAt = DateTime.UtcNow;
            events.Publish(accountId, PipelineEventTypes.StageChanged, new Dictionary<string, object?>
            {
                ["stage"] = PipelineStage.Training.ToString(),
            });
        }

        events.Publish(accountId, PipelineEventTypes.ModelProgress, new Dictionary<string, object?>
        {
            ["phase"] = "Teaching your model",
            ["processed"] = 0,
            ["total"] = labels.Count,
        });

        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        var vectors = await featureBuilder.BuildAsync(taste, labels.Select(l => l.TitleId).ToList(), now, ct);
        var rows = labels.Where(l => vectors.ContainsKey(l.TitleId)).ToList();
        if (rows.Count < MinTrainingRows)
        {
            // Rated, but nothing vectorisable (catalogue rows missing) — same parking, same reason.
            await ParkForInsufficientRatingsAsync(job, account, rows.Count, ct);
            return;
        }

        var matrix = new TrainingMatrix(
            rows.Select(l => vectors[l.TitleId]).ToArray(),
            rows.Select(l => (float)l.Rating).ToArray(),
            FeatureSchema.AllNames());

        var trained = engine.Train(matrix, hyperparams);

        // Per-feature μ/σ over the very matrix this artifact was trained on. The FastTree base does
        // not need them (trees are scale-invariant); the LINEAR online residual head does, because
        // the raw vector's ‖x‖² is almost entirely runtimeMinutes and every other slot would learn at
        // a rate set by a number unrelated to it. Persisted inside the existing FeatureSchemaJson
        // document rather than as a column, so this carries no migration and no schema-version bump —
        // the computed FeatureSchemaVersion column reads 'version', which is untouched.
        var standardizer = FeatureStandardizer.FromRows(matrix.Features);

        var latestRun = await db.TrainingRuns
            .Where(r => r.AccountId == accountId)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        // IgnoreQueryFilters: artifacts trained against a superseded FeatureSchema version are hidden
        // from every read path on purpose (see ReelDbContext), but this handler is the one place that
        // must still SEE them — to keep the per-account version counter monotonic and to demote them
        // out of Active so the partial unique index has room for the new one.
        var artifacts = db.ModelArtifacts.IgnoreQueryFilters().Where(a => a.AccountId == accountId);
        var version = await artifacts.MaxAsync(a => (int?)a.Version, ct) ?? 0;
        foreach (var stale in await artifacts.Where(a => a.Status == ArtifactStatus.Active).ToListAsync(ct))
        {
            stale.Status = ArtifactStatus.Superseded;
        }

        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = version + 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = JsonSerializer.Serialize(new
            {
                version = FeatureSchema.Version,
                names = FeatureSchema.AllNames(),
                stats = standardizer.ToPayload(),
            }),
            TrainingRunId = latestRun?.Id ?? Guid.Empty,
            Status = ArtifactStatus.Active,
            TrainedAt = now,
        });

        // Validate this same config on the time split.
        var hasEval = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == JobKind.Evaluate
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (!hasEval)
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.Evaluate,
                Priority = 1,
                EnqueuedAt = DateTime.UtcNow,
                CursorJson = JsonSerializer.Serialize(new { hyperparams = JsonSerializer.Deserialize<JsonElement>(hyperparams) }),
            });
        }

        // The Active-artifact and in-flight-job indexes are check-then-insert races: another instance
        // (or the enqueue in EnrichCatalogJobHandler / NightlyReconcileService) can win between the
        // check above and this write. Postgres rejects the loser with 23505; that is the index doing
        // its job, not a training failure, so it is absorbed rather than propagated into a retry storm.
        if (!await SaveToleratingUniqueRaceAsync(ct))
        {
            logger.LogWarning(
                "Train for {AccountId} lost a concurrent race on a unique constraint; a competing retrain already landed.",
                accountId);
            return;
        }

        logger.LogInformation("Serving model v{Version} trained for {AccountId} on {Rows} labels.", version + 1, accountId, rows.Count);
    }

    /// <summary>
    /// Parks an account that cannot be trained yet. No artifact, no chained Evaluate, and — critically
    /// — no exception: a throw here would burn the job's three retries and the next sync would enqueue
    /// another certain failure. <see cref="PipelineStage.Degraded"/> is the only terminal stage the
    /// existing UI renders as a stopped state rather than a spinner; the job's progress message carries
    /// the actual reason, which /api/sync/status already surfaces.
    /// </summary>
    private async Task ParkForInsufficientRatingsAsync(SyncJob job, Account account, int rowCount, CancellationToken ct)
    {
        var message = $"Not enough ratings to train yet — {rowCount} of {MinTrainingRows} needed. Rate a few titles on Trakt and Reel will build your model.";
        job.ProgressMessage = message;
        job.ProgressPct = 100;

        if (account.PipelineStage != PipelineStage.Degraded)
        {
            account.PipelineStage = PipelineStage.Degraded;
            account.PipelineStageChangedAt = DateTime.UtcNow;
            events.Publish(account.Id, PipelineEventTypes.StageChanged, new Dictionary<string, object?>
            {
                ["stage"] = PipelineStage.Degraded.ToString(),
                ["reason"] = "insufficient-ratings",
                ["message"] = message,
            });
        }

        events.Publish(account.Id, PipelineEventTypes.Insight, new Dictionary<string, object?>
        {
            ["id"] = $"train:insufficient:{account.Id:N}",
            ["kind"] = "stat",
            ["text"] = message,
        });

        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Train skipped for {AccountId}: {Rows} usable rating(s), below the {Min}-row floor. Account parked as Degraded (insufficient ratings).",
            account.Id, rowCount, MinTrainingRows);
    }

    /// <summary>True when the account's Trakt link is healthy — a Degraded stage caused by a revoked
    /// token must not be cleared just because a queued Train job happened to succeed.</summary>
    private Task<bool> ConnectionIsHealthyAsync(Guid accountId, CancellationToken ct) =>
        db.TraktConnections.AnyAsync(c => c.AccountId == accountId && c.Status == ConnectionStatus.Active, ct);

    /// <summary>Saves, returning false instead of throwing when the loser of a unique-index race is us.</summary>
    private async Task<bool> SaveToleratingUniqueRaceAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    /// <summary>Postgres 23505 = unique_violation, surfaced through EF as a DbUpdateException.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
