namespace FrostAura.Reel.Application.Ml;

/// <summary>Which rule a fitted <see cref="RatingCalibrator"/> ended up using — reported, never chosen by a caller.</summary>
public enum CalibrationKind
{
    /// <summary>No usable evidence at all: predicted → predicted (the strict no-op).</summary>
    Identity,

    /// <summary>Level only — identity shifted by the mean signed error. Slope 1, so differentiation survives intact.</summary>
    BiasShift,

    /// <summary>Pool-adjacent-violators isotonic regression, linearly interpolated between knots.</summary>
    Isotonic,

    /// <summary>Isotonic that flattened too far, averaged 50/50 with the bias shift so the cards still differ.</summary>
    IsotonicBlend,
}

/// <summary>
/// The <c>CALIBRATOR_MODE</c> override. <see cref="Auto"/> is the policy below; the rest exist so the
/// selection rule can be pinned from the environment while it is being evaluated on live data.
/// </summary>
public enum CalibratorMode
{
    /// <summary>The documented selection rule. The default, and the only value that should ship.</summary>
    Auto,

    /// <summary>Always the bias shift (level correction, ranking untouched). The safe manual floor.</summary>
    Shift,

    /// <summary>Force the shape wherever there is enough data to fit one — no correlation gate, no slope guard.</summary>
    Isotonic,

    /// <summary>Calibration off: every prediction is displayed exactly as the model produced it.</summary>
    Off,
}

/// <summary>
/// The per-account MONOTONE CALIBRATOR: predicted rating → the rating this account actually gives.
///
/// It exists because the base FastTree model is systematically miscalibrated in LEVEL — on the
/// founder's own closed loop the 116 pairs where a score predates its rating carry MAE 1.94 and
/// bias +0.37, and the 73 saved → watched → rated titles were predicted 6.81 against an actual
/// 5.78. Until now the only thing pushing back on that was the online residual head's unnormalised
/// bias term, which had wandered to −1.68 — an accidental calibrator, learned from soft pseudo-
/// labels, resettable by any retrain, and impossible to explain to a user. This class replaces it
/// with an explicit map fit on real outcomes.
///
/// <b>THE SELECTION RULE, AND WHY IT IS NOT "FIT THE BEST MAP".</b> A calibrator fit purely for
/// accuracy answers the wrong question. On the founder's live data the predictions and the ratings
/// are essentially uncorrelated in LEVEL (Pearson −0.004), so the most accurate monotone map is very
/// nearly the constant "predict the mean" — PAV duly produced 5 → 6.0, 6 → 6.0, 6.5–9.5 → 6.25,
/// 10 → 6.8. That is statistically honest and a product catastrophe: every card prints the same
/// number, the 7.5 hero floor becomes unreachable, and the model's weak-but-real RANK signal
/// (holdout Spearman 0.14–0.25) is thrown away to buy a tenth of a point of MAE. So the rule below
/// is deliberately conservative — it only reaches for a shape when the data can actually support
/// one, and it refuses to emit a map that cannot tell two titles apart:
///
/// <list type="number">
/// <item><b>Fewer than <see cref="BiasShiftMinPairs"/> pairs → <see cref="CalibrationKind.Identity"/>.</b>
/// Below ten observations even the LEVEL is noise; moving the number the user reads on that evidence
/// is worse than leaving it alone.</item>
/// <item><b>Fewer than <see cref="IsotonicMinPairs"/> pairs → <see cref="CalibrationKind.BiasShift"/>.</b>
/// Enough to say "you read about a point high", nowhere near enough to say where.</item>
/// <item><b>Rank correlation below <see cref="MinRankCorrelation"/> → <see cref="CalibrationKind.BiasShift"/>,
/// at ANY pair count.</b> This is the founder's case. If the predictions do not order the outcomes,
/// a shape fitted on them is fitting noise, and every shape fitted on noise collapses toward the
/// mean. Correct the level; leave the (weak) ordering alone.</item>
/// <item><b>Otherwise → <see cref="CalibrationKind.Isotonic"/>, subject to the MINIMUM-SLOPE GUARD.</b>
/// After PAV, if the map compresses the working range — <see cref="RangeProbeLow"/> to
/// <see cref="RangeProbeHigh"/> raw — to less than <see cref="MinCalibratedRange"/> rating points,
/// it is averaged 50/50 with the bias shift (<see cref="CalibrationKind.IsotonicBlend"/>).</item>
/// </list>
///
/// <b>The guard is a product decision, stated as one: a card must not show a constant.</b> It is not
/// a statistical improvement and it is not claimed as one — blending trades MAE for differentiation
/// on purpose, because a feed of identical numbers is not a feed. <see cref="ExpectedAbsoluteError"/>
/// is carried alongside precisely so the honesty lost in the point estimate can be handed back to the
/// user as a band ("7.4, ±1.9") instead of pretended away.
///
/// Two properties remain load-bearing and are asserted in the tests:
/// <list type="bullet">
/// <item><b>Monotone non-decreasing.</b> Calibration must never reorder the ranking — it changes the
/// number on the card, not which card is on top. Isotonic gives this by construction, the bias shift
/// has slope 1, and a convex blend of two non-decreasing maps is non-decreasing.</item>
/// <item><b>Near-idempotent.</b> Fitting on an already-calibrated distribution must land close to
/// identity, so a second calibration pass (or a retrain that has already absorbed the level) cannot
/// compound the correction. Pairs of the form (p, p) fit to exactly identity in-range.</item>
/// </list>
///
/// Outside the fitted range the map extrapolates with SLOPE 1 — i.e. it carries the boundary knot's
/// offset outward — rather than flattening. Flattening would collapse every prediction above the
/// highest observed pair onto one value, which destroys ordering precisely where the product cares
/// most (the hero slot). Slope-1 extrapolation stays monotone, stays continuous, and degrades to
/// identity when the fit is identity.
///
/// Pure math, no I/O: <see cref="RatingCalibrationService"/> owns loading the pairs, resolving the
/// Active artifact, reading <c>CALIBRATOR_MODE</c> and caching.
/// </summary>
public sealed class RatingCalibrator
{
    /// <summary>Fewest pairs worth fitting a shape on. Below this the shape is noise, so only the level is fitted.</summary>
    public const int IsotonicMinPairs = 30;

