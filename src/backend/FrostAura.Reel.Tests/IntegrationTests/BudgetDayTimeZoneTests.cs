using System.Net;
using System.Net.Http.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Budget days follow the zone the account set through the settings API (R1-14, WS-1 task 12,
/// ADD-22), end to end on real Postgres: <c>PUT /api/settings</c> writes the zone into the settings
/// jsonb, the host's own <see cref="IAccountZoneSource"/> reads it back, and the ledger row for a
/// per-account purpose lands on the account's local date while catalogue-wide work stays on UTC.
///
/// <para>The first zone source probed for an <c>Accounts.Timezone</c> column that M7 never added, so
/// every account read as UTC however its zone was set — and Ask told the user the allowance resets
/// "at midnight in your account's time zone". Only the host's real source against the real settings
/// document can catch that; the unit tests hand the calendar a dictionary.</para>
///
/// <para>The clock is pinned only in the calendar under test; the zone is read through the host. The
/// cases straddle midnight on both sides of UTC, because a spend at a time when the local and UTC
/// dates agree (23:30 in Johannesburg is 21:30 UTC) cannot tell a working zone from a missing one.</para>
/// </summary>
[Collection("postgres")]
public class BudgetDayTimeZoneTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    public static TheoryData<string?, string, string, string> Spends => new()
    {
        // 00:30 on the 27th in Johannesburg (+02:00) is 22:30 UTC on the 26th: local is a day AHEAD.
        { "Africa/Johannesburg", "2026-10-26T22:30:00Z", "2026-10-27", "2026-10-26" },
        // 23:30 on the 26th in São Paulo (-03:00, no daylight saving since 2019) is 02:30 UTC on the
        // 27th: local is a day BEHIND, so a fix that only ever adds a day cannot pass both.
        { "America/Sao_Paulo", "2026-10-27T02:30:00Z", "2026-10-26", "2026-10-27" },
        // 23:30 local in Johannesburg: both calendars agree, and the row says so.
        { "Africa/Johannesburg", "2026-10-26T21:30:00Z", "2026-10-26", "2026-10-26" },
        // Never set: the UTC day, exactly the behaviour before ADD-22.
        { null, "2026-10-26T22:30:00Z", "2026-10-26", "2026-10-26" },
    };

    [Theory]
    [MemberData(nameof(Spends))]
    public async Task A_spend_is_stamped_on_the_local_date_of_the_zone_set_through_the_settings_api(
        string? zone, string utcNow, string expectedAccountDay, string expectedUtcDay)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"budget-tz-{Guid.NewGuid():N}");
        if (zone is not null)
        {
            using var client = await api.CreateSignedInClientAsync(accountId);
            using var response = await client.PutAsJsonAsync("/api/settings", new { timeZone = zone });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The host's own zone source — the thing R1-14 was about — under a pinned clock.
        var source = api.Services.GetRequiredService<IAccountZoneSource>();
        var calendar = new BudgetCalendar(source, new FixedTime(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture)));
        var recorder = new ApiUsageRecorder(
            api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance, calendar);

        // The gate reads its ceiling on this day before the call; the recorder stamps the row after it.
        var accountDay = DateTime.Parse(expectedAccountDay, System.Globalization.CultureInfo.InvariantCulture);
        var utcDay = DateTime.Parse(expectedUtcDay, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(accountDay.Date, (await calendar.TodayAsync(accountId, default)).Date);

        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.AskPlan, "test/model", accountId, promptTokens: 40, completionTokens: 8);
        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.ExtractAttributes, "test/model", accountId, promptTokens: 4, completionTokens: 1);
        await recorder.FlushAsync(CancellationToken.None);

        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.ExternalApiUsages.Where(u => u.AccountId == accountId).ToListAsync();

        Assert.Equal(accountDay.Date, rows.Single(r => r.Purpose == ApiUsagePurpose.AskPlan).Day.Date);
        // A global ceiling sums across accounts, so its rows stay on the calendar it is read on.
        Assert.Equal(utcDay.Date, rows.Single(r => r.Purpose == ApiUsagePurpose.ExtractAttributes).Day.Date);
    }

    [Fact]
    public async Task Clearing_the_zone_through_the_settings_api_returns_the_account_to_the_utc_day()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"budget-tz-clear-{Guid.NewGuid():N}");
        using var client = await api.CreateSignedInClientAsync(accountId);
        var source = api.Services.GetRequiredService<IAccountZoneSource>();
        var lateEvening = new FixedTime(new DateTimeOffset(2026, 10, 26, 22, 30, 0, TimeSpan.Zero));

        using (var set = await client.PutAsJsonAsync("/api/settings", new { timeZone = "Africa/Johannesburg" }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        Assert.Equal("Africa/Johannesburg", await source.ZoneIdAsync(accountId, default));
        Assert.Equal("Africa/Johannesburg", source.ZoneId(accountId));
        Assert.Equal(new DateTime(2026, 10, 27), (await new BudgetCalendar(source, lateEvening).TodayAsync(accountId, default)).Date);

        using (var cleared = await client.PutAsJsonAsync("/api/settings", new { timeZone = string.Empty }))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }

        Assert.Null(await source.ZoneIdAsync(accountId, default));
        Assert.Equal(new DateTime(2026, 10, 26), (await new BudgetCalendar(source, lateEvening).TodayAsync(accountId, default)).Date);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
