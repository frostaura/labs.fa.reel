namespace FrostAura.Reel.Application.Ml;

/// <summary>Which rule a fitted <see cref="RatingCalibrator"/> ended up using — reported, never chosen by a caller.</summary>
public enum CalibrationKind
{
    /// <summary>No observed pairs at all: predicted → predicted (the strict no-op).</summary>
    Identity,

    /// <summary>Too few pairs to fit a slope — identity shifted by the mean signed error.</summary>
    BiasShift,

    /// <summary>Enough pairs for a least-squares line, not enough for a shape.</summary>
    Affine,

    /// <summary>Pool-adjacent-violators isotonic regression, linearly interpolated between knots.</summary>
    Isotonic,
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
/// Two properties are load-bearing and are asserted in the tests:
/// <list type="bullet">
/// <item><b>Monotone non-decreasing.</b> Calibration must never reorder the ranking — it changes the
/// number on the card, not which card is on top. Isotonic regression gives this by construction;
/// the affine fallback clamps its slope to ≥ 0 for the same reason.</item>
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
/// Pure math, no I/O: <see cref="RatingCalibrationService"/> owns loading the pairs and caching.
/// </summary>
public sealed class RatingCalibrator
{
    /// <summary>Fewest pairs worth fitting a shape on. Below this the shape is noise, so a line is fitted instead.</summary>
    public const int IsotonicMinPairs = 30;

    /// <summary>Fewest pairs worth fitting a SLOPE on. Below this only the level is estimable.</summary>
    public const int AffineMinPairs = 10;

    /// <summary>The product's rating scale. A calibrated prediction is always a plausible rating.</summary>
    public const double MinOutput = 1d;

    /// <inheritdoc cref="MinOutput"/>
    public const double MaxOutput = 10d;

    /// <summary>Slopes beyond this are an artefact of a short, noisy sample, not a real relationship.</summary>
    private const double MaxAffineSlope = 5d;

    private const double Epsilon = 1e-9;

    private readonly double[] _knotsX = [];
    private readonly double[] _knotsY = [];
    private readonly double _slope;
    private readonly double _intercept;

    private RatingCalibrator(CalibrationKind kind, int pairCount, double[] knotsX, double[] knotsY, double slope, double intercept)
    {
        Kind = kind;
        PairCount = pairCount;
        _knotsX = knotsX;
        _knotsY = knotsY;
        _slope = slope;
        _intercept = intercept;
    }

    /// <summary>The rule this instance ended up with, for the founder-facing diagnostics.</summary>
    public CalibrationKind Kind { get; }

    /// <summary>How many predicted/actual pairs the fit saw.</summary>
    public int PairCount { get; }

    /// <summary>The strict no-op: clamped identity. What a brand-new account gets.</summary>
    public static RatingCalibrator Identity { get; } =
        new(CalibrationKind.Identity, 0, [], [], 1d, 0d);

    /// <summary>
    /// Fits the map. <paramref name="pairs"/> are (what the product predicted, what the user actually
    /// rated it) — order irrelevant, duplicates fine, ties on the predicted value expected.
    /// </summary>
    public static RatingCalibrator Fit(IReadOnlyList<CalibrationPair> pairs)
    {
        if (pairs.Count == 0)
        {
            return Identity;
        }

        if (pairs.Count < AffineMinPairs)
        {
            // Only the level is estimable from a handful of points; a slope fitted here would be
            // noise amplified straight onto the number the user reads.
            var shift = pairs.Average(p => p.Actual - p.Predicted);
            return new RatingCalibrator(CalibrationKind.BiasShift, pairs.Count, [], [], 1d, shift);
        }

        if (pairs.Count < IsotonicMinPairs)
        {
            return FitAffine(pairs);
        }

        var (x, y) = FitIsotonic(pairs);
        return x.Length == 1
            // Every pair shares one predicted value (a saturated head does exactly this): there is no
            // shape to fit, only an offset. Degrades to the bias shift rather than to a constant map.
            ? new RatingCalibrator(CalibrationKind.BiasShift, pairs.Count, [], [], 1d, y[0] - x[0])
            : new RatingCalibrator(CalibrationKind.Isotonic, pairs.Count, x, y, 1d, 0d);
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
            CalibrationKind.Affine => (_slope * predicted) + _intercept,
            _ => Interpolate(predicted),
        };

        return Math.Clamp(mapped, MinOutput, MaxOutput);
    }

    /// <summary>How much this map moves a given prediction — the value an exposure log should persist.</summary>
    public double Delta(double predicted) => Calibrate(predicted) - predicted;

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

    private static RatingCalibrator FitAffine(IReadOnlyList<CalibrationPair> pairs)
    {
        var meanX = pairs.Average(p => p.Predicted);
        var meanY = pairs.Average(p => p.Actual);
        var covariance = pairs.Sum(p => (p.Predicted - meanX) * (p.Actual - meanY));
        var variance = pairs.Sum(p => (p.Predicted - meanX) * (p.Predicted - meanX));

        if (variance <= Epsilon)
        {
            // Zero spread in the predictions — no slope is identifiable, only the level.
            return new RatingCalibrator(CalibrationKind.BiasShift, pairs.Count, [], [], 1d, meanY - meanX);
        }

        // Clamped to ≥ 0 so the map cannot invert the ranking, and to a sane ceiling so a short,
        // noisy sample cannot turn a one-point prediction difference into a five-point one.
        var slope = Math.Clamp(covariance / variance, 0d, MaxAffineSlope);
        return new RatingCalibrator(CalibrationKind.Affine, pairs.Count, [], [], slope, meanY - (slope * meanX));
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
/// (ratings, and managed-list watch conversions) can be de-duplicated by title.
/// </summary>
public readonly record struct CalibrationPair(Guid TitleId, double Predicted, double Actual);
