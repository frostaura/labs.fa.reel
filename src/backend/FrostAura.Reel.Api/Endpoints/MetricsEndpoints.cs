using System.Text.Json;
using FrostAura.Reel.Application.Experiments;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
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
                    // The gate is NDCG@10: the bootstrap 95% CI on the paired model-minus-baseline
                    // lift must exclude zero, AND the mean relative lift across the five rolling
                    // split origins must clear 20%. `threshold` is that second clause; the CI
                    // bounds are reported so the page can say which clause failed.
                    threshold = EvalHarness.NdcgLiftGateThreshold,
                    passed = latestEval?.PassedGate ?? false,
                    latestLift = latestEval?.RelativeImprovement,
                    ndcgModel = latestEval?.NdcgAt10Model,
                    ndcgBaseline = latestEval?.NdcgAt10Baseline,
                    ndcgLiftCiLow = latestEval?.NdcgLiftCiLow,
                    ndcgLiftCiHigh = latestEval?.NdcgLiftCiHigh,
                    ciExcludesZero = latestEval?.NdcgLiftCiLow > 0m,
                    // The retired precision@10 gate, carried so the ledger stays continuous.
                    legacyThreshold = EvalHarness.LegacyGateThreshold,
                    legacyPassed = latestEval?.PassedLegacyGate ?? false,
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
                            spearmanSe = eval.SpearmanSe,
                            ndcgAt10Model = eval.NdcgAt10Model,
                            ndcgAt10Baseline = eval.NdcgAt10Baseline,
                            ndcgLiftCiLow = eval.NdcgLiftCiLow,
                            ndcgLiftCiHigh = eval.NdcgLiftCiHigh,
                            calibrationBias = eval.CalibrationBias,
                            ece = eval.Ece,
                            eligibleHoldoutCount = eval.EligibleHoldoutCount,
                            unfilteredHoldoutCount = eval.UnfilteredHoldoutCount,
                            eval.HoldoutPositiveCount,
                            eval.LowSample,
                            eval.PassedGate,
                            eval.PassedLegacyGate,
                            origins = eval.OriginsJson is null ? null : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(eval.OriginsJson),
                            detail = JsonSerializer.Deserialize<JsonElement>(eval.DetailJson),
                        },
                    };
                }),
            });
        });

        // Founder-only at the route (RequireFounder), like every founder surface, rather than by an
        // inline tier comparison a later edit could drop (WS-8 CR-B, R1-24).
        group.MapPost("/model/train", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = http.GatedAccount();

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
        }).RequireFounder();

        // The interleaving instrument. Founder-only, like the training trigger: it is the readout of
        // a live experiment on this account's own feed, and the honest cadence attached to it
        // (months, not days) is a founder conversation rather than a product surface.
        // The cross-reference stats, moved here from the track record (WS-5 task 11): what the
        // offline backtest measured per prompt generation, and how titles served under each standing
        // were later rated. Same projection as /api/accuracy served it; same window keys.
        group.MapGet("/cross-reference", async (string? window, AccuracyQuery accuracy, CancellationToken ct) =>
        {
            var requested = string.IsNullOrWhiteSpace(window) ? "90d" : window;
            if (!AccuracyQuery.Windows.ContainsKey(requested))
            {
                return Results.BadRequest(new
                {
                    error = $"window must be one of: {string.Join(", ", AccuracyQuery.Windows.Keys)}",
                });
            }

            var crossReference = await accuracy.CrossReferenceForWindowAsync(requested, DateTime.UtcNow, ct);
            return Results.Ok(new
            {
                window = requested,
                population = new
                {
                    name = AccuracyQuery.PopulationsFor(AccuracySource.Exposures).CrossReference.Name,
                    selectionNote = AccuracyQuery.PopulationsFor(AccuracySource.Exposures).CrossReference.SelectionNote,
                },
                crossReference = AccuracyEndpoints.CrossReferencePayload(crossReference),
            });
        });

        group.MapGet("/interleave", async (
            HttpContext http,
            InterleaveSummaryQuery summaries,
            CancellationToken ct) =>
        {
            var account = http.GatedAccount();

            var summary = await summaries.ComputeAsync(account.Id, DateTime.UtcNow, ct);
            return Results.Ok(new
            {
                computedAt = summary.ComputedAt,
                enabled = summary.Enabled,
                policyA = summary.PolicyA,
                policyB = summary.PolicyB,
                tau = summary.Tau,
                alpha = summary.Alpha,
                slates = summary.Slates,
                decidedSlates = summary.DecidedSlates,
                ties = summary.Ties,
                winsA = summary.WinsA,
                winsB = summary.WinsB,
                // Four decimals, un-softened: an always-valid p of 0.41 is the answer, and rounding
                // it toward a story is exactly the failure the harness exists to prevent.
                alwaysValidP = Round(summary.AlwaysValidP, 4),
                significant = summary.Significant,
                preferenceRateA = Round(summary.PreferenceRateA, 4),
                ciLow = Round(summary.CiLow, 4),
                ciHigh = Round(summary.CiHigh, 4),
                signTestP = Round(summary.SignTestP, 4),
                firstSlateAt = summary.FirstSlateAt,
                lastSlateAt = summary.LastSlateAt,
                decidedSlatesPerDay = Round(summary.DecidedSlatesPerDay, 3),
                slatesToDecisionAt65 = summary.SlatesToDecisionAt65,
                estimatedDaysToDecision = Round(summary.EstimatedDaysToDecision, 1),
                standings = summary.Standings.Select(s => new
                {
                    policyId = s.PolicyId,
                    description = s.Description,
                    team = s.Team,
                    slates = s.Slates,
                    cards = s.Cards,
                    credits = s.Credits,
                    slateWins = s.SlateWins,
                }),
            });
        }).RequireFounder();

        group.MapGet("/pipeline", async (
            IReelDbContext db,
            IAccountContext accountContext,
            ITitleAttributeExtractor extractor,
            CatalogSeedProgressQuery seedProgress,
            CancellationToken ct) =>
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

            // External API usage is per account, and the ledger is not IAccountScoped (catalogue-wide
            // work writes rows with no account), so no query filter scopes it — this does. A
            // non-founder sees its own calls only; the founder, who runs the system and pays every
            // provider, also sees every other account's and the catalogue-wide rows. Filtered on the
            // day alone, a Free account was shown the founder's daily OpenRouter and Trakt counts
            // (HREV1-PLAT-3). The tier is read from the row: billing changes it without a new cookie.
            var seesAllUsage = await db.Accounts
                .Where(a => a.Id == accountId)
                .Select(a => a.Tier)
                .FirstOrDefaultAsync(ct) == AccountTier.Founder;
            var weekAgo = DateTime.UtcNow.Date.AddDays(-7);
            var usage = await db.ExternalApiUsages
                .Where(u => u.Day >= weekAgo && (seesAllUsage || u.AccountId == accountId))
                .OrderBy(u => u.Day)
                .Select(u => new { provider = u.Provider.ToString(), u.Day, u.CallCount })
                .ToListAsync(ct);

            // Build quality, per build. Every number here used to be computed and thrown into a log
            // line; without them "the picks get weaker each round" is an argument rather than a chart.
            var builds = await db.FeedBuildStats
                .Where(s => s.AccountId == accountId)
                .OrderByDescending(s => s.BuiltAt)
                .Take(30)
                .Select(s => new
                {
                    s.BuiltAt,
                    mode = s.Mode.ToString(),
                    s.CandidateCount,
                    s.NewCandidatesUpserted,
                    s.EligiblePoolCount,
                    s.ShortlistCount,
                    s.HeroEligibleCount,
                    s.ShortlistP50,
                    s.ShortlistP90,
                    s.RailsDropped,
                    s.DegradedReason,
                    s.DurationMs,
                })
                .ToListAsync(ct);

            // How far behind LLM enrichment is. Global, not per-account: catalog rows are
            // tenant-shared, so one enrichment serves everyone and the gap is one number for the
            // whole system. This is the drain gauge for the 2026-09-09 finding that EnrichCatalog
            // was enqueued only from the onboarding chain — ~1,360 hydrated titles with no embedding
            // and ~1,400 with no Done attributes. staleExtractorVersion counts rows that are present
            // and Done and still wrong: written by a prompt generation whose values mean something
            // else now (the pre-2026-08-07 free-text Tone/Era rows).
            var enrichment = await EnrichmentScheduler.BacklogAsync(db, extractor.Version, ct);

            // The supply half of the same picture. It belongs in the enrichment block rather than
            // beside it because the three numbers are one pipeline: the seed decides how many rows
            // exist, hydration decides how many are usable, enrichment decides how many are
            // FEATURISED — and a missingAttributes count read without knowing a seed is running
            // looks like a regression instead of a queue.
            var seed = await seedProgress.RunAsync(extractor.Version, DateTime.UtcNow, ct);

            return Results.Ok(new
            {
                jobs,
                outbox,
                apiUsage = usage,
                builds,
                enrichment = new
                {
                    missingEmbedding = enrichment.MissingEmbedding,
                    missingAttributes = enrichment.MissingAttributes,
                    staleExtractorVersion = enrichment.StaleExtractorVersion,
                    lastRunAt = enrichment.LastRunAt,
                    seed = new
                    {
                        status = seed.Status,
                        done = seed.Done,
                        partitionsDone = seed.PartitionsDone,
                        partitionsTotal = seed.PartitionsTotal,
                        titlesAdded = seed.TitlesAdded,
                        pagesFetched = seed.PagesFetched,
                        pagesUsedToday = seed.PagesUsedToday,
                        hydrationBacklog = seed.HydrationBacklog,
                        hydrateTitlesPerDay = seed.HydrationAllowance.Limit,
                        hydrateTitlesUsedToday = seed.HydrationAllowance.Used,
                        extractDailyTokens = seed.ExtractAllowance.Limit,
                        extractTokensUsedToday = seed.ExtractAllowance.Used,
                        embedDailyTokens = seed.EmbedAllowance.Limit,
                        embedTokensUsedToday = seed.EmbedAllowance.Used,
                        estimatedDaysToFullyEnriched = seed.EstimatedDaysToEnriched,
                    },
                },
            });
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
