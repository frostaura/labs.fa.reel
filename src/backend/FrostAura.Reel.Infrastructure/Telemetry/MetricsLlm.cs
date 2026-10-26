using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>One purpose's model calls since this process started.</summary>
/// <param name="Purpose">The <c>ApiUsagePurpose</c> the calls were made for.</param>
/// <param name="Calls">Calls that settled (an answer or a typed failure), retries inside a call not counted.</param>
/// <param name="Answered">Calls that produced usable content.</param>
/// <param name="Truncated">Calls that ended <c>finish_reason=length</c> after their retry.</param>
/// <param name="Fallbacks">Calls that produced nothing usable for any reason, truncation included: each one is a caller on its fallback.</param>
/// <param name="Outcomes">Settled calls per outcome code (<see cref="LlmCallMetrics.Outcome"/>).</param>
public sealed record LlmPurposeMetrics(
    string Purpose,
    long Calls,
    long Answered,
    long Truncated,
    long Fallbacks,
    IReadOnlyDictionary<string, long> Outcomes);

/// <summary>
/// LLM truncation and fallback, per purpose (K-7). Static because the chat client is built by hand
/// inside each adapter rather than resolved from DI; the instrument is on meter
/// <see cref="ReelMetrics.MeterName"/> like the others, and the in-process tally is what the metrics
/// sink reads. <c>OpenRouterChatClient.CompleteAsync</c> records every settled call through
/// <see cref="Record"/>.
///
/// <para>Tags and keys are closed vocabularies: the purpose constants and the outcome codes below.</para>
/// </summary>
public static class LlmCallMetrics
{
    public const string Answered = "answered";

    private static readonly Meter Meter = new(ReelMetrics.MeterName);

    private static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        "reel.llm.calls", "{call}", "Settled model calls by purpose and outcome (answered, truncated, timed_out, ...).");

    private static readonly ConcurrentDictionary<(string Purpose, string Outcome), long> Tally = new();

    /// <summary>The closed outcome code for a call result.</summary>
    public static string Outcome(LlmChatResult result) => result.Failure switch
    {
        null => Answered,
        LlmCallFailure.Truncated => "truncated",
        LlmCallFailure.EmptyAnswer => "empty_answer",
        LlmCallFailure.ProviderError => "provider_error",
        LlmCallFailure.BadRequest => "bad_request",
        LlmCallFailure.ProviderRefused => "provider_refused",
        LlmCallFailure.TimedOut => "timed_out",
        LlmCallFailure.ProviderUnavailable => "provider_unavailable",
        LlmCallFailure.BudgetSpent => "budget_spent",
        _ => "other",
    };

    /// <summary>Records one settled call. Call once per <c>CompleteAsync</c>, after its retries.</summary>
    public static void Record(string purpose, LlmChatResult result)
    {
        var outcome = Outcome(result);
        Calls.Add(1, new KeyValuePair<string, object?>("purpose", purpose), new KeyValuePair<string, object?>("outcome", outcome));
        Tally.AddOrUpdate((purpose, outcome), 1, (_, n) => n + 1);
    }

    /// <summary>Per purpose, since process start. Purposes with no settled call are absent.</summary>
    public static IReadOnlyList<LlmPurposeMetrics> Snapshot() =>
        Tally.ToArray()
            .GroupBy(e => e.Key.Purpose, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var outcomes = g
                    .OrderBy(e => e.Key.Outcome, StringComparer.Ordinal)
                    .ToDictionary(e => e.Key.Outcome, e => e.Value, StringComparer.Ordinal);
                var calls = outcomes.Values.Sum();
                var answered = outcomes.GetValueOrDefault(Answered);
                return new LlmPurposeMetrics(
                    g.Key, calls, answered, outcomes.GetValueOrDefault("truncated"), calls - answered, outcomes);
            })
            .ToList();
}
