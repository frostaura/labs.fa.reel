using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// Budget days follow the account's time zone (WS-1 task 12, ADD-22): a per-account ceiling resets
/// at the account's midnight, the recorder stamps those rows with the same day, and everything
/// catalogue-wide stays on the UTC day.
/// </summary>
public class BudgetCalendarTests
{
    /// <summary>22:30 UTC on the 26th: 00:30 on the 27th in Johannesburg (+02:00), still the 26th in UTC.</summary>
    private static readonly DateTimeOffset LateEvening = new(2026, 10, 26, 22, 30, 0, TimeSpan.Zero);

    private static readonly DateTime The26th = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime The27th = new(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_per_account_ceiling_counts_on_the_accounts_own_day()
    {
        var account = Guid.NewGuid();
        var calendar = Calendar(new() { [account] = "Africa/Johannesburg" });

        Assert.Equal(The27th, await calendar.TodayAsync(account, default));
        foreach (var purpose in new[]
                 {
                     ApiUsagePurpose.AskPlan, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskAnswer,
                     ApiUsagePurpose.RerankFeed, ApiUsagePurpose.RerankPage, ApiUsagePurpose.CrossReference,
                     ApiUsagePurpose.Lens, ApiUsagePurpose.SearchAgent,
                 })
        {
            Assert.Equal(The27th, calendar.DayFor(account, purpose));
        }
    }

    [Fact]
    public void Catalogue_wide_work_probes_and_unowned_rows_stay_on_the_utc_day()
    {
        var account = Guid.NewGuid();
        var calendar = Calendar(new() { [account] = "Africa/Johannesburg" });

        // A global ceiling sums across accounts, so it must be written on the calendar it is read on.
        Assert.Equal(The26th, calendar.DayFor(account, ApiUsagePurpose.ExtractAttributes));
        Assert.Equal(The26th, calendar.DayFor(account, ApiUsagePurpose.Probe(ApiUsagePurpose.AskPlan)));
        Assert.Equal(The26th, calendar.DayFor(account, purpose: null));
        Assert.Equal(The26th, calendar.DayFor(accountId: null, ApiUsagePurpose.AskPlan));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("UTC")]
    public async Task No_zone_or_an_unknown_one_is_the_utc_day_as_before(string? zone)
    {
        var account = Guid.NewGuid();
        var calendar = Calendar(new() { [account] = zone });

        Assert.Equal(The26th, await calendar.TodayAsync(account, default));
        Assert.Equal(The26th, calendar.DayFor(account, ApiUsagePurpose.AskPlan));
    }

    [Fact]
    public async Task A_zone_is_read_once_and_forgetting_it_reads_the_new_one()
    {
        var account = Guid.NewGuid();
        var zones = new Dictionary<Guid, string?> { [account] = "Africa/Johannesburg" };
        var source = new MapZones(zones);
        var calendar = new BudgetCalendar(source, new FixedTime(LateEvening));

        Assert.Equal(The27th, await calendar.TodayAsync(account, default));
        zones[account] = "UTC";
        Assert.Equal(The27th, calendar.DayFor(account, ApiUsagePurpose.Lens));
        Assert.Equal(1, source.Reads);

        calendar.Forget(account);
        Assert.Equal(The26th, await calendar.TodayAsync(account, default));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void A_zone_that_cannot_be_read_never_costs_the_recorder_its_row()
    {
        var calendar = new BudgetCalendar(new ThrowingZones(), new FixedTime(LateEvening));

        Assert.Equal(The26th, calendar.DayFor(Guid.NewGuid(), ApiUsagePurpose.AskPlan));
    }

    [Fact]
    public async Task The_recorder_stamps_and_the_budget_reads_the_same_account_day()
    {
        var account = Guid.NewGuid();
        var calendar = Calendar(new() { [account] = "Africa/Johannesburg" });
        var recorder = new ApiUsageRecorder(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ApiUsageRecorder>.Instance,
            calendar);

        // The gate checks the ceiling first (warming the zone), then the call is recorded.
        _ = await calendar.TodayAsync(account, default);
        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.AskPlan, "m", account, promptTokens: 10, completionTokens: 5);
        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.ExtractAttributes, "m", account, promptTokens: 1, completionTokens: 1);

        // In flight today, on each row's own day: the Ask row on the 27th, the extraction row on the 26th.
        Assert.Equal((1L, 15L), recorder.PendingToday(ApiProvider.OpenRouter, account, ApiUsagePurpose.AskPlan));
        Assert.Equal((1L, 2L), recorder.PendingTodayAllAccounts(ApiProvider.OpenRouter, ApiUsagePurpose.ExtractAttributes));

    }

    private static BudgetCalendar Calendar(Dictionary<Guid, string?> zones) =>
        new(new MapZones(zones), new FixedTime(LateEvening));

    private sealed class MapZones(Dictionary<Guid, string?> zones) : IAccountZoneSource
    {
        public int Reads { get; private set; }

        public Task<string?> ZoneIdAsync(Guid accountId, CancellationToken ct) => Task.FromResult(ZoneId(accountId));

        public string? ZoneId(Guid accountId)
        {
            Reads++;
            return zones.GetValueOrDefault(accountId);
        }
    }

    private sealed class ThrowingZones : IAccountZoneSource
    {
        public Task<string?> ZoneIdAsync(Guid accountId, CancellationToken ct) => throw new InvalidOperationException("db down");

        public string? ZoneId(Guid accountId) => throw new InvalidOperationException("db down");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