    /// <summary>Fewest pairs worth moving the number AT ALL. Below this the level itself is noise.</summary>
    public const int BiasShiftMinPairs = 10;

    /// <summary>
    /// Fewest pairs on one artifact before its mean prediction is treated as that artifact's LEVEL.
    /// A mean of one sample is the sample, so de-meaning a singleton would map it exactly onto the
    /// reference level and manufacture a spurious tie — the fastest possible way to turn a healthy
    /// spread of predictions into the constant map this whole class exists to avoid.
    /// </summary>
    public const int ArtifactLevelMinPairs = 10;

    /// <summary>
    /// Rank correlation below which the predictions are taken to carry NO usable ordering, so no
    /// shape may be fitted from them. Set beneath the model's own holdout Spearman (0.14–0.25) is
    /// tempting; it is set at 0.20 instead so that a fit only earns a shape when this account's own
    /// closed loop is at least as informative as the holdout's mid-point.
    /// </summary>
    public const double MinRankCorrelation = 0.20d;

    /// <summary>Least spread a shipped map may leave across <see cref="RangeProbeLow"/>–<see cref="RangeProbeHigh"/>.</summary>
    public const double MinCalibratedRange = 1.5d;

    /// <summary>Bottom of the range the guard measures — roughly where a feed's tail sits.</summary>
    public const double RangeProbeLow = 6d;

    /// <summary>Top of the range the guard measures — roughly where the hero slot sits.</summary>
    public const double RangeProbeHigh = 9.5d;

    /// <summary>How much of the blend is isotonic. Half: enough shape to keep, enough slope to differentiate.</summary>
    public const double BlendWeight = 0.5d;

    /// <summary>The product's rating scale. A calibrated prediction is always a plausible rating.</summary>
    public const double MinOutput = 1d;

    /// <inheritdoc cref="MinOutput"/>
    public const double MaxOutput = 10d;

    private const double Epsilon = 1e-9;

    private readonly double[] _knotsX = [];
    private readonly double[] _knotsY = [];
    private readonly double _blendWeight;
    private readonly double _intercept;

    private RatingCalibrator(CalibrationKind kind, int pairCount, double[] knotsX, double[] knotsY, double blendWeight, double intercept)
    {
        Kind = kind;
        PairCount = pairCount;
        _knotsX = knotsX;
        _knotsY = knotsY;
        _blendWeight = blendWeight;
        _intercept = intercept;
    }

    /// <summary>The rule this instance ended up with, for the founder-facing diagnostics.</summary>
    public CalibrationKind Kind { get; }

    /// <summary>How many predicted/actual pairs the fit saw, AFTER artifact selection.</summary>
    public int PairCount { get; }

    /// <summary>
    /// Spearman rank correlation between predicted and actual over the fitted pairs — the number the
    /// selection rule turns on. Zero when it is not estimable (no spread on either side).
    /// </summary>
    public double Correlation { get; private set; }

