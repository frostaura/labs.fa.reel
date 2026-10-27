using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.Auth;

/// <summary>
/// The pure rules behind WS-8's P2 account settings: the founder allow-list's parsing (K-10, FD-11)
/// and the account day an IANA zone produces (ADD-22).
/// </summary>
public class AccountSettingsRulesTests
{
    private static FounderAccessOptions Founders(string? value) =>
        ValidatedOptions.From<FounderAccessOptions>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FounderAccessOptions.SlugsKey] = value })
            .Build());

    [Fact]
    public void The_allow_list_is_case_insensitive_and_tolerates_commas_and_whitespace()
    {
        var options = Founders(" DeanMar09 ,\tsecond-founder\nthird.one ");

        Assert.Empty(options.Problems);
        Assert.Equal(["deanmar09", "second-founder", "third.one"], options.Slugs.Order(StringComparer.Ordinal));
        Assert.True(options.IsFounder("deanmar09"));
        Assert.True(options.IsFounder("DEANMAR09"));
        Assert.False(options.IsFounder("deanmar"));
        Assert.False(options.IsFounder(null));
        Assert.False(options.IsFounder(string.Empty));
    }

    [Fact]
    public void An_unset_allow_list_grants_nobody()
    {
        var options = Founders(null);

        Assert.Empty(options.Problems);
        Assert.Empty(options.Slugs);
        Assert.False(options.IsFounder("deanmar09"));
    }

    [Fact]
    public void A_value_that_cannot_be_a_slug_stops_the_host_without_echoing_it()
    {
        var options = Founders("deanmar09, https://trakt.tv/users/x, sk-or-v1-abc/def");

        var problem = Assert.Single(options.Problems);
        Assert.Contains(FounderAccessOptions.SlugsKey, problem, StringComparison.Ordinal);
        Assert.Contains("2 entries", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Africa/Johannesburg", "Africa/Johannesburg")]
    [InlineData(" America/New_York ", "America/New_York")]
    [InlineData("UTC", "UTC")]
    [InlineData("America/Argentina/Buenos_Aires", "America/Argentina/Buenos_Aires")]
    public void An_IANA_zone_normalises_to_its_own_spelling(string input, string expected) =>
        Assert.Equal(expected, AccountTimeZone.Normalise(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("+02:00")]
    [InlineData("South Africa Standard Time")]
    [InlineData("../../etc/passwd")]
    [InlineData("Africa/../Johannesburg")]
    public void Anything_else_is_refused(string? input) =>
        Assert.Null(AccountTimeZone.Normalise(input));

    [Fact]
    public void The_account_day_turns_at_local_midnight_not_UTC_midnight()
    {
        // 23:30 UTC on 26 October is already 27 October at +0200.
        var lateUtc = new DateTime(2026, 10, 26, 23, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 10, 27), AccountTimeZone.LocalDate("Africa/Johannesburg", lateUtc));
        Assert.Equal(new DateOnly(2026, 10, 26), AccountTimeZone.LocalDate(null, lateUtc));
        Assert.Equal(new DateTime(2026, 10, 26, 22, 0, 0, DateTimeKind.Utc), AccountTimeZone.DayStartUtc("Africa/Johannesburg", lateUtc));
        Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), AccountTimeZone.DayStartUtc(null, lateUtc));
    }

    [Theory]
    [InlineData("Africa/Johannesburg", "2026-10-27T22:00:00Z")]
    [InlineData(null, "2026-10-27T00:00:00Z")]
    [InlineData("Pacific/Kiritimati", "2026-10-27T10:00:00Z")]
    public void The_next_day_starts_at_the_next_local_midnight(string? zone, string expected)
    {
        var lateUtc = new DateTime(2026, 10, 26, 23, 30, 0, DateTimeKind.Utc);

        var next = AccountTimeZone.NextDayStartUtc(zone, lateUtc);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), next);
        Assert.Equal(AccountTimeZone.LocalDate(zone, lateUtc).AddDays(1), AccountTimeZone.LocalDate(zone, next));
    }

    [Fact]
    public void A_day_that_ends_in_a_skipped_midnight_hands_over_at_the_next_days_first_instant()
    {
        // America/Santiago springs forward at 00:00 on 6 September 2026: the 5th is 23 hours long
        // for the turn window, and its successor starts at the first instant that exists.
        var evening = new DateTime(2026, 9, 6, 2, 0, 0, DateTimeKind.Utc); // 22:00 on the 5th, -04:00

        var next = AccountTimeZone.NextDayStartUtc("America/Santiago", evening);

        Assert.Equal(AccountTimeZone.DayStartUtc("America/Santiago", next), next);
        Assert.Equal(new DateOnly(2026, 9, 6), AccountTimeZone.LocalDate("America/Santiago", next));
        Assert.True(next > evening && next - evening <= TimeSpan.FromHours(3));
    }

    [Fact]
    public void A_zone_this_host_cannot_resolve_counts_in_UTC()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.Same(TimeZoneInfo.Utc, AccountTimeZone.Resolve("Mars/Olympus_Mons"));
        Assert.Equal(new DateOnly(2026, 10, 26), AccountTimeZone.LocalDate("Mars/Olympus_Mons", now));
    }

    [Fact]
    public void A_day_that_skips_midnight_starts_at_its_first_real_instant()
    {
        // America/Santiago springs forward at 00:00 on 6 September 2026: 00:00–00:59 does not exist.
        var noon = new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Utc);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

        var start = AccountTimeZone.DayStartUtc("America/Santiago", noon);

        var local = TimeZoneInfo.ConvertTimeFromUtc(start, zone);
        Assert.Equal(new DateOnly(2026, 9, 6), DateOnly.FromDateTime(local));
        Assert.False(zone.IsInvalidTime(local));
        Assert.True(local.TimeOfDay <= TimeSpan.FromHours(1));
    }
}
