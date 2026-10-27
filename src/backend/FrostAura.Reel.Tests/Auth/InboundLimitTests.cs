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
    private static AskTurnLimiter.Refusal? Ask(AskTurnLimiter limiter, Guid account, AccountTier tier = AccountTier.Founder, string? zone = null)
    {
        var (lease, refusal) = limiter.TryStart(account, tier, zone);
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
        // The day is the account's calendar day (R1-15) — UTC for an account with no zone — so the
        // allowance comes back at midnight: 22:20 now, so in 1 h 40 min, not 24 h after the first ask.
        Assert.Equal(new TimeSpan(1, 40, 0), refused!.RetryAfter);

        clock.Now = clock.Now.Add(refused.RetryAfter);
        Assert.Null(Ask(limiter, free, AccountTier.Free));
    }

    /// <summary>
    /// R1-15: the day limit counts the account's own calendar day. Ten Free asks an hour apart from
    /// 12:00 UTC on 26 October, then an eleventh at 21:30 UTC: refused in every zone (all ten fall in
    /// one local day), with the wait running to THAT zone's next midnight — and admitted once it passes.
    /// </summary>
    [Theory]
    [InlineData(null, 2, 30)]                       // UTC: midnight is 2 h 30 min away
    [InlineData("Africa/Johannesburg", 0, 30)]      // 23:30 at +02:00
    [InlineData("America/New_York", 6, 30)]         // 17:30 at -04:00 (EDT until 1 November)
    [InlineData("Pacific/Kiritimati", 12, 30)]      // already 27 October, 11:30 at +14:00
    [InlineData("Mars/Olympus_Mons", 2, 30)]        // a zone this host cannot resolve counts UTC
    public void The_day_limit_resets_at_the_accounts_local_midnight(string? zone, int hours, int minutes)
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(Ask(limiter, account, AccountTier.Free, zone));
            clock.Now = clock.Now.AddHours(1);
        }

        clock.Now = clock.Now.AddMinutes(-30);
        var refused = Ask(limiter, account, AccountTier.Free, zone);

        Assert.Equal("ask_rate_day", refused?.Code);
        Assert.Equal(new TimeSpan(hours, minutes, 0), refused!.RetryAfter);

        clock.Now = clock.Now.Add(refused.RetryAfter).AddSeconds(-1);
        Assert.Equal("ask_rate_day", Ask(limiter, account, AccountTier.Free, zone)?.Code);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Null(Ask(limiter, account, AccountTier.Free, zone));
    }

    [Fact]
    public void Turns_asked_before_local_midnight_do_not_count_against_the_new_day()
    {
        // Johannesburg: five asks from 17:00 to 19:00 local on the 26th, five more from 00:10 local on
        // the 27th, and an eleventh at 01:50 local (23:50 UTC). With the day split at local midnight
        // the eleventh is the sixth of the new day and is admitted; on the UTC day all eleven fall on
        // the 26th and the eleventh is refused.
        var clock = new ManualClock { Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero) };
        var local = Limiter(clock);
        var utc = Limiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(Ask(local, account, AccountTier.Free, "Africa/Johannesburg"));
            Assert.Null(Ask(utc, account, AccountTier.Free));
            clock.Now = clock.Now.AddMinutes(i < 4 ? 30 : i == 4 ? 310 : 20);
        }

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 23, 50, 0, TimeSpan.Zero), clock.Now);
        Assert.Null(Ask(local, account, AccountTier.Free, "Africa/Johannesburg"));
        Assert.Equal("ask_rate_day", Ask(utc, account, AccountTier.Free)?.Code);
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

    /// <summary>
    /// HREV1-PLAT-2: a turn the handler refuses as a request gives its window slot back, so six
    /// malformed asks cannot spend the minute's six turns. Only the refunded start is returned —
    /// the turn that was answered still counts.
    /// </summary>
    [Fact]
    public void A_refunded_turn_gives_its_window_slot_back_and_an_answered_one_keeps_it()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock, new() { [AskTurnLimitOptions.PerMinuteKey] = "2" });
        var account = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            var (refused, refusal) = limiter.TryStart(account, AccountTier.Paid);
            Assert.Null(refusal);
            refused!.Refund();
            refused.Refund(); // idempotent: one refund returns one slot, never a second
            refused.Dispose();
        }

        Assert.Null(Ask(limiter, account, AccountTier.Paid));
        Assert.Null(Ask(limiter, account, AccountTier.Paid)); // the 2nd of PER_MINUTE=2
        Assert.Equal("ask_rate_minute", Ask(limiter, account, AccountTier.Paid)?.Code);
    }

    [Fact]
    public void Five_wrong_pins_lock_the_sixth_attempt_for_the_rest_of_the_window()
    {
        var clock = new ManualClock();
        var pins = new PinAttemptLimiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < PinAttemptLimiter.MaxFailures; i++)
        {
            Fail(pins, account);
            clock.Now = clock.Now.AddMinutes(1);
        }

        // Five failures over minutes 0..4; now minute 5: locked until the first is 15 minutes old.
        Assert.Equal(TimeSpan.FromMinutes(10), pins.TryBegin(account).LockedFor);
        Assert.Null(pins.TryBegin(Guid.NewGuid()).LockedFor);

        clock.Now = clock.Now.AddMinutes(10);
        Fail(pins, account); // the oldest aged out: one more try is allowed
        Assert.NotNull(pins.TryBegin(account).LockedFor);
    }

    /// <summary>
    /// HREV1-PLAT-1: an attempt is taken BEFORE its PIN is compared, so attempts still being hashed
    /// count against the allowance. Five in flight and the sixth is refused although no failure has
    /// been recorded yet; an attempt that ends without a verdict (the request was cancelled) gives
    /// its place back and counts as nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Attempts_in_flight_count_against_the_allowance_before_any_failure_is_recorded(int earlierFailures)
    {
        var clock = new ManualClock();
        var pins = new PinAttemptLimiter(clock);
        var account = Guid.NewGuid();
        for (var i = 0; i < earlierFailures; i++)
        {
            Fail(pins, account);
        }

        var inFlight = new List<PinAttemptLimiter.Attempt>();
        for (var i = 0; i < PinAttemptLimiter.MaxFailures - earlierFailures; i++)
        {
            var (attempt, locked) = pins.TryBegin(account);
            Assert.Null(locked);
            inFlight.Add(attempt!);
        }

        var (refused, wait) = pins.TryBegin(account);
        Assert.Null(refused);
        Assert.NotNull(wait);
        Assert.Null(pins.TryBegin(Guid.NewGuid()).LockedFor); // another account is its own partition

        inFlight[0].Dispose(); // cancelled before a verdict: the place comes back, nothing is counted
        var (again, _) = pins.TryBegin(account);
        Assert.NotNull(again);
        inFlight[0] = again!;

        foreach (var attempt in inFlight)
        {
            attempt.Failed();
            attempt.Failed(); // a verdict is given once
            attempt.Dispose();
        }

        // Exactly the allowance was spent: locked for the full window from the newest failures.
        Assert.Equal(PinAttemptLimiter.Window, pins.TryBegin(account).LockedFor);
    }

    [Fact]
    public void A_correct_pin_clears_the_failures()
    {
        var clock = new ManualClock();
        var pins = new PinAttemptLimiter(clock);
        var account = Guid.NewGuid();

        for (var i = 0; i < PinAttemptLimiter.MaxFailures - 1; i++)
        {
            Fail(pins, account);
        }

        using (var right = pins.TryBegin(account).Attempt!)
        {
            right.Succeeded();
        }

        Fail(pins, account);

        Assert.Null(pins.TryBegin(account).LockedFor);
    }

    private static void Fail(PinAttemptLimiter pins, Guid account)
    {
        var (attempt, locked) = pins.TryBegin(account);
        Assert.Null(locked);
        using (attempt!)
        {
            attempt!.Failed();
        }
    }
}