    /// <summary>Mean absolute error over the fitted pairs BEFORE the map is applied.</summary>
    public double MaeBefore { get; private set; }

    /// <summary>Mean absolute error over the fitted pairs AFTER the map is applied.</summary>
    public double MaeAfter { get; private set; }

    /// <summary>
    /// What a displayed prediction from this calibrator is typically off by — <see cref="MaeAfter"/>,
    /// named for what the UI does with it. The band beside a number ("7.4, ±1.9") is the honest half
    /// of the minimum-slope guard: the guard keeps the numbers distinguishable, this says how much to
    /// trust the distinction.
    /// </summary>
    public double ExpectedAbsoluteError => MaeAfter;

    /// <summary>The strict no-op: clamped identity. What a brand-new account gets.</summary>
    public static RatingCalibrator Identity { get; } =
        new(CalibrationKind.Identity, 0, [], [], 0d, 0d);

    /// <summary>
    /// Fits the map. <paramref name="pairs"/> are (what the product predicted, what the user actually
    /// rated it) — order irrelevant, duplicates fine, ties on the predicted value expected.
    ///
    /// <paramref name="activeArtifactId"/> makes the pairs COMPARABLE. A prediction is only meaningful
    /// relative to the model that produced it, and successive artifacts sit at different levels, so
    /// pooling them raw would let a retrain's level change masquerade as curvature. Two strategies,
    /// in order: if the Active artifact alone has <see cref="IsotonicMinPairs"/> pairs, fit on those
    /// and discard the rest; otherwise keep every pair but DE-MEAN BY ARTIFACT — shift each artifact's
    /// pairs by the difference between its own mean prediction and the Active artifact's, so all of
    /// them are expressed on the level the map will actually be applied at. Artifacts with fewer than
    /// <see cref="ArtifactLevelMinPairs"/> pairs, and pairs with no artifact recorded, are left alone
    /// (see <see cref="ArtifactLevelMinPairs"/>).
    ///
    /// <paramref name="mode"/> is the <c>CALIBRATOR_MODE</c> override; leave it
    /// <see cref="CalibratorMode.Auto"/> outside diagnostics.
    /// </summary>
    public static RatingCalibrator Fit(
        IReadOnlyList<CalibrationPair> pairs,
        Guid? activeArtifactId = null,
        CalibratorMode mode = CalibratorMode.Auto)
    {
        if (pairs.Count == 0)
        {
            return Identity;
        }

        var fitted = SelectPairs(pairs, activeArtifactId);
        var correlation = RankCorrelation(fitted);
        var calibrator = Shape(fitted, correlation, mode);

        calibrator.Correlation = correlation;
        calibrator.MaeBefore = fitted.Average(p => Math.Abs(p.Predicted - p.Actual));
        calibrator.MaeAfter = fitted.Average(p => Math.Abs(calibrator.Calibrate(p.Predicted) - p.Actual));
        return calibrator;
    }

    /// <summary>
    /// Maps one raw prediction (base + residual) onto the calibrated scale, clamped to
    /// [<see cref="MinOutput"/>, <see cref="MaxOutput"/>]. Monotone non-decreasing in
    /// <paramref name="predicted"/> for every <see cref="Kind"/>.
    /// </summary>
    public double Calibrate(double predicted)
    {
        var mapped = Kind switch
        {
            CalibrationKind.Identity => predicted,
            CalibrationKind.BiasShift => predicted + _intercept,
            CalibrationKind.IsotonicBlend =>
                (_blendWeight * Interpolate(predicted)) + ((1d - _blendWeight) * (predicted + _intercept)),
            _ => Interpolate(predicted),
        };

        return Math.Clamp(mapped, MinOutput, MaxOutput);
    }

    /// <summary>How much this map moves a given prediction — the value an exposure log should persist.</summary>
    public double Delta(double predicted) => Calibrate(predicted) - predicted;

