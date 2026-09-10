using System.Globalization;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The maths that answers "does the verdict carry information", on hand-computed fixtures.
///
/// <para><b>These exist because the gate could not catch what the first live run found.</b> On
/// 2026-09-10 the instrument reported a false-rule-out rate of 12.0% and a true-rule-out rate of
/// 10.1%: every clause of the gate reads one rate alone, and "12% is under the 15% ceiling" is a
/// true sentence about a coin. An AUC of 0.5 says the same thing in one number and does not move
/// when the Out threshold does — which is exactly why it is worth persisting beside the rates
/// rather than instead of them.</para>
/// </summary>
public class DiscriminationMetricsTests
{
    [Fact]
    public void An_uncorrelated_judge_scores_an_auc_of_one_half()
    {
        // Four titles, two loved and two disliked, and the judge's signed score is identical
        // across the two classes. There is no ordering to find, and 0.5 is the number that says so.
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(0.8d, 9),
            new DiscriminationMetrics.Observation(-0.8d, 9),
            new DiscriminationMetrics.Observation(0.8d, 3),
            new DiscriminationMetrics.Observation(-0.8d, 3),
        };

        Assert.Equal(0.5m, DiscriminationMetrics.Auc(observations));
    }

    [Fact]
    public void A_perfectly_separating_judge_scores_an_auc_of_one()
    {
        // Every loved title carries a higher signed score than every disliked one.
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(0.9d, 10),
            new DiscriminationMetrics.Observation(0.6d, 8),
            new DiscriminationMetrics.Observation(-0.5d, 4),
            new DiscriminationMetrics.Observation(-0.95d, 1),
        };

        Assert.Equal(1m, DiscriminationMetrics.Auc(observations));
    }

    [Fact]
    public void A_judge_that_has_it_exactly_backwards_scores_an_auc_of_zero()
    {
        // Not an error and not clamped: below 0.5 means the ordering is inverted, which is a
        // finding — and one an absolute-value "how far from chance" metric would have hidden.
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(-0.9d, 10),
            new DiscriminationMetrics.Observation(0.9d, 2),
        };

        Assert.Equal(0m, DiscriminationMetrics.Auc(observations));
    }

    [Fact]
    public void Ties_in_the_signed_score_count_as_half_a_win()
    {
        // Two unsure verdicts both sit at exactly 0. Counting a tie as a win would flatter the
        // judge; counting it as a loss would punish it for declining to answer.
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(0d, 9),
            new DiscriminationMetrics.Observation(0d, 2),
        };

        Assert.Equal(0.5m, DiscriminationMetrics.Auc(observations));
    }

    [Fact]
    public void An_auc_over_one_class_is_not_an_auc()
    {
        // Null rather than 0.5 — "nothing to compare against" is a different statement from
        // "compared, and it was chance", and a persisted 0.5 would read as the second.
        Assert.Null(DiscriminationMetrics.Auc(
        [
            new DiscriminationMetrics.Observation(0.9d, 9),
            new DiscriminationMetrics.Observation(0.4d, 10),
        ]));

        Assert.Null(DiscriminationMetrics.Auc([]));
    }

    [Fact]
    public void Titles_rated_in_the_middle_are_in_neither_class()
    {
        // 5, 6 and 7 are outcomes the product has no opinion about. Including them would put the
        // judge's hardest cases into whichever class the threshold happened to reach.
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(0.9d, 9),
            new DiscriminationMetrics.Observation(0.9d, 6),
            new DiscriminationMetrics.Observation(0.9d, 7),
            new DiscriminationMetrics.Observation(-0.9d, 3),
        };

        Assert.Equal(1m, DiscriminationMetrics.Auc(observations));
    }

    [Fact]
    public void Spearman_is_positive_when_the_judge_orders_titles_the_way_the_viewer_did()
    {
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(-0.9d, 2),
            new DiscriminationMetrics.Observation(0d, 6),
            new DiscriminationMetrics.Observation(0.5d, 8),
            new DiscriminationMetrics.Observation(0.9d, 10),
        };

        // Perfectly concordant ranks.
        Assert.Equal(1m, DiscriminationMetrics.Spearman(observations));
    }

    [Fact]
    public void Spearman_is_negative_when_the_ordering_is_inverted()
    {
        var observations = new[]
        {
            new DiscriminationMetrics.Observation(0.9d, 2),
            new DiscriminationMetrics.Observation(0.5d, 6),
            new DiscriminationMetrics.Observation(0d, 8),
            new DiscriminationMetrics.Observation(-0.9d, 10),
        };

        Assert.Equal(-1m, DiscriminationMetrics.Spearman(observations));
    }

    [Fact]
    public void Spearman_over_a_constant_is_null_rather_than_zero()
    {
        // A judge that answered "unsure" to everything has no variance to correlate. Zero would
        // read as "measured, no relationship"; the truth is that there was nothing to measure —
        // the same distinction the calibrator-collapse guard exists for.
        Assert.Null(DiscriminationMetrics.Spearman(
        [
            new DiscriminationMetrics.Observation(0d, 9),
            new DiscriminationMetrics.Observation(0d, 3),
        ]));

        Assert.Null(DiscriminationMetrics.Spearman([new DiscriminationMetrics.Observation(0.5d, 9)]));
    }

    [Fact]
    public void Tied_values_take_the_average_of_the_ranks_they_span()
    {
        // Ordinal ranks would let Postgres row order decide which of two identical unsure verdicts
        // ranked higher, and make the correlation depend on it.
        Assert.Equal([1d, 2.5d, 2.5d, 4d], DiscriminationMetrics.AverageRanks([0.1d, 0.4d, 0.4d, 0.9d]));
    }

    [Fact]
    public void The_signed_score_puts_a_keep_and_an_out_of_equal_strength_on_opposite_sides()
    {
        Assert.Equal(0.8d, DiscriminationMetrics.SignedScore(CrossReferenceVerdict.Keep, 0.8d));
        Assert.Equal(-0.8d, DiscriminationMetrics.SignedScore(CrossReferenceVerdict.Out, 0.8d));
        // An unsure is the judge declining to place the title on the axis, not a weak keep.
        Assert.Equal(0d, DiscriminationMetrics.SignedScore(CrossReferenceVerdict.Unsure, 0.95d));
    }

    [Fact]
    public void The_summary_reports_each_verdict_class_against_the_outcome()
    {
        var summary = DiscriminationMetrics.Summarise(
        [
            (CrossReferenceVerdict.Keep, 0.9d, (short)10),
            (CrossReferenceVerdict.Keep, 0.7d, (short)4),
            (CrossReferenceVerdict.Out, 0.8d, (short)2),
            (CrossReferenceVerdict.Unsure, 0.5d, (short)6),
        ]);

        Assert.Equal(2, summary.KeepCount);
        Assert.Equal(1, summary.OutCount);
        Assert.Equal(1, summary.UnsureCount);
        Assert.Equal(7m, summary.MeanLaterRatingKeep);
        Assert.Equal(2m, summary.MeanLaterRatingOut);
        Assert.Equal(6m, summary.MeanLaterRatingUnsure);
        Assert.Equal(0.5m, summary.KeepLovedRate);
        Assert.Equal(0.5m, summary.KeepDislikedRate);
        Assert.Equal(0m, summary.OutLovedRate);
        Assert.Equal(1m, summary.OutDislikedRate);
    }

    [Fact]
    public void A_class_with_no_members_reports_null_rather_than_zero()
    {
        // "It kept nothing" is not "everything it kept was disliked".
        var summary = DiscriminationMetrics.Summarise([(CrossReferenceVerdict.Unsure, 0.5d, (short)7)]);

        Assert.Equal(0, summary.KeepCount);
        Assert.Null(summary.MeanLaterRatingKeep);
        Assert.Null(summary.KeepLovedRate);
        Assert.Null(summary.OutDislikedRate);
    }

    [Fact]
    public void The_caveat_fires_when_the_two_rule_out_rates_are_indistinguishable()
    {
        // The live numbers, exactly: 12.0% false, 10.1% true.
        var caveat = DiscriminationMetrics.DiscriminationCaveat(0.1200m, 0.1010m, 0.5100m);

        Assert.NotNull(caveat);
        Assert.Contains("within 5 points of each other", caveat, StringComparison.Ordinal);
        Assert.Contains("not distinguishing loved from disliked on this history", caveat, StringComparison.Ordinal);
        Assert.Contains("0.510", caveat, StringComparison.Ordinal);
        Assert.Contains("0.500 is chance", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void The_caveat_stays_silent_when_the_judge_is_actually_discriminating()
    {
        // 5% false against 55% true is an instrument, not a coin, and a sentence saying otherwise
        // would be the mirror image of the failure this whole class exists to catch.
        Assert.Null(DiscriminationMetrics.DiscriminationCaveat(0.05m, 0.55m, 0.88m));
    }

    [Fact]
    public void The_caveat_is_stated_without_an_auc_when_there_is_no_auc()
    {
        var caveat = DiscriminationMetrics.DiscriminationCaveat(0.12m, 0.10m, null);

        Assert.NotNull(caveat);
        Assert.Contains("within 5 points", caveat, StringComparison.Ordinal);
        Assert.DoesNotContain("AUC", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void The_caveat_is_invariant_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var caveat = DiscriminationMetrics.DiscriminationCaveat(0.12m, 0.10m, 0.5100m);

            Assert.Contains("0.510", caveat!, StringComparison.Ordinal);
            Assert.DoesNotContain("0,510", caveat!, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
