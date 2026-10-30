using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// The kind rows' admission rules, shared by the build and the Home refill (HREV6-RET-01). The
/// endpoint behaviour is pinned over Postgres in <c>FeedKindRowRefillTests</c>; this pins the rule's
/// edges — the window, the floor, the two bounds — that a fixture of a few titles cannot all reach.
/// </summary>
public class FeedRowRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, true)]
    [InlineData(899, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    [InlineData(-30, true)] // a dated upcoming release is new, as the build has always read it
    public void New_for_you_is_a_release_inside_900_days(int daysAgo, bool expected) =>
        Assert.Equal(expected, FeedRowRules.IsNewForYou(7.0m, Movie(released: Now.AddDays(-daysAgo)), 0m, Now));

    [Fact]
    public void A_show_is_dated_by_its_first_air_date_and_an_undated_title_is_never_new()
    {
        var show = Movie(released: null);
        show.MediaType = MediaType.Show;
        show.FirstAiredAt = Now.AddDays(-100);
        Assert.True(FeedRowRules.IsNewForYou(7.0m, show, 0m, Now));
        Assert.False(FeedRowRules.IsNewForYou(7.0m, Movie(released: null), 0m, Now));
    }

    [Theory]
    [InlineData(7.45, 7.5, true)]  // shown 7.5: held to the badge
    [InlineData(7.44, 7.5, false)]
    [InlineData(5.00, 0, true)]   // no bar clears everything
    public void New_for_you_holds_the_bar_to_the_badge(decimal predicted, decimal bar, bool expected) =>
        Assert.Equal(expected, FeedRowRules.IsNewForYou(predicted, Movie(released: Now.AddDays(-10)), bar, Now));

    [Theory]
    [InlineData(5.94, 0, false)]  // shown 5.9: under the deep-cuts floor of 6 with no bar
    [InlineData(5.95, 0, true)]   // shown 6.0
    [InlineData(6.50, 7, false)]  // the reader's own bar outranks the floor when higher
    [InlineData(7.00, 7, true)]
    public void A_deep_cut_clears_six_or_the_bar_whichever_is_higher(decimal predicted, decimal bar, bool expected) =>
        Assert.Equal(expected, FeedRowRules.IsDeepCut(predicted, Movie(popularity: 1m), bar, FeedRowRules.PopularityBound.Below(10m)));

    [Theory]
    [InlineData(31.24, true)]
    [InlineData(31.25, true)]  // the most popular dealt deep cut itself sits inside
    [InlineData(31.26, false)]
    [InlineData(104, false)]
    public void The_refill_bound_is_the_most_popular_deep_cut_the_build_dealt(decimal popularity, bool expected)
    {
        var bound = FeedRowRules.DeepCutBoundFromDealt([Movie(popularity: 12m), Movie(popularity: 31.25m), Movie(popularity: 7m)]);
        Assert.Equal(expected, FeedRowRules.IsDeepCut(8.0m, Movie(popularity: popularity), 0m, bound));
    }

    [Fact]
    public void No_dealt_deep_cuts_admit_nothing_but_unknown_popularity_reads_as_zero()
    {
        var none = FeedRowRules.DeepCutBoundFromDealt([]);
        Assert.False(FeedRowRules.IsDeepCut(8.0m, Movie(popularity: 0m), 0m, none));

        var unknownDealt = FeedRowRules.DeepCutBoundFromDealt([Movie(popularity: null)]);
        Assert.True(FeedRowRules.IsDeepCut(8.0m, Movie(popularity: null), 0m, unknownDealt));
        Assert.False(FeedRowRules.IsDeepCut(8.0m, Movie(popularity: 0.01m), 0m, unknownDealt));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(37)]
    public void The_build_bound_is_the_pool_70th_percentile_exactly_as_before(int poolSize)
    {
        // The formula this replaced: ascending popularity, element Count * 7 / 10, strictly below.
        var pool = Enumerable.Range(0, poolSize).Select(i => Movie(popularity: i % 4 == 0 ? null : (i * 37 % 101) + 0.5m)).ToList();
        var was = pool.Select(t => t.TmdbPopularity ?? 0m).OrderBy(p => p).ElementAt(pool.Count * 7 / 10);

        var bound = FeedRowRules.DeepCutBoundFromPool(pool);

        Assert.Equal(FeedRowRules.PopularityBound.Below(was), bound);
        Assert.False(bound.Admits(was));
        Assert.True(bound.Admits(was - 0.01m) || was == 0m);
    }

    [Fact]
    public void An_empty_build_pool_admits_no_deep_cut() =>
        Assert.False(FeedRowRules.DeepCutBoundFromPool([]).Admits(0m));

    private static Title Movie(DateTime? released = null, decimal? popularity = 5m) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        Name = "t",
        ReleasedAt = released,
        TmdbPopularity = popularity,
    };
}
