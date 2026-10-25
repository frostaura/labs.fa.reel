using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// The one mapping between the two failure vocabularies: <see cref="AgentFailureKind"/> (why a model
/// call produced nothing, written by the LLM layer onto turn records and counters) and
/// <see cref="JobFailureReason"/> (the stable code the status API's <c>lastFailures</c> carries and
/// the frontend maps to fixed copy). Without it the same 402 read as <c>ProviderRefused</c> on a turn
/// and <c>payment_required</c> on a job, and nothing could join the two.
///
/// <para>Total in both directions that matter: every kind has a code, and a kind that carries no
/// HTTP status falls back to the least specific honest code rather than a guess.</para>
/// </summary>
public static class LlmFailureCodes
{
    /// <summary>
    /// The status-API reason code for a failure. <paramref name="httpStatus"/> sharpens
    /// <see cref="AgentFailureKind.ProviderRefused"/> (401/402/403) and
    /// <see cref="AgentFailureKind.ProviderError"/> (429); <paramref name="breakerReason"/> is the
    /// open breaker's own code for <see cref="AgentFailureKind.ProviderUnavailable"/>.
    /// </summary>
    public static string ReasonCode(AgentFailureKind kind, int? httpStatus = null, string? breakerReason = null) => kind switch
    {
        AgentFailureKind.Truncated or AgentFailureKind.EmptyAnswer or AgentFailureKind.MalformedAnswer
            => JobFailureReason.NoAnswer,
        AgentFailureKind.ProviderRefused => httpStatus switch
        {
            401 => JobFailureReason.Unauthorized,
            402 => JobFailureReason.PaymentRequired,
            403 => JobFailureReason.Forbidden,
            _ => JobFailureReason.Error,
        },
        AgentFailureKind.ProviderError => httpStatus == 429 ? JobFailureReason.RateLimited : JobFailureReason.Error,
        AgentFailureKind.TimedOut => JobFailureReason.TimedOut,
        AgentFailureKind.BudgetSpent => JobFailureReason.BudgetSpent,
        AgentFailureKind.ProviderUnavailable => breakerReason ?? JobFailureReason.Error,
        _ => JobFailureReason.Error,
    };
}
