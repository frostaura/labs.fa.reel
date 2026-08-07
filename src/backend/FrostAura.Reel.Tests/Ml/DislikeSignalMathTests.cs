using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The pure math of the dislike demotion (<see cref="DislikeSignal.DemotionFor"/>): every scale,
/// cap and gate pinned without a database, because these constants ARE the product behaviour the
/// founder asked for — "the dislikes and their reasons I give must be taken into account".
/// </summary>
public class DislikeSignalMathTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A profile is only non-empty when it has at least one recent dislike on record.</summary>
    private static DislikeSignal.DislikedTitle Recent(double weight = 1d) => new(
        Guid.NewGuid(), "Disliked Title", ReactionKind.NotInterested,
        ReactionReason.Genre, Now, weight);

    private static DislikeSignal.Profile Profile(
        Dictionary<string, double>? genreWeights = null,
        Dictionary<string, double>? toneWeights = null,
        List<(float[] Embedding, double Weight)>? seenEnough = null,
        float[]? centroid = null) => new(
        [Recent()],
        genreWeights ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        toneWeights ?? new Dictionary<string, double>(StringComparer.Ordinal),
        seenEnough ?? [],
        centroid);

    [Fact]
    public void An_empty_profile_demotes_nothing()
    {
        var demotion = DislikeSignal.DemotionFor(
            ["horror"], "bleak", [1f, 0f, 0f], DislikeSignal.Profile.Empty);
        Assert.Equal(0d, demotion);
    }

    [Fact]
    public void Genre_overlap_is_scaled_and_capped()
    {
        // One fresh same-genre dislike (weight 1) → 0.3 rating points.
        var one = Profile(genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["horror"] = 1d });
        Assert.Equal(0.3d, DislikeSignal.DemotionFor(["horror"], null, null, one), 10);

        // Piling on more same-genre dislikes saturates at the 0.6 cap — genre alone can nudge,
        // never bury.
        var many = Profile(genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["horror"] = 5d });
        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["horror"], null, null, many), 10);

        // No overlap ⇒ no genre penalty.
        Assert.Equal(0d, DislikeSignal.DemotionFor(["comedy"], null, null, many));
    }

    [Fact]
    public void Genre_overlap_sums_across_matched_genres()
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["horror"] = 0.5d,
            ["thriller"] = 0.4d,
        };
        Assert.Equal(0.9d, DislikeSignal.GenreOverlap(["horror", "thriller", "drama"], weights), 10);
    }

    [Fact]
    public void A_disliked_tone_costs_its_recency_weighted_penalty()
    {
        var profile = Profile(toneWeights: new(StringComparer.Ordinal) { ["bleak"] = 1d });
        Assert.Equal(0.3d, DislikeSignal.DemotionFor([], "bleak", null, profile), 10);

        // Recency-weighted: a half-decayed tone dislike costs half the penalty.
        var stale = Profile(toneWeights: new(StringComparer.Ordinal) { ["bleak"] = 0.5d });
        Assert.Equal(0.15d, DislikeSignal.DemotionFor([], "bleak", null, stale), 10);

        // A tone the user never declined — or no tone at all — costs nothing.
        Assert.Equal(0d, DislikeSignal.DemotionFor([], "warm", null, profile));
        Assert.Equal(0d, DislikeSignal.DemotionFor([], null, null, profile));
    }

    [Fact]
    public void Seen_enough_similarity_is_gated_then_strong()
    {
        float[] disliked = [1f, 0f, 0f];
        var profile = Profile(seenEnough: [(disliked, 1d)]);

        // Below the 0.5 cosine gate: unrelated titles pay nothing at all.
        float[] barely = [0.3f, 0.95f, 0f]; // cos ≈ 0.30
        Assert.Equal(0d, DislikeSignal.DemotionFor([], null, barely, profile));

        // A near-identical plot: cos 1 × 0.9 × weight 1 = 0.9 — the strongest single component.
        Assert.Equal(0.9d, DislikeSignal.DemotionFor([], null, [1f, 0f, 0f], profile), 10);

        // Recency decay applies per reaction: the same similarity on an aged dislike costs less.
        var aged = Profile(seenEnough: [(disliked, 0.5d)]);
        Assert.Equal(0.45d, DislikeSignal.DemotionFor([], null, [1f, 0f, 0f], aged), 10);
    }

    [Fact]
    public void The_general_centroid_is_mild_and_gated_high()
    {
        float[] centroid = [1f, 0f, 0f];
        var profile = Profile(centroid: centroid);

        // Above the 0.6 gate: cos × 0.3.
        Assert.Equal(0.3d, DislikeSignal.DemotionFor([], null, [1f, 0f, 0f], profile), 10);

        // Below the gate (orthogonal): nothing — the mixed-dislike centroid is diffuse, so only a
        // clearly-inside candidate pays.
        Assert.Equal(0d, DislikeSignal.DemotionFor([], null, [0f, 1f, 0f], profile));
    }

    [Fact]
    public void The_total_is_capped_at_one_point_five()
    {
        // Genre at cap (0.6) + tone (0.3) + seen-enough (0.9) + centroid (0.3) = 2.1 raw → 1.5.
        float[] embedding = [1f, 0f, 0f];
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["horror"] = 5d },
            toneWeights: new(StringComparer.Ordinal) { ["bleak"] = 1d },
            seenEnough: [(embedding, 1d)],
            centroid: embedding);

        Assert.Equal(
            DislikeSignal.TotalCap,
            DislikeSignal.DemotionFor(["horror"], "bleak", embedding, profile),
            10);
    }

    [Fact]
    public void A_missing_embedding_never_penalises()
    {
        // Null embedding ⇒ the two similarity components contribute 0; genre + tone still apply.
        float[] disliked = [1f, 0f, 0f];
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["horror"] = 1d },
            toneWeights: new(StringComparer.Ordinal) { ["bleak"] = 1d },
            seenEnough: [(disliked, 1d)],
            centroid: disliked);

        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["horror"], "bleak", null, profile), 10);
    }

    [Fact]
    public void Recency_weight_decays_on_the_ninety_day_scale()
    {
        Assert.Equal(1d, DislikeSignal.RecencyWeight(Now, Now), 10);
        Assert.Equal(Math.Exp(-1d), DislikeSignal.RecencyWeight(Now.AddDays(-90), Now), 10);
        Assert.Equal(Math.Exp(-2d), DislikeSignal.RecencyWeight(Now.AddDays(-180), Now), 10);

        // A clock-skewed future timestamp reads as fresh, never as a boosted weight.
        Assert.Equal(1d, DislikeSignal.RecencyWeight(Now.AddDays(3), Now), 10);
    }

    [Fact]
    public void Apply_demotion_subtracts_floors_at_one_and_never_raises()
    {
        Assert.Equal(6.5m, DislikeSignal.ApplyDemotion(7.0m, 0.5d));
        Assert.Equal(7.0m, DislikeSignal.ApplyDemotion(7.0m, 0d)); // exact no-op without dislikes

        // The floor: a demotion drags down to 1, never below.
        Assert.Equal(1m, DislikeSignal.ApplyDemotion(1.8m, 1.5d));

        // And a score already under 1 is never RAISED to the floor by being demoted.
        Assert.Equal(0.5m, DislikeSignal.ApplyDemotion(0.5m, 0.3d));
    }

    [Fact]
    public void The_declined_prompt_line_names_titles_with_their_reasons()
    {
        var profile = new DislikeSignal.Profile(
            [
                new(Guid.NewGuid(), "Ghost Ship", ReactionKind.NotInterested, ReactionReason.Genre, Now, 1d),
                new(Guid.NewGuid(), "Slow Meadow", ReactionKind.Dropped, null, Now.AddDays(-1), 0.9d),
            ],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.Ordinal),
            [],
            null);

        var line = DislikeSignal.DeclinedLine(profile);
        Assert.NotNull(line);
        Assert.Contains("Ghost Ship (wrong genre)", line);
        Assert.Contains("Slow Meadow (dropped)", line);

        Assert.Null(DislikeSignal.DeclinedLine(DislikeSignal.Profile.Empty));
    }
}
