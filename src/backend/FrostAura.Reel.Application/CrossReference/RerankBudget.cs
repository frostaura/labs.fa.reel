using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// What the re-rank family had left, and whether a given member may spend.
///
/// <para><see cref="HasHeadroom"/> is the interactive answer — Ask Reel and the paginator get the
/// whole ceiling. <see cref="HasBackgroundHeadroom"/> is the background answer and stops earlier,
/// so a feed build cannot spend the day's allowance before the founder has typed anything.</para>
/// </summary>
public sealed record RerankAllowance(long CallsUsed, long CallsLimit, long TokensUsed, long TokensLimit)
{
    public bool HasHeadroom => CallsUsed < CallsLimit && TokensUsed < TokensLimit;

    /// <summary>
    /// Whether a BACKGROUND member of the family (<c>rerank-feed</c>) may spend. It stops at
    /// <see cref="BackgroundFraction"/> of both ceilings — the reserve is not for the feed, it is
    /// for whoever is still awake at eleven.
    /// </summary>
    public bool HasBackgroundHeadroom =>
        CallsUsed < (long)(CallsLimit * RerankBudget.BackgroundFraction)
        && TokensUsed < (long)(TokensLimit * RerankBudget.BackgroundFraction);

    public long CallsRemaining => Math.Max(0, CallsLimit - CallsUsed);

    public string Describe() =>
        $"{CallsUsed}/{CallsLimit} calls, {TokensUsed}/{TokensLimit} tokens";
}

/// <summary>
/// The per-account daily ceiling on the whole re-rank FAMILY — the one budget in this product that
/// was measured before it was written, and the one surface that had never had a ceiling at all.
///
/// <para><b>Why a family and not four budgets.</b> <c>ApiUsagePurpose.Rerank</c> was written by
/// four callers at once (two feed passes, the paginator, Ask Reel) and the ledger is day-grained,
/// so "what does a turn cost" was unanswerable and every per-surface figure derived from it was
/// wrong. Splitting the purpose makes the question answerable; a single family ceiling is what
/// makes the ANSWER binding, because four independent ceilings sum to four times the number anyone
/// signed off on. Legacy <c>"rerank"</c> rows are counted too — a ceiling that ignored them would
/// read a day with real spend on it as empty.</para>
///
/// <para><b>The numbers.</b> Measured 2026-09-10: 167 calls and 298k tokens across three days,
/// i.e. ~56 calls and ~100k tokens on a busy day for one account. The defaults are ~2.7× that —
/// generous enough that no ordinary day touches them and tight enough that a runaway loop stops
/// inside one day rather than inside one month.</para>
///
/// <para><b>Background stops at 70%.</b> The feed build's three re-rank passes are the family's
/// heaviest spender and the only one nobody is waiting on. Ask Reel keeps the full ceiling: a
/// person mid-conversation being told the model is out of budget because a 3 a.m. feed build spent
/// it is the exact failure this gate exists to prevent.</para>
///
/// <para><b>Exhaustion is never an error and never an empty page.</b> Every re-rank caller already
/// degrades to model order when the agent is unavailable or a batch fails — the budget simply
/// enters through the same door. A page that ships uncurated is a slightly worse order, not a
/// wrong one.</para>
/// </summary>
public sealed class RerankBudget(IReelDbContext db, IConfiguration configuration, IPendingApiUsage? pending = null)
{
    /// <summary>Calls per account per day across the whole family. ~2.7× the measured busy day.</summary>
    public const int DefaultDailyCalls = 150;

    /// <summary>Tokens per account per day across the whole family, prompt + completion.</summary>
    public const int DefaultDailyTokens = 300_000;

    /// <summary>
    /// The fraction of both ceilings background re-ranking may reach. 0.70 leaves roughly 45 calls
    /// and 90k tokens — about fifteen Ask Reel turns — standing after a maximally expensive day of
    /// feed builds.
    /// </summary>
    public const double BackgroundFraction = 0.70d;

    public const string CallsSetting = "LLM_RERANK_DAILY_CALLS";

    public const string TokensSetting = "LLM_RERANK_DAILY_TOKENS";

    public long DailyCalls => Math.Max(0, FeatureFlags.ReadInt(configuration, CallsSetting, DefaultDailyCalls));

    public long DailyTokens => Math.Max(0, FeatureFlags.ReadInt(configuration, TokensSetting, DefaultDailyTokens));

    /// <summary>Today's family spend against today's ceiling, for one account.</summary>
    public async Task<RerankAllowance> AllowanceAsync(Guid accountId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var family = ApiUsagePurpose.RerankFamily;

        // AccountId is a dimension on the ledger rather than ownership of it, so the predicate is
        // written out here — the same reason LensBudget writes its own.
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter
                && u.AccountId == accountId
                && u.Purpose != null
                && family.Contains(u.Purpose)
                && u.Day == today)
            .Select(u => new { u.CallCount, u.TokensUsed })
            .ToListAsync(ct);

        var calls = rows.Sum(r => r.CallCount);
        var tokens = rows.Sum(r => r.TokensUsed ?? 0);

        // Summed across every member, legacy included: the recorder's un-flushed counters are keyed
        // per purpose, so a family gate has to ask for each of them.
        long pendingCalls = 0;
        long pendingTokens = 0;
        if (pending is not null)
        {
            foreach (var purpose in family)
            {
                var (c, t) = pending.PendingToday(ApiProvider.OpenRouter, accountId, purpose);
                pendingCalls += c;
                pendingTokens += t;
            }
        }

        return new RerankAllowance(
            calls + pendingCalls, DailyCalls, tokens + pendingTokens, DailyTokens);
    }
}
