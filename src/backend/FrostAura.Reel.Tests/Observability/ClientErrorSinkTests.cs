using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.Observability;

/// <summary>
/// The client-error sink (I-4) without a host: what it writes, what it refuses, and the budgets.
/// The HTTP half is <c>ObservabilityEndpointsTests</c>.
/// </summary>
public sealed class ClientErrorSinkTests : IDisposable
{
    private readonly ScopedLogger _log = new();
    private readonly ReelMetrics _metrics = new();
    private readonly MeterRecorder _counters;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    public ClientErrorSinkTests() => _counters = new MeterRecorder(_metrics.Meter);

    public void Dispose()
    {
        _counters.Dispose();
        _metrics.Dispose();
    }

    private ClientErrorSink Sink(int perAccount = 3, int anonymous = 2) =>
        new(_log, _metrics, new ClientErrorSinkOptions { PerAccount = perAccount, Anonymous = anonymous }, _clock);

    private static ClientErrorReport Report(
        string? name = "TypeError",
        string? message = "x is undefined",
        string? origin = "caught",
        string? path = "/feed",
        string? stack = "at Feed (feed.tsx:10)") =>
        new(name, message, stack, "in Feed\n in App", origin, path, new DateTime(2026, 10, 25, 8, 59, 0, DateTimeKind.Utc));

    [Fact]
    public void An_accepted_report_is_one_warning_under_the_callers_account_with_the_query_and_fragment_gone()
    {
        var account = Guid.NewGuid();

        var outcome = Sink().Record(account, Report(path: "/search?q=films+my+ex+liked#top"));

        Assert.Equal(ClientErrorOutcome.Accepted, outcome);
        var line = Assert.Single(_log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Client error (caught) at /search: TypeError: x is undefined", line.Message);
        Assert.DoesNotContain("films", line.Message, StringComparison.Ordinal);
        Assert.Equal(account, line.Scope["AccountId"]);
        Assert.Equal("at Feed (feed.tsx:10)", line.Scope["ClientErrorStack"]);
        Assert.Equal("in Feed\n in App", line.Scope["ClientComponentStack"]);
        Assert.Equal(1, _counters.Sum("reel.client_errors.accepted", ("origin", "caught")));
    }

    [Fact]
    public void Control_characters_cannot_forge_a_second_log_line_and_every_field_is_clipped()
    {
        var huge = new string('m', 10_000);
        var outcome = Sink().Record(Guid.NewGuid(), Report(
            name: "Error\r\nERROR forged admin login\u001b[31m" + new string('n', 500),
            message: "boom\nINFO all good" + huge,
            path: "/x\n/y" + new string('p', 1_000),
            stack: huge));

        Assert.Equal(ClientErrorOutcome.Accepted, outcome);
        var line = Assert.Single(_log.Lines);
        Assert.DoesNotContain('\n', line.Message);
        Assert.DoesNotContain('\r', line.Message);
        Assert.DoesNotContain('\u001b', line.Message);
        Assert.Equal(200, ((string)line.Values["ClientErrorName"]!).Length);
        Assert.Equal(4000, ((string)line.Values["ClientErrorMessage"]!).Length);
        Assert.Equal(512, ((string)line.Values["ClientPath"]!).Length);
        Assert.Equal(4000, ((string)line.Scope["ClientErrorStack"]!).Length);
    }

    public static TheoryData<string, ClientErrorReport?> Invalid => new()
    {
        { "no body", null },
        { "no name", Report(name: null) },
        { "blank name", Report(name: "  ") },
        { "no message", Report(message: null) },
        { "unknown origin", Report(origin: "server") },
        { "no origin", Report(origin: null) },
        { "a full URL, not a path", Report(path: "https://evil.example/") },
        { "no path", Report(path: null) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_report_the_sink_cannot_record_honestly_is_refused_and_writes_nothing(string because, ClientErrorReport? report)
    {
        Assert.True(Sink().Record(Guid.NewGuid(), report) == ClientErrorOutcome.Invalid, because);
        Assert.Empty(_log.Lines);
        Assert.Equal(1, _counters.Sum("reel.client_errors.rejected", ("reason", "invalid")));
    }

    [Fact]
    public void An_account_past_its_budget_is_refused_with_one_warning_and_another_account_is_not_affected()
    {
        var sink = Sink(perAccount: 3);
        var looping = Guid.NewGuid();
        var other = Guid.NewGuid();

        var outcomes = Enumerable.Range(0, 6).Select(_ => sink.Record(looping, Report())).ToList();

        Assert.Equal(
            [ClientErrorOutcome.Accepted, ClientErrorOutcome.Accepted, ClientErrorOutcome.Accepted,
             ClientErrorOutcome.RateLimited, ClientErrorOutcome.RateLimited, ClientErrorOutcome.RateLimited],
            outcomes);
        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(other, Report()));

        // Three reports and one "rate-limited" line for the looping tab; the other account's report.
        Assert.Equal(5, _log.Lines.Count);
        Assert.Single(_log.Lines, l => l.Message.Contains("rate-limited", StringComparison.Ordinal));
        Assert.Equal(new ClientErrorTotals(3, 3), sink.TotalsFor(looping));
        Assert.Equal(new ClientErrorTotals(1, 0), sink.TotalsFor(other));
        Assert.Equal(3, _counters.Sum("reel.client_errors.rejected", ("reason", "rate_limited")));
        Assert.Equal(TimeSpan.FromMinutes(10), sink.RetryAfter(looping));
    }

    [Fact]
    public void A_new_window_restores_the_budget()
    {
        var sink = Sink(perAccount: 1);
        var account = Guid.NewGuid();
        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(account, Report()));
        Assert.Equal(ClientErrorOutcome.RateLimited, sink.Record(account, Report()));

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(ClientErrorOutcome.RateLimited, sink.Record(account, Report()));
        Assert.Equal(TimeSpan.FromMinutes(1), sink.RetryAfter(account));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(account, Report()));
        Assert.Equal(TimeSpan.FromMinutes(10), sink.RetryAfter(account));
    }

