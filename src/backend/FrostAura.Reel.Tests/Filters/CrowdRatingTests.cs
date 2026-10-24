using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The community-rating badge every card carries, and the crowd half of ranking: a vote-shrunk
/// Bayesian average, Trakt first when Trakt has a crowd, TMDB otherwise (D-12, NEW-2).
/// </summary>
public class CrowdRatingTests
{
    [Fact]
    public void A_ten_on_two_votes_ranks_below_a_seven_on_five_thousand()
    {
        // The acceptance pair, verbatim: "Late Night Repairs" showed 10.0 on 2 votes.
        var tiny = Title(tmdbAverage: 10.0m, tmdbVotes: 2, traktRating: null, traktVotes: 0);
        var solid = Title(tmdbAverage: 7.0m, tmdbVotes: 5000, traktRating: null, traktVotes: 0);

        Assert.True(CrowdRating.For(tiny) < CrowdRating.For(solid));
        Assert.Equal(6.6m, CrowdRating.For(tiny));
        Assert.Equal(7.0m, CrowdRating.For(solid));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(19)]
    public void Fewer_than_twenty_votes_can_never_show_nine_or_more(int votes)
    {
        foreach (var rating in new[] { 9.0m, 9.5m, 10.0m })
        {
            Assert.True(CrowdRating.For(Title(rating, votes, null, 0)) < 9.0m);
            Assert.True(CrowdRating.For(Title(null, 0, rating, votes)) < 9.0m);
        }
    }

    [Fact]
    public void A_well_voted_title_barely_moves_and_the_prior_pulls_both_ways()
    {
        Assert.Equal(8.4m, CrowdRating.For(Title(8.44m, 20_000, null, 0)));
        // Below the prior a thin sample is pulled UP: one 2.0 vote is not a verdict either.
        Assert.Equal(6.3m, CrowdRating.For(Title(2.0m, 2, null, 0)));
        Assert.Equal(6.5m, CrowdRating.Shrink(9m, 0));
    }

    [Fact]
    public void Trakt_leads_when_it_has_a_crowd_and_brings_its_own_vote_count()
    {
        var title = Title(tmdbAverage: 7.44m, tmdbVotes: 1200, traktRating: 8.1m, traktVotes: 4000);

        Assert.Equal(CrowdSource.Trakt, CrowdRating.Source(title));
        Assert.Equal(8.1m, CrowdRating.For(title));
        Assert.Equal(4000, CrowdRating.VotesFor(title));
    }

    [Fact]
    public void A_thin_trakt_sample_yields_to_tmdb()
    {
        var title = Title(tmdbAverage: 7.44m, tmdbVotes: 1200, traktRating: 9.1m, traktVotes: 40);

        Assert.Equal(CrowdSource.Tmdb, CrowdRating.Source(title));
        Assert.Equal(1200, CrowdRating.VotesFor(title));
        Assert.Equal(Math.Round(CrowdRating.Shrink(7.44m, 1200), 1, MidpointRounding.AwayFromZero), CrowdRating.For(title));
    }

    [Fact]
    public void Trakt_alone_is_used_when_tmdb_is_missing_or_zero()
    {
        var missing = Title(tmdbAverage: null, tmdbVotes: 0, traktRating: 8.25m, traktVotes: 77);
        Assert.Equal(CrowdSource.Trakt, CrowdRating.Source(missing));
        Assert.Equal(77, CrowdRating.VotesFor(missing));

        // Zero is TMDB's "never rated" sentinel, not a rating of zero — it falls through to Trakt,
        // and the Trakt vote count comes with it rather than TMDB's.
        var zero = Title(tmdbAverage: 0m, tmdbVotes: 0, traktRating: 6.5m, traktVotes: 12);
        Assert.Equal(6.5m, CrowdRating.For(zero));
        Assert.Equal(12, CrowdRating.VotesFor(zero));
    }

    [Fact]
    public void Neither_source_rated_yields_null_rather_than_a_fabricated_number()
    {
        Assert.Null(CrowdRating.For(Title(null, 0, null, 0)));
        Assert.Null(CrowdRating.VotesFor(Title(null, 0, null, 0)));
        Assert.Null(CrowdRating.For(Title(0m, 0, 0m, 0)));
    }

    [Fact]
    public void A_rating_with_no_vote_count_behind_it_is_not_shown()
    {
        // Unweighable: raw would undo the shrinkage, the prior would be invented.
        var title = Title(tmdbAverage: 9.6m, tmdbVotes: 0, traktRating: null, traktVotes: 0);
        Assert.Null(CrowdRating.For(title));
        Assert.Null(CrowdRating.VotesFor(title));
    }

    [Fact]
    public void The_floor_still_reads_the_raw_tmdb_first_average_until_fd7()
    {
        var title = Title(tmdbAverage: 10.0m, tmdbVotes: 2, traktRating: 4.0m, traktVotes: 4000);

        Assert.Equal((10.0m, 2), CrowdRating.ResolveRaw(title));
        Assert.Equal(CrowdSource.Trakt, CrowdRating.Source(title));
        Assert.Equal(4.0m, CrowdRating.For(title));
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
