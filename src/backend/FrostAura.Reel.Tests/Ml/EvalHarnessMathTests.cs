using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The harness's own arithmetic and its SPLIT LOGIC — the thinnest coverage in the repository
/// before the 2026-09 instrument repair. Three facts used to live here (precision@k, median,
/// Spearman) while the 80% split index, the holdout triple-filter, the LowSample fallback, the ±99
/// clamp, the gate comparison and the ConfigHash ledger were all untested. Every one of those is a
/// place where a silent change would move the number the kill criterion reads.
/// </summary>
public class EvalHarnessMathTests
{
    private static readonly DateTime Origin = new(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Split index and the split itself ────────────────────────────────────────────────

    [Fact]
    public void Split_index_lands_on_the_boundary_element_and_never_off_the_end()
    {
        // 100 labels at 80% → index 80, i.e. 80 elements strictly before it.
        Assert.Equal(80, EvalHarness.SplitIndex(100, 0.80d));
        Assert.Equal(60, EvalHarness.SplitIndex(100, 0.60d));

        // Truncation, not rounding: 0.8 × 7 = 5.6 → 5.
        Assert.Equal(5, EvalHarness.SplitIndex(7, 0.80d));

        // A fraction of 1.0 would index one past the end; the clamp is what stops the harness
        // throwing on a degenerate origin rather than reporting an empty holdout.
        Assert.Equal(9, EvalHarness.SplitIndex(10, 1.0d));
        Assert.Equal(0, EvalHarness.SplitIndex(0, 0.80d));
        Assert.Equal(0, EvalHarness.SplitIndex(1, 0.80d));
    }

    [Fact]
    public void Split_puts_everything_at_or_before_the_cut_in_train_and_the_rest_in_holdout()
    {
        var labels = Enumerable.Range(0, 10)
            .Select(i => new EvalHarness.SplitLabel(Guid.NewGuid(), (short)(i + 1), Origin.AddDays(i)))
            .ToList();

        var split = EvalHarness.Split(labels, 0.80d, new HashSet<Guid>());

        // Index 8 is the cut; train is "≤ splitAt", so the boundary element itself is TRAIN.
        Assert.Equal(Origin.AddDays(8), split.SplitAt);
        Assert.Equal(9, split.Train.Count);
        Assert.Single(split.Holdout);
        Assert.Equal(10, split.Holdout[0].Rating);
    }

    [Fact]
    public void Holdout_is_triple_filtered_newest_not_in_train_and_not_already_watched()
    {
        var repeated = Guid.NewGuid();
        var watched = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var alsoClean = Guid.NewGuid();

        var labels = new List<EvalHarness.SplitLabel>();
        // Eight old ratings, one of them on the title that gets re-rated later.
        for (var i = 0; i < 7; i++)
        {
            labels.Add(new EvalHarness.SplitLabel(Guid.NewGuid(), 6, Origin.AddDays(i)));
        }

        labels.Add(new EvalHarness.SplitLabel(repeated, 5, Origin.AddDays(7)));
        // Newer than the cut: one already-in-train title, one watched before the split, two clean.
        labels.Add(new EvalHarness.SplitLabel(repeated, 9, Origin.AddDays(20)));
        labels.Add(new EvalHarness.SplitLabel(watched, 9, Origin.AddDays(21)));
        labels.Add(new EvalHarness.SplitLabel(clean, 8, Origin.AddDays(22)));
        labels.Add(new EvalHarness.SplitLabel(alsoClean, 4, Origin.AddDays(23)));

        var split = EvalHarness.Split(labels, 0.60d, new HashSet<Guid> { watched });

        Assert.Equal(Origin.AddDays(7), split.SplitAt);

        var holdoutIds = split.Holdout.Select(h => h.TitleId).ToHashSet();
        Assert.Equal(2, holdoutIds.Count);
        Assert.Contains(clean, holdoutIds);
        Assert.Contains(alsoClean, holdoutIds);
        // Already in train — its later 9 would be a label the split model was fitted on.
        Assert.DoesNotContain(repeated, holdoutIds);
        // Already watched at the split — the product could never have recommended it.
        Assert.DoesNotContain(watched, holdoutIds);
    }

    [Fact]
    public void Multiple_holdout_ratings_of_one_title_collapse_to_the_highest()
    {
        var title = Guid.NewGuid();
        var labels = Enumerable.Range(0, 18)
            .Select(i => new EvalHarness.SplitLabel(Guid.NewGuid(), 6, Origin.AddDays(i)))
            .ToList();
        labels.Add(new EvalHarness.SplitLabel(title, 4, Origin.AddDays(20)));
        labels.Add(new EvalHarness.SplitLabel(title, 9, Origin.AddDays(21)));

        // 20 labels at 60% → the cut is day 12, so both ratings of `title` are on the holdout side.
        var split = EvalHarness.Split(labels, 0.60d, new HashSet<Guid>());

        var forTitle = split.Holdout.Where(h => h.TitleId == title).ToList();
        Assert.Single(forTitle);
        Assert.Equal(9, forTitle[0].Rating);
    }

    // ── The reason FirstRatedAt exists ──────────────────────────────────────────────────

    [Fact]
    public void A_re_rate_moves_a_row_across_the_split_on_RatedAt_and_never_on_FirstRatedAt()
    {
        // 100 ratings over four years. One of them — an old one, firmly in the train half — is
        // re-rated today. On the MUTABLE axis (Trakt's RatedAt, overwritten by every ratings walk)
        // that row jumps into the holdout and takes a label with it that the split model could not
        // have known. On FirstRatedAt it cannot move, which is the whole point of the column.
        var rerated = Guid.NewGuid();
        var firstRated = new List<EvalHarness.SplitLabel>();
        for (var i = 0; i < 100; i++)
        {
            var id = i == 10 ? rerated : Guid.NewGuid();
            firstRated.Add(new EvalHarness.SplitLabel(id, (short)(4 + (i % 7)), Origin.AddDays(i * 14)));
        }

        var mutated = firstRated
            .Select(l => l.TitleId == rerated ? l with { Axis = Origin.AddDays(100 * 14) } : l)
            .OrderBy(l => l.Axis)
            .ToList();

        var stable = EvalHarness.Split(firstRated, 0.80d, new HashSet<Guid>());
        var moved = EvalHarness.Split(mutated, 0.80d, new HashSet<Guid>());

        Assert.Contains(rerated, stable.Train.Select(t => t.TitleId));
        Assert.DoesNotContain(rerated, stable.Holdout.Select(h => h.TitleId));

        Assert.DoesNotContain(rerated, moved.Train.Select(t => t.TitleId));
        Assert.Contains(rerated, moved.Holdout.Select(h => h.TitleId));
    }

    [Fact]
    public void The_FirstRatedAt_backfill_keeps_the_train_and_holdout_counts_within_five_percent()
    {
        // The migration backfills FirstRatedAt := RatedAt, so iteration 13's split must land where
        // iteration 12's did. Shaped like the live account: ~2,000 ratings over eight years with a
        // heavy recent tail, and the handful of in-app re-rates that make the two axes differ at
        // all (dossier 05: 41 of 2,044 rows carry Source=Reel).
        var rng = new Random(4242);
        var backfilled = new List<EvalHarness.SplitLabel>();
        for (var i = 0; i < 2_000; i++)
        {
            // Squared quantile → density rising toward the present: ~45% of the history in the last
            // two of eight years, which is what the live cadence looks like.
            var q = Math.Pow(i / 2_000d, 0.55d);
            var day = (int)(q * 365 * 8);
            backfilled.Add(new EvalHarness.SplitLabel(Guid.NewGuid(), (short)(1 + rng.Next(10)), Origin.AddDays(day)));
        }

        backfilled = [.. backfilled.OrderBy(l => l.Axis)];

        // The pre-repair axis: the same rows, with 41 of them re-rated "today".
        var mutable = backfilled
            .Select((l, i) => i % 49 == 0 ? l with { Axis = Origin.AddDays(365 * 8 + 1) } : l)
            .OrderBy(l => l.Axis)
            .ToList();

        var onFirstRatedAt = EvalHarness.Split(backfilled, 0.80d, new HashSet<Guid>());
        var onRatedAt = EvalHarness.Split(mutable, 0.80d, new HashSet<Guid>());

        AssertWithinFivePercent(onRatedAt.Train.Count, onFirstRatedAt.Train.Count, "train rows");
        AssertWithinFivePercent(onRatedAt.Holdout.Count, onFirstRatedAt.Holdout.Count, "holdout rows");

        // And the holdout is a real one, not an artefact of a degenerate split.
        Assert.InRange(onFirstRatedAt.Holdout.Count, 300, 500);
    }

    private static void AssertWithinFivePercent(int reference, int actual, string what)
    {
        var tolerance = Math.Max(1d, reference * 0.05d);
        Assert.True(
            Math.Abs(actual - reference) <= tolerance,
            $"{what}: {actual} is more than 5% from the reference {reference}");
    }

    // ── Threshold, clamp and gate arithmetic ────────────────────────────────────────────

    [Fact]
    public void The_frozen_threshold_is_the_top_quintile_boundary_with_eight_as_a_floor()
    {
        // The live distribution (dossier 05, 2,044 whole-title ratings): p80 lands on 8.
        var live = new List<short>();
        void Add(short rating, int count) => live.AddRange(Enumerable.Repeat(rating, count));
        Add(1, 3); Add(2, 17); Add(3, 55); Add(4, 136); Add(5, 146);
        Add(6, 407); Add(7, 628); Add(8, 429); Add(9, 205); Add(10, 18);
        Assert.Equal(8m, EvalHarness.TopQuintileThreshold(live));

        // A generous rater's top quintile is 9 — "loved" moves UP with the user, never down.
        // (75 eights then 25 nines: the 80th-percentile element is the 80th, which is a nine.)
        Assert.Equal(9m, EvalHarness.TopQuintileThreshold([.. Enumerable.Repeat((short)8, 75).Concat(Enumerable.Repeat((short)9, 25))]));

        // Exactly on the boundary the 80th element is still an eight — the percentile is the value
        // AT the boundary, not the first value above it.
        Assert.Equal(8m, EvalHarness.TopQuintileThreshold([.. Enumerable.Repeat((short)8, 80).Concat(Enumerable.Repeat((short)9, 20))]));

        // A harsh rater's top quintile is 5, but the floor holds: loved, not merely tolerated.
        Assert.Equal(8m, EvalHarness.TopQuintileThreshold([.. Enumerable.Repeat((short)3, 80).Concat(Enumerable.Repeat((short)5, 20))]));

        Assert.Equal(8m, EvalHarness.TopQuintileThreshold([]));
    }

    [Fact]
    public void Relative_lift_is_clamped_to_plus_or_minus_ninety_nine()
    {
        Assert.Equal(0.20m, EvalHarness.RelativeLift(0.6m, 0.5m));
        Assert.Equal(-1.0m, EvalHarness.RelativeLift(0m, 0.5m));

        // A zero baseline with a non-zero model is the sentinel, not infinity.
        Assert.Equal(99m, EvalHarness.RelativeLift(0.1m, 0m));

        // Both zero is "no information", not a win.
        Assert.Equal(0m, EvalHarness.RelativeLift(0m, 0m));

        // And a ratio that would exceed the sentinel is pulled back to it.
        Assert.Equal(99m, EvalHarness.RelativeLift(1000m, 0.0001m));
    }

    [Fact]
    public void The_gate_needs_a_ci_that_excludes_zero_and_a_lift_that_clears_twenty_percent()
    {
        // Exactly at the mean-lift threshold passes; a hair under does not. >= not >.
        Assert.True(EvalHarness.GatePassed(0.001m, 0.20m));
        Assert.False(EvalHarness.GatePassed(0.001m, 0.1999m));

        // A CI that merely touches zero fails — the lower bound must EXCLUDE it.
        Assert.False(EvalHarness.GatePassed(0m, 0.50m));
        Assert.False(EvalHarness.GatePassed(-0.01m, 5m));

        // Both clauses are required: a huge real effect with no margin, and a big margin that
        // could be noise, each fail on their own.
        Assert.False(EvalHarness.GatePassed(0.30m, 0.05m));

        Assert.True(EvalHarness.LegacyGatePassed(0.20m));
        Assert.False(EvalHarness.LegacyGatePassed(0.1999m));
    }

    // ── ConfigHash: the iteration ledger's key ──────────────────────────────────────────

    [Fact]
    public void Config_hash_is_stable_for_one_config_and_moves_with_the_schema_version()
    {
        const string hyperparams = """{"Trees":200,"Leaves":20}""";

        Assert.Equal(EvalHarness.ConfigHash(hyperparams), EvalHarness.ConfigHash(hyperparams));
        Assert.NotEqual(EvalHarness.ConfigHash(hyperparams), EvalHarness.ConfigHash("""{"Trees":300,"Leaves":20}"""));

        // Pin the payload shape, which is what makes a FeatureSchema bump move the hash — and, just
        // as importantly, what proves coverage statistics are NOT in it. Coverage grew with every
        // new rating, so hashing it minted a fresh iteration on every nightly drift retrain and
        // pushed the ledger to "iteration 12" against a three-iteration kill criterion.
        Assert.Equal(Sha16($"{FeatureSchema.Version}|{hyperparams}"), EvalHarness.ConfigHash(hyperparams));
        Assert.NotEqual(Sha16($"{FeatureSchema.Version + 1}|{hyperparams}"), EvalHarness.ConfigHash(hyperparams));
    }

    private static string Sha16(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];

    // ── Ranking metrics ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Precision_at_k_counts_loved_hits_in_the_top()
    {
        short[] ranked = [9, 8, 6, 10, 5, 7, 8, 4, 3, 2, 10, 10];
        // Top-10 of the ranking, threshold 8 → hits: 9, 8, 10, 8 = 4.
        Assert.Equal(0.4m, EvalHarness.PrecisionAtK(ranked, threshold: 8m, k: 10));
        // Shorter pool than k normalizes by pool size.
        Assert.Equal(0.5m, EvalHarness.PrecisionAtK([9, 5], threshold: 8m, k: 10));
        Assert.Equal(0m, EvalHarness.PrecisionAtK([], threshold: 8m, k: 10));
    }

    [Fact]
    public void Graded_relevance_counts_the_threshold_itself_as_the_first_rung()
    {
        Assert.Equal(0d, EvalHarness.Relevance(7, 8m));
        Assert.Equal(1d, EvalHarness.Relevance(8, 8m));
        Assert.Equal(2d, EvalHarness.Relevance(9, 8m));
        Assert.Equal(3d, EvalHarness.Relevance(10, 8m));
        // Everything below the line is equally irrelevant — a 1 is not "more negative" than a 6.
        Assert.Equal(0d, EvalHarness.Relevance(1, 8m));
    }

    [Fact]
    public void Ndcg_is_one_on_a_perfect_ranking_lower_when_reversed_and_one_under_full_ties()
    {
        short[] ratings = [10, 9, 8, 8, 7, 6, 5, 4, 3, 2, 1, 1];

        Assert.Equal(1m, EvalHarness.NdcgAtK(ratings, 8m, 10));

        var reversed = EvalHarness.NdcgAtK(ratings.Reverse(), 8m, 10);
        Assert.InRange(reversed, 0m, 0.5m);
        Assert.True(reversed < 1m);

        // Every title equally relevant → any order is ideal.
        Assert.Equal(1m, EvalHarness.NdcgAtK([9, 9, 9, 9, 9, 9, 9, 9, 9, 9], 8m, 10));

        // Nothing above the threshold → the ideal DCG is zero and NDCG is defined as 0, not NaN.
        Assert.Equal(0m, EvalHarness.NdcgAtK([5, 4, 3, 2, 1], 8m, 10));
        Assert.Equal(0m, EvalHarness.NdcgAtK([], 8m, 10));

        // A single loved title ranked first beats the same title ranked last.
        var first = EvalHarness.NdcgAtK([10, 3, 3, 3, 3, 3, 3, 3, 3, 3], 8m, 10);
        var last = EvalHarness.NdcgAtK([3, 3, 3, 3, 3, 3, 3, 3, 3, 10], 8m, 10);
        Assert.Equal(1m, first);
        Assert.True(last < first);
    }

    [Fact]
    public void Spearman_detects_monotone_agreement_and_disagreement()
    {
        double[] x = [1, 2, 3, 4, 5];
        Assert.Equal(1d, EvalHarness.SpearmanRho(x, [10, 20, 30, 40, 50]), 3);
        Assert.Equal(-1d, EvalHarness.SpearmanRho(x, [50, 40, 30, 20, 10]), 3);

        // Ties share average ranks and keep rho in [-1, 1].
        var tied = EvalHarness.SpearmanRho([1, 2, 2, 3], [1, 2, 3, 4]);
        Assert.InRange(tied, 0.7d, 1d);

        Assert.Equal(0d, EvalHarness.SpearmanRho([1], [1]), 3);
    }

    [Fact]
    public void Spearman_standard_error_is_one_over_root_n_minus_three()
    {
        // At the live holdout size a rho of 0.14 is ~2.8 SE from zero — real, and far weaker than
        // the bare number reads. That is exactly why the SE is persisted next to it.
        Assert.Equal(0.0502d, EvalHarness.SpearmanStandardError(400)!.Value, 4);
        Assert.Null(EvalHarness.SpearmanStandardError(3));
        Assert.Null(EvalHarness.SpearmanStandardError(0));
    }

    [Fact]
    public void Median_handles_even_and_odd_counts()
    {
        Assert.Equal(7m, EvalHarness.Median([7f]));
        Assert.Equal(7.5m, EvalHarness.Median([7f, 8f]));
        Assert.Equal(8m, EvalHarness.Median([10f, 8f, 6f]));
        Assert.Equal(0m, EvalHarness.Median([]));
    }

    // ── The bootstrap CI, which is now the gate ─────────────────────────────────────────

    [Fact]
    public void Bootstrap_ci_excludes_zero_when_the_model_really_ranks_and_includes_it_when_it_does_not()
    {
        var rng = new Random(11);
        var lifted = new List<EvalHarness.HoldoutRow>();
        var null_ = new List<EvalHarness.HoldoutRow>();
        for (var i = 0; i < 200; i++)
        {
            var rating = (short)(rng.Next(10) < 3 ? 8 + rng.Next(3) : 1 + rng.Next(7));
            // A model that genuinely ranks: its score tracks the label. A baseline that does not.
            lifted.Add(new EvalHarness.HoldoutRow(rating, rating + (rng.NextDouble() * 0.5), rng.NextDouble()));
            // The null: BOTH rankers are noise, so the paired difference is centred on zero.
            null_.Add(new EvalHarness.HoldoutRow(rating, rng.NextDouble(), rng.NextDouble()));
        }

        var (low, high) = EvalHarness.BootstrapPairedNdcgCi(lifted, 8m, 10, EvalHarness.BootstrapResamples, EvalHarness.BootstrapSeed);
        Assert.True(low > 0m, $"a known-lift distribution should exclude zero, got [{low}, {high}]");
        Assert.True(high > low);

        var (nullLow, nullHigh) = EvalHarness.BootstrapPairedNdcgCi(null_, 8m, 10, EvalHarness.BootstrapResamples, EvalHarness.BootstrapSeed);
        Assert.True(nullLow <= 0m && nullHigh >= 0m, $"a null distribution should straddle zero, got [{nullLow}, {nullHigh}]");
    }

    [Fact]
    public void Bootstrap_ci_is_deterministic_for_a_given_seed()
    {
        var rows = Enumerable.Range(0, 60)
            .Select(i => new EvalHarness.HoldoutRow((short)(1 + (i % 10)), i * 0.37d % 1d, i * 0.11d % 1d))
            .ToList();

        var first = EvalHarness.BootstrapPairedNdcgCi(rows, 8m, 10, 500, 99);
        var second = EvalHarness.BootstrapPairedNdcgCi(rows, 8m, 10, 500, 99);
        Assert.Equal(first, second);

        // A different seed is allowed to differ; an empty pool must not throw.
        Assert.Equal((0m, 0m), EvalHarness.BootstrapPairedNdcgCi([], 8m, 10, 500, 99));
        Assert.Equal((0m, 0m), EvalHarness.BootstrapPairedNdcgCi(rows, 8m, 10, 0, 99));
    }

    // ── Calibration ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Expected_calibration_error_is_zero_when_predictions_match_and_grows_with_the_gap()
    {
        List<(double, short)> perfect = [(8d, 8), (6d, 6), (4d, 4), (9d, 9)];
        Assert.Equal(0m, EvalHarness.ExpectedCalibrationError(perfect, 10));

        // Every prediction two points high → ECE is exactly 2, whatever the bucketing.
        List<(double, short)> biased = [(8d, 6), (6d, 4), (4d, 2), (9d, 7)];
        Assert.Equal(2m, EvalHarness.ExpectedCalibrationError(biased, 10));

        // Errors that cancel WITHIN a bucket cancel — ECE measures bucket-level calibration, not
        // per-item accuracy, which is why MAE is persisted next to it rather than instead of it.
        List<(double, short)> cancelling = [(8d, 6), (8d, 10)];
        Assert.Equal(0m, EvalHarness.ExpectedCalibrationError(cancelling, 10));

        Assert.Equal(0m, EvalHarness.ExpectedCalibrationError([], 10));
        Assert.Equal(0m, EvalHarness.ExpectedCalibrationError(perfect, 0));
    }

    [Fact]
    public void Out_of_scale_predictions_are_clamped_into_the_label_range_not_dropped()
    {
        // A model that says 14.0 for a title the user rated 8 is 6 points wrong; silently dropping
        // it would report a better-calibrated model than exists.
        List<(double, short)> pairs = [(14d, 8)];
        Assert.Equal(2m, EvalHarness.ExpectedCalibrationError(pairs, 10));
    }

    // ── The user-visible caveat ─────────────────────────────────────────────────────────

    [Fact]
    public void The_popularity_caveat_carries_the_live_vector_length()
    {
        // It shipped "~86" to the user for months after the vector reached 92; v4 moved it to 124,
        // v5 to 132 and v6 to 137. Interpolated now, and pinned here so a schema bump cannot make it
        // a lie again — the literal below is the tripwire that forces this test to be re-read on
        // every bump, and it has now caught three of them.
        Assert.Contains(
            FeatureSchema.VectorLength.ToString(CultureInfo.InvariantCulture),
            EvalHarness.PopularityCaveat,
            StringComparison.Ordinal);
        Assert.DoesNotContain("~86", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
        Assert.DoesNotContain("92 features", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
        Assert.Equal(137, FeatureSchema.VectorLength);

        // And it still must not claim the leak is symmetric — the asymmetry runs against the model.
        Assert.Contains("NOT symmetric", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
    }
}
