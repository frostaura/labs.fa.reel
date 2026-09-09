using System.Text.Json;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// The in-app metrics surface the M2 milestone demands: the iteration ledger, the gate, and
/// pipeline health. EDA and edge metrics are app outputs — no notebooks as path of record.
/// </summary>
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics").RequireAccount();

        group.MapGet("/model", async (
            IReelDbContext db,
            IAccountContext accountContext,
            RetroLoopQuery retroLoop,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;

            var runs = await db.TrainingRuns
                .Where(r => r.AccountId == accountId)
                .OrderByDescending(r => r.StartedAt)
                .Take(25)
                .ToListAsync(ct);
            var evals = await db.EvaluationResults
                .Where(e => e.AccountId == accountId)
                .ToDictionaryAsync(e => e.TrainingRunId, ct);
            var activeArtifact = await db.ModelArtifacts
                .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
                .Select(a => new { a.Version, a.Algo, a.TrainedAt })
                .FirstOrDefaultAsync(ct);

            var latestEval = runs
                .Select(r => evals.GetValueOrDefault(r.Id))
                .FirstOrDefault(e => e is not null);

            // The kill criterion is a founder decision, not a constant: it was hard-coded to 3 while
            // the ledger ran past 12, so the page kept rendering "iteration 12 of 3" without ever
            // saying the criterion had been blown. Read it from config, and state the breach.
            var killCriterionAt = configuration.GetValue("KILL_CRITERION_ITERATIONS", 3);
            var iterationsUsed = runs.Select(r => r.Iteration).DefaultIfEmpty(0).Max();

            var retro = await retroLoop.ComputeAsync(DateTime.UtcNow, ct);

            return Results.Ok(new
            {
                gate = new
                {
                    threshold = 0.20m,
                    passed = latestEval?.PassedGate ?? false,
                    latestLift = latestEval?.RelativeImprovement,
                    iterationsUsed,
                    killCriterionAt,
                    criterionExceeded = iterationsUsed > killCriterionAt,
                },
                efficiency = Efficiency(retro),
                activeArtifact,
                runs = runs.Select(r =>
                {
                    var eval = evals.GetValueOrDefault(r.Id);
                    return new
                    {
                        r.Id,
                        r.Iteration,
                        r.ConfigHash,
                        r.SplitAt,
                        r.TrainRowCount,
                        r.HoldoutRowCount,
                        positiveThreshold = r.PositiveThreshold,
                        status = r.Status.ToString(),
                        r.StartedAt,
                        hyperparams = JsonSerializer.Deserialize<JsonElement>(r.HyperparamsJson),
                        eval = eval is null ? null : new
                        {
                            modelPrecisionAt10 = eval.ModelPrecisionAt10,
                            baselinePrecisionAt10 = eval.BaselinePrecisionAt10,
                            relativeImprovement = eval.RelativeImprovement,
                            rmse = eval.Rmse,
                            mae = eval.Mae,
                            spearmanRho = eval.SpearmanRho,
                            eval.HoldoutPositiveCount,
                            eval.LowSample,
                            eval.PassedGate,
                            detail = JsonSerializer.Deserialize<JsonElement>(eval.DetailJson),
                        },
                    };
                }),
            });
        });

        group.MapPost("/model/train", async (IReelDbContext db, IAccountContext accountContext, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null || account.Tier != AccountTier.Founder)
            {
                return Results.Forbid();
            }

            var inFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == account.Id
                    && (j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
            if (!inFlight)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    Kind = JobKind.Train,
                    Priority = 0,
                    EnqueuedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.Accepted();
        });

        group.MapGet("/pipeline", async (IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var jobs = await db.SyncJobs
                .Where(j => j.AccountId == accountId)
                .OrderByDescending(j => j.EnqueuedAt)
                .Take(20)
                .Select(j => new
                {
                    kind = j.Kind.ToString(),
                    status = j.Status.ToString(),
                    j.ProgressPct,
                    j.ProgressMessage,
                    j.AttemptCount,
                    j.EnqueuedAt,
                    j.CompletedAt,
                    j.Error,
                })
                .ToListAsync(ct);

            var outbox = new
            {
                pending = await db.TraktOutbox.CountAsync(o => o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed, ct),
                deadLetters = await db.TraktOutbox.CountAsync(o => o.Status == OutboxStatus.DeadLetter, ct),
            };

            var weekAgo = DateTime.UtcNow.Date.AddDays(-7);
            var usage = await db.ExternalApiUsages
                .Where(u => u.Day >= weekAgo)
                .OrderBy(u => u.Day)
                .Select(u => new { provider = u.Provider.ToString(), u.Day, u.CallCount })
                .ToListAsync(ct);

            return Results.Ok(new { jobs, outbox, apiUsage = usage });
        });
    }

    /// <summary>
    /// The founder-visible track record. Rounding happens HERE and nowhere else — the query itself
    /// keeps full precision so the numbers can be re-derived, and the page gets the digits a human
    /// can read. Nothing is softened: a negative delta or a near-zero correlation renders as-is.
    /// </summary>
    private static object Efficiency(RetroLoopReport retro) => new
    {
        computedAt = retro.ComputedAt,
        conversions = new
        {
            count = retro.Conversions.Count,
            ratedCount = retro.Conversions.RatedCount,
            scoredCount = retro.Conversions.ScoredCount,
            accuracy = Accuracy(retro.Conversions.Accuracy),
            ratingDistribution = retro.Conversions.RatingDistribution
                .Select(kv => new { rating = kv.Key, count = kv.Value }),
            worstMisses = retro.Conversions.WorstMisses.Select(Miss),
        },
        predictedVsActual = new
        {
            pairs = retro.PredictedVsActualPairs,
            accuracy = Accuracy(retro.PredictedVsActual),
            worstMisses = retro.PredictedVsActualWorstMisses.Select(Miss),
        },
        counterfactual = retro.Counterfactual.Select(w => new
        {
            window = w.Window,
            days = w.Days,
            viaReelCount = w.ViaReelCount,
            viaReelMeanRating = Round(w.ViaReelMeanRating, 2),
            otherCount = w.OtherCount,
            otherMeanRating = Round(w.OtherMeanRating, 2),
            delta = Round(w.Delta, 2),
        }),
    };

    private static object Accuracy(PredictionAccuracy accuracy) => new
    {
        count = accuracy.Count,
        meanPredicted = Round(accuracy.MeanPredicted, 2),
        meanActual = Round(accuracy.MeanActual, 2),
        meanAbsoluteError = Round(accuracy.MeanAbsoluteError, 2),
        bias = Round(accuracy.Bias, 2),
        withinOnePointRate = Round(accuracy.WithinOnePointRate, 4),
        pearsonCorrelation = Round(accuracy.PearsonCorrelation, 4),
    };

    private static object Miss(RetroMiss miss) => new
    {
        titleId = miss.TitleId,
        name = miss.Name,
        year = miss.Year,
        predicted = Round(miss.Predicted, 2),
        actual = miss.Actual,
        gap = Round(miss.Gap, 2),
    };

    private static double? Round(double? value, int digits) =>
        value is null ? null : Math.Round(value.Value, digits, MidpointRounding.AwayFromZero);

    private static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
