using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>What the gate had left when it was asked, and whether that is enough for one more batch.</summary>
public sealed record CrossReferenceAllowance(long CallsUsed, long CallsLimit, long TokensUsed, long TokensLimit)
{
    /// <summary>
    /// BOTH ceilings bind. A call ceiling alone cannot stop a run that batches sixty titles into
    /// one enormous prompt, and a token ceiling alone cannot stop a run that makes a thousand tiny
    /// ones; the pair is what makes the cost case a budget rather than a forecast.
    /// </summary>
    public bool HasHeadroom => CallsUsed < CallsLimit && TokensUsed < TokensLimit;

    /// <summary>How many more calls the gate will admit today. Never negative.</summary>
    public long CallsRemaining => Math.Max(0, CallsLimit - CallsUsed);

    public string Describe() =>
        $"{CallsUsed}/{CallsLimit} calls, {TokensUsed}/{TokensLimit} tokens";
}

/// <summary>
/// The per-account daily ceiling on cross-reference judging, enforced against the token ledger.
///
/// <para>Modelled on <c>LensBudget</c>, for the same reason and with the same two-source read: the
/// persisted ledger for <c>purpose = cross-reference</c> AND the recorder's in-flight counters,
/// because the ledger flushes every fifteen seconds and a batch loop can spend a day's allowance
/// well inside one window. That is not a hypothetical here — it is the catalogue-ceiling lesson
/// already written into <c>CLAUDE.md</c>, and it is why the caller must re-check this gate
/// <b>per batch</b> rather than once per run.</para>
///
/// <para><b>The defaults are derived, not chosen.</b> A batch of 60 costs roughly 11k prompt +
/// 2.7k completion tokens. The founder's eligible pool today is ~2,242 titles: 38 round-1 calls
/// (~545k tokens) plus 23 round-2 calls (~330k) is one complete cold pass at ~68 calls and ~895k
/// tokens. <c>LLM_XREF_DAILY_CALLS = 80</c> / <c>LLM_XREF_DAILY_TOKENS = 1,200,000</c> therefore
/// admits today's whole pool in a day with margin, and paces a 10,000-title pool over roughly four
/// days — the trade the founder explicitly asked for, provided the counter is honest about it.</para>
///
/// <para><b>Exhaustion is never an error.</b> The run stops, saves what it judged, and the anti-join
/// picks the remainder up on the next build. Verdicts are additive, so a half-judged pool is a
/// smaller instrument, never a wrong one.</para>
///
/// <para><b>The backtest is deliberately a different purpose.</b> It spends under
/// <c>cross-reference-eval</c>, so validating the judge cannot consume the budget the judge runs
/// on — the same separation <c>lens-audit</c> already has from <c>lens</c>.</para>
/// </summary>
public sealed class CrossReferenceBudget(IReelDbContext db, IConfiguration configuration, IPendingApiUsage? pending = null)
{
    /// <summary>Calls per account per day. One complete cold pass over today's pool is ~68.</summary>
    public const int DefaultDailyCalls = 80;

    /// <summary>Tokens per account per day, prompt + completion. ~13.7k per batch of 60.</summary>
    public const int DefaultDailyTokens = 1_200_000;

    public const string CallsSetting = "LLM_XREF_DAILY_CALLS";

    public const string TokensSetting = "LLM_XREF_DAILY_TOKENS";

    public long DailyCalls => Math.Max(0, FeatureFlags.ReadInt(configuration, CallsSetting, DefaultDailyCalls));

    public long DailyTokens => Math.Max(0, FeatureFlags.ReadInt(configuration, TokensSetting, DefaultDailyTokens));

    /// <summary>Today's spend against today's ceiling, for one account.</summary>
    public async Task<CrossReferenceAllowance> AllowanceAsync(Guid accountId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // The ledger is not IAccountScoped — AccountId is a dimension on an operations table, not
        // ownership of it — so the account predicate is written out here rather than arriving from
        // a global filter.
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter
                && u.AccountId == accountId
                && u.Purpose == ApiUsagePurpose.CrossReference
                && u.Day == today)
            .Select(u => new { u.CallCount, u.TokensUsed })
            .ToListAsync(ct);

        var calls = rows.Sum(r => r.CallCount);
        var tokens = rows.Sum(r => r.TokensUsed ?? 0);

        var inFlight = pending?.PendingToday(ApiProvider.OpenRouter, accountId, ApiUsagePurpose.CrossReference)
            ?? (0L, 0L);
        return new CrossReferenceAllowance(
            calls + inFlight.Calls, DailyCalls, tokens + inFlight.Tokens, DailyTokens);
    }
}
