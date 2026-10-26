using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FrostAura.Reel.Application.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Infrastructure.Llm;

/// <summary>
/// Per-purpose counters for every LLM call the shared transport makes — length finishes, fallbacks
/// and retries — with an alarm when a purpose's length or fallback rate is above 2% over its last
/// 50 calls (WS-1 task 8). See <see cref="LlmPurposeHealth"/> for what each count means.
///
/// <para><b>Two exports.</b> Instruments on the <c>FrostAura.Reel</c> meter (the one WS-6's
/// <c>ReelMetrics</c> uses), readable live with <c>dotnet-counters monitor --counters
/// FrostAura.Reel</c>; and <see cref="ILlmCallHealthReader.Snapshot"/>, for the metrics report and
/// the status API. Tags are closed vocabularies (purpose, outcome), never free text.</para>
///
/// <para><b>The alarm is a log line at Warning on the way in and at Information on the way out</b>,
/// once per crossing rather than once per call, because a purpose stuck above the line for an
/// evening would otherwise write one warning per request. The §7 stop rule (length above 5% over
/// 50 calls) is read off the same window.</para>
/// </summary>
public sealed class LlmCallMetrics : ILlmCallHealthReader, IDisposable
{
    /// <summary>The rolling window, in calls.</summary>
    public const int Window = LlmPurposeHealth.Window;

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly ILogger _logger;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _lengthFinishes;
    private readonly Counter<long> _retries;
    private readonly ConcurrentDictionary<string, PurposeState> _purposes = new(StringComparer.Ordinal);

    public LlmCallMetrics(IMeterFactory? meterFactory = null, ILogger<LlmCallMetrics>? logger = null)
    {
        _ownsMeter = meterFactory is null;
        _meter = meterFactory?.Create("FrostAura.Reel") ?? new Meter("FrostAura.Reel");
        _logger = logger ?? NullLogger<LlmCallMetrics>.Instance;
        _calls = _meter.CreateCounter<long>(
            "reel.llm.calls", "{call}", "Sent LLM calls, by purpose and outcome (answered, length, fallback).");
        _lengthFinishes = _meter.CreateCounter<long>(
            "reel.llm.length_finishes", "{attempt}", "Attempts that finished with finish_reason=length, by purpose.");
        _retries = _meter.CreateCounter<long>(
            "reel.llm.retries", "{attempt}", "Attempts beyond a call's first, by purpose.");
        _meter.CreateObservableGauge(
            "reel.llm.window.length_rate",
            () => Rates(h => h.WindowLengthRate),
            "{ratio}",
            "Length-finish rate over each purpose's last 50 calls.");
        _meter.CreateObservableGauge(
            "reel.llm.window.fallback_rate",
            () => Rates(h => h.WindowFallbackRate),
            "{ratio}",
            "Fallback rate over each purpose's last 50 calls.");
    }

    /// <summary>
    /// Records one sent call. <paramref name="attempts"/> is the HTTP attempts it took (≥ 1);
    /// a call with no attempts was never sent and is ignored.
    /// </summary>
    public void Record(string purpose, int lengthFinishes, int attempts, bool fallback)
    {
        if (attempts < 1)
        {
            return;
        }

        var tag = new KeyValuePair<string, object?>("purpose", purpose);
        var outcome = fallback ? "fallback" : lengthFinishes > 0 ? "length" : "answered";
        _calls.Add(1, tag, new KeyValuePair<string, object?>("outcome", outcome));
        if (lengthFinishes > 0)
        {
            _lengthFinishes.Add(lengthFinishes, tag);
        }

        if (attempts > 1)
        {
            _retries.Add(attempts - 1, tag);
        }

        var state = _purposes.GetOrAdd(purpose, _ => new PurposeState());
        bool? crossed;
        LlmPurposeHealth health;
        lock (state)
        {
            var wasAlarming = state.Alarming;
            state.Add(lengthFinishes > 0, fallback, attempts - 1);
            health = state.Health(purpose);
            state.Alarming = health.Alarming;
            crossed = health.Alarming == wasAlarming ? null : health.Alarming;
        }

        if (crossed == true)
        {
            _logger.LogWarning(
                "LLM alarm: {Purpose} is above {Threshold:P0} over its last {Window} calls — length {Length:P1}, "
                + "fallback {Fallback:P1}, {Retries:0.00} retries per call.",
                purpose, LlmPurposeHealth.AlarmRate, health.WindowCalls, health.WindowLengthRate,
                health.WindowFallbackRate, health.WindowRetryRate);
        }
        else if (crossed == false)
        {
            _logger.LogInformation(
                "LLM alarm cleared: {Purpose} is back at or under {Threshold:P0} over its last {Window} calls.",
                purpose, LlmPurposeHealth.AlarmRate, health.WindowCalls);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LlmPurposeHealth> Snapshot() =>
        _purposes
            .Select(kv =>
            {
                lock (kv.Value)
                {
                    return kv.Value.Health(kv.Key);
                }
            })
            .OrderBy(h => h.Purpose, StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    private IEnumerable<Measurement<double>> Rates(Func<LlmPurposeHealth, double> pick) =>
        Snapshot().Select(h => new Measurement<double>(pick(h), new KeyValuePair<string, object?>("purpose", h.Purpose)));

    /// <summary>One purpose's totals and its ring of the last <see cref="Window"/> calls. Guarded by its own lock.</summary>
    private sealed class PurposeState
    {
        private readonly (bool Length, bool Fallback, int Retries)[] _ring = new (bool, bool, int)[Window];
        private int _next;
        private int _filled;

        public long Calls { get; private set; }

        public long LengthCalls { get; private set; }

        public long Fallbacks { get; private set; }

        public long Retries { get; private set; }

        public bool Alarming { get; set; }

        public void Add(bool length, bool fallback, int retries)
        {
            Calls++;
            LengthCalls += length ? 1 : 0;
            Fallbacks += fallback ? 1 : 0;
            Retries += retries;
            _ring[_next] = (length, fallback, retries);
            _next = (_next + 1) % Window;
            _filled = Math.Min(_filled + 1, Window);
        }

        public LlmPurposeHealth Health(string purpose)
        {
            var window = _ring.Take(_filled).ToList();
            double Rate(int count) => _filled == 0 ? 0d : (double)count / _filled;
            var lengthRate = Rate(window.Count(c => c.Length));
            var fallbackRate = Rate(window.Count(c => c.Fallback));
            var retryRate = Rate(window.Sum(c => c.Retries));
            var alarming = _filled >= Window
                && (lengthRate > LlmPurposeHealth.AlarmRate || fallbackRate > LlmPurposeHealth.AlarmRate);
            return new LlmPurposeHealth(
                purpose, Calls, LengthCalls, Fallbacks, Retries, _filled, lengthRate, fallbackRate, retryRate, alarming);
        }
    }
}
