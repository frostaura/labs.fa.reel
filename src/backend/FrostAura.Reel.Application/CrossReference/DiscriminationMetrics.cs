using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// Does the judge's answer tell you anything about the outcome? Pure, I/O-free, and computed over
/// the per-title <see cref="CrossReferenceEvaluationItem"/> rows so a new metric is a recomputation
/// rather than another 25 provider calls.
///
/// <para><b>Why this exists at all.</b> The first live run reported false-rule-out 12.0% and
/// true-rule-out 10.1% — a verdict that hides loved titles at very nearly the rate it hides
/// disliked ones is not a cautious judge, it is a coin. The gate's three clauses could never have
/// caught that: each reads one rate in isolation, and "12% is under the 15% ceiling" is a true
/// sentence about a worthless instrument. Everything here compares the two sides of the answer to
/// each other.</para>
///
/// <para><b>The signed score is the axis.</b> <c>+confidence</c> for a keep, <c>−confidence</c> for
/// an out, <c>0</c> for an unsure. A keep at 0.9 and an out at 0.9 are opposite claims of equal
/// strength; treating confidence as an unsigned magnitude would rank them together and measure
/// nothing. Both <see cref="Auc"/> and <see cref="Spearman"/> read this one axis, which is what
/// lets them be compared with each other.</para>
///
/// <para><b>Every metric returns null rather than a number when it has no basis</b> — one class
/// empty, one observation, no variance. A 0.5 written where "not measurable" was meant is the same
/// failure as the calibrator that mapped every prediction to the mean: honest-looking, and a lie
/// about what was known.</para>
/// </summary>
public static class DiscriminationMetrics
{
    /// <summary>Later-rated at or above this is a positive outcome. The eval harness's own bar.</summary>
    public const short LovedFloor = 8;

    /// <summary>Later-rated at or below this is a negative outcome.</summary>
    public const short DislikedCeiling = 4;

    /// <summary>
    /// The gap, in percentage points, below which the false- and true-rule-out rates are treated as
    /// the same number and the caveat says the verdict is not discriminating.
    ///
    /// <para>Five points is not a significance test and does not pretend to be one — it is a
    /// reading aid on a rate pair whose confidence intervals at this sample size are ten times
    /// wider than the gap. Its job is to stop a 12.0-versus-10.1 result being quoted as "it caught
    /// 10% of the duds" without the sentence that says the 10% was free.</para>
    /// </summary>
    public const decimal IndistinguishableRuleOutGap = 0.05m;

    /// <summary>One judged title reduced to the two things a metric needs.</summary>
    /// <param name="SignedScore">
    /// <c>+confidence</c> kept, <c>−confidence</c> ruled out, <c>0</c> unsure.
    /// </param>
    /// <param name="LaterRating">What the account rated it after the split.</param>
    public readonly record struct Observation(double SignedScore, short LaterRating);

    /// <summary>Everything the evaluation row stores about discrimination, in one pass.</summary>
    public sealed record Summary(
        int KeepCount,
        int OutCount,
        int UnsureCount,
        decimal? MeanLaterRatingKeep,
        decimal? MeanLaterRatingOut,
        decimal? MeanLaterRatingUnsure,
        decimal? KeepLovedRate,
        decimal? KeepDislikedRate,
        decimal? OutLovedRate,
        decimal? OutDislikedRate,
        decimal? Spearman,
        decimal? Auc);

    /// <summary>The signed score for one verdict — the single definition, so nothing forks it.</summary>
    public static double SignedScore(CrossReferenceVerdict verdict, double confidence) => verdict switch
    {
        CrossReferenceVerdict.Keep => confidence,
        CrossReferenceVerdict.Out => -confidence,
        // Not "a weak keep" and not "a weak out". An unsure is the judge declining to place the
        // title on this axis, and the middle of the axis is where that belongs.
        _ => 0d,
    };

    /// <summary>
    /// Everything, over one origin's items. <paramref name="items"/> may be empty, in which case
    /// every rate and every correlation is null and the three counts are zero.
    /// </summary>
    public static Summary Summarise(IReadOnlyList<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> items)
    {
        var keeps = items.Where(i => i.Verdict == CrossReferenceVerdict.Keep).ToList();
        var outs = items.Where(i => i.Verdict == CrossReferenceVerdict.Out).ToList();
        var unsures = items.Where(i => i.Verdict == CrossReferenceVerdict.Unsure).ToList();

        var observations = items
            .Select(i => new Observation(SignedScore(i.Verdict, i.Confidence), i.LaterRating))
            .ToList();

        return new Summary(
            keeps.Count,
            outs.Count,
            unsures.Count,
            MeanRating(keeps),
            MeanRating(outs),
            MeanRating(unsures),
            ShareAtOrAbove(keeps, LovedFloor),
            ShareAtOrBelow(keeps, DislikedCeiling),
            ShareAtOrAbove(outs, LovedFloor),
            ShareAtOrBelow(outs, DislikedCeiling),
            Spearman(observations),
            Auc(observations));
    }

