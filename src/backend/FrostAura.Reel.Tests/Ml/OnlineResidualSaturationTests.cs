using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The residual head must stay a NUDGE on the base score, never a constant offset applied to the
/// whole catalogue.
///
/// Regression for the 2026-08-07 empty-feed defect. The head's SGD step used to be
/// <c>w += lr·sw·err·x</c> over RAW features, and <c>runtimeMinutes</c> is ~105. One
/// not-interested event on a title the base scored ~7 gives err ≈ −5, so that single weight moved
/// by 0.03·0.8·(−5)·105 ≈ −12.6 and pinned itself to the ±2 clamp immediately. With a handful of
/// scalars pinned, <c>dot(w, x)</c> ran to several hundred and <see cref="OnlineResidualModel.Residual"/>
/// returned the −3 rail for EVERY title. Every predicted rating in the product silently became
/// <c>base − 3</c>, the whole distribution topped out around 4.2, and the account's
/// MinPredictedRating filter could no longer be satisfied at any value the UI let the user pick —
/// the feed built to zero cards while hundreds of perfectly good candidates sat in the pool.
///
/// The v2 rule extends the same discipline to the BIAS, which v1 left on the raw update. That is
/// what the last two tests here are for: a free bias beside normalised weights is a level-only
/// learner with a ~100x larger step than anything next to it, and on the founder's live account it
/// duly converged to a constant −1.68 offset that became, by accident, the only calibration in the
/// product. Level is now <c>RatingCalibrator</c>'s job; the head must keep its hands off it.
///
/// These are pure arithmetic over the real feature schema — no database, no model artifact.
/// </summary>
public class OnlineResidualSaturationTests
{
    /// <summary>
    /// A vector shaped like a real one: the scalars carry their true magnitudes (runtime in MINUTES,
    /// affinities on the 0–10 rating scale), which is the whole point — a head that only ever sees
    /// unit-scaled features cannot reproduce the bug.
    /// </summary>
    private static float[] RealisticVector()
    {
        var v = new float[FeatureSchema.VectorLength];
        var scalars = FeatureSchema.Scalars;
        for (var i = 0; i < scalars.Length; i++)
        {
            v[i] = scalars[i] switch
            {
                "runtimeMinutes" => 105f,
                "releaseAgeLog" => 2.3f,
                "traktRating" or "contrarianAdjustedRating" => 7.4f,
                "traktVotesLog" => 9.1f,
                "tmdbPopularityLog" => 3.2f,
                "certificationOrdinal" => 3f,
                "userGenreAffinity" or "decadeAffinity" or "directorAffinity"
                    or "castAffinity" or "writerAffinity" => 7.1f,
                "lovedCastCount" => 2f,
                _ => 0.5f,
            };
        }

        return v;
    }

    [Fact]
    public void One_negative_event_does_not_saturate_the_output_clamp()
    {
        var vector = RealisticVector();
        var (weights, bias) = ApplyStep(new float[FeatureSchema.VectorLength], 0d, vector, target: 2d, sampleWeight: 0.8, baseScore: 7d);

        var residual = OnlineResidualModel.Residual(weights, bias, vector);

        // The pre-fix rule produced exactly -3 here — the rail. The head is allowed to push the score
        // DOWN (that is its job), just not to slam straight into its own bound on a single event.
        Assert.True(residual > -3d, $"a single event must not saturate the residual clamp (got {residual}).");
        Assert.True(residual < 0d, $"a strongly negative event should still push the score down (got {residual}).");
    }

    [Fact]
    public void No_weight_reaches_its_clamp_from_a_single_event()
    {
        var vector = RealisticVector();
        var (weights, _) = ApplyStep(new float[FeatureSchema.VectorLength], 0d, vector, target: 2d, sampleWeight: 0.8, baseScore: 7d);

        // runtimeMinutes is the canary: the largest-magnitude scalar in the schema, and the first one
        // the unnormalised rule used to pin.
        var runtimeIndex = Array.IndexOf(FeatureSchema.Scalars, "runtimeMinutes");
        Assert.True(Math.Abs(weights[runtimeIndex]) < 2f,
            $"runtimeMinutes weight hit the clamp on one event ({weights[runtimeIndex]}).");
        Assert.All(weights, w => Assert.True(Math.Abs(w) < 2f, $"a weight hit the clamp on one event ({w})."));
    }

