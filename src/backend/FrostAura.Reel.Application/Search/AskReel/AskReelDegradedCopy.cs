using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The sentence a search turn leads with when its READING of the request (the plan stage) degraded.
///
/// <para><b>Why a search turn does not use the plan's own sentence for a spent allowance
/// (IREV3-SYS-03).</b> The day's allowances are counted per family: the plan stage (interpret and
/// plan) has one, the judge's Ask share of the rerank family another. With only the plan's spent, a
/// "heist movies" turn replied "Today's Ask allowance is used up, so I can't think this one through"
/// and then made three judge calls and showed eight judged picks under it. On a search turn the
/// spent plan allowance costs the close reading of the words, nothing more, so that is all the
/// sentence names — and it claims nothing about the judge, whose own failures are told by the
/// unchecked tray's copy. Every other cause (a refused key, an empty account, a transient failure)
/// reaches every call the turn makes, so its sentence is the plan's own.</para>
/// </summary>
public static class AskReelDegradedCopy
{
    /// <summary>The lead of a search turn whose plan the day's plan allowance refused.</summary>
    public const string PlanBudgetSpentReply =
        "Today's allowance for reading requests closely is used up, so I searched on the words you typed. "
        + "It resets at midnight UTC.";

    /// <summary>
    /// The lead for a search turn whose plan degraded: <see cref="PlanBudgetSpentReply"/> for a spent
    /// plan allowance, and otherwise the plan's own sentence for its cause.
    /// </summary>
    public static string SearchLead(AgentFailureKind? kind, string? reasonCode) =>
        kind == AgentFailureKind.BudgetSpent
            ? PlanBudgetSpentReply
            : LlmFailureCodes.DegradedReply(kind, reasonCode);
}
