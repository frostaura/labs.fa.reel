using System.Globalization;
using FrostAura.Reel.Infrastructure.Background;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// R1-28: the weekly digest's scheduling window — Friday from 16:00 UTC to the end of the day — as
/// one testable rule rather than a condition inlined in the timer loop. Every edge is pinned: the
/// minute before and the minute it opens, the last minute of Friday and the first of Saturday, and
/// the same hour on the days either side.
/// </summary>
public class WeeklyDigestWindowTests
{
    [Theory]
    [InlineData("2026-10-30T16:00:00", true)]
    [InlineData("2026-10-30T16:00:01", true)]
    [InlineData("2026-10-30T20:30:00", true)]
    [InlineData("2026-10-30T23:59:59", true)]
    [InlineData("2026-10-30T15:59:59", false)]
    [InlineData("2026-10-30T00:00:00", false)]
    [InlineData("2026-10-31T00:00:00", false)]
    [InlineData("2026-10-31T16:00:00", false)]
    [InlineData("2026-10-29T16:00:00", false)]
    [InlineData("2026-11-06T16:15:00", true)]
    public void The_digest_builds_only_on_Friday_from_16_UTC(string utc, bool open)
    {
        var now = DateTime.SpecifyKind(DateTime.Parse(utc, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        Assert.Equal(open, WeeklyDigestService.IsDigestWindow(now));
    }

    [Fact]
    public void The_fixture_dates_are_the_weekdays_they_claim()
    {
        Assert.Equal(DayOfWeek.Friday, new DateTime(2026, 10, 30).DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, new DateTime(2026, 10, 31).DayOfWeek);
        Assert.Equal(DayOfWeek.Thursday, new DateTime(2026, 10, 29).DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, new DateTime(2026, 11, 6).DayOfWeek);
        Assert.Equal(16, WeeklyDigestService.DigestHourUtc);
    }
}
