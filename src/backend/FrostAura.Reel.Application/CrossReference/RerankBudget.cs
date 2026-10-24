using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// What the re-rank family had left, and whether a given member may spend.
///
/// <para><see cref="HasHeadroom"/> is the interactive answer — the paginator gets the whole ceiling.
/// <see cref="HasBackgroundHeadroom"/> is the background answer and stops earlier, so a feed build
/// cannot spend the day's allowance before the founder has typed anything.
/// <see cref="HasAskHeadroom"/> is Ask Reel v1's answer and stops at
/// <see cref="RerankBudget.AskShare"/>, so a conversation cannot spend the curation reserve.</para>
///
/// <para><paramref name="AskCallsUsed"/> / <paramref name="AskTokensUsed"/> are the Ask members'
/// part (<c>rerank-ask</c>, <c>rerank-expand</c>) of <paramref name="CallsUsed"/> /
/// <paramref name="TokensUsed"/>, which stay the WHOLE family's totals.</para>
/// </summary>
public sealed record RerankAllowance(
    long CallsUsed, long CallsLimit, long TokensUsed, long TokensLimit, long AskCallsUsed = 0, long AskTokensUsed = 0)
{
    public bool HasHeadroom => CallsUsed < CallsLimit && TokensUsed < TokensLimit;

    /// <summary>
    /// Whether a BACKGROUND member of the family (<c>rerank-feed</c>) may spend: the curation
    /// members' own spend stays under <see cref="RerankBudget.BackgroundFraction"/> of what Ask has
    /// left of the ceiling. With no Ask spend that is exactly the old 70% stop; with Ask at its full
    /// share the feed still has 70% of the 40% reserve, and the paginator keeps the rest.
    /// </summary>
    public bool HasBackgroundHeadroom =>
        HasHeadroom
        && CallsUsed - AskCallsUsed < (long)((CallsLimit - AskCallsUsed) * RerankBudget.BackgroundFraction)
        && TokensUsed - AskTokensUsed < (long)((TokensLimit - AskTokensUsed) * RerankBudget.BackgroundFraction);

    /// <summary>
    /// Whether an ASK member (<c>rerank-ask</c>, <c>rerank-expand</c>) may spend: the Ask members'
    /// own spend stays under <see cref="RerankBudget.AskShare"/> of both ceilings, which is what keeps
    /// at least 40% of the family for page and feed curation however long the conversation runs.
    /// </summary>
    public bool HasAskHeadroom =>
        HasHeadroom
        && AskCallsUsed < (long)(CallsLimit * RerankBudget.AskShare)
        && AskTokensUsed < (long)(TokensLimit * RerankBudget.AskShare);

    public long CallsRemaining => Math.Max(0, CallsLimit - CallsUsed);

    /// <summary>
    /// Whether one more call for <paramref name="purpose"/>, spending up to
    /// <paramref name="tokens"/>, fits — the reservation form of the three predicates above, used
    /// BEFORE a call is sent so nothing overshoots. A call admitted here can never take any of the
    /// three totals past its ceiling.
    /// </summary>
    public bool Admits(string purpose, long tokens)
    {
        var after = this with
        {
            CallsUsed = CallsUsed + 1,
            TokensUsed = TokensUsed + tokens,
            AskCallsUsed = RerankBudget.IsAskMember(purpose) ? AskCallsUsed + 1 : AskCallsUsed,
            AskTokensUsed = RerankBudget.IsAskMember(purpose) ? AskTokensUsed + tokens : AskTokensUsed,
        };

        // "After the call, still within" — the <= forms of the predicates.
        if (after.CallsUsed > CallsLimit || after.TokensUsed > TokensLimit)
        {
            return false;
        }

        if (RerankBudget.IsAskMember(purpose))
        {
            return after.AskCallsUsed <= (long)(CallsLimit * RerankBudget.AskShare)
                && after.AskTokensUsed <= (long)(TokensLimit * RerankBudget.AskShare);
        }

        if (purpose == ApiUsagePurpose.RerankFeed)
        {
            return after.CallsUsed - after.AskCallsUsed <= (long)((CallsLimit - AskCallsUsed) * RerankBudget.BackgroundFraction)
                && after.TokensUsed - after.AskTokensUsed <= (long)((TokensLimit - AskTokensUsed) * RerankBudget.BackgroundFraction);
        }

        return true;
    }

    public string Describe() =>
        AskCallsUsed > 0
            ? $"{CallsUsed}/{CallsLimit} calls ({AskCallsUsed} Ask), {TokensUsed}/{TokensLimit} tokens"
            : $"{CallsUsed}/{CallsLimit} calls, {TokensUsed}/{TokensLimit} tokens";
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

    /// <summary>
    /// The most of both ceilings Ask Reel v1's members (<c>rerank-ask</c>, <c>rerank-expand</c>)
    /// may spend: 60%, so page and feed curation always keep at least 40% of the family. Ask v2
    /// bills its own purposes (<c>AskBudget</c>) and never reaches this share at all.
    /// </summary>
    public const double AskShare = 0.60d;

    /// <summary>True for the family members that are a person mid-conversation.</summary>
    public static bool IsAskMember(string purpose) =>
        purpose is ApiUsagePurpose.RerankAsk or ApiUsagePurpose.RerankExpand;

    public const string CallsSetting = "LLM_RERANK_DAILY_CALLS";

    public const string TokensSetting = "LLM_RERANK_DAILY_TOKENS";

    public long DailyCalls => Math.Max(0, FeatureFlags.ReadInt(configuration, CallsSetting, DefaultDailyCalls));

    public long DailyTokens => Math.Max(0, FeatureFlags.ReadInt(configuration, TokensSetting, DefaultDailyTokens));

    /// <summary>Today's family spend against today's ceiling, for one account.</summary>
    public async Task<RerankAllowance> AllowanceAsync(Guid accountId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var family = ApiUsagePurpose.RerankFamily;

        long calls = 0, tokens = 0, askCalls = 0, askTokens = 0;

        // Summed across every member, legacy included: the recorder's un-flushed counters are keyed
        // per purpose, so a family gate has to ask for each of them. Read BEFORE the ledger: a
        // flush landing between the two reads then counts its rows twice (conservative) rather
        // than not at all.
        if (pending is not null)
        {
            foreach (var purpose in family)
            {
                var (c, t) = pending.PendingToday(ApiProvider.OpenRouter, accountId, purpose);
                Add(purpose, c, t);
            }
        }

        // AccountId is a dimension on the ledger rather than ownership of it, so the predicate is
        // written out here — the same reason LensBudget writes its own.
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter
                && u.AccountId == accountId
                && u.Purpose != null
                && family.Contains(u.Purpose)
                && u.Day == today)
            .Select(u => new { u.Purpose, u.CallCount, u.TokensUsed })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            Add(r.Purpose!, r.CallCount, r.TokensUsed ?? 0);
        }

        return new RerankAllowance(calls, DailyCalls, tokens, DailyTokens, askCalls, askTokens);

        void Add(string purpose, long c, long t)
        {
            calls += c;
            tokens += t;
            if (IsAskMember(purpose))
            {
                askCalls += c;
                askTokens += t;
            }
        }
    }
}
