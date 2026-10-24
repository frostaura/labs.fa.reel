using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Llm;

/// <summary>
/// The provider-health service: the breakers every model call is admitted through, and the reading
/// the status API polls. One in-memory singleton implements both <see cref="IProviderHealth"/> and
/// WS-6's <see cref="IProviderHealthReader"/>, so the pill can never say "healthy" about a provider
/// the client is refusing to call.
///
/// <para><b>Provider breaker.</b> A 401, 402 or 403 opens it for <see cref="CoolOff"/>. While open,
/// every purpose on that provider is refused before anything is sent — the 11-day unnoticed 402
/// outage was every caller paying for (or waiting on) a refusal and then hiding it in a fallback.
/// Past the cool-off exactly ONE call is let through as a probe (half-open); an answer closes the
/// breaker, another refusal re-opens it for a fresh cool-off. A refused call costs nothing, so a
/// probe every quarter of an hour is the cheapest honest way to notice the credit coming back.</para>
///
/// <para><b>Purpose breakers</b> turn one purpose off until a stated time — the cross-reference
/// cross-run breaker's "off for the rest of the UTC day".</para>
///
/// <para><b>Process-local, deliberately for now.</b> A restart forgets an open breaker; the next
/// call re-learns a dead key at the price of one refused request. Persisting the state across
/// restarts is the M5 purpose-health columns (a WS-6 schema request).</para>
/// </summary>
public sealed class ProviderHealthService(ILogger<ProviderHealthService>? logger = null, TimeProvider? clock = null)
    : IProviderHealth, IProviderHealthReader
{
    /// <summary>How long a provider breaker stays open before one probe call is admitted.</summary>
    public static readonly TimeSpan CoolOff = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<ApiProvider, ProviderState> _providers = [];
    private readonly Dictionary<(ApiProvider Provider, string Purpose), ProviderBreaker> _purposes = [];

    private sealed class ProviderState
    {
        public required ProviderBreaker Breaker { get; set; }

        /// <summary>True while the single half-open probe call is out.</summary>
        public bool ProbeInFlight { get; set; }
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public bool TryAdmit(ApiProvider provider, string purpose, out ProviderBreaker? breaker)
    {
        lock (_lock)
        {
            if (_purposes.TryGetValue((provider, purpose), out var own))
            {
                if (own.RetryAt is { } until && until <= Now)
                {
                    _purposes.Remove((provider, purpose));
                }
                else
                {
                    breaker = own;
                    return false;
                }
            }

            if (_providers.TryGetValue(provider, out var state))
            {
                var halfOpen = state.Breaker.RetryAt is { } retryAt && retryAt <= Now;
                if (!halfOpen || state.ProbeInFlight)
                {
                    breaker = state.Breaker;
                    return false;
                }

                // Half-open: this caller is the probe; everyone else waits on its outcome.
                state.ProbeInFlight = true;
            }

            breaker = null;
            return true;
        }
    }

    public void ReportRefused(ApiProvider provider, int httpStatus)
    {
        var reason = ProviderBreakerReasons.ForStatus(httpStatus);
        lock (_lock)
        {
            var since = _providers.TryGetValue(provider, out var existing) ? existing.Breaker.Since : Now;
            _providers[provider] = new ProviderState
            {
                Breaker = new ProviderBreaker(provider, null, ProviderBreakerState.Open, reason, since, Now + CoolOff),
            };
        }

        logger?.LogError(
            "{Provider} refused a call with HTTP {Status} ({Reason}); every {Provider} purpose is off for {CoolOff} "
            + "and then probed with a single call.",
            provider, httpStatus, reason, provider, CoolOff);
    }

    public void ReportAnswered(ApiProvider provider)
    {
        lock (_lock)
        {
            if (_providers.Remove(provider, out var closed))
            {
                logger?.LogInformation(
                    "{Provider} answered again after {Reason} since {Since:O}; its breaker is closed.",
                    provider, closed.Breaker.ReasonCode, closed.Breaker.Since);
            }
        }
    }

    public void ReportInconclusive(ApiProvider provider)
    {
        lock (_lock)
        {
            if (_providers.TryGetValue(provider, out var state))
            {
                state.ProbeInFlight = false;
            }
        }
    }

    public void OpenPurpose(ApiProvider provider, string purpose, string reasonCode, DateTime until)
    {
        lock (_lock)
        {
            _purposes[(provider, purpose)] =
                new ProviderBreaker(provider, purpose, ProviderBreakerState.Open, reasonCode, Now, until);
        }

        logger?.LogWarning(
            "{Purpose} on {Provider} is off until {Until:O} ({Reason}).", purpose, provider, until, reasonCode);
    }

    public ProviderBreaker? OpenBreakerFor(ApiProvider provider, string purpose)
    {
        lock (_lock)
        {
            if (_purposes.TryGetValue((provider, purpose), out var own) && !(own.RetryAt <= Now))
            {
                return own;
            }

            return _providers.TryGetValue(provider, out var state) ? state.Breaker : null;
        }
    }

    /// <summary>
    /// The status API's reading. A registered breaker means the provider IS monitored, so the
    /// floor is <see cref="ProviderHealthState.Healthy"/>, never <see cref="ProviderHealthState.Unmonitored"/>.
    /// </summary>
    public ProviderHealthSnapshot Current()
    {
        lock (_lock)
        {
            var now = Now;
            foreach (var expired in _purposes.Where(p => p.Value.RetryAt <= now).Select(p => p.Key).ToList())
            {
                _purposes.Remove(expired);
            }

            var breakers = _providers.Values.Select(s => s.Breaker)
                .Concat(_purposes.Values)
                .OrderBy(b => b.Since)
                .ToList();
            var state = _providers.Count > 0 ? ProviderHealthState.Down
                : _purposes.Count > 0 ? ProviderHealthState.Degraded
                : ProviderHealthState.Healthy;
            return new ProviderHealthSnapshot(state, now, breakers);
        }
    }
}
