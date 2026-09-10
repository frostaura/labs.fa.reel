using FrostAura.Reel.Application.Ml;
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
        float[]? centroid = null,
        DislikeSignal.GenreTaste? userGenres = null) => new(
        [Recent()],
        genreWeights ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        toneWeights ?? new Dictionary<string, double>(StringComparer.Ordinal),
        seenEnough ?? [],
        centroid,
        DislikeSignal.Profile.EmptyVersion,
        userGenres);

    /// <summary>
    /// The founder's live genre distribution, read off the production account on 2026-09-10 — the
    /// shape the whole v5 genre rule exists for. Overall mean 6.737; the five genres his Genre-reason
    /// dismissals weight most heavily are also five of the six he has rated most, ALL of them at or
    /// above that mean. Horror and thriller are the counter-examples: high volume, affinity BELOW
    /// the mean, so his ratings do not argue against demoting them.
    /// </summary>
    private static DislikeSignal.GenreTaste FounderGenres() => new(
        new Dictionary<string, DislikeSignal.GenreEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["drama"] = new(744, 6.834d),
            ["action"] = new(787, 6.770d),
            ["comedy"] = new(486, 6.933d),
            ["romance"] = new(144, 6.879d),
            ["science-fiction"] = new(408, 6.850d),
            ["thriller"] = new(850, 6.521d),
            ["horror"] = new(337, 6.165d),
            // A genre he has barely touched and rates below his mean — the case the rule must NOT
            // soften, because twelve ratings is not a body of evidence.
            ["suspense"] = new(12, 6.081d),
        },
        UserMean: 6.737d);

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
    public void A_dismissed_genre_the_user_rates_heavily_and_highly_is_barely_demoted()
    {
        // THE founder bug, in one assertion. 19 Genre-reason dismissals put drama at a
        // recency-weighted weight of 13.57 — which, raw, is 4.07 rating points and therefore the
        // full 0.6 cap. Against 744 drama ratings averaging 6.83, above his 6.74 overall mean. A
        // "wrong genre" dismissal on a drama from someone with 744 drama ratings is evidence about
        // that TITLE, and this is the rule that says so.
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d },
            userGenres: FounderGenres());

        var demotion = DislikeSignal.DemotionFor(["drama"], null, null, profile);

        Assert.Equal(DislikeSignal.SupportedGenreCap, demotion, 10);
        Assert.True(demotion <= 0.15d, $"drama demotion {demotion} must not exceed the supported cap");

        // Without the v5 bounds this was the full genre cap — a 4x difference, and the whole reason
        // every scripted title on his page sat below every reality show.
        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["drama"], null, null,
            Profile(genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d })), 10);
    }

    [Fact]
    public void A_dismissed_genre_the_user_has_barely_rated_still_lands_near_the_cap()
    {
        // The permissive direction has to keep working, or the fix is just a different failure. Five
        // fresh dismissals against twelve ratings averaging BELOW his mean: nothing here argues for
        // leniency, so the demotion runs to the ordinary cap.
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["suspense"] = 5d },
            userGenres: FounderGenres());

        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["suspense"], null, null, profile), 10);

        // And it is not merely clamping: halve the dismissal weight and the demotion moves, which is
        // what distinguishes a live signal from a constant.
        var lighter = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["suspense"] = 2d },
            userGenres: FounderGenres());
        var expected = 2d * DislikeSignal.VolumeDamping(12) * 0.3d;
        Assert.Equal(expected, DislikeSignal.DemotionFor(["suspense"], null, null, lighter), 10);
        Assert.InRange(expected, 0.35d, 0.45d);
    }

    [Fact]
    public void Volume_damping_is_one_at_zero_ratings_and_decays_logarithmically()
    {
        // The shape, pinned: 1 / (1 + log(1 + N/20)).
        Assert.Equal(1d, DislikeSignal.VolumeDamping(0), 10);
        Assert.Equal(1d / (1d + Math.Log(2d)), DislikeSignal.VolumeDamping(20), 10);

        // Monotone, and never zero — volume makes a dismissal count for LESS, never for nothing.
        double previous = DislikeSignal.VolumeDamping(0);
        foreach (var n in new[] { 20, 100, 300, 744, 5_000 })
        {
            var damping = DislikeSignal.VolumeDamping(n);
            Assert.True(damping < previous, $"damping at {n} ({damping}) must sit below {previous}");
            Assert.True(damping > 0d, $"damping at {n} must stay positive");
            previous = damping;
        }

        // A negative count cannot exist but must not produce a boost if it ever did.
        Assert.Equal(1d, DislikeSignal.VolumeDamping(-5), 10);
    }

    [Fact]
    public void Volume_damping_alone_would_not_have_been_enough()
    {
        // Stated because it is the reason the rule has TWO halves rather than one. 13.57 damped by
        // 744 ratings is still 2.92 units, which is still 0.88 rating points — over the old cap. The
        // affinity bound is what actually holds the founder's dramas at 0.15.
        var damped = 13.570d * DislikeSignal.VolumeDamping(744) * 0.3d;
        Assert.True(damped > 0.6d, $"damped drama weight {damped} still exceeds the unsupported cap");
    }

    [Fact]
    public void Each_genre_of_a_title_answers_for_itself()
    {
        // A title tagged [drama, horror] for a user who rates drama above his mean and horror below
        // it. The per-genre cap means the loved genre cannot launder the disliked one, and the
        // disliked one cannot drag the loved one up to the full cap.
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d, ["horror"] = 1d },
            userGenres: FounderGenres());

        var expected = DislikeSignal.SupportedGenreCap + (1d * DislikeSignal.VolumeDamping(337) * 0.3d);
        Assert.Equal(expected, DislikeSignal.DemotionFor(["drama", "horror"], null, null, profile), 10);

        // Still bounded by the global genre cap when the parts add up past it.
        var heavy = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d, ["suspense"] = 5d },
            userGenres: FounderGenres());
        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["drama", "suspense"], null, null, heavy), 10);
    }

    [Fact]
    public void A_genre_the_user_has_never_rated_is_never_treated_as_supported()
    {
        // "No evidence" must not read as "supported" — otherwise a brand-new account, or a genre
        // outside the user's library entirely, would get the lenient cap for free.
        var taste = FounderGenres();
        Assert.False(taste.Supports("documentary"));
        Assert.Equal(0, taste.For("documentary").RatingCount);

        // …and the demotion for it runs at the ordinary cap, exactly as it did before v5.
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["documentary"] = 5d },
            userGenres: taste);
        Assert.Equal(0.6d, DislikeSignal.DemotionFor(["documentary"], null, null, profile), 10);

        // An account with no ratings at all behaves as it always did: undamped, uncapped-by-affinity.
        Assert.False(DislikeSignal.GenreTaste.None.Supports("drama"));
        Assert.Equal(1d, DislikeSignal.VolumeDamping(DislikeSignal.GenreTaste.None.For("drama").RatingCount), 10);
    }

    [Fact]
    public void The_support_test_is_affinity_at_or_above_the_users_own_mean()
    {
        var taste = FounderGenres();

        // Above the mean — his five most-dismissed genres are ALL here, which is the whole problem.
        Assert.True(taste.Supports("drama"));
        Assert.True(taste.Supports("action"));
        Assert.True(taste.Supports("comedy"));
        Assert.True(taste.Supports("romance"));
        Assert.True(taste.Supports("science-fiction"));

        // Below it — high volume does not by itself buy leniency.
        Assert.False(taste.Supports("thriller"));
        Assert.False(taste.Supports("horror"));
    }

    [Fact]
    public void The_shrunken_genre_affinity_matches_the_feature_layers_formula()
    {
        // Ranking must not depend on Ml, so the k is restated rather than referenced — which makes
        // "they are the same number" a thing that needs a test rather than a thing that is obvious.
        Assert.Equal((double)TasteMath.ShrinkageK, DislikeSignal.GenreAffinityShrinkageK, 10);
        Assert.Equal(
            (double)TasteMath.ShrunkenMean(sum: 68m, count: 10, priorMean: 7m),
            DislikeSignal.ShrunkenGenreAffinity(ratingSum: 68d, ratingCount: 10, userMean: 7d),
            9);

        // No ratings ⇒ the prior itself, which is exactly the value Supports() then compares against
        // (and why RatingCount, not affinity, is what makes a genre unsupported).
        Assert.Equal(7d, DislikeSignal.ShrunkenGenreAffinity(0d, 0, 7d), 10);
    }

    [Fact]
    public void The_learned_overlap_feature_stays_raw_while_the_served_demotion_is_bounded()
    {
        // GenreOverlap still feeds the schema-v3 `dislikedGenreOverlap` feature and is deliberately
        // NOT damped or capped: it is raw evidence, and since schema v5 the model can condition its
        // own weight on userGenreAffinity and the genre one-hots — a better instrument than any
        // constant in this file. GenreDemotion is a hand-set serving rule, and a hand-set rule is
        // the thing that must not out-shout the user's ratings. Pinned so the split is intentional
        // rather than an oversight someone "fixes" later.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d };
        Assert.Equal(13.570d, DislikeSignal.GenreOverlap(["drama"], weights), 10);

        var profile = Profile(genreWeights: weights, userGenres: FounderGenres());
        Assert.Equal(DislikeSignal.SupportedGenreCap, DislikeSignal.GenreDemotion(["drama"], profile), 10);
    }

    [Fact]
    public void The_dislike_centroid_does_not_saturate_the_way_the_genre_map_did()
    {
        // The "is the centroid doing the same thing" check, as a structural property rather than a
        // measurement: the genre map SUMMED per-reaction weights, so N same-genre dismissals grew
        // without bound until they hit the cap. The centroid is a weighted MEAN, so N dismissals of
        // the same kind make it a more coherent centroid, never a larger penalty — its ceiling is
        // one flat GeneralScale regardless of how many reactions built it.
        float[] direction = [1f, 0f, 0f];

        var few = Profile(centroid: direction);
        var many = Profile(centroid: direction); // a mean over 44 titles is still a unit direction

        Assert.Equal(
            DislikeSignal.DemotionFor([], null, direction, few),
            DislikeSignal.DemotionFor([], null, direction, many),
            10);

        // Its worst case is 0.3 — half the genre cap, a fifth of the total cap — and it is gated, so
        // a candidate that is not clearly inside the disliked region pays nothing at all. Measured
        // on the live account (2026-09-10, 174 embedded not-interested titles vs 4,265 candidates):
        // mean cosine 0.505, max 0.713, only 10% clear the 0.6 gate, and the per-genre gate rates
        // run horror 31% / thriller 27% / drama 11% / comedy 4% — highest on the two genres the
        // founder rates BELOW his mean. It discriminates within a genre; it does not condemn one.
        Assert.True(DislikeSignal.DemotionFor([], null, direction, few) <= 0.3d);
        Assert.Equal(0d, DislikeSignal.DemotionFor([], null, [0f, 1f, 0f], few));
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
    public void The_why_not_note_says_when_the_penalty_is_being_held_down_by_your_own_ratings()
    {
        // The visible half of the v5 rule. A user with 744 drama ratings, looking at a demoted
        // drama, must not read the bar as "we think you dislike drama" — it is the individual titles
        // he passed on, deliberately kept small BECAUSE of his record in the genre.
        var profile = Profile(
            genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["drama"] = 13.570d },
            userGenres: FounderGenres());

        var supported = ContributionDetails.DislikedSimilarity(["drama"], profile);
        Assert.Equal(ContributionDetails.DislikedSimilarityFeature, supported.Feature);
        Assert.Contains("you passed on similar titles recently", supported.Note!, StringComparison.Ordinal);
        Assert.Contains("kept small because you rate drama highly", supported.Note!, StringComparison.Ordinal);

        // A genre his own ratings do NOT vouch for gets the plain note — the clause has to be earned
        // or it is just reassuring noise.
        var unsupported = ContributionDetails.DislikedSimilarity(
            ["horror"],
            Profile(
                genreWeights: new(StringComparer.OrdinalIgnoreCase) { ["horror"] = 3d },
                userGenres: FounderGenres()));
        Assert.Equal("you passed on similar titles recently", unsupported.Note);

        // …as does a caller with nothing to go on, which is the pre-v5 behaviour unchanged.
        Assert.Equal("you passed on similar titles recently", ContributionDetails.DislikedSimilarity().Note);

        // A genre the demotion never charged for cannot explain it away either, even when supported.
        var uncharged = ContributionDetails.DislikedSimilarity(["comedy"], profile);
        Assert.Equal("you passed on similar titles recently", uncharged.Note);
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
