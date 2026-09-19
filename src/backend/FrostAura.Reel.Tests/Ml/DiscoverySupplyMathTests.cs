using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The arithmetic behind supply: how deep a build reads, and which medium each arm asks for.
///
/// <para>
/// Both were previously constants. Discovery read page 1 for ever, so a user working through the
/// reservoir was re-offered the head of the same popularity-ordered lists every build — the
/// mechanism behind "the picks get weaker every round". And the media split was a fixed one movie
/// arm plus one TV arm per genre, which on the founder's account produced a live feed of nothing but
/// television.
/// </para>
/// </summary>
public class DiscoverySupplyMathTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(19, 1, 1)]
    [InlineData(20, 2, 1)]
    [InlineData(59, 3, 1)]
    [InlineData(60, 4, 2)]
    [InlineData(120, 7, 3)]
    [InlineData(240, 13, 5)]
    // Depth is unbounded; breadth is not. Five pages × four genres is already half a build's calls,
    // and the arms below it are the ones that make the pool personal rather than merely bigger.
    [InlineData(10_000, 501, 5)]
    public void Consumption_buys_depth_faster_than_breadth(int consumed, int expectedStart, int expectedPages)
    {
        Assert.Equal(expectedStart, DiscoveryPaging.StartPage(consumed));
        Assert.Equal(expectedPages, DiscoveryPaging.PageCount(consumed));
    }

    [Fact]
    public void A_negative_consumption_cannot_produce_page_zero()
    {
        // TMDB rejects page 0, and a clock skew or a corrupt counter must degrade to "start at the
        // top", never to a call that 400s the whole arm.
        Assert.Equal(1, DiscoveryPaging.StartPage(-5));
        Assert.Equal(1, DiscoveryPaging.PageCount(-5));
    }

    [Fact]
    public void Row_deepening_never_re_reads_the_page_the_row_is_already_showing()
    {
        Assert.Equal(2, DiscoveryPaging.DeepenPage(0, 25));
        Assert.Equal(2, DiscoveryPaging.DeepenPage(19, 25));
        Assert.Equal(3, DiscoveryPaging.DeepenPage(20, 25));
        Assert.Equal(25, DiscoveryPaging.DeepenPage(10_000, 25));
    }

    [Fact]
    public void The_global_sweep_walks_a_different_page_each_day_and_wraps_inside_the_span()
    {
        var day = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Utc);
        var pages = Enumerable.Range(0, 10)
            .Select(i => DiscoveryPaging.RotatedPage(AnchorRotation.OffsetFor(day.AddDays(i)), 5))
            .ToList();

        Assert.All(pages, p => Assert.InRange(p, 1, 5));
        // Consecutive days differ, and ten days cover the whole span rather than oscillating.
        Assert.All(pages.Zip(pages.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal(5, pages.Distinct().Count());
        // Stable within a UTC day: two builds on the same day sweep the same page, so a pass is
        // reproducible from its own timestamp.
        Assert.Equal(pages[0], DiscoveryPaging.RotatedPage(AnchorRotation.OffsetFor(day.AddHours(20)), 5));
    }

    [Fact]
    public void The_media_plan_tracks_the_users_own_rated_mix()
    {
        var planner = new MediaPlanner(0.75);

        var plan = Enumerable.Range(0, 8).Select(_ => planner.NextIsMovie()!.Value).ToList();

        Assert.Equal(6, plan.Count(m => m));
        Assert.Equal(2, plan.Count(m => !m));
        // Largest-remainder, not "all the movies then all the shows": the mix has to hold at every
        // prefix, because the call budget can end the plan early.
        Assert.Equal(3, plan.Take(4).Count(m => m));
    }

    [Fact]
    public void An_even_mix_alternates()
    {
        var planner = new MediaPlanner(0.5);

        var plan = Enumerable.Range(0, 6).Select(_ => planner.NextIsMovie()!.Value).ToList();

        Assert.Equal([true, false, true, false, true, false], plan);
    }

    [Fact]
    public void Neither_medium_is_ever_starved_however_lopsided_the_history()
    {
        // A pool that can never contain movies cannot produce the evidence that would change the
        // ratio — the floor is a supply hedge, not a claim about taste.
        var planner = new MediaPlanner(0.0);

        var plan = Enumerable.Range(0, 10).Select(_ => planner.NextIsMovie()!.Value).ToList();

        Assert.Equal(MediaPlanner.MinShare, planner.Target);
        Assert.Contains(true, plan);
        Assert.Contains(false, plan);
        Assert.Equal(8, plan.Count(m => !m));
    }

    [Fact]
    public void An_arm_with_only_one_medium_available_takes_it_and_still_counts()
    {
        var planner = new MediaPlanner(0.5);

        // "romance" and "thriller" exist in TMDB's movie id space and not in its TV one.
        Assert.True(planner.NextIsMovie(movieAvailable: true, tvAvailable: false));
        // The forced movie counts, so the next free slot corrects toward TV rather than ignoring it.
        Assert.False(planner.NextIsMovie()!.Value);
        Assert.Null(planner.NextIsMovie(movieAvailable: false, tvAvailable: false));
    }

    [Fact]
    public void Nan_is_treated_as_no_information_rather_than_propagating()
    {
        var planner = new MediaPlanner(double.NaN);

        Assert.Equal(MediaPlanner.NeutralShare, planner.Target);
        Assert.True(planner.NextIsMovie()!.Value);
    }

    [Fact]
    public void Seed_rotation_is_a_pure_function_of_the_builds_own_timestamp()
    {
        var pool = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var monday = new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc);

        var morning = AnchorRotation.Rotate(pool, AnchorRotation.OffsetFor(monday)).Take(4).ToList();
        var evening = AnchorRotation.Rotate(pool, AnchorRotation.OffsetFor(monday.AddHours(15))).Take(4).ToList();
        var tuesday = AnchorRotation.Rotate(pool, AnchorRotation.OffsetFor(monday.AddDays(1))).Take(4).ToList();

        // Stable within a UTC day — the same replayability contract the row anchors hold — and
        // guaranteed to move across days, which is the whole point of rotating a top-ten that a
        // heavy rater's ratings can no longer reorder.
        Assert.Equal(morning, evening);
        Assert.NotEqual(morning, tuesday);
        Assert.Equal(pool.Count, AnchorRotation.Rotate(pool, AnchorRotation.OffsetFor(monday)).Distinct().Count());
        Assert.Empty(AnchorRotation.Rotate<Guid>([], 12));
    }
}
