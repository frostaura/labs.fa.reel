using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FrostAura.Reel.Tests.Auth;

/// <summary>The per-account Ask turn limits (FD-8, J-8) and the settings-PIN lockout (K-4).</summary>
public class InboundLimitTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AskTurnLimiter Limiter(ManualClock clock, Dictionary<string, string?>? settings = null) =>
        new(Options.Create(ValidatedOptions.From<AskTurnLimitOptions>(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build())), clock);

    /// <summary>Starts a turn and finishes it at once, as a short answer would.</summary>
    private static AskTurnLimiter.Refusal? Ask(AskTurnLimiter limiter, Guid account, AccountTier tier = AccountTier.Founder)
    {
        var (lease, refusal) = limiter.TryStart(account, tier);
        lease?.Dispose();
        return refusal;
    }

    [Fact]
    public void The_seventh_ask_inside_a_minute_is_refused_with_a_code_and_the_wait()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < 6; i++)
        {
            Assert.Null(Ask(limiter, account));
            clock.Now = clock.Now.AddSeconds(5);
        }

        var refused = Ask(limiter, account);

        Assert.NotNull(refused);
        Assert.Equal("ask_rate_minute", refused.Code);
        Assert.Equal(429, refused.Status);
        // The first of the six was 30 s ago, so its slot frees in 30 s.
        Assert.Equal(TimeSpan.FromSeconds(30), refused.RetryAfter);

        clock.Now = clock.Now.AddSeconds(30);
        Assert.Null(Ask(limiter, account));
    }

    [Fact]
    public void The_hour_and_day_limits_follow_fd8_by_tier()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock);
        var founder = Guid.NewGuid();

        // 20 in an hour, a minute apart so the burst limit never fires.
        for (var i = 0; i < 20; i++)
        {
            Assert.Null(Ask(limiter, founder));
            clock.Now = clock.Now.AddMinutes(1);
        }

        Assert.Equal("ask_rate_hour", Ask(limiter, founder)?.Code);

        // A Free account gets 10 a day: the 11th, spread over hours, is refused by the day limit.
        var free = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(Ask(limiter, free, AccountTier.Free));
            clock.Now = clock.Now.AddHours(1);
        }

        var refused = Ask(limiter, free, AccountTier.Free);
        Assert.Equal("ask_rate_day", refused?.Code);
        // Rolling 24 h: the first of the ten frees 24 h after it was asked, which is 14 h from now.
        Assert.Equal(TimeSpan.FromHours(14), refused!.RetryAfter);
    }

    [Fact]
    public void One_turn_at_a_time_until_the_lease_ends_and_a_refusal_spends_nothing()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock, new() { [AskTurnLimitOptions.PerMinuteKey] = "2" });
        var account = Guid.NewGuid();

        var (lease, refusal) = limiter.TryStart(account, AccountTier.Paid);
        Assert.NotNull(lease);
        Assert.Null(refusal);

        // While the first streams, every further ask is "in flight" — and none of them is counted.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("turn_in_progress", Ask(limiter, account, AccountTier.Paid)?.Code);
        }

        // Another account is a different partition.
        Assert.Null(Ask(limiter, Guid.NewGuid(), AccountTier.Paid));

        lease!.Dispose();
        lease.Dispose(); // idempotent: a double dispose must not free a second slot
        Assert.Null(Ask(limiter, account, AccountTier.Paid)); // the 2nd of PER_MINUTE=2
        Assert.Equal("ask_rate_minute", Ask(limiter, account, AccountTier.Paid)?.Code);
    }

    [Fact]
    public void A_present_but_invalid_limit_fails_validation_naming_the_key()
    {
        var options = ValidatedOptions.From<AskTurnLimitOptions>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AskTurnLimitOptions.PerHourKey] = "0" }).Build());

        var problem = Assert.Single(options.Problems);
        Assert.Contains(AskTurnLimitOptions.PerHourKey, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Five_wrong_pins_lock_the_sixth_attempt_for_the_rest_of_the_window()
    {
        var clock = new ManualClock();
        var pins = new PinAttemptLimiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < PinAttemptLimiter.MaxFailures; i++)
        {
            Assert.Null(pins.LockedFor(account));
            pins.RecordFailure(account);
            clock.Now = clock.Now.AddMinutes(1);
        }

        // Five failures over minutes 0..4; now minute 5: locked until the first is 15 minutes old.
        Assert.Equal(TimeSpan.FromMinutes(10), pins.LockedFor(account));
        Assert.Null(pins.LockedFor(Guid.NewGuid()));

        clock.Now = clock.Now.AddMinutes(10);
        Assert.Null(pins.LockedFor(account)); // the oldest aged out: one more try is allowed

        pins.RecordFailure(account);
        Assert.NotNull(pins.LockedFor(account));
    }

    [Fact]
    public void A_correct_pin_clears_the_failures()
    {
        var clock = new ManualClock();
        var pins = new PinAttemptLimiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < PinAttemptLimiter.MaxFailures - 1; i++)
        {
            pins.RecordFailure(account);
        }

        pins.RecordSuccess(account);
        pins.RecordFailure(account);

        Assert.Null(pins.LockedFor(account));
    }
}
