using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The community-rating badge every card carries. TMDB leads because it covers ~100% of the live
/// catalogue against Trakt's 50% — pick the wrong source first and half the cards go blank.
/// </summary>
public class CrowdRatingTests
{
    [Fact]
    public void Tmdb_average_wins_and_brings_its_own_vote_count()
    {
        var title = Title(tmdbAverage: 7.44m, tmdbVotes: 1200, traktRating: 9.1m, traktVotes: 40);

        Assert.Equal(7.4m, CrowdRating.For(title));
        Assert.Equal(1200, CrowdRating.VotesFor(title));
    }

    [Fact]
    public void Trakt_is_the_fallback_when_tmdb_is_missing_or_zero()
    {
        var missing = Title(tmdbAverage: null, tmdbVotes: 0, traktRating: 8.25m, traktVotes: 77);
        Assert.Equal(8.3m, CrowdRating.For(missing));
        Assert.Equal(77, CrowdRating.VotesFor(missing));

        // Zero is TMDB's "never rated" sentinel, not a rating of zero — it must fall through too,
        // and the Trakt vote count must come with it rather than TMDB's.
        var zero = Title(tmdbAverage: 0m, tmdbVotes: 0, traktRating: 6.5m, traktVotes: 12);
        Assert.Equal(6.5m, CrowdRating.For(zero));
        Assert.Equal(12, CrowdRating.VotesFor(zero));
    }

    [Fact]
    public void Neither_source_rated_yields_null_rather_than_a_fabricated_zero()
    {
        var none = Title(tmdbAverage: null, tmdbVotes: 0, traktRating: null, traktVotes: 0);
        Assert.Null(CrowdRating.For(none));
        Assert.Null(CrowdRating.VotesFor(none));

        var zeroes = Title(tmdbAverage: 0m, tmdbVotes: 0, traktRating: 0m, traktVotes: 0);
        Assert.Null(CrowdRating.For(zeroes));
        Assert.Null(CrowdRating.VotesFor(zeroes));
    }

    [Fact]
    public void A_rating_with_an_unknown_vote_count_still_shows_the_rating()
    {
        var title = Title(tmdbAverage: 7.0m, tmdbVotes: 0, traktRating: null, traktVotes: 0);

        Assert.Equal(7.0m, CrowdRating.For(title));
        Assert.Null(CrowdRating.VotesFor(title));
    }

    private static Title Title(decimal? tmdbAverage, int tmdbVotes, decimal? traktRating, int traktVotes) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Some Title",
        TmdbVoteAverage = tmdbAverage,
        TmdbVoteCount = tmdbVotes,
        TraktRating = traktRating,
        TraktVotes = traktVotes,
    };
}
