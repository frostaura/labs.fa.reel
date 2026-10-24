using System.Text.Json.Serialization;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Application.Pipeline;

/// <summary>The status API's rollup of external-provider health.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderHealthState>))]
public enum ProviderHealthState
{
    /// <summary>
    /// No breaker is registered in this process, so nothing is being measured. Distinct from
    /// <see cref="Healthy"/> on purpose: "we do not know" must never render as "all good".
    /// </summary>
    Unmonitored,

    /// <summary>A breaker is registered and every purpose it watches is closed.</summary>
    Healthy,

    /// <summary>At least one purpose is off (its breaker is open), but not the provider as a whole.</summary>
    Degraded,

    /// <summary>The provider refuses every call (a 401, 402 or 403 tripped the provider-wide breaker).</summary>
    Down,
}

/// <summary>The state of one breaker the reader reports as not closed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderBreakerState>))]
public enum ProviderBreakerState
{
    /// <summary>The breaker is open: calls for this purpose are refused until <c>retryAt</c>.</summary>
    Open,

    /// <summary>Calls still run but are failing or truncating above the alarm rate.</summary>
    Degraded,
}

/// <summary>
/// One provider or purpose that is not healthy. <see cref="Purpose"/> is null when the whole
/// provider is affected. <see cref="ReasonCode"/> is a stable machine code (for example
/// <c>payment_required</c>, <c>unauthorized</c>, <c>truncation_breaker</c>) that the frontend
/// maps to fixed copy; it is never free text.
/// </summary>
public sealed record ProviderBreaker(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ApiProvider>))] ApiProvider Provider,
    string? Purpose,
    ProviderBreakerState State,
    string ReasonCode,
    DateTime Since,
    DateTime? RetryAt);

/// <summary>A point-in-time reading of provider health.</summary>
public sealed record ProviderHealthSnapshot(
    ProviderHealthState State,
    DateTime? CheckedAt,
    IReadOnlyList<ProviderBreaker> Breakers)
{
    /// <summary>The honest reading when no breaker exists in the process.</summary>
    public static ProviderHealthSnapshot Unmonitored { get; } = new(ProviderHealthState.Unmonitored, null, []);
}

/// <summary>
/// Read side of provider health, consumed by the status API. WS-6 owns this port; WS-1's
/// provider-health service (the 401/402/403 and cross-run truncation breakers) implements it and
/// registers itself as the implementation, replacing <see cref="UnmonitoredProviderHealthReader"/>.
/// Synchronous by design: the breaker state is in memory (and in the M5 purpose-health columns for
/// restarts), and a status poll every 5 seconds must not wait on a provider.
/// </summary>
public interface IProviderHealthReader
{
    ProviderHealthSnapshot Current();
}

/// <summary>
/// The default reader until a breaker is registered: reports <see cref="ProviderHealthState.Unmonitored"/>
/// rather than inventing a healthy reading nobody measured.
/// </summary>
public sealed class UnmonitoredProviderHealthReader : IProviderHealthReader
{
    public ProviderHealthSnapshot Current() => ProviderHealthSnapshot.Unmonitored;
}
