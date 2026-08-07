using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// "Because you loved …" must not show the same three titles for ever.
///
/// Regression for the founder's 2026-08-07 report: with 3,605 ratings the strict rating-first
/// top-10 is frozen, so the old selection (one drift slot + two fixed all-time slots) anchored on
/// the same titles on every single build. These tests pin the two properties the fix has to hold at
/// once — the anchors MOVE between builds, and they are a deterministic function of their inputs so
/// a build stays reproducible and the tests stay assertable.
/// </summary>
public class AnchorRotationTests
{
    private static readonly Guid[] Pool = Enumerable.Range(0, 12)
        .Select(i => new Guid($"0000000{i:x}-0000-0000-0000-000000000000"))
        .ToArray();

    [Fact]
    public void Two_consecutive_builds_over_identical_ratings_produce_different_anchors()
    {
        var first = AnchorRotation.Select(
            driftPick: Pool[0],
            anchorPool: Pool,
            allTimeOrder: Pool,
            previousAnchors: new HashSet<Guid>(),
            rotationOffset: 0,
            count: 3);

        // The second build sees the first build's anchors as "previous" — exactly what the handler
        // reads off the Active snapshot before superseding it.
        var second = AnchorRotation.Select(
            driftPick: Pool[0],
            anchorPool: Pool,
            allTimeOrder: Pool,
            previousAnchors: first.ToHashSet(),
            rotationOffset: 1,
            count: 3);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void A_long_run_of_builds_keeps_turning_the_rows_over()
    {
        // Ten builds in a row, each told what the last one used: no build may repeat its predecessor,
        // and across the run the rows must visit a real spread of the loved pool rather than
        // oscillating between two sets.
        var previous = new HashSet<Guid>();
        var seen = new HashSet<Guid>();
        var sets = new List<string>();
        for (var build = 0; build < 10; build++)
        {
            var anchors = AnchorRotation.Select(
                driftPick: Pool[0],
                anchorPool: Pool,
                allTimeOrder: Pool,
                previousAnchors: previous,
                rotationOffset: build,
                count: 3);

            Assert.Empty(anchors.Intersect(previous));
            seen.UnionWith(anchors);
            sets.Add(string.Join(",", anchors.Order()));
            previous = anchors.ToHashSet();
        }

        Assert.True(seen.Count >= 8, $"ten builds should reach most of a 12-title pool (reached {seen.Count}).");

        // The specific failure this guards: "different from last time" is satisfied by an A/B/A/B
        // alternation, which is what a stride-of-one rotation actually produced against the founder's
        // real library. Ten builds must yield more than two distinct anchor sets.
        Assert.True(sets.Distinct().Count() > 2,
            $"anchors must walk the pool, not alternate between two sets (saw {sets.Distinct().Count()} distinct sets).");
    }

    [Fact]
    public void Selection_is_deterministic_within_a_build()
    {
        // Same inputs ⇒ same anchors. This is what keeps a build reproducible from its persisted
        // GeneratedAt, and it is why the rotation is seeded rather than drawn from Random.
        var a = AnchorRotation.Select(Pool[2], Pool, Pool, new HashSet<Guid>(), rotationOffset: 7, count: 3);
        var b = AnchorRotation.Select(Pool[2], Pool, Pool, new HashSet<Guid>(), rotationOffset: 7, count: 3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_drift_pick_leads_when_it_is_not_a_repeat()
    {
        // One row still tracks where taste is NOW — the property the original single drift slot was
        // added for, which the rotation must not trade away.
        var anchors = AnchorRotation.Select(Pool[5], Pool, Pool, new HashSet<Guid>(), rotationOffset: 3, count: 3);
        Assert.Equal(Pool[5], anchors[0]);
    }

    [Fact]
    public void A_pool_too_small_to_rotate_repeats_rather_than_returning_fewer_rows()
    {
        // A brand-new account may have only two loved titles. A repeated row beats a missing one, so
        // the exclusion is a preference, never a hard constraint that can starve the feed.
        var tiny = new[] { Pool[0], Pool[1] };
        var anchors = AnchorRotation.Select(
            driftPick: Pool[0],
            anchorPool: tiny,
            allTimeOrder: tiny,
            previousAnchors: tiny.ToHashSet(),
            rotationOffset: 4,
            count: 3);

        Assert.Equal(2, anchors.Count);
        Assert.Equal(anchors.Distinct().Count(), anchors.Count);
    }

    [Fact]
    public void No_loved_titles_at_all_is_an_empty_selection_not_a_throw()
    {
        var anchors = AnchorRotation.Select(null, [], [], new HashSet<Guid>(), rotationOffset: 0, count: 3);
        Assert.Empty(anchors);
    }

    [Fact]
    public void Consecutive_build_timestamps_give_different_rotation_offsets()
    {
        // The offset is minute-grained: two builds a few minutes apart (the founder's actual cadence
        // when tuning filters) must not land on the same starting point in the pool.
        var first = new DateTime(2026, 8, 7, 10, 19, 26, DateTimeKind.Utc);
        Assert.NotEqual(AnchorRotation.OffsetFor(first), AnchorRotation.OffsetFor(first.AddMinutes(5)));
        Assert.True(AnchorRotation.OffsetFor(first) >= 0, "the offset must stay non-negative for the modulo.");
    }
}