    /// <summary>
    /// Picks the rule and builds the (metric-less) instance. Always returns a FRESH object — the
    /// <see cref="Identity"/> singleton is shared and must never have metrics written onto it.
    /// </summary>
    private static RatingCalibrator Shape(IReadOnlyList<CalibrationPair> pairs, double correlation, CalibratorMode mode)
    {
        if (mode == CalibratorMode.Off)
        {
            return new RatingCalibrator(CalibrationKind.Identity, pairs.Count, [], [], 0d, 0d);
        }

        var shift = pairs.Average(p => p.Actual - p.Predicted);
        var biasShift = new RatingCalibrator(CalibrationKind.BiasShift, pairs.Count, [], [], 0d, shift);

        if (mode == CalibratorMode.Shift)
        {
            return biasShift;
        }

        var forced = mode == CalibratorMode.Isotonic;
        if (!forced && pairs.Count < BiasShiftMinPairs)
        {
            return new RatingCalibrator(CalibrationKind.Identity, pairs.Count, [], [], 0d, 0d);
        }

        // Below IsotonicMinPairs there is no shape to find even when one is demanded, and below
        // MinRankCorrelation the "shape" PAV would find is the mean — the collapse this rule exists
        // to stop. Both fall back to correcting the level and nothing else.
        if (pairs.Count < IsotonicMinPairs || (!forced && correlation < MinRankCorrelation))
        {
            return biasShift;
        }

        var (x, y) = FitIsotonic(pairs);
        if (x.Length == 1)
        {
            // Every pair shares one predicted value (a saturated head does exactly this): there is no
            // shape to fit, only an offset. Degrades to the bias shift rather than to a constant map.
            return biasShift;
        }

        var isotonic = new RatingCalibrator(CalibrationKind.Isotonic, pairs.Count, x, y, 1d, 0d);
        if (forced)
        {
            return isotonic;
        }

        // The minimum-slope guard. See the class remarks: this is a product decision, not a
        // statistical one — it knowingly gives up accuracy to keep two different titles showing two
        // different numbers.
        var range = isotonic.Calibrate(RangeProbeHigh) - isotonic.Calibrate(RangeProbeLow);
        return range >= MinCalibratedRange
            ? isotonic
            : new RatingCalibrator(CalibrationKind.IsotonicBlend, pairs.Count, x, y, BlendWeight, shift);
    }

    /// <summary>
    /// Puts the pairs on one artifact's level so they can be pooled. See <see cref="Fit"/> for the
    /// two strategies and why raw pooling is wrong.
    /// </summary>
    private static IReadOnlyList<CalibrationPair> SelectPairs(IReadOnlyList<CalibrationPair> pairs, Guid? activeArtifactId)
    {
        if (activeArtifactId is { } active)
        {
            var onActive = pairs.Where(p => p.ArtifactId == active).ToList();
            if (onActive.Count >= IsotonicMinPairs)
            {
                // Best case by a distance: every pair was produced by the model now in service, so no
                // adjustment is needed and none is invented.
                return onActive;
            }
        }

        var levels = pairs
            .Where(p => p.ArtifactId is not null)
            .GroupBy(p => p.ArtifactId!.Value)
            .Where(g => g.Count() >= ArtifactLevelMinPairs)
            .ToDictionary(g => g.Key, g => g.Average(p => p.Predicted));

        if (levels.Count == 0)
        {
            // Nothing is levellable — one artifact, or a scatter of singletons. Pooling raw is then
            // both the only option and the right one.
            return pairs;
        }

        // The level the map will be APPLIED at. The Active artifact's own, when it has enough pairs
        // to have one; otherwise the pooled mean, which is the best available stand-in for "where
        // this account's predictions sit".
        var reference = activeArtifactId is { } id && levels.TryGetValue(id, out var activeLevel)
            ? activeLevel
            : pairs.Average(p => p.Predicted);

        return pairs
            .Select(p => p.ArtifactId is { } artifact && levels.TryGetValue(artifact, out var level)
                ? p with { Predicted = p.Predicted - level + reference }
                : p)
            .ToList();
    }

