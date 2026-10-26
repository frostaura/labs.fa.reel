namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// One purpose's call health since the process started, and over its last
/// <see cref="Window"/> sent calls (WS-1 task 8). The one tally of LLM call outcomes in the
/// process: the metrics report and the status API both read it (WS-1 CR-1).
///
/// <para><b>What counts.</b> A <i>settled</i> call is one <c>CompleteAsync</c>, after its retries.
/// A <i>sent</i> call is a settled one that made at least one HTTP attempt; a call refused before
/// sending (spent allowance, open breaker) is a <i>stop</i>, not a sent call, and never moves a rate
/// or the alarm — counting it would raise the fallback alarm every evening an allowance runs out.
/// A <i>length call</i> saw <c>finish_reason=length</c> on any attempt, its 2× retry included; a
/// <i>truncated</i> one still had it after that retry. A <i>fallback</i> is a sent call that returned
/// no usable answer, so its caller fell back. <i>Retries</i> are attempts beyond the first: the
/// length retry, transient and body-drop retries, and schema or effort downgrades.</para>
/// </summary>
/// <param name="Purpose">The <c>ApiUsagePurpose</c>.</param>
/// <param name="Calls">Sent calls since the process started.</param>
/// <param name="Answered">Of those, calls that returned a usable answer.</param>
/// <param name="LengthCalls">Of those, calls that saw a length finish on any attempt.</param>
/// <param name="Truncated">Of those, calls still at <c>finish_reason=length</c> after their retry.</param>
/// <param name="Fallbacks">Of those, calls that returned no usable answer.</param>
/// <param name="Retries">Attempts beyond the first, summed.</param>
/// <param name="Stops">Calls refused before anything was sent (not in <paramref name="Calls"/>).</param>
/// <param name="Outcomes">Every settled call — stops included — by closed outcome code (<see cref="LlmCallOutcome"/>).</param>
/// <param name="WindowCalls">Calls in the rolling window (at most <see cref="Window"/>).</param>
/// <param name="WindowLengthRate">Length calls over the window, 0–1.</param>
/// <param name="WindowFallbackRate">Fallbacks over the window, 0–1.</param>
/// <param name="WindowRetryRate">Retries per call over the window.</param>
/// <param name="Alarming">A full window with a length or fallback rate above <see cref="AlarmRate"/>.</param>
public sealed record LlmPurposeHealth(
    string Purpose,
    long Calls,
    long Answered,
    long LengthCalls,
    long Truncated,
    long Fallbacks,
    long Retries,
    long Stops,
    IReadOnlyDictionary<string, long> Outcomes,
    int WindowCalls,
    double WindowLengthRate,
    double WindowFallbackRate,
    double WindowRetryRate,
    bool Alarming)
{
    /// <summary>The rolling window the alarm reads.</summary>
    public const int Window = 50;

    /// <summary>The alarm threshold: a rate strictly above this over a full window.</summary>
    public const double AlarmRate = 0.02;
}

/// <summary>
/// The closed vocabulary of a settled call's outcome — a metric tag and a report key, never free
/// text. Two of them are stops (nothing was sent); every other non-answer is a fallback.
/// </summary>
public static class LlmCallOutcome
{
    public const string Answered = "answered";
    public const string Truncated = "truncated";
    public const string EmptyAnswer = "empty_answer";
    public const string ProviderError = "provider_error";
    public const string BadRequest = "bad_request";
    public const string ProviderRefused = "provider_refused";
    public const string TimedOut = "timed_out";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string BudgetSpent = "budget_spent";
    public const string Other = "other";
}

/// <summary>
/// The read side of the per-purpose LLM call counters, for the metrics report and the status API
/// (WS-6). In process: a restart starts the counts again; the durable record is the usage ledger.
/// </summary>
public interface ILlmCallHealthReader
{
    /// <summary>Every purpose that has made a call, by purpose name.</summary>
    IReadOnlyList<LlmPurposeHealth> Snapshot();
}
