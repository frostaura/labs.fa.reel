using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// The paginator's media balance (WS-5 task 9): a page on a movie-leaning account carries the
/// account's mix, drawn from further down the lane in rank order, and nothing is lost.
/// </summary>
public class MediaMixOrderTests
{
    [Fact]
    public void A_show_heavy_head_on_a_91_percent_movie_account_is_rebalanced_from_the_lane()
    {
        // Rank order: six shows, then movies.
        string[] lane = ["s1", "s2", "s3", "s4", "s5", "s6", "m1", "m2", "m3", "m4", "m5", "m6"];

        var ordered = MediaMix.BalanceOrder(lane, window: 6, id => id[0] == 'm', movieRatedShare: 0.91m);

        var head = ordered.Take(6).ToList();
        Assert.Equal(5, head.Count(id => id[0] == 'm')); // round(6 × 0.91) = 5, capped one seat below the row
        Assert.Equal("s1", head[0]); // the best-ranked show keeps its seat
        Assert.Equal(lane.Order(), ordered.Order()); // nothing dropped, nothing duplicated
        Assert.Equal(["s2", "s3", "s4", "s5", "s6"], ordered.Skip(6).Take(5)); // displaced shows lead the next page
    }

    [Fact]
    public void A_mixed_library_or_an_already_balanced_page_is_left_alone()
    {
        string[] lane = ["s1", "m1", "s2", "m2", "s3", "m3"];

        Assert.Equal(lane, MediaMix.BalanceOrder(lane, 4, id => id[0] == 'm', movieRatedShare: 0.55m));

        string[] balanced = ["m1", "s1", "m2", "m3", "s2"];
        Assert.Equal(balanced, MediaMix.BalanceOrder(balanced, 4, id => id[0] == 'm', movieRatedShare: 0.91m));
    }
}
