namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// One purpose's call health since the process started, and over its last
/// <see cref="Window"/> sent calls (WS-1 task 8).
///
/// <para><b>What counts.</b> A call is one <c>CompleteAsync</c> that sent at least one HTTP attempt;
/// a call refused before sending (spent allowance, open breaker) is a stop, not a call, and is not
/// counted. A <i>length call</i> saw <c>finish_reason=length</c> on any attempt, its 2× retry
/// included. A <i>fallback</i> is a sent call that returned no usable answer, so its caller fell
/// back. <i>Retries</i> are attempts beyond the first: the length retry, transient and body-drop
/// retries, and schema or effort downgrades.</para>
/// </summary>
/// <param name="Purpose">The <c>ApiUsagePurpose</c>.</param>
/// <param name="Calls">Sent calls since the process started.</param>
/// <param name="LengthCalls">Of those, calls that saw a length finish.</param>
/// <param name="Fallbacks">Of those, calls that returned no usable answer.</param>
/// <param name="Retries">Attempts beyond the first, summed.</param>
/// <param name="WindowCalls">Calls in the rolling window (at most <see cref="Window"/>).</param>
/// <param name="WindowLengthRate">Length calls over the window, 0–1.</param>
/// <param name="WindowFallbackRate">Fallbacks over the window, 0–1.</param>
/// <param name="WindowRetryRate">Retries per call over the window.</param>
/// <param name="Alarming">A full window with a length or fallback rate above <see cref="AlarmRate"/>.</param>
public sealed record LlmPurposeHealth(
    string Purpose,
    long Calls,
    long LengthCalls,
    long Fallbacks,
    long Retries,
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
/// The read side of the per-purpose LLM call counters, for the metrics report and the status API
/// (WS-6). In process: a restart starts the counts again; the durable record is the usage ledger.
/// </summary>
public interface ILlmCallHealthReader
{
    /// <summary>Every purpose that has made a call, by purpose name.</summary>
    IReadOnlyList<LlmPurposeHealth> Snapshot();
}
