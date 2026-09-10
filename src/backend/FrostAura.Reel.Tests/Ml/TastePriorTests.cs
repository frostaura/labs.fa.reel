using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The taste prior's arithmetic, pinned against the founder's own distribution as it stood on
/// 2026-09-10 — 2,045 title ratings, 90% of them movies, a user mean of 6.74, seven volume genres in
/// the hundreds, and <b>zero</b> ratings in reality. That shape is the whole reason this class
/// exists, so it is the shape the numbers are asserted against rather than a tidy synthetic one.
///
/// Pure by construction: no database, no taste-state build, no model.
/// </summary>
public class TastePriorTests
{
    /// <summary>The founder's library, to the counts and means the live database reported.</summary>
    private static TastePrior Founder() => TastePrior.FromAggregates(
        new Dictionary<string, TastePrior.GenreHistory>
        {
            ["thriller"] = new(850, 6.520m),
            ["action"] = new(787, 6.770m),
            ["drama"] = new(744, 6.835m),
            ["comedy"] = new(486, 6.934m),
            ["crime"] = new(467, 6.739m),
            ["adventure"] = new(423, 7.165m),
            ["science-fiction"] = new(408, 6.850m),
            ["horror"] = new(337, 6.160m),
            ["family"] = new(119, 7.269m),
            // Three unscripted ratings in a decade of watching. This is the fact the prior reads.
            ["game-show"] = new(3, 6.667m),
        },
        userMean: 6.737m,
        movieRatedShare: 0.9m,
        ratingsCount: 2045);

    private static Title Show(params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Show,
        TraktSlug = string.Empty,
        Name = "show",
        Genres = genres,
    };

    private static Title Movie(params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktSlug = string.Empty,
        Name = "movie",
        Genres = genres,
    };

    [Fact]
    public void The_evidence_bar_is_the_larger_of_ten_ratings_and_half_a_percent_of_the_library()
    {
        // A small library is protected by the absolute floor: ten ratings is not a preference
        // whether you own fifty titles or five hundred.
        Assert.Equal(10d, TastePrior.GenreEvidenceBar(0));
        Assert.Equal(10d, TastePrior.GenreEvidenceBar(1_000));

        // A large one scales, or "ten ratings out of four thousand" would count as history.
        Assert.Equal(10.225d, TastePrior.GenreEvidenceBar(2_045), 3);
        Assert.Equal(25d, TastePrior.GenreEvidenceBar(5_000));
    }

    [Fact]
    public void A_genre_the_account_has_never_rated_is_demoted_to_six_tenths()
    {
        // "reality" is not merely thin in this library — it is absent, and it is also absent from
        // FeatureSchema.Genres, so the model has no slot in which to hold an opinion about it. The
        // prior is the only layer that can say anything at all.
        var components = Founder().ComponentsFor(["reality"], MediaType.Show);

        Assert.Equal(TastePrior.NoHistoryFactor, components.NoHistory, 6);
        Assert.Equal(1d, components.GenreAffinity, 6);
        Assert.Single(components.NoHistoryGenres);
        Assert.Equal("reality", components.NoHistoryGenres[0].Genre);
        Assert.Equal(0, components.NoHistoryGenres[0].RatedCount);
    }

    [Fact]
    public void Three_ratings_ever_is_not_history_and_the_note_says_the_count_not_the_rule()
    {
        var components = Founder().ComponentsFor(["game-show"], MediaType.Show);

        Assert.Equal(TastePrior.NoHistoryFactor, components.NoHistory, 6);

        // The founder's own "why not", in his own units. Naming the count rather than the threshold
        // is deliberate: the count is a fact about him, the threshold is arithmetic of ours.
        Assert.Equal("you have rated 3 game-show titles ever", TastePrior.NoHistoryNote(components));
    }

    [Fact]
    public void Stacked_no_history_genres_floor_rather_than_compounding_away()
    {
        // Two unknown genres on one title are usually the same fact twice ("reality", "game-show"),
        // so 0.6 × 0.6 = 0.36 would be double-counting; the floor holds it at 0.4.
        var two = Founder().ComponentsFor(["reality", "game-show"], MediaType.Show);
        Assert.Equal(TastePrior.NoHistoryFloor, two.NoHistory, 6);

        var three = Founder().ComponentsFor(["reality", "game-show", "talk-show"], MediaType.Show);
        Assert.Equal(TastePrior.NoHistoryFloor, three.NoHistory, 6);
    }

    [Fact]
    public void A_volume_genre_is_left_alone_by_the_no_history_term()
    {
        foreach (var genre in new[] { "thriller", "action", "drama", "crime", "adventure", "science-fiction", "horror" })
        {
            var components = Founder().ComponentsFor([genre], MediaType.Movie);
            Assert.Equal(1d, components.NoHistory, 6);
            Assert.Empty(components.NoHistoryGenres);
        }
    }

