using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Ranking.Interleaving;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// The feed's crowd term (R1-49, WS-3 CR-1 "feed ranking blend should use CrowdVotes.ShrunkScore"):
/// a bounded multiplier off the vote-shrunk, Trakt-first crowd score, in the served ordering key and
/// never in the number the card shows.
/// </summary>
public class CrowdPriorTests
{
    [Fact]
    public void Ten_on_two_votes_ranks_below_seven_on_five_thousand_at_the_same_prediction()
    {
        // WS-3's crowd-score acceptance, put to the feed's own comparator.
        var fewVotes = Tmdb(10.0m, 2);
        var wellVoted = Tmdb(7.0m, 5_000);

        Assert.True(CrowdPrior.FactorFor(wellVoted) > CrowdPrior.FactorFor(fewVotes));
        Assert.True(Key(7.0m, wellVoted) > Key(7.0m, fewVotes));
    }

    [Fact]
    public void The_shrunk_score_is_the_badge_unrounded()
    {
        // Same source resolution as the badge, for every branch of it: Trakt at ≥ 50 votes, TMDB
        // otherwise, Trakt alone only when TMDB has no rating.
        Title[] titles =
        [
            Both(trakt: 8.1m, traktVotes: 50, tmdb: 6.0m, tmdbVotes: 9_000),  // Trakt leads at 50
            Both(trakt: 8.1m, traktVotes: 49, tmdb: 6.0m, tmdbVotes: 9_000),  // one short: TMDB
            Both(trakt: 8.1m, traktVotes: 12, tmdb: null, tmdbVotes: 0),      // Trakt alone
            Tmdb(7.3m, 640),
        ];

        foreach (var title in titles)
        {
            var shrunk = CrowdPrior.ShrunkScoreOf(title);
            Assert.NotNull(shrunk);
            Assert.Equal(CrowdRating.For(title), Math.Round(shrunk!.Value, 1, MidpointRounding.AwayFromZero));
        }

        Assert.Equal(CrowdRating.Shrink(8.1m, 50), CrowdPrior.ShrunkScoreOf(titles[0]));
        Assert.Equal(CrowdRating.Shrink(6.0m, 9_000), CrowdPrior.ShrunkScoreOf(titles[1]));
    }

    [Theory]
    [InlineData(null, 0, null, 0)]      // no crowd at all
    [InlineData(8.0, 0, 9.0, 0)]        // ratings with no votes behind them
    public void No_crowd_score_is_neutral(double? trakt, int traktVotes, double? tmdb, int tmdbVotes)
    {
        var title = Both((decimal?)trakt, traktVotes, (decimal?)tmdb, tmdbVotes);

        Assert.Null(CrowdPrior.ShrunkScoreOf(title));
        Assert.Equal(1d, CrowdPrior.FactorForScore(CrowdPrior.ShrunkScoreOf(title)));

        // The score term is neutral; the volume term is not: no votes is the least evidence there is.
        Assert.Equal(1d - CrowdPrior.VolumeSwing, CrowdPrior.FactorFor(title), 9);
    }

    [Theory]
    [InlineData(0, 0.90)]
    [InlineData(150, 0.90)]
    [InlineData(1_500, 1.00)]
    [InlineData(15_000, 1.10)]
    [InlineData(2_000_000, 1.10)]
    public void The_volume_term_is_log_linear_around_the_pivot_and_bounded(int votes, double expected)
    {
        Assert.Equal(expected, CrowdPrior.VolumeFactorFor(votes), 9);
    }

    [Fact]
    public void Among_equal_scores_the_better_voted_title_ranks_first()
    {
        // The founder's own data (task 9): his mean rating climbs from 5.4 under 300 TMDB votes to 7.8
        // above 10,000, while the shrinkage puts 7.0 on 400 and 7.0 on 40,000 almost level.
        Assert.True(CrowdPrior.VolumeFactorFor(40_000) > CrowdPrior.VolumeFactorFor(400));
        Assert.True(CrowdPrior.VolumeFactorFor(1_000) < 1d && CrowdPrior.VolumeFactorFor(2_000) > 1d);
    }

    [Theory]
    [InlineData(6.5, 1.0)]     // the prior itself moves nothing
    [InlineData(10.0, 1.10)]   // the full swing at the top of the scale
    [InlineData(1.0, 0.90)]    // and clamped at the bottom
    [InlineData(8.25, 1.05)]   // linear between
    [InlineData(4.75, 0.95)]
    public void The_swing_is_linear_around_the_prior_and_bounded(double shrunk, double factor)
    {
        Assert.Equal(factor, CrowdPrior.FactorForScore((decimal)shrunk), 6);
        Assert.InRange(CrowdPrior.FactorForScore((decimal)shrunk), 1d - CrowdPrior.MaxSwing, 1d + CrowdPrior.MaxSwing);
    }

    [Fact]
    public void Across_the_usual_crowd_range_a_half_point_prediction_lead_still_wins()
    {
        // The model stays the dominant multiplicand: a well-liked 8.0 on 20,000 votes does not
        // overturn half a point of prediction held by a middling 6.0 on 20,000.
        var loved = Tmdb(8.0m, 20_000);
        var middling = Tmdb(6.0m, 20_000);

        Assert.True(Key(6.5m, middling) > Key(6.0m, loved));

        // But at a tie, or a sliver apart — the shape of a shortlist the model scores inside a
        // fraction of a point — the crowd decides.
        Assert.True(Key(6.4m, loved) > Key(6.5m, middling));
    }

    [Fact]
    public void The_served_policy_key_carries_the_crowd_term()
    {
        // The interleaving harness's policy A must reproduce the order the feed serves, so the crowd
        // term is in its key — and genuinely so: the same pair flips when only the crowd differs.
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var a = new PolicyCandidate(id, 7.0m, 1m, 1d, 1m, CrowdPrior: CrowdPrior.FactorFor(Tmdb(10.0m, 2)));
        var b = new PolicyCandidate(other, 7.0m, 1m, 1d, 1m, CrowdPrior: CrowdPrior.FactorFor(Tmdb(7.0m, 5_000)));

        Assert.Equal(other, new CalibratedModelPolicy().Order([a, b])[0].TitleId);
        Assert.Equal(
            7.0m * (decimal)b.CrowdPrior,
            CalibratedModelPolicy.Key(b));

        // A candidate built without one is neutral, which keeps every existing caller's order.
        Assert.Equal(1d, new PolicyCandidate(id, 7.0m, 1m, 1d, 1m).CrowdPrior);
    }

    private static decimal Key(decimal predicted, Title title) => predicted * (decimal)CrowdPrior.FactorFor(title);

    private static Title Tmdb(decimal rating, int votes) => Both(null, 0, rating, votes);

    private static Title Both(decimal? trakt, int traktVotes, decimal? tmdb, int tmdbVotes) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktSlug = string.Empty,
        Name = "crowd fixture",
        TraktRating = trakt,
        TraktVotes = traktVotes,
        TmdbVoteAverage = tmdb,
        TmdbVoteCount = tmdbVotes,
    };
}
