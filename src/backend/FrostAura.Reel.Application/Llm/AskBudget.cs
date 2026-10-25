using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// The per-account daily ceilings on Ask Reel v2's three purposes — <c>ask-plan</c>,
/// <c>ask-judge</c> and <c>ask-answer</c> — each its own, and none of them the re-rank family's.
///
/// <para><b>Why separate from the re-rank family.</b> The family ceiling is 150 calls a day; on
/// 2026-10-22 a single evening's Ask Reel turns spent 93 of them and the feed's curation had to
/// share the rest. Ask v2 bills its own purposes against its own ceilings, so a conversation cannot
/// starve the feed and a feed build cannot starve a conversation. The v1 path (<c>rerank-ask</c>)
/// still bills the family, and is capped at <c>RerankBudget</c>'s Ask share of it.</para>
///
/// <para><b>The defaults are a runaway backstop sized for the founder tier</b> (FD-8: 300 turns a
/// day), from §4.11's per-turn stage budgets: one plan call (≤2 with its retry, ~1.35k tokens), up
/// to three judge batches (~5.9k tokens a turn) and at most one answer (~0.9k). The turn limits
/// themselves — 20 an hour, 100 a day, Free 10 — are the Ask engine's to enforce per turn; these
/// ceilings are what stops a loop that ignores them. Settings: <c>LLM_ASK_PLAN_DAILY_CALLS</c>,
/// <c>LLM_ASK_PLAN_DAILY_TOKENS</c>, and the same pair for <c>ASK_JUDGE</c> and
/// <c>ASK_ANSWER</c>.</para>
/// </summary>
public static class AskBudget
{
    /// <summary>The calls and tokens one Ask purpose may spend per account per day.</summary>
    public sealed record Ceiling(long Calls, long Tokens);

    public const int DefaultPlanCalls = 700;
    public const int DefaultPlanTokens = 1_000_000;
    public const int DefaultJudgeCalls = 1_000;
    public const int DefaultJudgeTokens = 2_500_000;
    public const int DefaultAnswerCalls = 350;
    public const int DefaultAnswerTokens = 400_000;

    /// <summary>The ceiling for an Ask purpose, or null when <paramref name="purpose"/> is not one.</summary>
    public static Ceiling? For(IConfiguration configuration, string purpose) => purpose switch
    {
        ApiUsagePurpose.AskPlan => Read(configuration, "ASK_PLAN", DefaultPlanCalls, DefaultPlanTokens),
        ApiUsagePurpose.AskJudge => Read(configuration, "ASK_JUDGE", DefaultJudgeCalls, DefaultJudgeTokens),
        ApiUsagePurpose.AskAnswer => Read(configuration, "ASK_ANSWER", DefaultAnswerCalls, DefaultAnswerTokens),
        _ => null,
    };

    /// <summary>The setting names for an Ask purpose's two ceilings.</summary>
    public static (string Calls, string Tokens) Settings(string purposeKey) =>
        ($"LLM_{purposeKey}_DAILY_CALLS", $"LLM_{purposeKey}_DAILY_TOKENS");

    private static Ceiling Read(IConfiguration configuration, string key, int calls, int tokens)
    {
        var (callsSetting, tokensSetting) = Settings(key);
        return new Ceiling(
            Math.Max(0, FeatureFlags.ReadInt(configuration, callsSetting, calls)),
            Math.Max(0, FeatureFlags.ReadInt(configuration, tokensSetting, tokens)));
    }
}
