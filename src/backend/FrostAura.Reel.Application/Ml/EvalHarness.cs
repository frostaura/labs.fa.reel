using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The M2 gate: leakage-clean time-split evaluation of the recommender against a popularity
/// baseline. Train on the older part of the user's ratings, hold out the newest, build every
/// feature as-of the split, and ask one falsifiable question — does the model rank the titles
/// this user went on to LOVE above what raw popularity would have shown him?
///
/// <para><b>What changed in the 2026-09 instrument repair, and why.</b> The ledger it produced
/// for twelve iterations was measured with an instrument that could not express the effect it
/// gated on, over a pool the product cannot serve, on a split axis that a re-rate could move:</para>
/// <list type="bullet">
/// <item><b>precision@10 is quantised to 0.1</b> on a top-10 cut, so the achievable lift lattice
/// depends on the baseline (+33% at a baseline of 0.3, +20% at 0.5). The gate's effective
/// threshold silently drifted across the ledger. It is kept and persisted for continuity — as a
/// DISPLAY metric with that caveat attached — and is no longer what the gate reads.</item>
/// <item><b>NDCG@10 with a bootstrap CI on the PAIRED difference</b> is the gate metric instead.
/// Model and baseline rank the identical pool, so the comparison is paired and the per-iteration
/// difference is the statistic with power, not either level on its own.</item>
/// <item><b>The evaluated pool is now the servable pool.</b> The harness used to read
/// <c>db.Titles</c> with no content filtering at all, scoring the model on pre-2000, non-English
/// and excluded-genre titles the feed can never surface. Both the holdout and the baseline are
/// now restricted to <see cref="EligibilityQueryBuilder.ContentFilteredTitles"/>, and both the
/// filtered and unfiltered sizes are persisted so the ledger shows what was dropped.</item>
/// <item><b>The split axis is <see cref="UserRating.FirstRatedAt"/></b>, which is set on insert
/// and never moved by a re-rate. <c>RatedAt</c> is overwritten by every Trakt ratings walk, so a
/// re-rated title used to be able to cross the split; <c>ObservedAt</c> (the rejected
/// alternative) would collapse the holdout to zero, because ~2,000 historical ratings share one
/// ingest timestamp.</item>
/// <item><b>Five rolling split origins</b> (60/65/70/75/80%) instead of one, reported per-origin
/// and as a mean, because a single origin is one draw.</item>
/// <item><b>Calibration is measured</b> — bias, MAE and ECE over ten predicted-rating buckets —
/// because the predicted rating is the product's stated falsifiable promise and nothing checked
/// whether it was kept.</item>
/// <item><b><see cref="ConfigHash"/> no longer includes coverage stats.</b> Coverage grew every
/// time the user rated anything, so every nightly drift retrain minted a new hash and burned an
/// iteration. "Iteration 12 of 3" meant "the catalogue grew twelve times".</item>
/// </list>
///
/// <para><b>Scoring path.</b> Both rankers are scored through exactly the code path the feed uses
/// for its BASE score — <see cref="IModelEngine.Score"/> over a vector from
/// <see cref="FeatureVectorBuilder"/>. The online residual head, the monotone rating calibrator
/// and the dislike demotion are all deliberately absent here: they are serving-time corrections
/// applied after the base score, and folding them in would make the gate measure the display
/// pipeline rather than the model. The calibrator is measured separately and explicitly, by the
/// calibration metrics below, which compare RAW base scores against actual ratings.</para>
/// </summary>
public class EvalHarness(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    EligibilityQueryBuilder eligibility,
    IPipelineEventHub events,
    ILogger<EvalHarness> logger)
{
    /// <summary>
    /// The caveat persisted with every evaluation. It used to claim the present-day-popularity
    /// problem hit "model features and baseline symmetrically", which is false by construction:
    /// popularity is 100% of the baseline's ranking signal and one slot of the model's, so
    /// post-split popularity flatters the BASELINE and the measured lift is conservative. Saying it
    /// was symmetric made the harness sound more self-critical than it was while actually
    /// understating how hostile the comparison is to the model.
    /// <para>The feature count is interpolated from <see cref="FeatureSchema.VectorLength"/> rather
    /// than typed: it shipped to the user as "~86" long after the vector had grown to 92, and a
    /// hand-maintained number in a user-visible string is a number that will be wrong again.</para>
    /// </summary>
    public static readonly string PopularityCaveat =
        "popularity baseline uses present-day TMDB popularity (no historical snapshots exist). "
        + "This is NOT symmetric: present-day popularity is 100% of the baseline's ranking signal, "
        + $"while for the model it is one of {FeatureSchema.VectorLength} features. Post-split popularity therefore flatters "
        + "the baseline, so the measured lift understates the model rather than overstating it.";

    /// <summary>
    /// precision@10 is quantised to 0.1 by construction (ten slots, ten hits at most), so the
    /// smallest lift it can express depends on the baseline it is measured against. Persisted with
    /// every run so nobody reads the p@10 column as a continuous quantity again.
    /// </summary>
    public const string PrecisionQuantisationCaveat =
        "precision@10 is quantised to 0.1 (a top-10 cut), so the achievable relative-lift lattice "
        + "depends on the baseline: +33% is the smallest passing step at a baseline of 0.3, +20% at 0.5. "
        + "It is retained for ledger continuity and DISPLAY ONLY — the gate reads NDCG@10 with a "
        + "bootstrap CI on the paired model-minus-baseline difference.";

    /// <summary>Mean relative NDCG@10 lift across the rolling origins the model must clear.</summary>
    public const decimal NdcgLiftGateThreshold = 0.20m;

    /// <summary>The retired precision gate, kept only to keep <c>PassedLegacyGate</c> comparable.</summary>
    public const decimal LegacyGateThreshold = 0.20m;

    public const int PrecisionDepth = 10;
    public const int NdcgDepth = 10;
    public const int MinHoldoutPositives = 10;
    public const int MinRatingsToEvaluate = 50;
    public const int BootstrapResamples = 1_000;
    public const int CalibrationBuckets = 10;

    /// <summary>Fixed so two runs over the same holdout produce the same interval. A CI that moves
    /// when nothing else did is not evidence.</summary>
    public const int BootstrapSeed = 20260909;

    /// <summary>Rolling split origins as a fraction of the ordered rating history.</summary>
    public static readonly double[] SplitOrigins = [0.60d, 0.65d, 0.70d, 0.75d, 0.80d];

    /// <summary>The origin whose numbers land in the ledger columns — the historical 80/20 split,
    /// so iteration 13's train/holdout counts stay comparable with iteration 12's.</summary>
    public const double PrimaryOrigin = 0.80d;

    public record HarnessOutcome(TrainingRun Run, EvaluationResult Result, TrainedModel SplitModel);

    /// <summary>One holdout title reduced to what the metrics need: its label, the model's score,
    /// and a baseline score that encodes the popularity ordering (higher is better).</summary>
    public readonly record struct HoldoutRow(short Rating, double ModelScore, double BaselineScore);

    /// <summary>One rating reduced to the split's inputs. <c>Axis</c> is <c>FirstRatedAt</c>.</summary>
    public readonly record struct SplitLabel(Guid TitleId, short Rating, DateTime Axis);

    /// <summary>The result of one time split: where it cut, and what fell on each side.</summary>
    public readonly record struct TimeSplit(
        DateTime SplitAt,
        IReadOnlyList<SplitLabel> Train,
        IReadOnlyList<(Guid TitleId, short Rating)> Holdout);

    /// <summary>
    /// The time split and the holdout's triple filter, as one pure function so the harness's LOGIC
    /// is testable and not merely its plumbing. The holdout is every rating strictly newer than the
    /// split point, on a title that is (1) not already in the train half, and (2) carried no watch
    /// footprint at the split point; multiple ratings of one title collapse to its highest.
    /// <param name="orderedLabels">Ordered ASCENDING by <see cref="SplitLabel.Axis"/>.</param>
    /// <param name="fraction">Split origin as a fraction of the history, e.g. 0.80.</param>
    /// <param name="watchedBeforeSplit">Titles with LastWatchedAt ≤ the split point.</param>
    /// </summary>
    public static TimeSplit Split(
        IReadOnlyList<SplitLabel> orderedLabels, double fraction, IReadOnlySet<Guid> watchedBeforeSplit)
    {
        if (orderedLabels.Count == 0)
        {
            return new TimeSplit(default, [], []);
        }

        var splitAt = orderedLabels[SplitIndex(orderedLabels.Count, fraction)].Axis;
        var train = orderedLabels.Where(l => l.Axis <= splitAt).ToList();
        var trainTitleIds = train.Select(t => t.TitleId).ToHashSet();
        var holdout = orderedLabels
            .Where(l => l.Axis > splitAt && !trainTitleIds.Contains(l.TitleId) && !watchedBeforeSplit.Contains(l.TitleId))
            .GroupBy(l => l.TitleId)
            .Select(g => (TitleId: g.Key, Rating: g.Max(x => x.Rating)))
            .ToList();

        return new TimeSplit(splitAt, train, holdout);
    }

    /// <summary>
    /// The gate, as arithmetic. Both clauses must hold: the bootstrap CI on the paired NDCG lift
    /// excludes zero in the model's favour (is this difference real at all), and the mean relative
    /// NDCG lift across the rolling origins clears 20% (is it big enough to be the M2 margin).
    /// A single origin passing on noise cannot carry it.
    /// </summary>
    public static bool GatePassed(decimal ndcgLiftCiLow, decimal meanRelativeNdcgLift) =>
        ndcgLiftCiLow > 0m && meanRelativeNdcgLift >= NdcgLiftGateThreshold;

    /// <summary>The retired precision@10 gate, kept so the twelve historical rows stay comparable.</summary>
    public static bool LegacyGatePassed(decimal precisionRelativeLift) =>
        precisionRelativeLift >= LegacyGateThreshold;

    public async Task<HarnessOutcome?> RunAsync(Guid accountId, string hyperparamsJson, CancellationToken ct)
    {
        var labels = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .OrderBy(r => r.FirstRatedAt)
            .Select(r => new SplitLabel(r.TitleId, r.Rating, r.FirstRatedAt))
            .ToListAsync(ct);

        if (labels.Count < MinRatingsToEvaluate)
        {
            logger.LogWarning("Account {AccountId} has {Count} ratings — below the {Min} evaluation floor.",
                accountId, labels.Count, MinRatingsToEvaluate);
            return null;
        }

        PublishProgress(accountId, "Assembling features", 0, labels.Count);

        var eligibleTitleIds = await EligibleEvaluationPoolAsync(accountId, ct);
        var frozenThreshold = await FrozenThresholdAsync(accountId, labels, ct);

        var origins = new List<OriginOutcome>();
        foreach (var fraction in SplitOrigins)
        {
            var origin = await EvaluateOriginAsync(
                accountId, labels, eligibleTitleIds, frozenThreshold, fraction, hyperparamsJson, ct);
            if (origin is not null)
            {
                origins.Add(origin);
            }
        }

        var primary = origins.FirstOrDefault(o => Math.Abs(o.Fraction - PrimaryOrigin) < 1e-9);
        if (primary is null)
        {
            logger.LogWarning(
                "Account {AccountId}: the primary {Origin:P0} split produced no evaluable holdout — evaluation skipped.",
                accountId, PrimaryOrigin);
            return null;
        }

        // ── The gate statistic: a bootstrap CI on the PAIRED NDCG difference ─────────────
        var (ciLow, ciHigh) = BootstrapPairedNdcgCi(
            primary.Rows, primary.Threshold, NdcgDepth, BootstrapResamples, BootstrapSeed);
        var meanRelativeNdcgLift = origins.Count == 0
            ? 0m
            : origins.Select(o => RelativeLift(o.ModelNdcg, o.BaselineNdcg)).Average();

        var precisionLift = RelativeLift(primary.ModelPrecision, primary.BaselinePrecision);

        // ── Secondary metrics on the primary origin ─────────────────────────────────────
        var errors = primary.Ranked.Select(r => r.Predicted - (double)r.Rating).ToList();
        var rmse = errors.Count == 0 ? 0d : Math.Sqrt(errors.Average(e => e * e));
        var mae = errors.Count == 0 ? 0d : errors.Average(Math.Abs);
        var calibrationBias = errors.Count == 0 ? 0d : errors.Average();
        var spearman = SpearmanRho(
            primary.Ranked.Select(r => r.Predicted).ToArray(),
            primary.Ranked.Select(r => (double)r.Rating).ToArray());
        var spearmanSe = SpearmanStandardError(primary.Ranked.Count);
        var ece = ExpectedCalibrationError(
            primary.Ranked.Select(r => (r.Predicted, r.Rating)).ToList(), CalibrationBuckets);

        // ── Ledger persistence ──────────────────────────────────────────────────────────
        var coverage = await CoverageStatsAsync(
            primary.Ranked.Select(r => r.TitleId).Concat(primary.TrainTitleIds).ToList(), ct);
        var configHash = ConfigHash(hyperparamsJson);
        var iteration = await NextIterationAsync(accountId, configHash, ct);

        var run = new TrainingRun
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Iteration = iteration,
            ConfigHash = configHash,
            HyperparamsJson = JsonSerializer.Serialize(new
            {
                hyperparams = JsonSerializer.Deserialize<JsonElement>(hyperparamsJson),
                // Coverage is recorded but NO LONGER hashed — it grew with every new rating and
                // minted a fresh iteration on every nightly drift retrain.
                coverage,
                frozenThreshold,
                lowSampleFallback = primary.LowSample,
            }),
            SplitAt = primary.SplitAt,
            TrainRowCount = primary.TrainRowCount,
            HoldoutRowCount = primary.EligibleHoldoutCount,
            PositiveThreshold = primary.Threshold,
            Status = TrainingRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };

        var detail = new
        {
            // Explicitly camelCased: the tuple's own field names serialised as "TitleId"/"Rating",
            // which is not what the frontend's ModelEvalDetail type has always declared.
            topRanked = primary.Ranked
                .Take(PrecisionDepth)
                .Select(r => new
                {
                    titleId = r.TitleId,
                    rating = r.Rating,
                    predicted = Math.Round(r.Predicted, 2),
                    hit = r.Rating >= primary.Threshold,
                }),
            featureImportance = primary.Model.FeatureImportance
                .OrderByDescending(kv => Math.Abs(kv.Value)).Take(15).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            ndcg = new
            {
                model = primary.ModelNdcg,
                baseline = primary.BaselineNdcg,
                pairedLift = primary.ModelNdcg - primary.BaselineNdcg,
                ciLow,
                ciHigh,
                resamples = BootstrapResamples,
                seed = BootstrapSeed,
                relevance = "graded: max(0, rating − threshold + 1), gain 2^rel − 1 — the threshold itself is the first rung, so a title rated exactly at the 'loved' line still earns gain",
            },
            calibration = new
            {
                bias = Math.Round(calibrationBias, 4),
                mae = Math.Round(mae, 4),
                ece,
                buckets = CalibrationBuckets,
                measuredOn = "raw base scores vs actual ratings — no residual head, no calibrator, no demotion",
            },
            spearman = new { rho = Math.Round(spearman, 4), se = spearmanSe },
            eligibility = new
            {
                unfilteredHoldout = primary.UnfilteredHoldoutCount,
                eligibleHoldout = primary.EligibleHoldoutCount,
                dropped = primary.UnfilteredHoldoutCount - primary.EligibleHoldoutCount,
                rule = "EligibilityQueryBuilder.ContentFilteredTitles + PassesMaturity — content rules only; the interaction-footprint exclusion is deliberately NOT applied, since every holdout title is by definition rated",
            },
            origins = OriginPayloads(origins),
            gates = new
            {
                ndcgCiExcludesZero = ciLow > 0m,
                meanRelativeNdcgLift,
                ndcgLiftGateThreshold = NdcgLiftGateThreshold,
                legacyPrecisionLift = precisionLift,
                legacyGateThreshold = LegacyGateThreshold,
            },
            threshold = new { frozen = frozenThreshold, effective = primary.Threshold, lowSampleFallback = primary.LowSample },
            caveats = new[] { PopularityCaveat, PrecisionQuantisationCaveat },
        };

        // The gate: the CI on the paired NDCG lift must exclude zero in the model's favour AND the
        // mean relative lift across the five rolling origins must clear 20%. Two clauses, because
        // each answers a different question — the CI says "is this difference real at all", the
        // mean relative lift says "is it big enough to matter", and the M2 milestone was always
        // stated as a MARGIN, not merely a win. A single origin passing on noise cannot carry it.
        var passedGate = GatePassed(ciLow, meanRelativeNdcgLift);

        var result = new EvaluationResult
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TrainingRunId = run.Id,
            ModelPrecisionAt10 = primary.ModelPrecision,
            BaselinePrecisionAt10 = primary.BaselinePrecision,
            RelativeImprovement = Math.Round(precisionLift, 4),
            Rmse = (decimal)Math.Round(rmse, 4),
            Mae = (decimal)Math.Round(mae, 4),
            SpearmanRho = (decimal)Math.Round(spearman, 4),
            HoldoutPositiveCount = primary.Positives,
            LowSample = primary.LowSample,
            PassedGate = passedGate,
            PassedLegacyGate = LegacyGatePassed(precisionLift),
            NdcgAt10Model = primary.ModelNdcg,
            NdcgAt10Baseline = primary.BaselineNdcg,
            NdcgLiftCiLow = ciLow,
            NdcgLiftCiHigh = ciHigh,
            SpearmanSe = spearmanSe is null ? null : (decimal)Math.Round(spearmanSe.Value, 4),
            CalibrationBias = (decimal)Math.Round(calibrationBias, 4),
            Ece = ece,
            EligibleHoldoutCount = primary.EligibleHoldoutCount,
            UnfilteredHoldoutCount = primary.UnfilteredHoldoutCount,
            OriginsJson = JsonSerializer.Serialize(OriginPayloads(origins)),
            DetailJson = JsonSerializer.Serialize(detail),
            ComputedAt = DateTime.UtcNow,
        };

        db.TrainingRuns.Add(run);
        db.EvaluationResults.Add(result);
        await db.SaveChangesAsync(ct);

        events.Publish(accountId, PipelineEventTypes.Insight, new Dictionary<string, object?>
        {
            ["id"] = $"eval:{run.Id:N}",
            ["kind"] = "stat",
            ["text"] = $"NDCG@10 {primary.ModelNdcg:F3} vs popularity {primary.BaselineNdcg:F3} "
                + $"(95% CI on the lift {ciLow:F3}…{ciHigh:F3}) — {(passedGate ? "gate PASSED" : "gate not met")} (iteration {iteration})",
        });

        logger.LogInformation(
            "Eval account={AccountId} iter={Iteration}: ndcg={Ndcg:F3} baseline={Baseline:F3} ci=[{Low:F3},{High:F3}] "
            + "meanRelLift={MeanLift:P1} gate={Gate} legacyGate={LegacyGate} p@10={Model:P1}/{BaselineP:P1} "
            + "holdout={Eligible}/{Unfiltered} eligible (threshold {Threshold}, lowSample {LowSample})",
            accountId, iteration, primary.ModelNdcg, primary.BaselineNdcg, ciLow, ciHigh,
            meanRelativeNdcgLift, passedGate, result.PassedLegacyGate, primary.ModelPrecision, primary.BaselinePrecision,
            primary.EligibleHoldoutCount, primary.UnfilteredHoldoutCount, primary.Threshold, primary.LowSample);

        return new HarnessOutcome(run, result, primary.Model);
    }

    // ── Per-origin evaluation ───────────────────────────────────────────────────────────

    private sealed record OriginOutcome(
        double Fraction,
        DateTime SplitAt,
        int TrainRowCount,
        IReadOnlyCollection<Guid> TrainTitleIds,
        int UnfilteredHoldoutCount,
        int EligibleHoldoutCount,
        decimal Threshold,
        bool LowSample,
        int Positives,
        decimal ModelPrecision,
        decimal BaselinePrecision,
        decimal ModelNdcg,
        decimal BaselineNdcg,
        IReadOnlyList<HoldoutRow> Rows,
        IReadOnlyList<(Guid TitleId, short Rating, double Predicted)> Ranked,
        TrainedModel Model);

    private async Task<OriginOutcome?> EvaluateOriginAsync(
        Guid accountId,
        IReadOnlyList<SplitLabel> labels,
        IReadOnlySet<Guid> eligibleTitleIds,
        decimal frozenThreshold,
        double fraction,
        string hyperparamsJson,
        CancellationToken ct)
    {
        // The split point has to be known before the watch-footprint query can be bounded by it, so
        // the cut is taken once against no footprint and then re-taken with the real one. Both calls
        // go through the same pure Split, so there is no second copy of the filter to drift.
        var splitAt = labels[SplitIndex(labels.Count, fraction)].Axis;
        var watchedBeforeSplit = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && w.LastWatchedAt != null && w.LastWatchedAt <= splitAt)
                .Select(w => w.TitleId)
                .ToListAsync(ct))
            .ToHashSet();

        // Holdout = newest ratings on titles genuinely unwatched at the split, then restricted to
        // titles the feed could actually have served. Both counts are kept: the unfiltered one is
        // what previous iterations reported, the eligible one is what is actually measured.
        var split = Split(labels, fraction, watchedBeforeSplit);
        var train = split.Train;
        var holdoutAll = split.Holdout;
        var holdout = holdoutAll.Where(h => eligibleTitleIds.Contains(h.TitleId)).ToList();

        if (holdout.Count < NdcgDepth)
        {
            logger.LogWarning(
                "Account {AccountId} at origin {Fraction:P0}: {Eligible} eligible holdout titles of {Unfiltered} clean "
                + "— below the depth-{Depth} floor, origin skipped.",
                accountId, fraction, holdout.Count, holdoutAll.Count, NdcgDepth);
            return null;
        }

        var taste = await featureBuilder.BuildTasteStateAsync(accountId, splitAt, ct);
        var trainLabelByTitle = train
            .GroupBy(t => t.TitleId)
            .ToDictionary(g => g.Key, g => (float)g.Max(x => x.Rating));
        var trainVectors = await featureBuilder.BuildAsync(taste, trainLabelByTitle.Keys.ToList(), splitAt, ct);
        var holdoutVectors = await featureBuilder.BuildAsync(taste, holdout.Select(h => h.TitleId).ToList(), splitAt, ct);

        var matrixRows = trainVectors.Where(kv => trainLabelByTitle.ContainsKey(kv.Key)).ToList();
        if (matrixRows.Count == 0)
        {
            logger.LogWarning("Account {AccountId} at origin {Fraction:P0}: no vectorisable training rows.", accountId, fraction);
            return null;
        }

        var matrix = new TrainingMatrix(
            matrixRows.Select(kv => kv.Value).ToArray(),
            matrixRows.Select(kv => trainLabelByTitle[kv.Key]).ToArray(),
            FeatureSchema.AllNames());

        PublishProgress(accountId, $"Fitting the split model ({fraction:P0})", matrixRows.Count, labels.Count);
        var model = engine.Train(matrix, hyperparamsJson);

        // ── Threshold: frozen per account, "loved" not "tolerated" ──────────────────────
        var threshold = frozenThreshold;
        var positives = holdout.Count(h => h.Rating >= threshold);
        var lowSample = positives < MinHoldoutPositives;
        if (lowSample)
        {
            threshold = Median(matrix.Labels);
            positives = holdout.Count(h => h.Rating >= threshold);
            // Loud on purpose: this branch changes what "loved" means for one row of a ledger whose
            // whole value is that its rows are comparable. It had never fired in twelve iterations,
            // and if it fires now the ledger reader must know before comparing anything.
            logger.LogError(
                "Account {AccountId} at origin {Fraction:P0}: only {Positives} holdout positives at the frozen "
                + "threshold {Frozen} — falling back to the train median {Fallback}. THIS RUN IS NOT COMPARABLE "
                + "with rows evaluated at the frozen threshold.",
                accountId, fraction, holdout.Count(h => h.Rating >= frozenThreshold), frozenThreshold, threshold);
        }

        // ── Model ranking — the feed's own base-score path, nothing else ────────────────
        var scoreable = holdout.Where(h => holdoutVectors.ContainsKey(h.TitleId)).ToList();
        if (scoreable.Count < NdcgDepth)
        {
            logger.LogWarning(
                "Account {AccountId} at origin {Fraction:P0}: {Count} scoreable holdout titles — origin skipped.",
                accountId, fraction, scoreable.Count);
            return null;
        }

        var predictions = engine.Score(model.ArtifactBytes, scoreable.Select(h => holdoutVectors[h.TitleId]).ToArray());
        var ranked = scoreable
            .Zip(predictions, (h, p) => (h.TitleId, h.Rating, Predicted: (double)p))
            .OrderByDescending(x => x.Predicted)
            .ToList();

        // ── Popularity baseline on the identical (eligible) pool ────────────────────────
        // Present-day TMDB popularity is the only ranking signal here (votes break ties), because no
        // historical snapshot exists. That is a leak in the BASELINE's favour, not a shared one: a
        // title that became popular after splitAt looks popular to this ranker today, and popularity
        // is 100% of what it knows. The model sees the same field as one slot of its vector, so the
        // measured lift is conservative rather than flattering. Do not "correct" for this by
        // weakening the baseline — the asymmetry runs against the model.
        var scoreableIds = scoreable.Select(h => h.TitleId).ToList();
        var popularity = await db.Titles
            .Where(t => scoreableIds.Contains(t.Id))
            .Select(t => new { t.Id, t.TmdbPopularity, t.TraktVotes })
            .ToDictionaryAsync(t => t.Id, ct);
        var baselineRanked = scoreable
            .OrderByDescending(h => popularity.GetValueOrDefault(h.TitleId)?.TmdbPopularity ?? 0m)
            .ThenByDescending(h => popularity.GetValueOrDefault(h.TitleId)?.TraktVotes ?? 0)
            .ThenBy(h => h.TitleId)
            .ToList();

        // The baseline's ORDER is the baseline's signal; re-expressing it as a descending score
        // (negated rank) makes the bootstrap resample the same total order the point estimate used,
        // ties and all, without re-deriving the popularity comparison inside the resample loop.
        var baselineScoreByTitle = baselineRanked
            .Select((h, index) => (h.TitleId, Score: -(double)index))
            .ToDictionary(x => x.TitleId, x => x.Score);
        var predictedByTitle = ranked.ToDictionary(r => r.TitleId, r => r.Predicted);
        var rows = scoreable
            .Select(h => new HoldoutRow(h.Rating, predictedByTitle[h.TitleId], baselineScoreByTitle[h.TitleId]))
            .ToList();

        return new OriginOutcome(
            fraction,
            splitAt,
            matrix.Labels.Length,
            [.. trainLabelByTitle.Keys],
            holdoutAll.Count,
            scoreable.Count,
            threshold,
            lowSample,
            positives,
            PrecisionAtK(ranked.Select(r => r.Rating), threshold, PrecisionDepth),
            PrecisionAtK(baselineRanked.Select(r => r.Rating), threshold, PrecisionDepth),
            NdcgAtK(ranked.Select(r => r.Rating), threshold, NdcgDepth),
            NdcgAtK(baselineRanked.Select(r => r.Rating), threshold, NdcgDepth),
            rows,
            ranked,
            model);
    }

    private static object[] OriginPayloads(IReadOnlyCollection<OriginOutcome> origins) =>
        [.. origins.Select(o => (object)new
        {
            fraction = o.Fraction,
            splitAt = o.SplitAt,
            trainRows = o.TrainRowCount,
            unfilteredHoldout = o.UnfilteredHoldoutCount,
            eligibleHoldout = o.EligibleHoldoutCount,
            threshold = o.Threshold,
            lowSample = o.LowSample,
            ndcgModel = o.ModelNdcg,
            ndcgBaseline = o.BaselineNdcg,
            ndcgRelativeLift = RelativeLift(o.ModelNdcg, o.BaselineNdcg),
            precisionModel = o.ModelPrecision,
            precisionBaseline = o.BaselinePrecision,
        })];

    // ── Pool, threshold and ledger helpers ──────────────────────────────────────────────

    /// <summary>
    /// Titles the feed could actually serve to this account: the account's CONTENT rules only —
    /// language, release-year window, crowd-rating floor, excluded/included genres, keyword
    /// exclusions — plus the in-memory maturity ceiling.
    /// <para>The interaction-footprint half of <see cref="EligibilityQueryBuilder.EligibleTitles"/>
    /// is deliberately NOT applied: every holdout title is by definition rated, so composing the
    /// full eligibility rule would empty the holdout entirely. The distinction matters — the
    /// content rules describe what the user has told the product never to show him, which is a
    /// property of the title; the footprint rules describe what he has already seen, which is
    /// exactly what a holdout is made of.</para>
    /// </summary>
    private async Task<HashSet<Guid>> EligibleEvaluationPoolAsync(Guid accountId, CancellationToken ct)
    {
        var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);
        var rows = await eligibility.ContentFilteredTitles(accountId)
            .Select(t => new { t.Id, t.Certification })
            .ToListAsync(ct);

        return [.. rows
            .Where(r => EligibilityQueryBuilder.PassesMaturity(new Title { Id = r.Id, Certification = r.Certification }, ceiling))
            .Select(r => r.Id)];
    }

    /// <summary>
    /// The account's "loved" line, computed ONCE and then frozen. It is the top-quintile boundary of
    /// the account's own whole-title ratings with 8 as a floor — "loved, not tolerated" — and once a
    /// run has recorded it at that definition, every later run reuses the recorded value.
    /// <para>Freezing it is the point: a threshold recomputed per run from a growing rating history
    /// silently redefines the positive class between ledger rows, and two rows measured against
    /// different definitions of "loved" cannot be compared even though the column looks the same.
    /// Runs that fell back to the median (<c>LowSample</c>) are excluded from the lookup, so a thin
    /// early account cannot freeze the wrong number for ever.</para>
    /// </summary>
    private async Task<decimal> FrozenThresholdAsync(Guid accountId, IReadOnlyList<SplitLabel> labels, CancellationToken ct)
    {
        var recorded = await db.TrainingRuns
            .Where(r => r.AccountId == accountId && r.PositiveThreshold > 0m)
            .Join(
                db.EvaluationResults.Where(e => e.AccountId == accountId && !e.LowSample),
                r => r.Id,
                e => e.TrainingRunId,
                (r, e) => new { r.StartedAt, r.PositiveThreshold })
            .OrderBy(x => x.StartedAt)
            .Select(x => (decimal?)x.PositiveThreshold)
            .FirstOrDefaultAsync(ct);

        return recorded ?? TopQuintileThreshold([.. labels.Select(l => l.Rating)]);
    }

    private void PublishProgress(Guid accountId, string phase, int processed, int total) =>
        events.Publish(accountId, PipelineEventTypes.ModelProgress, new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["processed"] = processed,
            ["total"] = total,
        });

    private async Task<object> CoverageStatsAsync(IReadOnlyCollection<Guid> titleIds, CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new { titles = 0, attrCoverage = 0d, embeddingCoverage = 0d, schemaVersion = FeatureSchema.Version };
        }

        var withAttrs = await db.TitleAttributes.CountAsync(
            a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done, ct);
        var withEmbeddings = await db.TitleEmbeddings.CountAsync(e => ids.Contains(e.TitleId), ct);
        return new
        {
            titles = ids.Count,
            attrCoverage = Math.Round((double)withAttrs / ids.Count, 2),
            embeddingCoverage = Math.Round((double)withEmbeddings / ids.Count, 2),
            schemaVersion = FeatureSchema.Version,
        };
    }

    /// <summary>
    /// The iteration key: feature-schema version and hyperparameters, and nothing else.
    /// <para>It used to include the coverage block, whose <c>titles</c> count is the train-plus-holdout
    /// set — so it grew every time the user rated anything and every nightly drift retrain minted a
    /// fresh hash and incremented the iteration counter the kill criterion reads. Twelve "iterations"
    /// against a three-iteration criterion were substantially "the catalogue grew twelve times".
    /// Coverage is still recorded on the run, for the record; it no longer moves the counter.</para>
    /// </summary>
    public static string ConfigHash(string hyperparamsJson)
    {
        var payload = $"{FeatureSchema.Version}|{hyperparamsJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
    }

    private async Task<int> NextIterationAsync(Guid accountId, string configHash, CancellationToken ct)
    {
        var latest = await db.TrainingRuns
            .Where(r => r.AccountId == accountId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.Iteration, r.ConfigHash })
            .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            return 1;
        }

        return latest.ConfigHash == configHash ? latest.Iteration : latest.Iteration + 1;
    }

    // ── Pure metric maths (public so the harness LOGIC is testable, not just its plumbing) ──

    /// <summary>The 0-based index of the split point in an ordered label list.</summary>
    public static int SplitIndex(int count, double fraction) =>
        count <= 0 ? 0 : Math.Clamp((int)(count * fraction), 0, count - 1);

    /// <summary>
    /// The account's frozen "loved" line: the top-quintile (80th-percentile) boundary of its own
    /// ratings, floored at 8 so a user who rates everything 6 cannot define "loved" downward.
    /// </summary>
    public static decimal TopQuintileThreshold(IReadOnlyCollection<short> ratings)
    {
        if (ratings.Count == 0)
        {
            return 8m;
        }

        var sorted = ratings.OrderBy(r => r).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.8d) - 1, 0, sorted.Length - 1);
        return Math.Max(8m, sorted[index]);
    }

    /// <summary>(model − baseline) / baseline, clamped to ±99 so a zero baseline cannot produce ∞.</summary>
    public static decimal RelativeLift(decimal model, decimal baseline)
    {
        var lift = baseline > 0m
            ? (model - baseline) / baseline
            : model > 0m ? 99m : 0m;
        return Math.Clamp(lift, -99m, 99m);
    }

    public static decimal PrecisionAtK(IEnumerable<short> rankedRatings, decimal threshold, int k)
    {
        var top = rankedRatings.Take(k).ToList();
        return top.Count == 0 ? 0m : (decimal)top.Count(r => r >= threshold) / top.Count;
    }

    /// <summary>
    /// Graded relevance for NDCG: how far the rating sits ABOVE the loved threshold, counting the
    /// threshold itself as the first rung. At threshold 8 that gives 8→1, 9→2, 10→3 and everything
    /// below 8→0.
    /// <para>The alternative readings were rejected deliberately. Plain <c>rating − threshold</c>
    /// floored at 0 scores a title rated exactly 8 as irrelevant, which contradicts the harness's
    /// own definition of "loved". The raw rating as relevance makes a 6 nearly as valuable as a 10
    /// once the exponential gain is applied, which is the opposite of what the gate is asking.</para>
    /// </summary>
    public static double Relevance(short rating, decimal threshold) =>
        Math.Max(0d, rating - (double)threshold + 1d);

    /// <summary>NDCG@k over a ranking, with the ideal DCG taken over the same pool.</summary>
    public static decimal NdcgAtK(IEnumerable<short> rankedRatings, decimal threshold, int k)
    {
        var ratings = rankedRatings.ToList();
        if (ratings.Count == 0)
        {
            return 0m;
        }

        var dcg = Dcg(ratings.Take(k), threshold);
        var idcg = Dcg(ratings.OrderByDescending(r => r).Take(k), threshold);
        return idcg <= 0d ? 0m : Math.Round((decimal)(dcg / idcg), 6);
    }

    private static double Dcg(IEnumerable<short> ratings, decimal threshold) =>
        ratings.Select((r, i) => (Math.Pow(2d, Relevance(r, threshold)) - 1d) / Math.Log2(i + 2d)).Sum();

    /// <summary>
    /// A percentile bootstrap 95% CI on the PAIRED NDCG@k difference (model − baseline), resampling
    /// holdout titles with replacement. Paired because both rankers order the identical pool — the
    /// difference is the statistic with power, and testing either level on its own throws away the
    /// pairing that makes this comparison worth running at all.
    /// </summary>
    public static (decimal Low, decimal High) BootstrapPairedNdcgCi(
        IReadOnlyList<HoldoutRow> rows, decimal threshold, int k, int resamples, int seed)
    {
        if (rows.Count == 0 || resamples <= 0)
        {
            return (0m, 0m);
        }

        var rng = new Random(seed);
        var diffs = new double[resamples];
        var sample = new HoldoutRow[rows.Count];
        for (var r = 0; r < resamples; r++)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                sample[i] = rows[rng.Next(rows.Count)];
            }

            var modelNdcg = NdcgAtK(sample.OrderByDescending(s => s.ModelScore).Select(s => s.Rating), threshold, k);
            var baselineNdcg = NdcgAtK(sample.OrderByDescending(s => s.BaselineScore).Select(s => s.Rating), threshold, k);
            diffs[r] = (double)(modelNdcg - baselineNdcg);
        }

        Array.Sort(diffs);
        return ((decimal)Percentile(diffs, 0.025d), (decimal)Percentile(diffs, 0.975d));
    }

    private static double Percentile(double[] sorted, double p)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>
    /// Expected Calibration Error over <paramref name="buckets"/> equal-width predicted-rating bins
    /// spanning 1–10: Σ (n_b / n) · |mean predicted − mean actual| within each bin. Predictions
    /// outside the label scale are clamped into it rather than dropped — an out-of-range prediction
    /// is a calibration failure, not a missing measurement.
    /// </summary>
    public static decimal ExpectedCalibrationError(IReadOnlyList<(double Predicted, short Actual)> pairs, int buckets)
    {
        if (pairs.Count == 0 || buckets <= 0)
        {
            return 0m;
        }

        const double low = 1d, high = 10d;
        var width = (high - low) / buckets;
        var clamped = pairs.Select(p => (Predicted: Math.Clamp(p.Predicted, low, high), Actual: (double)p.Actual)).ToList();

        var total = 0d;
        for (var b = 0; b < buckets; b++)
        {
            var start = low + (b * width);
            var end = start + width;
            var isLast = b == buckets - 1;
            var inBucket = clamped
                .Where(p => p.Predicted >= start && (isLast ? p.Predicted <= end : p.Predicted < end))
                .ToList();
            if (inBucket.Count == 0)
            {
                continue;
            }

            total += inBucket.Count / (double)clamped.Count
                * Math.Abs(inBucket.Average(p => p.Predicted) - inBucket.Average(p => p.Actual));
        }

        return Math.Round((decimal)total, 6);
    }

    public static decimal Median(IReadOnlyCollection<float> values)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (decimal)((sorted[mid - 1] + sorted[mid]) / 2f)
            : (decimal)sorted[mid];
    }

    /// <summary>
    /// The standard error of Spearman's ρ under the null, 1/√(n−3). Persisted alongside ρ because ρ
    /// on its own reads as precise: at the live holdout size of ~400 the SE is ~0.05, so a ρ of 0.14
    /// is about 2.8 SE from zero — real, and much weaker than the number looks.
    /// </summary>
    public static double? SpearmanStandardError(int n) => n > 3 ? 1d / Math.Sqrt(n - 3) : null;

    public static double SpearmanRho(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length < 2)
        {
            return 0d;
        }

        var ranksA = Ranks(a);
        var ranksB = Ranks(b);
        var meanA = ranksA.Average();
        var meanB = ranksB.Average();
        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            cov += (ranksA[i] - meanA) * (ranksB[i] - meanB);
            varA += Math.Pow(ranksA[i] - meanA, 2);
            varB += Math.Pow(ranksB[i] - meanB, 2);
        }

        return varA <= 0 || varB <= 0 ? 0d : cov / Math.Sqrt(varA * varB);
    }

    private static double[] Ranks(double[] values)
    {
        var indexed = values.Select((v, i) => (v, i)).OrderBy(x => x.v).ToArray();
        var ranks = new double[values.Length];
        var position = 0;
        while (position < indexed.Length)
        {
            var end = position;
            while (end + 1 < indexed.Length && indexed[end + 1].v == indexed[position].v)
            {
                end++;
            }

            var averageRank = (position + end) / 2d + 1;
            for (var i = position; i <= end; i++)
            {
                ranks[indexed[i].i] = averageRank; // ties share the average rank
            }

            position = end + 1;
        }

        return ranks;
    }
}
