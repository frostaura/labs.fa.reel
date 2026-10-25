using System.Globalization;
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
/// Evaluates, THEN trains. The leakage-clean split runs first, inside this job, on the same
/// hyperparameters and the same <see cref="FeatureSchema"/> version; the serving model is then fit
/// on 100% of the ratings and the artifact records that eval run's id as its
/// <c>TrainingRunId</c>. Chains straight to BuildFeed.
///
/// <para><b>What "served model = evaluated model" does and does not claim.</b> The two models are
/// not byte-identical and cannot be: the gate is only meaningful on a holdout, and the serving
/// model must see every row. What is now guaranteed — and was not, when Train fit an artifact and
/// then enqueued a separate Evaluate job that fit its own model from its own job payload — is that
/// the artifact the user is served and the measurement printed on the ledger come from one job,
/// one hyperparameter string, one feature-schema version and one <c>ConfigHash</c>, and that the
/// artifact points at the run that measured it. The only difference between them is the split.</para>
///
/// <para>The old <c>Evaluate</c> job kind still exists and still works when explicitly enqueued —
/// a founder re-run, or a row queued before this change — but nothing chains it any more.</para>
/// </summary>
public class TrainJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    EvalHarness harness,
    PersonAffinityWriter personAffinities,
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

        // ── Retrain hygiene: same inputs, same model ──────────────────────────────────────
        // Every enrich run used to chain a retrain and every retrain was promoted — four in eight
        // minutes with zero new ratings, each re-dealing the feed and re-spending the cross-reference
        // pass on noise. A retrain now has to have something to learn from. There is deliberately NO
        // "promote only if it beats the current model" rule: on this holdout that would select on
        // noise (0 of 101 evaluations beat popularity), so the honest rule is about INPUTS.
        var inputHashes = await TrainingInputHash.ComputeAsync(db, accountId, hyperparams, ct);
        if (await SkipReasonAsync(account, inputHashes, now, ct) is { } skip)
        {
            job.ProgressMessage = skip;
            job.ProgressPct = 100;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Train skipped for {AccountId}: {Reason}", accountId, skip);
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

        // ── Evaluate FIRST, on this job's hyperparameters ────────────────────────────────
        // The harness writes its own TrainingRun + EvaluationResult and saves them; whatever it
        // returns describes the very config the serving fit below is about to use. A null outcome
        // means the account has no evaluable split yet (too few ratings, or too thin a clean
        // holdout) — the serving artifact is still built, because a user with 60 ratings deserves
        // a feed, and only the ledger waits.
        //
        // A harness FAILURE must not cost the user a model. When evaluation was a separate job that
        // was structurally true — Train had already persisted its artifact by the time Evaluate ran.
        // Running it inline puts the ledger on the serving path, so the failure is absorbed here,
        // loudly, rather than being allowed to leave the account without an artifact and behind a
        // spinner while the job burns its three retries on the same exception.
        EvalHarness.HarnessOutcome? outcome;
        try
        {
            outcome = await harness.RunAsync(accountId, hyperparams, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Eval harness failed for {AccountId}; training the serving model without a ledger row.", accountId);
            outcome = null;
        }

        if (outcome is null)
        {
            job.ProgressMessage = "evaluation skipped — not enough clean data; feed built from the trained model";
            logger.LogWarning("Eval produced no ledger row for {AccountId}; training the serving model anyway.", accountId);
        }
        else
        {
            job.ProgressMessage =
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"iteration {outcome.Run.Iteration}: NDCG@10 {outcome.Result.NdcgAt10Model:F3} vs {outcome.Result.NdcgAt10Baseline:F3} "
                    + $"— gate {(outcome.Result.PassedGate ? "passed" : "not met")}");
        }

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

        // The artifact points at the run that MEASURED it — this job's own eval, not whichever run
        // happened to be newest. Falling back to the newest only covers the skipped-eval case.
        var trainingRunId = outcome?.Run.Id ?? (await db.TrainingRuns
            .Where(r => r.AccountId == accountId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct)) ?? Guid.Empty;

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
                // What this artifact learned from, for the next retrain's same-inputs check. In the
                // existing document for the same reason the standardizer stats are: no column.
                hashVersion = TrainingInputHash.FormulaVersion,
                labelHash = inputHashes.Labels,
                inputHash = inputHashes.Inputs,
            }),
            TrainingRunId = trainingRunId,
            Status = ArtifactStatus.Active,
            TrainedAt = now,
        });

        // The evaluation already ran, above. Advance the stage and chain the feed build directly —
        // this is the work the separate Evaluate job used to do after Train enqueued it.
        if (account.PipelineStage is PipelineStage.Training or PipelineStage.Extracting)
        {
            account.PipelineStage = PipelineStage.Evaluated;
            account.PipelineStageChangedAt = DateTime.UtcNow;
            events.Publish(accountId, PipelineEventTypes.StageChanged, new Dictionary<string, object?>
            {
                ["stage"] = PipelineStage.Evaluated.ToString(),
            });
        }

        var hasFeedBuild = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (!hasFeedBuild)
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                Priority = 1,
                EnqueuedAt = DateTime.UtcNow,
                // Provenance only — a freshly-trained artifact already forces a Fresh build via
                // FeedBuildPolicy's artifact-id check, whatever reason is recorded here.
                TriggerReason = "model-trained",
            });
        }

        // Person affinities are a SERVING-time cache derived from the same taste state this train
        // was fitted on, so they are refreshed here rather than on their own schedule: the discover
        // arm that reads them and the model that ranks what it returns then describe one snapshot of
        // the user, and there is no window where a new model is being served against people the old
        // one implied. Staged, not saved — it lands in the same write as the artifact below.
        await personAffinities.RefreshAsync(accountId, taste, now, ct);

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
    /// How close two retrains on the same LABELS may land: one a day. Catalogue drift alone (an
    /// enrich pass re-extracting rated titles' attributes, crowd numbers moving) is a real input
    /// change, but every promotion re-deals the feed with a Fresh build and re-spends its model
    /// passes, so drift with no new rating is folded in at most daily — the acceptance's "at most
    /// one retrain a day without new ratings". New labels are never debounced: the user rating
    /// something is the point of retraining.
    /// </summary>
    public static readonly TimeSpan DriftDebounce = TimeSpan.FromHours(24);

    /// <summary>
    /// Why this run must not train, or null when it should. Only a STEADY account (its pipeline
    /// already past evaluation) is ever skipped: an onboarding account parked mid-pipeline would
    /// otherwise wait behind a spinner for a model it already has.
    /// </summary>
    private async Task<string?> SkipReasonAsync(
        Account account, TrainingInputHash.Hashes current, DateTime now, CancellationToken ct)
    {
        if (account.PipelineStage is not (PipelineStage.FeedReady or PipelineStage.Evaluated))
        {
            return null;
        }

        var active = await db.ModelArtifacts
            .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
        if (active is null)
        {
            return null;
        }

        if (TrainingInputHash.ReadFrom(active.FeatureSchemaJson) is not { } trainedOn)
        {
            return await SkipHashlessAsync(account.Id, active, current, ct);
        }

        if (trainedOn.Inputs == current.Inputs)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Nothing new to learn from since model v{active.Version} — no new ratings and no changed inputs, so the current model stays.");
        }

        if (trainedOn.Labels == current.Labels && now - active.TrainedAt < DriftDebounce)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Catalogue details changed but no ratings did; model v{active.Version} is under a day old, so the refit waits.");
        }

        return null;
    }

    /// <summary>
    /// An Active artifact trained before input hashes existed. Treating "no hash" as "inputs
    /// changed" made the first retrain after the deploy that introduced them ALWAYS train — and
    /// promote, and force a Fresh build — on an account with zero new ratings: on the slot that was
    /// purge build → enrich → Train v102 on 0 new labels → a second paid Fresh build replacing the
    /// purge's own snapshot. So the hashless case asks the question the hash would have answered,
    /// from timestamps: has the user said anything since this model was trained (a first rating, a
    /// person rating, a reaction made or revoked)? If not, the run is skipped and the artifact is
    /// stamped with today's hash, so every later check compares hashes. A re-rating of an existing
    /// title moves no immutable timestamp and is missed by this one check only; the stamped hash
    /// catches it from then on.
    /// </summary>
    private async Task<string?> SkipHashlessAsync(
        Guid accountId, ModelArtifact active, TrainingInputHash.Hashes current, CancellationToken ct)
    {
        var since = active.TrainedAt;
        var spoke = await db.UserRatings.AnyAsync(r => r.AccountId == accountId && r.FirstRatedAt > since, ct)
            || await db.UserPersonRatings.AnyAsync(r => r.AccountId == accountId && r.UpdatedAt > since, ct)
            || await db.UserTitleReactions.AnyAsync(
                r => r.AccountId == accountId && (r.CreatedAt > since || (r.RevokedAt != null && r.RevokedAt > since)), ct);
        if (spoke)
        {
            return null;
        }

        active.FeatureSchemaJson = TrainingInputHash.StampInto(active.FeatureSchemaJson, current);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Nothing new to learn from since model v{active.Version} — no ratings or reactions since it was trained, so the current model stays.");
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

    /// <summary>
    /// Postgres 23505 = unique_violation, surfaced through EF as a DbUpdateException. The single
    /// definition now lives on <see cref="UniqueRaceSave"/> so the reaction endpoints' absorber and
    /// this handler cannot drift apart; kept here as a forwarder because callers already name it.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        UniqueRaceSave.IsUniqueViolation(exception);
}
