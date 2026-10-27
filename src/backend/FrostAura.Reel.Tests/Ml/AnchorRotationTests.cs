using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// "Because you loved …" must not show the same three titles for ever — and must not churn either.
///
/// Regression for the founder's 2026-08-07 report: with 3,605 ratings the strict rating-first
/// top-10 is frozen, so the old selection (one drift slot + two fixed all-time slots) anchored on
/// the same titles on every single build. The first fix rotated per BUILD, which over-corrected —
/// delta sync polls every 5 minutes and the rows changed 3×/hour. The standing contract is
/// day-granular: <see cref="AnchorRotation.OffsetFor"/> is constant within the account's day and moves
/// across days, and the hold-back set the handler passes is the previous DAY's anchors (or the
/// superseded snapshot's, on a user-forced refresh). These tests pin the properties the selection
/// has to hold — anchors move when the hold-back says they must, and they are a deterministic
/// function of their inputs so a build stays reproducible and the tests stay assertable.
/// </summary>
public class AnchorRotationTests
{
    private static readonly Guid[] Pool = Enumerable.Range(0, 12)
        .Select(i => new Guid($"0000000{i:x}-0000-0000-0000-000000000000"))
        .ToArray();

    [Fact]
    public void A_build_that_holds_back_its_predecessors_anchors_cannot_repeat_them()
    {
        var first = AnchorRotation.Select(
            driftPick: Pool[0],
            anchorPool: Pool,
            allTimeOrder: Pool,
            previousAnchors: new HashSet<Guid>(),
            rotationOffset: 0,
            count: 3);

        // The second selection sees the first's anchors as held back — what the handler passes for
        // day N+1 (yesterday's anchors) and for a manual refresh (the superseded snapshot's).
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
    public void Every_timestamp_inside_one_utc_day_shares_a_single_rotation_offset()
    {
        // The stability half of the contract: delta sync polls every 5 minutes, and every one of
        // those rebuilds must read the loved pool from the SAME starting point, or the rows churn
        // under the user all day (the exact 3×/hour QA failure the day granularity exists to stop).
        var midnight = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);
        var morning = new DateTime(2026, 8, 7, 10, 19, 26, DateTimeKind.Utc);
        var lastTick = new DateTime(2026, 8, 7, 23, 59, 59, DateTimeKind.Utc);

        Assert.Equal(AnchorRotation.OffsetFor(midnight), AnchorRotation.OffsetFor(morning));
        Assert.Equal(AnchorRotation.OffsetFor(midnight), AnchorRotation.OffsetFor(morning.AddMinutes(5)));
        Assert.Equal(AnchorRotation.OffsetFor(midnight), AnchorRotation.OffsetFor(lastTick));
    }

    [Fact]
    public void Consecutive_utc_days_give_different_rotation_offsets()
    {
        // The freshness half: a new day must start reading the pool somewhere else, so tomorrow's
        // first build cannot land on today's rows even before the hold-back set weighs in.
        var today = new DateTime(2026, 8, 7, 10, 19, 26, DateTimeKind.Utc);
        Assert.NotEqual(AnchorRotation.OffsetFor(today), AnchorRotation.OffsetFor(today.AddDays(1)));
        Assert.NotEqual(AnchorRotation.OffsetFor(today.AddDays(1)), AnchorRotation.OffsetFor(today.AddDays(2)));
        Assert.True(AnchorRotation.OffsetFor(today) >= 0, "the offset must stay non-negative for the modulo.");
    }

    /// <summary>
    /// R1-16: the rotation offset turns over at the account's midnight. With no zone it is the UTC
    /// day number the old tick arithmetic produced, so an account that set nothing rotates exactly as
    /// before; at +02:00 it is constant from 22:00 UTC to 22:00 UTC.
    /// </summary>
    [Fact]
    public void The_offset_follows_the_accounts_day()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var hours = 0; hours < 24 * 400; hours += 7)
        {
            var instant = start.AddHours(hours);
            var legacy = (int)((ulong)(instant.Ticks / TimeSpan.TicksPerDay) % int.MaxValue);
            Assert.Equal(legacy, AnchorRotation.OffsetFor(instant));
        }

        var lateEvening = new DateTime(2026, 10, 26, 22, 0, 0, DateTimeKind.Utc); // 00:00 local, the 27th
        const string Johannesburg = "Africa/Johannesburg";
        Assert.Equal(AnchorRotation.OffsetFor(lateEvening, Johannesburg), AnchorRotation.OffsetFor(lateEvening.AddHours(3.5), Johannesburg));
        Assert.NotEqual(AnchorRotation.OffsetFor(lateEvening.AddMinutes(-1), Johannesburg), AnchorRotation.OffsetFor(lateEvening, Johannesburg));
        Assert.NotEqual(AnchorRotation.OffsetFor(lateEvening), AnchorRotation.OffsetFor(lateEvening.AddHours(3.5)));
    }
}
