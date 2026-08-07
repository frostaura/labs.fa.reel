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