    [Fact]
    public void Signed_out_callers_share_one_smaller_budget()
    {
        var sink = Sink(perAccount: 5, anonymous: 2);

        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(null, Report()));
        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(null, Report(path: "/signin")));
        Assert.Equal(ClientErrorOutcome.RateLimited, sink.Record(null, Report(path: "/somewhere-else")));
        Assert.Equal(new ClientErrorTotals(2, 1), sink.TotalsFor(null));
        Assert.Null(_log.Lines.First().Scope["AccountId"]);

        // An account's budget is its own.
        Assert.Equal(ClientErrorOutcome.Accepted, sink.Record(Guid.NewGuid(), Report()));
    }

    /// <summary>A logger that keeps each line with its structured values and the scope it was written in.</summary>
    private sealed class ScopedLogger : ILogger<ClientErrorSink>
    {
        private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _scope = new();

        public List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?> Scope)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var previous = _scope.Value;
            _scope.Value = state as IReadOnlyDictionary<string, object?>
                ?? (state as IDictionary<string, object?>)?.ToDictionary(k => k.Key, k => k.Value);
            return new Restore(() => _scope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(k => k.Key, k => k.Value)
                ?? new Dictionary<string, object?>();
            Lines.Add((logLevel, formatter(state, exception), values, _scope.Value ?? new Dictionary<string, object?>()));
        }

        private sealed class Restore(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>Collects every measurement one meter instance publishes, for asserting on counters.</summary>
internal sealed class MeterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

    public MeterRecorder(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        _listener.Start();
    }

    /// <summary>The total recorded on <paramref name="instrument"/> by measurements carrying every one of <paramref name="tags"/>.</summary>
    public long Sum(string instrument, params (string Key, object? Value)[] tags) =>
        _measurements
            .Where(m => m.Instrument == instrument)
            .Where(m => tags.All(t => m.Tags.Any(x => x.Key == t.Key && Equals(x.Value, t.Value))))
            .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
