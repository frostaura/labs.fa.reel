using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The monotone predicted→actual map. These are the properties the product depends on, asserted as
/// arithmetic rather than through a database:
///
/// <list type="bullet">
/// <item><b>It never reorders.</b> A calibrator that inverted anywhere would silently reshuffle the
/// hero and every rail, which is a ranking change dressed up as a display change.</item>
/// <item><b>It corrects level.</b> On an over-predicting distribution — the founder's actual
/// situation, predicted 6.81 against an actual 5.78 — it must pull the numbers down toward truth.</item>
/// <item><b>It is near-idempotent.</b> Fitting on an already-calibrated distribution must land close
/// to identity, or a second pass (or a retrain that already absorbed the level) would compound.</item>
/// <item><b>It degrades sanely.</b> Few pairs, one pair, all-identical pairs — each has a defined
/// answer, and none of them is a crash or a constant map.</item>
/// </list>
/// </summary>
public class RatingCalibratorTests
{
    [Fact]
    public void No_pairs_is_the_strict_no_op()
    {
        var calibrator = RatingCalibrator.Fit([]);

        Assert.Equal(CalibrationKind.Identity, calibrator.Kind);
        Assert.Equal(6.4d, calibrator.Calibrate(6.4d), 9);
        Assert.Equal(0d, calibrator.Delta(6.4d), 9);
    }

    [Fact]
    public void A_single_pair_shifts_the_level_and_nothing_else()
    {
        // One observation cannot identify a slope; it can only say "you were 2 points high here".
        var calibrator = RatingCalibrator.Fit([new CalibrationPair(Guid.NewGuid(), 8d, 6d)]);

        Assert.Equal(CalibrationKind.BiasShift, calibrator.Kind);
        Assert.Equal(6d, calibrator.Calibrate(8d), 9);
        Assert.Equal(4d, calibrator.Calibrate(6d), 9); // same shift, applied everywhere
    }