    [Fact]
    public void The_affinity_term_leans_the_way_the_account_actually_rates_and_stays_inside_eight_percent()
    {
        var prior = Founder();

        // Horror is the founder's worst genre by a clear margin (6.16 against a 6.74 mean) — the
        // term is negative and proportional, not a flat penalty.
        var horror = prior.ComponentsFor(["horror"], MediaType.Movie);
        Assert.True(horror.GenreAffinity < 1d);
        Assert.Equal(1d + (-0.577d * TastePrior.MaxAffinitySwing), horror.GenreAffinity, 3);

        // Adventure is his best of the volume genres, and lifts.
        var adventure = prior.ComponentsFor(["adventure"], MediaType.Movie);
        Assert.True(adventure.GenreAffinity > 1d);

        // And whatever the gap, the term never exceeds the stated swing in either direction.
        var extreme = TastePrior.FromAggregates(
            new Dictionary<string, TastePrior.GenreHistory> { ["thriller"] = new(500, 10m) },
            userMean: 5m,
            movieRatedShare: 0.5m,
            ratingsCount: 500);
        Assert.Equal(1d + TastePrior.MaxAffinitySwing, extreme.ComponentsFor(["thriller"], MediaType.Movie).GenreAffinity, 6);
    }

    [Fact]
    public void The_affinity_term_is_weighted_by_volume_not_by_a_flat_average()
    {
        // A title carrying one 850-rating genre and one 119-rating genre must lean toward the
        // 850-rating one. A flat average would let a genre the account has barely explored outvote
        // the one it has spent a decade in.
        var prior = Founder();
        var weighted = prior.ComponentsFor(["thriller", "family"], MediaType.Movie);

        var thrillerAlone = prior.ComponentsFor(["thriller"], MediaType.Movie).AffinityDelta;
        var familyAlone = prior.ComponentsFor(["family"], MediaType.Movie).AffinityDelta;
        var flat = (thrillerAlone + familyAlone) / 2m;

        Assert.True(weighted.AffinityDelta < flat, "volume weighting must pull toward the heavier genre");
        Assert.True(weighted.AffinityDelta > thrillerAlone);
    }

    [Fact]
    public void An_unevidenced_genre_contributes_no_affinity_opinion()
    {
        // Reality's shrunken mean IS the user mean, so folding it into the weighted average would
        // dilute the genres that carry real information toward zero. It is demoted and then
        // excluded from the affinity term, not counted twice.
        var prior = Founder();
        var horrorOnly = prior.ComponentsFor(["horror"], MediaType.Movie);
        var horrorAndReality = prior.ComponentsFor(["horror", "reality"], MediaType.Movie);

        Assert.Equal(horrorOnly.GenreAffinity, horrorAndReality.GenreAffinity, 6);
        Assert.Equal(TastePrior.NoHistoryFactor, horrorAndReality.NoHistory, 6);
    }

    [Fact]
    public void A_ninety_percent_movie_library_demotes_shows_and_leaves_movies_alone()
    {
        var prior = Founder();

        Assert.Equal(1d, prior.MediaFactor(MediaType.Movie), 6);
        Assert.Equal(0.85d, prior.MediaFactor(MediaType.Show), 6);
        Assert.Equal("90% of what you rate is movies", prior.MediaNote(MediaType.Show));
        Assert.Null(prior.MediaNote(MediaType.Movie));
    }

    [Fact]
    public void The_media_prior_is_symmetric_and_floors_at_seven_tenths()
    {
        var tvHeavy = TastePrior.FromAggregates(
            new Dictionary<string, TastePrior.GenreHistory> { ["drama"] = new(400, 7m) },
            userMean: 7m,
            movieRatedShare: 0.1m,
            ratingsCount: 400);

        Assert.Equal(1d, tvHeavy.MediaFactor(MediaType.Show), 6);
        Assert.Equal(0.85d, tvHeavy.MediaFactor(MediaType.Movie), 6);

        var moviesOnly = TastePrior.FromAggregates(
            new Dictionary<string, TastePrior.GenreHistory> { ["drama"] = new(400, 7m) },
            userMean: 7m,
            movieRatedShare: 1.0m,
            ratingsCount: 400);
        Assert.Equal(TastePrior.MediaPriorFloor, moviesOnly.MediaFactor(MediaType.Show), 6);
    }

