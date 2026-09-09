using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// An EXPLICIT re-run of the leakage-clean eval harness. Nothing chains this any more:
/// <see cref="TrainJobHandler"/> runs the harness inline, before it fits the serving model, so the
/// artifact and the ledger row come from one job and one config. This handler survives for two
/// cases — a deliberate re-measurement of the current artifact, and rows already queued when the
/// ordering changed. Re-running it mints a fresh ledger row against the same ConfigHash, so the
/// iteration counter does not move.
/// </summary>
public class EvaluateJobHandler(
    IReelDbContext db,
    EvalHarness harness,
    IPipelineEventHub events,
    ILogger<EvaluateJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.Evaluate;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("Evaluate requires an account.");
        var hyperparams = job.CursorJson is { } cursor && JsonSerializer.Deserialize<JsonElement>(cursor).TryGetProperty("hyperparams", out var hp)
            ? hp.GetRawText()
            : TrainJobHandler.DefaultHyperparams;

        // A validated model feeds the feed — but the Active artifact exists (Train persisted it)
        // whether or not the leakage-clean split has enough clean holdout to score a lift. So the feed
        // build chains unconditionally; only the metrics ledger depends on the eval outcome. This is
        // what keeps a freshly-trained sub-50-rating account from opening to a permanently blank feed.
        async Task EnsureFeedBuildAsync()
        {
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
        }

        var outcome = await harness.RunAsync(accountId, hyperparams, ct);
        if (outcome is null)
        {
            job.ProgressMessage = "evaluation skipped — not enough clean data; feed built from the trained model";
            await EnsureFeedBuildAsync();
            await db.SaveChangesAsync(ct);
            return;
        }

        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        if (account.PipelineStage is PipelineStage.Training or PipelineStage.Extracting)
        {
            account.PipelineStage = PipelineStage.Evaluated;
            account.PipelineStageChangedAt = DateTime.UtcNow;
            events.Publish(accountId, PipelineEventTypes.StageChanged, new Dictionary<string, object?>
            {
                ["stage"] = PipelineStage.Evaluated.ToString(),
            });
        }

        job.ProgressMessage =
            string.Create(
                CultureInfo.InvariantCulture,
                $"iteration {outcome.Run.Iteration}: NDCG@10 {outcome.Result.NdcgAt10Model:F3} vs {outcome.Result.NdcgAt10Baseline:F3} "
                + $"— gate {(outcome.Result.PassedGate ? "passed" : "not met")}");

        // Chain: a validated model feeds the feed.
        await EnsureFeedBuildAsync();

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Evaluate completed for {AccountId}: {Message}", accountId, job.ProgressMessage);
    }
}