    [Fact]
    public void A_run_of_negative_events_still_leaves_the_ranking_spread_intact()
    {
        // The failure that actually reached the founder was not "scores are low" — it was that EVERY
        // title collapsed onto the same clamped correction, so the head stopped distinguishing
        // titles at all. Two different titles must still receive different corrections after a
        // sustained run of negative feedback.
        var disliked = RealisticVector();
        var other = RealisticVector();
        var castIndex = Array.IndexOf(FeatureSchema.Scalars, "castAffinity");
        other[castIndex] = 2.0f; // a title by people the user does NOT rate highly

        var weights = new float[FeatureSchema.VectorLength];
        var bias = 0d;
        for (var i = 0; i < 10; i++)
        {
            (weights, bias) = ApplyStep(weights, bias, disliked, target: 2d, sampleWeight: 0.8, baseScore: 7d);
        }

        var a = OnlineResidualModel.Residual(weights, bias, disliked);
        var b = OnlineResidualModel.Residual(weights, bias, other);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Twenty_mixed_events_do_not_accumulate_a_level_offset()
    {
        // A realistic mixed stream that is NET NEGATIVE: the user's ratings average about a point
        // and a half below what the base model said. Under the v1 rule the free bias integrated
        // exactly this into a large constant offset — the −1.68 found in production. Under v2 the
        // bias is a constant-1 feature taking the same normalised step as every weight, so the head
        // absorbs the correction into SHAPE and leaves LEVEL to the calibrator.
        double[] targets = [8, 3, 7, 2, 9, 4, 6, 3, 8, 5, 7, 2, 9, 4, 6, 3, 8, 5, 7, 4];
        var weights = new float[FeatureSchema.VectorLength];
        var bias = 0d;

        for (var i = 0; i < targets.Length; i++)
        {
            var vector = StandardizedVector(seed: i);
            (weights, bias) = ApplyStep(weights, bias, vector, targets[i], sampleWeight: 1.0, baseScore: 7d);
        }

        Assert.True(Math.Abs(bias) < 0.15d,
            $"the head must not accumulate a level offset from mixed feedback (bias {bias}).");
    }

    [Fact]
    public void A_single_step_cannot_move_the_observed_vectors_own_prediction_beyond_lr_times_error()
    {
        // The defining property of a normalised step, and the one that makes the head safe whatever
        // the features' units are: one event moves THIS vector's own prediction by at most
        // lr·sw·|err|. The tiny slack is the L2 decay, which shrinks an existing dot product by
        // lr·λ per step independently of the error.
        var warm = new float[FeatureSchema.VectorLength];
        var warmBias = 0d;
        for (var i = 0; i < 15; i++)
        {
            (warm, warmBias) = ApplyStep(warm, warmBias, StandardizedVector(seed: i), target: 4d, sampleWeight: 1.0, baseScore: 7d);
        }

        foreach (var (weights, bias) in new[] { (new float[FeatureSchema.VectorLength], 0d), (warm, warmBias) })
        {
            foreach (var target in new[] { 1d, 5d, 10d })
            {
                foreach (var sampleWeight in new[] { 0.2d, 1.0d })
                {
                    foreach (var vector in new[] { StandardizedVector(seed: 99), RealisticVector() })
                    {
                        const double baseScore = 7d;
                        var before = OnlineResidualModel.Residual(weights, bias, vector);
                        var error = target - (baseScore + before);
                        var (stepped, steppedBias) = OnlineResidualModel.Step(weights, bias, vector, error, sampleWeight);
                        var after = OnlineResidualModel.Residual(stepped, steppedBias, vector);

                        var bound = (OnlineResidualModel.LearningRate * sampleWeight * Math.Abs(error)) + 1e-4;
                        Assert.True(Math.Abs(after - before) <= bound,
                            $"one step moved the observed vector's prediction by {after - before}, over the {bound} bound.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A vector on the scale the production path actually feeds the head: standardised by the
    /// artifact's per-feature μ/σ, so every slot sits around zero mean and unit variance.
    /// </summary>
    private static float[] StandardizedVector(int seed)
    {
        var random = new Random(seed);
        var v = new float[FeatureSchema.VectorLength];
        for (var i = 0; i < v.Length; i++)
        {
            // Box–Muller, so the spread is genuinely unit-ish rather than a flat ±0.5.
            var u1 = 1d - random.NextDouble();
            var u2 = random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2));
        }

        return v;
    }

    /// <summary>
    /// One event through the PRODUCTION step, given the base score the model would have returned.
    /// </summary>
    private static (float[] Weights, double Bias) ApplyStep(
        float[] weights, double bias, float[] vector, double target, double sampleWeight, double baseScore)
    {
        var error = target - (baseScore + OnlineResidualModel.Residual(weights, bias, vector));
        return OnlineResidualModel.Step(weights, bias, vector, error, sampleWeight);
    }
}
