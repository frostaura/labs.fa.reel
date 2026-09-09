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
/// <item><b>It NEVER COLLAPSES.</b> The rule that put this class in its current shape: on the
/// founder's live 116 pairs (Pearson −0.004) the most accurate monotone map is very nearly a
/// constant, and a feed of identical numbers is not a feed. A map that cannot separate 6 from 9.5 is
/// a defect no matter what it does to MAE.</item>
/// <item><b>It is near-idempotent.</b> Fitting on an already-calibrated distribution must land close
/// to identity, or a second pass (or a retrain that already absorbed the level) would compound.</item>
/// <item><b>It pools only comparable pairs.</b> Two artifacts sitting at different levels must be put
/// on one level before they are pooled, or a retrain's level change reads as curvature.</item>
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
    public void A_single_pair_moves_nothing_at_all()
    {
        // One observation cannot identify a slope, and it cannot identify a level either — it is one
        // film. Shifting every number the user reads on that evidence is worse than doing nothing.
        var calibrator = RatingCalibrator.Fit([new CalibrationPair(Guid.NewGuid(), 8d, 6d)]);

        Assert.Equal(CalibrationKind.Identity, calibrator.Kind);
        Assert.Equal(8d, calibrator.Calibrate(8d), 9);
        Assert.Equal(0d, calibrator.Delta(6d), 9);
    }

    [Fact]
    public void Under_ten_pairs_is_the_no_op()
    {
        var pairs = Enumerable.Range(0, 9)
            .Select(i => new CalibrationPair(Guid.NewGuid(), 5d + (i * 0.4d), 4d + (i * 0.4d)))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.Identity, calibrator.Kind);
        Assert.Equal(RatingCalibrator.BiasShiftMinPairs - 1, calibrator.PairCount);
        Assert.Equal(7d, calibrator.Calibrate(7d), 6);
    }

    [Fact]
    public void Ten_to_twenty_nine_pairs_correct_the_level_only()
    {
        // A clean y = x − 1 over twenty pairs. There is plenty of evidence for the LEVEL and none at
        // all for a shape, so the answer is a shift — and the shift is exact.
        var pairs = Enumerable.Range(0, 20)
            .Select(i => 3d + (i * 0.3d))
            .Select(x => new CalibrationPair(Guid.NewGuid(), x, x - 1d))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.BiasShift, calibrator.Kind);
        Assert.Equal(5d, calibrator.Calibrate(6d), 6);
        Assert.Equal(7d, calibrator.Calibrate(8d), 6); // slope 1: two points apart, still two apart
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

    // ── The collapse, and the rules that stop it ────────────────────────────────────────

    [Fact]
    public void A_signal_free_account_gets_a_bias_shift_and_keeps_its_slope()
    {
        // THE CASE THIS POLICY EXISTS FOR. 116 pairs — the founder's live count — in which the
        // predictions carry no ordering information whatsoever. Fitting a shape here produced
        // 5 → 6.0, 6 → 6.0, 6.5–9.5 → 6.25, 10 → 6.8: statistically the best monotone map available,
        // and a product that prints one number on every card and can never reach the 7.5 hero floor.
        var pairs = SignalFree(116);

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.BiasShift, calibrator.Kind);
        Assert.Equal(116, calibrator.PairCount);
        Assert.True(Math.Abs(calibrator.Correlation) < RatingCalibrator.MinRankCorrelation,
            $"the fixture must be genuinely signal-free (rho was {calibrator.Correlation}).");

        // The property the founder actually cares about: two different titles still show two
        // different numbers, across the whole working range.
        var range = calibrator.Calibrate(9.5d) - calibrator.Calibrate(5d);
        Assert.True(range >= 3d, $"a signal-free account must keep its spread (range was {range}).");
    }

    [Fact]
    public void A_signal_free_account_still_has_its_level_corrected()
    {
        var pairs = SignalFree(116);
        var calibrator = RatingCalibrator.Fit(pairs);

        var biasBefore = pairs.Average(p => p.Predicted - p.Actual);
        var biasAfter = pairs.Average(p => calibrator.Calibrate(p.Predicted) - p.Actual);

        Assert.True(Math.Abs(biasBefore) > 0.5d, "the fixture must actually be miscalibrated in level.");
        Assert.True(Math.Abs(biasAfter) < 0.01d, $"the shift must remove the level bias ({biasAfter}).");
    }

    [Fact]
    public void A_real_rank_signal_earns_the_shape()
    {
        var pairs = NoisySignal(200);

        var calibrator = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.Isotonic, calibrator.Kind);
        Assert.True(calibrator.Correlation >= RatingCalibrator.MinRankCorrelation,
            $"the fixture must carry usable rank signal (rho was {calibrator.Correlation}).");

        var range = calibrator.Calibrate(RatingCalibrator.RangeProbeHigh) - calibrator.Calibrate(RatingCalibrator.RangeProbeLow);
        Assert.True(range >= RatingCalibrator.MinCalibratedRange,
            $"a shape earned on real signal must clear the slope guard on its own (range was {range}).");
    }

    [Fact]
    public void A_flat_shape_is_blended_back_up_to_a_usable_slope()
    {
        // Real ordering — the correlation gate passes — but the outcomes barely move with the
        // prediction, so PAV lands on a nearly flat map. Accurate, unusable. The guard averages it
        // with the bias shift, which is a PRODUCT decision: a card must not show a constant.
        var pairs = ShallowSignal(120);

        var flat = RatingCalibrator.Fit(pairs, activeArtifactId: null, CalibratorMode.Isotonic);
        var guarded = RatingCalibrator.Fit(pairs);

        Assert.Equal(CalibrationKind.Isotonic, flat.Kind);
        var flatRange = flat.Calibrate(RatingCalibrator.RangeProbeHigh) - flat.Calibrate(RatingCalibrator.RangeProbeLow);
        Assert.True(flatRange < RatingCalibrator.MinCalibratedRange,
            $"the fixture must actually trip the guard (unguarded range was {flatRange}).");

        Assert.Equal(CalibrationKind.IsotonicBlend, guarded.Kind);
        Assert.True(guarded.Correlation >= RatingCalibrator.MinRankCorrelation);

        var range = guarded.Calibrate(RatingCalibrator.RangeProbeHigh) - guarded.Calibrate(RatingCalibrator.RangeProbeLow);
        Assert.True(range >= RatingCalibrator.MinCalibratedRange,
            $"the blend must restore a usable slope (range was {range}).");

        // The trade is deliberate and is declared: differentiation is bought with accuracy.
        Assert.True(guarded.MaeAfter >= flat.MaeAfter - 1e-9,
            "the blend is not claimed to be more accurate than the shape it replaced.");

        // A blend is still a blend — it must sit between its two components everywhere it is probed.
        var shift = RatingCalibrator.Fit(pairs, activeArtifactId: null, CalibratorMode.Shift);
        foreach (var x in new[] { 6d, 7d, 8d, 9d })
        {
            var low = Math.Min(flat.Calibrate(x), shift.Calibrate(x));
            var high = Math.Max(flat.Calibrate(x), shift.Calibrate(x));
            Assert.InRange(guarded.Calibrate(x), low - 1e-9, high + 1e-9);
        }
    }

    [Fact]
    public void The_blend_is_still_monotone_non_decreasing()
    {
        var calibrator = RatingCalibrator.Fit(ShallowSignal(120));
        Assert.Equal(CalibrationKind.IsotonicBlend, calibrator.Kind);

        var previous = double.NegativeInfinity;
        for (var x = -2d; x <= 12d; x += 0.01d)
        {
            var y = calibrator.Calibrate(x);
            Assert.True(y >= previous - 1e-9, $"the blend inverted at {x} ({y} < {previous}).");
            previous = y;
        }
    }

    // ── The CALIBRATOR_MODE override ────────────────────────────────────────────────────

    [Fact]
    public void Mode_off_displays_the_model_untouched()
    {
        var calibrator = RatingCalibrator.Fit(OverPredicting(200), activeArtifactId: null, CalibratorMode.Off);

        Assert.Equal(CalibrationKind.Identity, calibrator.Kind);
        Assert.Equal(200, calibrator.PairCount);
        Assert.Equal(0d, calibrator.Delta(8.5d), 9);
        // The metrics are still reported, so "off" can be compared against the alternatives on one page.
        Assert.Equal(calibrator.MaeBefore, calibrator.MaeAfter, 9);
    }

    [Fact]
    public void Mode_shift_refuses_a_shape_even_where_one_is_earned()
    {
        var pairs = NoisySignal(200);
        Assert.Equal(CalibrationKind.Isotonic, RatingCalibrator.Fit(pairs).Kind);

        var pinned = RatingCalibrator.Fit(pairs, activeArtifactId: null, CalibratorMode.Shift);

        Assert.Equal(CalibrationKind.BiasShift, pinned.Kind);
        Assert.Equal(2d, pinned.Calibrate(8d) - pinned.Calibrate(6d), 9); // slope 1, by construction
    }

    [Fact]
    public void Mode_isotonic_forces_the_shape_past_the_correlation_gate()
    {
        // The escape hatch that reproduces the collapse on purpose, so it can be looked at.
        var pinned = RatingCalibrator.Fit(SignalFree(116), activeArtifactId: null, CalibratorMode.Isotonic);

        Assert.Equal(CalibrationKind.Isotonic, pinned.Kind);
        Assert.True(pinned.Correlation < RatingCalibrator.MinRankCorrelation);
        Assert.True(
            pinned.Calibrate(RatingCalibrator.RangeProbeHigh) - pinned.Calibrate(RatingCalibrator.RangeProbeLow)
                < RatingCalibrator.MinCalibratedRange,
            "forcing the shape past the gate is expected to collapse — that is what the gate is for.");
    }

    [Fact]
    public void Mode_never_manufactures_evidence_that_is_not_there()
    {
        // Even pinned, four pairs cannot support a shape. The override picks the RULE, not the data.
        var pairs = Enumerable.Range(0, 4)
            .Select(i => new CalibrationPair(Guid.NewGuid(), 6d + i, 4d + i))
            .ToList();

        Assert.Equal(CalibrationKind.BiasShift, RatingCalibrator.Fit(pairs, null, CalibratorMode.Isotonic).Kind);
        Assert.Equal(CalibrationKind.BiasShift, RatingCalibrator.Fit(pairs, null, CalibratorMode.Shift).Kind);
        Assert.Equal(CalibrationKind.Identity, RatingCalibrator.Fit(pairs).Kind);
    }

    // ── Artifact comparability ──────────────────────────────────────────────────────────

    [Fact]
    public void The_active_artifact_wins_outright_when_it_has_enough_pairs()
    {
        // 40 pairs from the model in service, and 60 from a retired one that was two points high.
        // The retired model's pairs describe a model nobody is being shown any more.
        var active = Guid.NewGuid();
        var retired = Guid.NewGuid();
        var pairs = Levelled(active, offset: 0d, count: 40)
            .Concat(Levelled(retired, offset: 2d, count: 60))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs, active);

        Assert.Equal(40, calibrator.PairCount);
        // Active-artifact pairs are y = x − 1 exactly, so the map is too.
        Assert.Equal(6d, calibrator.Calibrate(7d), 6);
    }

    [Fact]
    public void Pairs_from_two_artifacts_are_de_meaned_onto_the_active_level_before_pooling()
    {
        // Neither artifact clears the 30-pair bar on its own, so both are kept — but the retired one
        // sat two points higher for the same taste, and pooling that raw would read as curvature.
        var active = Guid.NewGuid();
        var retired = Guid.NewGuid();
        var pairs = Levelled(active, offset: 0d, count: 15)
            .Concat(Levelled(retired, offset: 2d, count: 15))
            .ToList();

        var levelled = RatingCalibrator.Fit(pairs, active);

        Assert.Equal(30, levelled.PairCount);
        Assert.Equal(CalibrationKind.Isotonic, levelled.Kind);
        // De-meaned, both artifacts describe the same y = x − 1 relationship on the active level.
        Assert.Equal(6d, levelled.Calibrate(7d), 6);
        Assert.Equal(7d, levelled.Calibrate(8d), 6);

        // The same pairs with no artifact recorded are pooled raw, and the contradiction at the
        // overlap flattens the map — which is exactly the damage the de-meaning exists to prevent.
        var pooled = RatingCalibrator.Fit(pairs.Select(p => p with { ArtifactId = null }).ToList());
        Assert.True(pooled.MaeAfter > levelled.MaeAfter,
            $"pooling two levels raw must fit worse ({pooled.MaeAfter} vs {levelled.MaeAfter}).");
    }

    [Fact]
    public void An_artifact_with_too_few_pairs_to_level_is_left_where_it_stands()
    {
        // One pair per artifact — the shape every account had before scoring settled down. Its
        // "artifact mean" IS the pair, so de-meaning would map all fifty onto one value and
        // manufacture the constant map. They must be pooled untouched instead.
        var pairs = Enumerable.Range(0, 50)
            .Select(i => new CalibrationPair(Guid.NewGuid(), 4d + (i * 0.1d), 3d + (i * 0.1d), Guid.NewGuid()))
            .ToList();

        var calibrator = RatingCalibrator.Fit(pairs, Guid.NewGuid());

        Assert.Equal(50, calibrator.PairCount);
        Assert.Equal(CalibrationKind.Isotonic, calibrator.Kind);
        Assert.Equal(5d, calibrator.Calibrate(6d), 6);
    }

    // ── Reported diagnostics ────────────────────────────────────────────────────────────

    [Fact]
    public void The_fit_reports_what_the_ui_needs_to_show_a_band()
    {
        var calibrator = RatingCalibrator.Fit(SignalFree(116));

        Assert.Equal(calibrator.MaeAfter, calibrator.ExpectedAbsoluteError, 9);
        Assert.True(calibrator.MaeAfter < calibrator.MaeBefore,
            $"the shift must still improve MAE ({calibrator.MaeAfter} vs {calibrator.MaeBefore}).");
        Assert.True(calibrator.ExpectedAbsoluteError > 0.5d,
            "a signal-free account's band must be honest about how wide it is.");
        Assert.Equal(0d, RatingCalibrator.Identity.ExpectedAbsoluteError, 9);
    }

    /// <summary>
    /// The founder's live case: 116 pairs whose predictions order the outcomes exactly as well as a
    /// coin does. The actuals cycle on a period of four against a strictly increasing prediction, so
    /// the rank correlation is ZERO by construction rather than by luck of a seed — and the level is
    /// off by about a point, so there is still something real for the calibrator to fix.
    /// </summary>
    private static List<CalibrationPair> SignalFree(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new CalibrationPair(
                Guid.NewGuid(),
                5d + (5d * i / count),
                new[] { 7d, 5d, 8d, 6d }[i % 4]))
            .ToList();

    /// <summary>
    /// A weak but real relationship, of roughly the strength the model's own holdout shows: the
    /// ordering is informative, the individual number is not.
    /// </summary>
    private static List<CalibrationPair> NoisySignal(int count)
    {
        var random = new Random(20260909);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var predicted = 4d + (6d * i / count);
                var noise = (random.NextDouble() - 0.5d) * 4d;
                return new CalibrationPair(Guid.NewGuid(), predicted, Math.Clamp((0.9d * predicted) + noise, 1d, 10d));
            })
            .ToList();
    }

    /// <summary>
    /// Ordering that passes the correlation gate attached to outcomes that barely move — five points
    /// of prediction buying three quarters of a point of rating. PAV fits this faithfully and the
    /// result is unusable on a card.
    /// </summary>
    private static List<CalibrationPair> ShallowSignal(int count)
    {
        var random = new Random(20260910);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var predicted = 5d + (5d * i / count);
                var noise = (random.NextDouble() - 0.5d) * 0.3d;
                return new CalibrationPair(Guid.NewGuid(), predicted, (0.15d * (predicted - 5d)) + 6d + noise);
            })
            .ToList();
    }

    /// <summary>
    /// <paramref name="count"/> pairs from one artifact sitting <paramref name="offset"/> points
    /// above the active level, all describing the same underlying taste (actual = active-level
    /// prediction − 1).
    /// </summary>
    private static List<CalibrationPair> Levelled(Guid artifactId, double offset, int count) =>
        Enumerable.Range(0, count)
            .Select(i => 6d + (2d * i / count))
            .Select(x => new CalibrationPair(Guid.NewGuid(), x + offset, x - 1d, artifactId))
            .ToList();

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
