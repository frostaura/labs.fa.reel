using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// The write side of provider health: the breakers every model call consults BEFORE it is sent.
/// The read side the status API polls is <see cref="IProviderHealthReader"/>; one service
/// implements both, so what the pill says and what the client refuses can never disagree.
///
/// <para><b>Two kinds of breaker.</b> A <i>provider</i> breaker opens on any 401, 402 or 403 — the
/// key, the credit or the permission is gone, every purpose is affected, and an 11-day 402 outage
/// went unnoticed because every caller swallowed it into a fallback. A <i>purpose</i> breaker turns
/// one purpose off until a stated time — the cross-reference cross-run breaker uses it to stop a
/// model/prompt pair that truncates every batch from billing the rest of the UTC day.</para>
/// </summary>
public interface IProviderHealth
{
    /// <summary>
    /// Whether a call for <paramref name="purpose"/> may be sent now. When it may not,
    /// <paramref name="breaker"/> is the open breaker that refuses it. A provider breaker past its
    /// retry time admits exactly ONE probe call (half-open); the rest stay refused until that call
    /// reports back.
    /// </summary>
    bool TryAdmit(ApiProvider provider, string purpose, out ProviderBreaker? breaker);

    /// <summary>A call was refused by the provider with 401, 402 or 403. Opens the provider breaker.</summary>
    void ReportRefused(ApiProvider provider, int httpStatus);

    /// <summary>
    /// A call got an HTTP answer that was not a refusal — the key works, whatever the body said.
    /// Closes a provider breaker.
    /// </summary>
    void ReportAnswered(ApiProvider provider);

    /// <summary>
    /// A call ended without telling us whether the key works (a dropped connection, a timeout). A
    /// half-open probe in that state is released so the next call can probe again; nothing closes.
    /// </summary>
    void ReportInconclusive(ApiProvider provider);

    /// <summary>Turns one purpose off until <paramref name="until"/>, with a stable reason code.</summary>
    void OpenPurpose(ApiProvider provider, string purpose, string reasonCode, DateTime until);

    /// <summary>The open breaker for a purpose (its own, or the provider's), or null when calls may run.</summary>
    ProviderBreaker? OpenBreakerFor(ApiProvider provider, string purpose);
}

/// <summary>
/// The stable codes a breaker carries to the status API — the same vocabulary the job-failure
/// reasons use where the cause is the same, so the frontend maps one code to one sentence.
/// </summary>
public static class ProviderBreakerReasons
{
    public const string Unauthorized = JobFailureReason.Unauthorized;
    public const string PaymentRequired = JobFailureReason.PaymentRequired;
    public const string Forbidden = JobFailureReason.Forbidden;

    /// <summary>Every batch of two consecutive runs truncated or went unanswered on the same model and prompt.</summary>
    public const string TruncationBreaker = "truncation_breaker";

    /// <summary>The code for a refusing HTTP status.</summary>
    public static string ForStatus(int httpStatus) => httpStatus switch
    {
        401 => Unauthorized,
        402 => PaymentRequired,
        _ => Forbidden,
    };
}
