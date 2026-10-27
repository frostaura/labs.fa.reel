using System.Text.Json.Serialization;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Application.Pipeline;

/// <summary>The closed vocabulary of operator alerts. A client maps a code it does not know to generic copy.</summary>
public static class OperatorAlertCodes
{
    /// <summary>Job attempts failed at or above the hourly threshold, across every account.</summary>
    public const string JobFailures = "job_failures";

    /// <summary>
    /// The model provider refused a call with 401, 402 or 403 and every purpose on it is off.
    /// <see cref="OperatorAlert.ReasonCode"/> says which (<c>payment_required</c> for a 402).
    /// </summary>
    public const string ProviderRefused = "provider_refused";

    /// <summary>
    /// A model purpose's length-finish or fallback rate is above WS-1's alarm line (2% over its
    /// last 50 sent calls). <see cref="OperatorAlert.Purpose"/> names it, <see cref="OperatorAlert.ReasonCode"/>
    /// says which rate (<c>length</c> or <c>fallback</c>), <see cref="OperatorAlert.Count"/> is the window's calls.
    /// </summary>
    public const string LlmAlarm = "llm_alarm";
}

/// <summary>One job kind's failed attempts inside the alert window.</summary>
public sealed record AlertKindCount(
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobKind>))] JobKind Kind,
    int Count);

/// <summary>
/// A condition the operator must hear about (K-7, E-12). Structured only — codes and counts, never
/// free text — so a client renders its own copy and nothing from a provider reaches a page.
/// </summary>
/// <param name="Code">One of <see cref="OperatorAlertCodes"/>.</param>
/// <param name="Since">When the condition began: the breaker's opening, or the start of the hour in which failures crossed the threshold.</param>
/// <param name="RaisedAt">When this process first raised it.</param>
/// <param name="ReasonCode">For <see cref="OperatorAlertCodes.ProviderRefused"/>: <c>unauthorized</c>, <c>payment_required</c> or <c>forbidden</c>.</param>
/// <param name="Provider">For <see cref="OperatorAlertCodes.ProviderRefused"/>: the provider.</param>
/// <param name="Count">For <see cref="OperatorAlertCodes.JobFailures"/>: failed attempts in the window.</param>
/// <param name="Threshold">For <see cref="OperatorAlertCodes.JobFailures"/>: the hourly count that raises it.</param>
/// <param name="Kinds">For <see cref="OperatorAlertCodes.JobFailures"/>: the count per job kind, largest first.</param>
/// <remarks>For <see cref="OperatorAlertCodes.LlmAlarm"/>, <see cref="Purpose"/> names the model purpose.</remarks>
public sealed record OperatorAlert(
    string Code,
    DateTime Since,
    DateTime RaisedAt,
    string? ReasonCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ApiProvider>))] ApiProvider? Provider,
    int? Count,
    int? Threshold,
    IReadOnlyList<AlertKindCount>? Kinds)
{
    /// <summary>For <see cref="OperatorAlertCodes.LlmAlarm"/>: the <c>ApiUsagePurpose</c> that is alarming.</summary>
    public string? Purpose { get; init; }
}

/// <summary>
/// The alerts active right now, as the process's alert monitor last evaluated them (once a minute).
/// Operator-facing: the status API and the metrics sink show them to Founder-tier accounts only.
/// </summary>
public interface IOperatorAlertReader
{
    IReadOnlyList<OperatorAlert> Current();
}
