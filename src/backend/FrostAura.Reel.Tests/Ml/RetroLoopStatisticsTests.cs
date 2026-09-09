using FrostAura.Reel.Application.Ml.ClosedLoop;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The retro loop's arithmetic, against hand-computed fixtures. These numbers are the ones the
/// founder reads off /lab as "here is how wrong I am", so a silently wrong mean is worse than no
/// number at all — every case below is worked out by hand in the comment beside it.
/// </summary>
public class RetroLoopStatisticsTests
{
    // predicted 8,6,9,5 (mean 7) vs actual 7,6,4,7 (mean 6)
    // |err| 1,0,5,2 → MAE 2.0 ;  signed 1,0,5,−2 → bias 1.0 ;  within 1 → 2 of 4 = 0.5
    // dx 1,−1,2,−2 · dy 1,0,−2,1 → cov −5, varX 10, varY 6 → r = −5/√60 = −0.645497…
    private static readonly PredictionPair[] Worked =
    [
        new(8, 7),
        new(6, 6),
        new(9, 4),
        new(5, 7),
    ];

    [Fact]
    public void Summarise_computes_means_mae_bias_and_within_one()
    {
        var accuracy = RetroLoopStatistics.Summarise(Worked);

        Assert.Equal(4, accuracy.Count);
        Assert.Equal(7.0d, accuracy.MeanPredicted, 10);
        Assert.Equal(6.0d, accuracy.MeanActual, 10);
        Assert.Equal(2.0d, accuracy.MeanAbsoluteError, 10);
        Assert.Equal(1.0d, accuracy.Bias, 10);
        Assert.Equal(0.5d, accuracy.WithinOnePointRate, 10);
    }

    [Fact]
    public void Pearson_matches_the_hand_computed_value()
    {
        var r = RetroLoopStatistics.Pearson(Worked);

        Assert.NotNull(r);
        Assert.Equal(-5d / Math.Sqrt(60d), r!.Value, 10);
    }

    [Fact]
    public void Within_one_point_is_inclusive_of_exactly_one()
    {
        // The boundary is a product statement — "within one point" includes a one-point miss.
        var accuracy = RetroLoopStatistics.Summarise([new PredictionPair(7, 6), new PredictionPair(7, 5.9)]);

        Assert.Equal(0.5d, accuracy.WithinOnePointRate, 10);
    }

    [Fact]
    public void Bias_is_signed_and_positive_when_the_model_over_promises()
    {
        var over = RetroLoopStatistics.Summarise([new PredictionPair(9, 5), new PredictionPair(8, 6)]);
        var under = RetroLoopStatistics.Summarise([new PredictionPair(5, 9), new PredictionPair(6, 8)]);

        Assert.Equal(3d, over.Bias, 10);
        Assert.Equal(-3d, under.Bias, 10);
        Assert.Equal(3d, over.MeanAbsoluteError, 10);
        Assert.Equal(3d, under.MeanAbsoluteError, 10);
    }

    [Fact]
    public void Pearson_is_null_when_every_prediction_ties()
    {
        // A model that says 6.5 for everything has no variance to correlate. Reporting 0.0 would
        // read as "measured, and there is no relationship"; the truth is that it is unmeasurable.
        Assert.Null(RetroLoopStatistics.Pearson([new PredictionPair(6.5, 4), new PredictionPair(6.5, 9)]));
    }

    [Fact]
    public void Pearson_is_null_when_every_rating_ties()
    {
        Assert.Null(RetroLoopStatistics.Pearson([new PredictionPair(4, 7), new PredictionPair(9, 7)]));
    }

    [Fact]
    public void Pearson_is_null_below_two_pairs()
    {
        Assert.Null(RetroLoopStatistics.Pearson([]));
        Assert.Null(RetroLoopStatistics.Pearson([new PredictionPair(8, 8)]));
    }

    [Fact]
    public void Pearson_is_clamped_to_the_possible_range()
    {
        var perfect = RetroLoopStatistics.Pearson(
            [new PredictionPair(1, 2), new PredictionPair(2, 4), new PredictionPair(3, 6)]);
        var inverse = RetroLoopStatistics.Pearson(
            [new PredictionPair(1, 6), new PredictionPair(2, 4), new PredictionPair(3, 2)]);

        Assert.Equal(1d, perfect!.Value, 10);
        Assert.Equal(-1d, inverse!.Value, 10);
    }

    [Fact]
    public void Empty_set_reports_zeroes_and_an_undefined_correlation()
    {
        var accuracy = RetroLoopStatistics.Summarise([]);

        Assert.Equal(0, accuracy.Count);
        Assert.Equal(0d, accuracy.MeanAbsoluteError);
        Assert.Equal(0d, accuracy.Bias);
        Assert.Equal(0d, accuracy.WithinOnePointRate);
        Assert.Null(accuracy.PearsonCorrelation);
    }

    [Fact]
    public void Mean_or_null_distinguishes_no_data_from_zero()
    {
        Assert.Null(RetroLoopStatistics.MeanOrNull([]));
        Assert.Equal(0d, RetroLoopStatistics.MeanOrNull([0d, 0d]));
        Assert.Equal(6.5d, RetroLoopStatistics.MeanOrNull([6d, 7d])!.Value, 10);
    }
}