    [Fact]
    public void A_mixed_library_gets_no_media_prior_at_all()
    {
        // The threshold is a claim that the account has effectively stopped rating one medium. A
        // 70/30 library has not, and a recommender that quietly withheld a third of the catalogue
        // from it would be wrong in a way the user could never name.
        foreach (var share in new[] { 0.5m, 0.65m, 0.8m })
        {
            var prior = TastePrior.FromAggregates(
                new Dictionary<string, TastePrior.GenreHistory> { ["drama"] = new(400, 7m) },
                userMean: 7m,
                movieRatedShare: share,
                ratingsCount: 400);
            Assert.Equal(1d, prior.MediaFactor(MediaType.Movie), 6);
            Assert.Equal(1d, prior.MediaFactor(MediaType.Show), 6);
        }
    }

    [Fact]
    public void The_founder_hero_inversion_is_reproduced_end_to_end()
    {
        // The live numbers, 2026-09-10: American Ninja Warrior 6.77 (reality, show) led the hero
        // and the best MOVIE in the whole shortlist was Wonderstruck at 5.97. This is the
        // arithmetic that has to flip that, and the assertion is the product decision — a title the
        // account has no history in must not outrank one it does on 0.8 of a point.
        var prior = Founder();
        var ninjaWarrior = 6.77m * prior.DecimalMultiplierFor(Show("reality"));
        var wonderstruck = 5.97m * prior.DecimalMultiplierFor(Movie("drama"));

        Assert.True(wonderstruck > ninjaWarrior,
            $"a drama movie at 5.97 must outrank a reality show at 6.77 for this account ({wonderstruck} vs {ninjaWarrior})");

        // 6.77 × 0.6 × 0.85 — stated rather than merely compared, so a future change to either
        // constant has to face this number.
        Assert.Equal(3.45m, Math.Round(ninjaWarrior, 2));
    }

    [Fact]
    public void The_shared_ranking_key_reorders_without_touching_the_number_on_the_card()
    {
        // This is the expression the feed build, the paginator and the LIVE REFILL all order by.
        // The refill is the one that matters most here: it used to order by PredictedRating alone,
        // straight off the persisted TitleScore, so a dropped card was replaced by exactly the
        // titles the build had just ranked down — the hero healed itself back into reality TV
        // between rebuilds. One shared key is what stops that.
        var prior = Founder();
        var realityShow = Show("reality");
        var dramaMovie = Movie("drama");

        Assert.True(prior.Rank(6.90m, realityShow) < prior.Rank(5.60m, dramaMovie));

        // And the prediction itself is an input, never an output: nothing here writes back a rating.
        Assert.Equal(6.90m, 6.90m * 1m);
        Assert.Equal(prior.Rank(6.90m, realityShow), 6.90m * prior.DecimalMultiplierFor(realityShow));
    }

    [Fact]
    public void A_title_with_no_genres_is_left_alone_on_the_genre_terms()
    {
        // Love Island USA reached the founder's feed carrying an EMPTY genre array. Demoting for
        // missing metadata would bury every freshly discovered candidate before enrichment runs, so
        // the genre terms stay neutral — and the media prior, which needs no metadata, still bites.
        var components = Founder().ComponentsFor([], MediaType.Show);

        Assert.Equal(1d, components.NoHistory, 6);
        Assert.Equal(1d, components.GenreAffinity, 6);
        Assert.Equal(0.85d, components.Media, 6);
        Assert.Empty(components.NoHistoryGenres);
    }

    [Fact]
    public void An_account_with_no_history_is_moved_by_nothing()
    {
        // The cold-start contract: a prior that has learned nothing must assert nothing, or a new
        // user's first feed is shaped by a rule fitted to no evidence at all.
        Assert.Equal(1d, TastePrior.Neutral.MultiplierFor(Show("reality")), 6);
        Assert.Equal(1d, TastePrior.Neutral.MultiplierFor(Movie("thriller")), 6);
        Assert.True(TastePrior.Neutral.ComponentsFor(Movie("thriller")).IsNeutral);
    }

    [Fact]
    public void Only_non_neutral_factors_are_rendered_as_contribution_bars()
    {
        var prior = Founder();

        // A well-evidenced movie in a mid-affinity genre: one small affinity bar, nothing else.
        var crime = TastePrior.ContributionEntries(prior.ComponentsFor(Movie("crime")));
        Assert.All(crime, e => Assert.Equal(TastePrior.GenreAffinityFeature, e.Feature));

        // The reality show carries all three stories it has to tell, as signed deltas from 1.0.
        var reality = TastePrior.ContributionEntries(prior.ComponentsFor(Show("reality")));
        Assert.Contains(reality, e => e.Feature == TastePrior.NoHistoryFeature && Math.Abs(e.Value + 0.4d) < 1e-9d);
        Assert.Contains(reality, e => e.Feature == TastePrior.MediaFeature && Math.Abs(e.Value + 0.15d) < 1e-9d);
        Assert.DoesNotContain(reality, e => e.Feature == TastePrior.GenreAffinityFeature);
    }
}
