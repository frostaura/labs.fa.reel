using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Application.Auth;

/// <summary>
/// How many Ask Reel turns one account may START, inbound at the API edge (FD-8, J-8). These are
/// per-account limits on requests, enforced before a turn is written or a model is called; the
/// per-purpose LLM ceilings in <c>AskBudget</c> remain the runaway backstop behind them.
///
/// <para><b>Defaults are FD-8's recommendation</b>: one turn at a time, 20 an hour and 100 a day per
/// account, the Founder 300 a day, a Free account 10 a day until billing ships. The per-minute burst
/// (6) is the acceptance line's: a seventh ask inside a minute is refused. The eval stack raises
/// these for its own account only (§7 rule 1); nothing else should.</para>
///
/// <para>The minute and hour windows are rolling. The day is the account's calendar day, counted
/// from midnight in its own time zone (<see cref="AccountTimeZone.DayStartUtc"/>; UTC when it has
/// none), because the per-account Ask allowance in the ledger resets at that same midnight and the
/// 429 must not promise turns back at a different moment from the allowance behind it (R1-15). The
/// cost — 100 late in one day and 100 early in the next — is the same one the ledger's daily
/// ceiling already accepts.</para>
/// </summary>
public sealed class AskTurnLimitOptions : ValidatedOptions
{
    public const string ConcurrentKey = "ASK_TURNS_CONCURRENT";
    public const string PerMinuteKey = "ASK_TURNS_PER_MINUTE";
    public const string PerHourKey = "ASK_TURNS_PER_HOUR";
    public const string PerDayKey = "ASK_TURNS_PER_DAY";
    public const string PerDayFounderKey = "ASK_TURNS_PER_DAY_FOUNDER";
    public const string PerDayFreeKey = "ASK_TURNS_PER_DAY_FREE";

    /// <summary>Turns one account may have streaming at once.</summary>
    public int Concurrent { get; private set; } = 1;

    public int PerMinute { get; private set; } = 6;

    public int PerHour { get; private set; } = 20;

    /// <summary>The daily limit for a Paid account.</summary>
    public int PerDay { get; private set; } = 100;

    public int PerDayFounder { get; private set; } = 300;

    public int PerDayFree { get; private set; } = 10;

    /// <summary>The per-day limit for <paramref name="tier"/> (the account's local calendar day); an unknown tier gets the Free limit.</summary>
    public int PerDayFor(AccountTier tier) => tier switch
    {
        AccountTier.Founder => PerDayFounder,
        AccountTier.Paid => PerDay,
        _ => PerDayFree,
    };

    protected override void Read(SettingReader reader)
    {
        Concurrent = reader.Int(ConcurrentKey, 1, min: 1, max: 100);
        PerMinute = reader.Int(PerMinuteKey, 6, min: 1, max: 100_000);
        PerHour = reader.Int(PerHourKey, 20, min: 1, max: 100_000);
        PerDay = reader.Int(PerDayKey, 100, min: 1, max: 100_000);
        PerDayFounder = reader.Int(PerDayFounderKey, 300, min: 1, max: 100_000);
        PerDayFree = reader.Int(PerDayFreeKey, 10, min: 1, max: 100_000);
    }
}
