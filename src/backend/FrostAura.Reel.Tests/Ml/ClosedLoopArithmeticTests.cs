using FrostAura.Reel.Application.Ml.ClosedLoop;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The pure arithmetic behind the track record. Everything here is offline on purpose: the SQL half
/// of the closed loop is covered by <c>ExposureLogIntegrationTests</c>, and these are the parts that
/// have to be right regardless of what the database returns.
/// </summary>
public class ClosedLoopArithmeticTests
{
    [Fact]
    public void The_idempotency_key_collapses_a_burst_of_refetches_inside_one_minute()
    {
        // The founder's client refetches the feed on mount and on every Feed tag invalidation. Five
        // reads of one page separated by seconds must resolve to ONE bucket, or the calibration
        // denominator inflates by however many times the page happened to be re-requested.
        var basis = new DateTime(2026, 9, 10, 19, 17, 0, DateTimeKind.Utc);
        var burst = new[]
        {
            basis,
            basis.AddSeconds(3),
            basis.AddSeconds(31),
            basis.AddSeconds(59),
            basis.AddSeconds(59).AddMilliseconds(999),
        };

        Assert.Single(burst.Select(ExposureLog.Truncate).Distinct());
        Assert.Equal(basis, ExposureLog.Truncate(basis.AddSeconds(59)));
    }

    [Fact]
    public void The_idempotency_key_separates_the_next_minute()
    {
        // The other half of the contract: a genuinely later visit is a genuinely new exposure.
        // A key that swallowed the next minute would under-count a card the user came back to.
        var basis = new DateTime(2026, 9, 10, 19, 17, 0, DateTimeKind.Utc);

        Assert.NotEqual(ExposureLog.Truncate(basis), ExposureLog.Truncate(basis.AddMinutes(1)));
        Assert.Equal(DateTimeKind.Utc, ExposureLog.Truncate(basis).Kind);
    }

    [Fact]
    public void Calibration_emits_every_band_including_the_empty_ones()
    {
        // A chart drawn only over the bands that happen to hold data hides the single most telling
        // fact about this model — that it has never once predicted below about 4. The empty bands
        // ARE the finding, so they are emitted with a null mean rather than dropped.
        var buckets = AccuracyQuery.Bucket([new PredictionPair(8.2d, 7d), new PredictionPair(8.6d, 8d)]);

        Assert.Equal(AccuracyQuery.CalibrationBuckets, buckets.Count);
        Assert.Equal(1, buckets.Count(b => b.Count > 0));
        Assert.All(buckets.Where(b => b.Count == 0), b =>
        {
            Assert.Null(b.MeanPredicted);
            Assert.Null(b.MeanActual);
        });

        var band = buckets.Single(b => b.Count > 0);
        Assert.Equal(8, band.Index);
        Assert.Equal(8.0m, band.Low);
        Assert.Equal(9.0m, band.High);
        Assert.Equal(8.4d, band.MeanPredicted!.Value, 6);
        Assert.Equal(7.5d, band.MeanActual!.Value, 6);
    }

    [Fact]
    public void A_perfect_ten_lands_in_the_top_band_rather_than_falling_off_the_end()
    {
        // Half-open bands everywhere would leave 10.0 belonging to no band at all, so a maximal
        // prediction would silently vanish from the chart it is most interesting on.
        var buckets = AccuracyQuery.Bucket([new PredictionPair(10d, 4d), new PredictionPair(0d, 1d)]);

        Assert.Equal(1, buckets[^1].Count);
        Assert.Equal(1, buckets[0].Count);
        Assert.Equal(2, buckets.Sum(b => b.Count));
    }

    [Fact]
    public void A_rate_over_an_empty_denominator_is_unknown_and_never_zero()
    {
        // "We have shown you nothing, so your save rate is 0%" is a lie about the product's
        // performance. Null renders as an em-dash; zero renders as a failure that did not happen.
        Assert.Null(AccuracyQuery.Rate(0, 0));
        Assert.Equal(0d, AccuracyQuery.Rate(0, 12));
        Assert.Equal(0.25d, AccuracyQuery.Rate(3, 12)!.Value, 6);
    }

    [Fact]
    public void The_median_days_to_watch_handles_both_parities_and_an_empty_queue()
    {
        Assert.Null(AccuracyQuery.Median([]));
        Assert.Equal(4d, AccuracyQuery.Median([1d, 4d, 90d])!.Value, 6);
        Assert.Equal(3d, AccuracyQuery.Median([1d, 2d, 4d, 100d])!.Value, 6);
    }

    [Fact]
    public void The_headline_reports_the_founders_real_shape_rather_than_a_flattering_one()
    {
        // A regression guard on honesty, not on arithmetic. Fed pairs with the live data's
        // signature — over-prediction with essentially no rank information — the summary must
        // report a positive bias, a within-one-point rate well under a half, and a correlation
        // near zero. If someone ever "fixes" this by clamping or absolute-valuing the bias, this
        // test is what says no.
        var pairs = new[]
        {
            new PredictionPair(9.8d, 3d),
            new PredictionPair(9.4d, 4d),
            new PredictionPair(8.9d, 4d),
            new PredictionPair(7.8d, 4d),
            new PredictionPair(6.6d, 8d),
            new PredictionPair(6.9d, 9d),
        };

        var summary = RetroLoopStatistics.Summarise(pairs);

        Assert.Equal(6, summary.Count);
        Assert.True(summary.Bias > 0d, "an over-promising model must report a POSITIVE bias");
        Assert.True(summary.WithinOnePointRate < 0.5d);
        Assert.NotNull(summary.PearsonCorrelation);
        Assert.True(summary.PearsonCorrelation < 0d, "predicting high on what the user rates low is a negative correlation");
    }
}