    /// <summary>
    /// Spearman's rho: Pearson correlation of the tie-averaged ranks. Ranks rather than raw values
    /// because the question the selection rule asks is "do these predictions ORDER the outcomes",
    /// not "are they linearly related" — and the ratings are a coarse 1–10 integer scale on which a
    /// Pearson coefficient is dominated by its tails. Zero when either side has no spread.
    /// </summary>
    private static double RankCorrelation(IReadOnlyList<CalibrationPair> pairs)
    {
        if (pairs.Count < 2)
        {
            return 0d;
        }

        var predictedRanks = Ranks([.. pairs.Select(p => p.Predicted)]);
        var actualRanks = Ranks([.. pairs.Select(p => p.Actual)]);

        var meanX = predictedRanks.Average();
        var meanY = actualRanks.Average();
        var covariance = 0d;
        var varianceX = 0d;
        var varianceY = 0d;
        for (var i = 0; i < pairs.Count; i++)
        {
            var dx = predictedRanks[i] - meanX;
            var dy = actualRanks[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        return varianceX <= Epsilon || varianceY <= Epsilon
            ? 0d
            : covariance / Math.Sqrt(varianceX * varianceY);
    }

    /// <summary>Tie-averaged ranks, in the input's own order.</summary>
    private static double[] Ranks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        var i = 0;
        while (i < order.Length)
        {
            var j = i;
            while (j + 1 < order.Length && Math.Abs(values[order[j + 1]] - values[order[i]]) <= Epsilon)
            {
                j++;
            }

            var shared = ((i + j) / 2d) + 1d;
            for (var k = i; k <= j; k++)
            {
                ranks[order[k]] = shared;
            }

            i = j + 1;
        }

        return ranks;
    }

    /// <summary>Piecewise-linear through the isotonic knots, slope-1 outside the fitted range.</summary>
    private double Interpolate(double predicted)
    {
        if (predicted <= _knotsX[0])
        {
            return _knotsY[0] + (predicted - _knotsX[0]);
        }

        var last = _knotsX.Length - 1;
        if (predicted >= _knotsX[last])
        {
            return _knotsY[last] + (predicted - _knotsX[last]);
        }

        // _knotsX is strictly ascending, so a binary search lands in exactly one segment.
        var index = Array.BinarySearch(_knotsX, predicted);
        if (index >= 0)
        {
            return _knotsY[index];
        }

        var upper = ~index;
        var lower = upper - 1;
        var span = _knotsX[upper] - _knotsX[lower];
        var t = span <= Epsilon ? 0d : (predicted - _knotsX[lower]) / span;
        return _knotsY[lower] + (t * (_knotsY[upper] - _knotsY[lower]));
    }

    /// <summary>
    /// Pool-adjacent-violators. Ties on the predicted value are merged into one weighted point first
    /// (they are the same input, so they must map to the same output), then adjacent blocks that
    /// violate monotonicity are pooled to their weighted mean until the sequence is non-decreasing.
    /// Returns the distinct predicted values and their fitted actuals, both ascending.
    /// </summary>
    private static (double[] X, double[] Y) FitIsotonic(IReadOnlyList<CalibrationPair> pairs)
    {
        var sorted = pairs.OrderBy(p => p.Predicted).ToList();

        var xs = new List<double>(sorted.Count);
        var ys = new List<double>(sorted.Count);
        var ws = new List<double>(sorted.Count);
        foreach (var pair in sorted)
        {
            if (xs.Count > 0 && Math.Abs(xs[^1] - pair.Predicted) <= Epsilon)
            {
                var weight = ws[^1];
                ys[^1] = ((ys[^1] * weight) + pair.Actual) / (weight + 1d);
                ws[^1] = weight + 1d;
                continue;
            }

            xs.Add(pair.Predicted);
            ys.Add(pair.Actual);
            ws.Add(1d);
        }

        var blockValue = new List<double>(xs.Count);
        var blockWeight = new List<double>(xs.Count);
        var blockStart = new List<int>(xs.Count);
        for (var i = 0; i < xs.Count; i++)
        {
            blockValue.Add(ys[i]);
            blockWeight.Add(ws[i]);
            blockStart.Add(i);

            while (blockValue.Count > 1 && blockValue[^2] > blockValue[^1])
            {
                var weight = blockWeight[^2] + blockWeight[^1];
                blockValue[^2] = ((blockValue[^2] * blockWeight[^2]) + (blockValue[^1] * blockWeight[^1])) / weight;
                blockWeight[^2] = weight;
                blockValue.RemoveAt(blockValue.Count - 1);
                blockWeight.RemoveAt(blockWeight.Count - 1);
                blockStart.RemoveAt(blockStart.Count - 1);
            }
        }

        var fitted = new double[xs.Count];
        for (var b = 0; b < blockValue.Count; b++)
        {
            var from = blockStart[b];
            var to = b + 1 < blockStart.Count ? blockStart[b + 1] : xs.Count;
            for (var i = from; i < to; i++)
            {
                fitted[i] = blockValue[b];
            }
        }

        return (xs.ToArray(), fitted);
    }
}

/// <summary>
/// One closed-loop observation: what the product predicted for a title BEFORE the user rated it,
/// and what they then actually gave it. <see cref="TitleId"/> is carried so the two sources
/// (ratings, and managed-list watch conversions) can be de-duplicated by title;
/// <see cref="ArtifactId"/> is the model that made the prediction, so pairs from different models
/// can be put on a common level before they are pooled (see <see cref="RatingCalibrator.Fit"/>).
/// Null means "not recorded", and such a pair is pooled exactly as it stands.
/// </summary>
public readonly record struct CalibrationPair(Guid TitleId, double Predicted, double Actual, Guid? ArtifactId = null);