    [Fact]
    public void Under_ten_pairs_falls_back_to_a_bias_shift()
    {
        var pairs = Enumerable.Range(0, 9)
            .Select(i => new CalibrationPair(Guid.NewGuid(), 5d + (i * 0.4d), 4d + (i * 0.4d)))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.BiasShift, calibrator.Kind);
        Assert.Equal(RatingCalibrator.AffineMinPairs - 1, calibrator.PairCount);
        Assert.Equal(6d, calibrator.Calibrate(7d), 6); // a clean −1 everywhere
    }

    [Fact]
    public void Ten_to_twenty_nine_pairs_fit_a_line()
    {
        // y = 0.5x + 2, exactly — a slope a bias shift could not represent.
        var pairs = Enumerable.Range(0, 20)
            .Select(i => 3d + (i * 0.3d))
            .Select(x => new CalibrationPair(Guid.NewGuid(), x, (0.5d * x) + 2d))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.Affine, calibrator.Kind);
        Assert.Equal(5d, calibrator.Calibrate(6d), 6);
        Assert.Equal(6d, calibrator.Calibrate(8d), 6);
    }

    [Fact]
    public void Thirty_or_more_pairs_fit_a_shape()
    {
        var pairs = OverPredicting(60);

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.Isotonic, calibrator.Kind);
        Assert.Equal(60, calibrator.PairCount);
    }

    [Fact]
    public void An_over_predicting_distribution_is_pulled_down_toward_truth()
    {
        // The founder's live shape: the model reads high, and worse the higher it goes.
        var pairs = OverPredicting(120);
        var calibrator = RatingCalibrator.Fit(pairs);

        var before = pairs.Average(p => Math.Abs(p.Predicted - p.Actual));
        var after = pairs.Average(p => Math.Abs(calibrator.Calibrate(p.Predicted) - p.Actual));
        var biasBefore = pairs.Average(p => p.Predicted - p.Actual);
        var biasAfter = pairs.Average(p => calibrator.Calibrate(p.Predicted) - p.Actual);

        Assert.True(after < before, $"calibration must reduce MAE ({after} vs {before}).");
        Assert.True(Math.Abs(biasAfter) < Math.Abs(biasBefore) / 2d,
            $"calibration must more than halve the level bias ({biasAfter} vs {biasBefore}).");
        Assert.True(calibrator.Calibrate(8.5d) < 8.5d, "an over-predicting fit must map a high score DOWN.");
    }

    [Fact]
    public void The_map_is_monotone_non_decreasing_everywhere()
    {
        var calibrator = RatingCalibrator.Fit(OverPredicting(200));

        // Swept well outside the fitted range on both sides: the extrapolation must hold the
        // property too, or the hero slot (the very top of the distribution) is where it breaks.
        var previous = double.NegativeInfinity;
        for (var x = -2d; x <= 12d; x += 0.01d)
        {
            var y = calibrator.Calibrate(x);
            Assert.True(y >= previous - 1e-9, $"calibration inverted at {x} ({y} < {previous}).");
            previous = y;
        }
    }

    [Fact]
    public void Ties_on_the_predicted_value_map_to_one_output()
    {
        // Real data is rounded to 2dp, so exact ties are the norm, not an edge case. The same input
        // must never produce two different outputs.
        var pairs = new List<CalibrationPair>();
        for (var i = 0; i < 40; i++)
        {
            var predicted = 6d + (i % 4 * 0.5d);       // only four distinct values, ten of each
            var actual = predicted - 1d + (i % 3 * 0.5d); // noisy actuals on each
            pairs.Add(new CalibrationPair(Guid.NewGuid(), predicted, actual));
        }

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(calibrator.Calibrate(6.5d), calibrator.Calibrate(6.5d));
        Assert.True(calibrator.Calibrate(7.5d) >= calibrator.Calibrate(6d));
    }

    [Fact]
    public void All_pairs_sharing_one_prediction_degrade_to_a_bias_shift()
    {
        // The degenerate case a saturated head produces: every title scored the same number. There
        // is no shape to fit — and mapping every future prediction onto one constant would be far
        // worse than admitting only the level is known.
        var pairs = Enumerable.Range(0, 50)
            .Select(i => new CalibrationPair(Guid.NewGuid(), 4.2d, 5d + (i % 3)))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.BiasShift, calibrator.Kind);
        Assert.True(calibrator.Calibrate(9d) > calibrator.Calibrate(3d), "a degenerate fit must still preserve order.");
    }

    [Fact]
    public void Calibrating_an_already_calibrated_distribution_is_near_identity()
    {
        var pairs = OverPredicting(150);
        var first = RatingCalibrator.Fit(pairs);

        var calibrated = pairs
            .Select(p => new CalibrationPair(p.TitleId, first.Calibrate(p.Predicted), p.Actual))
            .ToList();
        var second = RatingCalibrator.Fit(calibrated);

        foreach (var x in new[] { 4d, 5d, 6d, 7d, 8d })
        {
            Assert.True(Math.Abs(second.Calibrate(x) - x) < 0.2d,
                $"a second pass moved {x} by {second.Calibrate(x) - x} — calibration must not compound.");
        }
    }

    [Fact]
    public void A_perfectly_calibrated_fit_is_exactly_identity_in_range()
    {
        var pairs = Enumerable.Range(0, 40)
            .Select(i => 2d + (i * 0.2d))
            .Select(x => new CalibrationPair(Guid.NewGuid(), x, x))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        foreach (var x in new[] { 2.5d, 4d, 6.7d, 9d })
        {
            Assert.Equal(x, calibrator.Calibrate(x), 6);
        }
    }

    [Fact]
    public void Output_is_clamped_to_the_rating_scale()
    {
        // A head that has run away, or a base score outside 1–10, must never produce a rating that
        // cannot exist. The demotion downstream already floors at 1; this keeps the two consistent.
        var pairs = Enumerable.Range(0, 40)
            .Select(i => 5d + (i * 0.1d))
            .Select(x => new CalibrationPair(Guid.NewGuid(), x, Math.Min(10d, x + 3d)))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(RatingCalibrator.MaxOutput, calibrator.Calibrate(50d));
        Assert.Equal(RatingCalibrator.MinOutput, calibrator.Calibrate(-20d));
    }

    /// <summary>
    /// A synthetic version of the founder's live miscalibration: the model reads roughly a point
    /// high at the bottom of its range and closer to two points high at the top, with noise.
    /// </summary>
    private static List<CalibrationPair> OverPredicting(int count)
    {
        var random = new Random(20260909);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var predicted = 4d + (6d * i / count);
                var drift = 1d + ((predicted - 4d) * 0.25d);
                var noise = (random.NextDouble() - 0.5d) * 1.2d;
                return new CalibrationPair(Guid.NewGuid(), predicted, Math.Clamp(predicted - drift + noise, 1d, 10d));
            })
            .ToList();
    }
}