    /// <summary>
    /// Rank-based AUC (the Mann-Whitney statistic) of the signed score separating later-loved from
    /// later-disliked titles, with ties contributing a half.
    ///
    /// <para><b>0.5 means no discrimination whatsoever</b> and is the number this instrument was
    /// built to be able to report. Unlike the rule-out rates it does not depend on where the Out
    /// threshold sits, so a judge that would look better at 0.6 and worse at 0.8 gets the same AUC
    /// either way — the ordering is the thing being measured.</para>
    ///
    /// <para>Null when either class is empty: an AUC over one class is not a poor AUC, it is not an
    /// AUC. Titles rated 5–7 are in NEITHER class and contribute nothing, deliberately — they are
    /// the outcomes the product has no opinion about.</para>
    /// </summary>
    public static decimal? Auc(IReadOnlyList<Observation> observations)
    {
        var positive = observations.Where(o => o.LaterRating >= LovedFloor).Select(o => o.SignedScore).ToList();
        var negative = observations.Where(o => o.LaterRating <= DislikedCeiling).Select(o => o.SignedScore).ToList();
        if (positive.Count == 0 || negative.Count == 0)
        {
            return null;
        }

        // The direct pairwise form rather than the rank-sum shortcut: at these n it is trivially
        // cheap, and it is the definition — "how often does a title the viewer loved carry a higher
        // signed score than one they disliked" — rather than an algebraic rearrangement of it that
        // a later reader has to trust.
        var wins = 0d;
        foreach (var p in positive)
        {
            foreach (var n in negative)
            {
                wins += p > n ? 1d : p < n ? 0d : 0.5d;
            }
        }

        return Round(wins / (positive.Count * (double)negative.Count));
    }

    /// <summary>
    /// Spearman rank correlation between the signed score and the later rating — Pearson over
    /// average ranks, which is what handles the heavy tie structure this data always has (every
    /// unsure shares a score of exactly 0, and ratings are integers).
    ///
    /// <para>Null below two observations, and null when either side has no variance at all: a
    /// correlation with a constant is undefined, and returning 0 for it would read as "measured, no
    /// relationship" when the truth is "there was nothing to correlate".</para>
    /// </summary>
    public static decimal? Spearman(IReadOnlyList<Observation> observations)
    {
        if (observations.Count < 2)
        {
            return null;
        }

        var scoreRanks = AverageRanks(observations.Select(o => o.SignedScore).ToList());
        var ratingRanks = AverageRanks(observations.Select(o => (double)o.LaterRating).ToList());

        var meanScore = scoreRanks.Average();
        var meanRating = ratingRanks.Average();
        double covariance = 0, scoreVariance = 0, ratingVariance = 0;
        for (var i = 0; i < scoreRanks.Count; i++)
        {
            var ds = scoreRanks[i] - meanScore;
            var dr = ratingRanks[i] - meanRating;
            covariance += ds * dr;
            scoreVariance += ds * ds;
            ratingVariance += dr * dr;
        }

        if (scoreVariance <= 0d || ratingVariance <= 0d)
        {
            return null;
        }

        return Round(covariance / Math.Sqrt(scoreVariance * ratingVariance));
    }

    /// <summary>
    /// Average ("fractional") ranks, 1-based: tied values all take the mean of the ranks they span.
    /// The alternative — ordinal ranks — would let the input order decide which of two identical
    /// unsure verdicts ranked higher, and make the correlation depend on Postgres row order.
    /// </summary>
    internal static IReadOnlyList<double> AverageRanks(IReadOnlyList<double> values)
    {
        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToList();
        var ranks = new double[values.Count];
        var position = 0;
        while (position < order.Count)
        {
            var end = position;
            while (end + 1 < order.Count && values[order[end + 1]].Equals(values[order[position]]))
            {
                end++;
            }

            // Ranks are 1-based, so the span [position, end] spans ranks position+1 … end+1.
            var averageRank = ((position + 1) + (end + 1)) / 2d;
            for (var i = position; i <= end; i++)
            {
                ranks[order[i]] = averageRank;
            }

            position = end + 1;
        }

        return ranks;
    }

    /// <summary>
    /// The sentence that says whether the verdicts discriminate at all, or null when there is
    /// nothing to say. Appended to the persisted caveat so the reading travels with the number.
    /// </summary>
    public static string? DiscriminationCaveat(decimal falseRuleOutRate, decimal trueRuleOutRate, decimal? auc)
    {
        if (Math.Abs(falseRuleOutRate - trueRuleOutRate) > IndistinguishableRuleOutGap)
        {
            return null;
        }

        var sentence =
            "False-rule-out and true-rule-out are within 5 points of each other, which means the out "
            + "verdict is not distinguishing loved from disliked on this history: it is hiding titles "
            + "this account went on to love at very nearly the rate it hides ones they went on to "
            + "dislike.";
        return auc is { } value
            ? sentence + string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $" The rank AUC of {value:0.000} says the same thing without reference to the out "
                + $"threshold — 0.500 is chance.")
            : sentence;
    }

    private static decimal? MeanRating(
        IReadOnlyList<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> rows) =>
        rows.Count == 0 ? null : Round(rows.Average(r => (double)r.LaterRating));

    private static decimal? ShareAtOrAbove(
        IReadOnlyList<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> rows, short floor) =>
        rows.Count == 0 ? null : Round(rows.Count(r => r.LaterRating >= floor) / (double)rows.Count);

    private static decimal? ShareAtOrBelow(
        IReadOnlyList<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> rows, short ceiling) =>
        rows.Count == 0 ? null : Round(rows.Count(r => r.LaterRating <= ceiling) / (double)rows.Count);

    /// <summary>Four decimals, matching the numeric(6,4) columns these land in.</summary>
    private static decimal Round(double value) => Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero);
}
