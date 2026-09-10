using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Lens;

/// <summary>What the gate had left when it was asked, and whether that is enough for one more call.</summary>
public sealed record LensAllowance(long CallsUsed, long CallsLimit, long TokensUsed, long TokensLimit)
{
    public bool HasHeadroom => CallsUsed < CallsLimit && TokensUsed < TokensLimit;

    /// <summary>How many more calls the gate will admit today. Never negative.</summary>
    public long CallsRemaining => Math.Max(0, CallsLimit - CallsUsed);

    public string Describe() =>
        $"{CallsUsed}/{CallsLimit} calls, {TokensUsed}/{TokensLimit} tokens";
}

/// <summary>
/// The per-account daily ceiling on lens generation, enforced against the token ledger.
///
/// <para>This exists because the whole cost case for the feature — comfortably inside the founder's
/// ≤$0.25/account-month assumption — rests on a bounded number of calls per account per day, and an
/// assumption nothing enforces is a forecast, not a budget. It reads the persisted ledger for
/// <c>purpose = lens</c> AND the recorder's in-flight counters, because the ledger flushes every
/// fifteen seconds and one job can spend a day's allowance inside a single window.</para>
///
/// <para><b>Exhaustion is never an error and never a blank card.</b> The caller falls back — a
/// stale note first, then the deterministic templater sentence and the overview. A user whose
/// budget ran out sees slightly less personal copy, which is what a budget is for.</para>
///
/// <para><b>The audit pass is deliberately a different purpose.</b> Scoring generated notes runs
/// under <c>lens-audit</c>, so auditing a prompt version cannot consume the allowance it is
/// auditing.</para>
/// </summary>
public sealed class LensBudget(IReelDbContext db, IConfiguration configuration, IPendingApiUsage? pending = null)
{
    /// <summary>Calls per account per day. A Fresh build spends at most 15 of these.</summary>
    public const int DefaultDailyCalls = 60;

    /// <summary>Tokens per account per day, prompt + completion. ~1,400 per lens ⇒ ~85 calls' worth.</summary>
    public const int DefaultDailyTokens = 120_000;

    public const string CallsSetting = "LLM_LENS_DAILY_CALLS";

    public const string TokensSetting = "LLM_LENS_DAILY_TOKENS";

    public long DailyCalls => Math.Max(0, FeatureFlags.ReadInt(configuration, CallsSetting, DefaultDailyCalls));

    public long DailyTokens => Math.Max(0, FeatureFlags.ReadInt(configuration, TokensSetting, DefaultDailyTokens));

    /// <summary>Today's spend against today's ceiling, for one account.</summary>
    public async Task<LensAllowance> AllowanceAsync(Guid accountId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // The ledger is not IAccountScoped — it is an operations table where AccountId is a
        // dimension, not ownership — so the account predicate is written out here rather than
        // arriving from a global filter.
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter
                && u.AccountId == accountId
                && u.Purpose == ApiUsagePurpose.Lens
                && u.Day == today)
            .Select(u => new { u.CallCount, u.TokensUsed })
            .ToListAsync(ct);

        var calls = rows.Sum(r => r.CallCount);
        var tokens = rows.Sum(r => r.TokensUsed ?? 0);

        var inFlight = pending?.PendingToday(ApiProvider.OpenRouter, accountId, ApiUsagePurpose.Lens) ?? (0L, 0L);
        return new LensAllowance(calls + inFlight.Calls, DailyCalls, tokens + inFlight.Tokens, DailyTokens);
    }
}
