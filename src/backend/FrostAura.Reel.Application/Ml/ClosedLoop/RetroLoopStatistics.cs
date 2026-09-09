namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// One (predicted, actual) observation the closed loop can score itself against.
/// <paramref name="Predicted"/> is what the model said before the outcome existed;
/// <paramref name="Actual"/> is the rating the user later gave.
/// </summary>
public readonly record struct PredictionPair(double Predicted, double Actual);

/// <summary>
/// The summary of a set of predicted-vs-actual pairs. Every field is nullable-free except the
/// correlation, which is genuinely undefined when either side has no variance (all-equal
/// predictions, or every title rated the same) — reporting 0 there would read as "no relationship
/// measured" when the truth is "no relationship measurable".
/// </summary>
/// <param name="Count">Pairs contributing to every other number here.</param>
/// <param name="MeanPredicted">Mean of the model's predictions.</param>
/// <param name="MeanActual">Mean of the user's own ratings for the same titles.</param>
/// <param name="MeanAbsoluteError">Mean |predicted − actual| — the headline "how wrong am I".</param>
/// <param name="Bias">Mean (predicted − actual). Positive = the model over-promises.</param>
/// <param name="WithinOnePointRate">Share of pairs where |predicted − actual| ≤ 1.0.</param>
/// <param name="PearsonCorrelation">Linear correlation, or null when it is undefined.</param>
public sealed record PredictionAccuracy(
    int Count,
    double MeanPredicted,
    double MeanActual,
    double MeanAbsoluteError,
    double Bias,
    double WithinOnePointRate,
    double? PearsonCorrelation)
{
    /// <summary>The honest answer for an empty set: zeros, and an explicitly undefined correlation.</summary>
    public static readonly PredictionAccuracy Empty = new(0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// The arithmetic behind the retro closed loop, kept pure and free of EF so it can be tested
/// against hand-computed fixtures. Nothing here rounds: rounding is a presentation decision and
/// belongs at the endpoint, not inside the measurement.
/// </summary>
public static class RetroLoopStatistics
{
    /// <summary>The tolerance the "within one point" rate is measured against.</summary>
    public const double WithinOnePointTolerance = 1.0d;

    /// <summary>
    /// Summarises a set of predicted-vs-actual pairs. An empty set returns
    /// <see cref="PredictionAccuracy.Empty"/> rather than throwing — a founder with no closed-loop
    /// history yet must see "0 conversions", not a 500.
    /// </summary>
    public static PredictionAccuracy Summarise(IReadOnlyCollection<PredictionPair> pairs)
    {
        if (pairs.Count == 0)
        {
            return PredictionAccuracy.Empty;
        }

        var n = pairs.Count;
        var meanPredicted = pairs.Average(p => p.Predicted);
        var meanActual = pairs.Average(p => p.Actual);
        var mae = pairs.Average(p => Math.Abs(p.Predicted - p.Actual));
        var bias = pairs.Average(p => p.Predicted - p.Actual);
        var withinOne = pairs.Count(p => Math.Abs(p.Predicted - p.Actual) <= WithinOnePointTolerance) / (double)n;

        return new PredictionAccuracy(n, meanPredicted, meanActual, mae, bias, withinOne, Pearson(pairs));
    }

    /// <summary>
    /// Pearson's r over the pairs, or null where it does not exist: fewer than two pairs, or a
    /// degenerate side (every prediction identical, or every rating identical — the tie case). A
    /// zero-variance side makes the denominator zero, and returning 0.0 there would be a claim the
    /// data cannot support.
    /// </summary>
    public static double? Pearson(IReadOnlyCollection<PredictionPair> pairs)
    {
        if (pairs.Count < 2)
        {
            return null;
        }

        var meanX = pairs.Average(p => p.Predicted);
        var meanY = pairs.Average(p => p.Actual);

        double covariance = 0d, varianceX = 0d, varianceY = 0d;
        foreach (var pair in pairs)
        {
            var dx = pair.Predicted - meanX;
            var dy = pair.Actual - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX <= 0d || varianceY <= 0d)
        {
            return null;
        }

        var r = covariance / Math.Sqrt(varianceX * varianceY);

        // Floating-point drift can push a perfect correlation a hair past ±1, which then reads as a
        // broken statistic on the page. Clamp to the mathematically possible range.
        return Math.Clamp(r, -1d, 1d);
    }

    /// <summary>Mean of a rating sequence, or null when there is nothing to average.</summary>
    public static double? MeanOrNull(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Average();
}
