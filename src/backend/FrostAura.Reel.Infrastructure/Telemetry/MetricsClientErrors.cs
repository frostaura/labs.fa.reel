using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>
/// The body of <c>POST /api/client-errors</c>: what the browser's React root hooks report
/// (<c>src/frontend/src/lib/clientErrors.ts</c> builds it). Every field is untrusted input.
/// </summary>
public sealed record ClientErrorReport(
    string? Name,
    string? Message,
    string? Stack,
    string? ComponentStack,
    string? Origin,
    string? Path,
    DateTime? OccurredAt);

/// <summary>How a report was handled.</summary>
public enum ClientErrorOutcome
{
    /// <summary>Written to the log and counted.</summary>
    Accepted,

    /// <summary>Refused: the caller's budget for the window is spent. Nothing written.</summary>
    RateLimited,

    /// <summary>Refused: not a report this sink can record honestly. Nothing written.</summary>
    Invalid,
}

/// <summary>Per-caller totals since this process started, for the metrics report.</summary>
public sealed record ClientErrorTotals(long Accepted, long RateLimited);

/// <summary>The sink's limits. Defaults are production; tests shrink them.</summary>
public sealed record ClientErrorSinkOptions
{
    /// <summary>The fixed window each budget below applies to.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reports one signed-in account may file per window. The browser already sends at most 10 per
    /// page load, one per distinct error; past this a tab is in a reload loop and more copies of the
    /// same stack only cost log volume.
    /// </summary>
    public int PerAccount { get; init; } = 20;

    /// <summary>
    /// Reports per window from callers with no account (a sign-in page that crashed), shared by all
    /// of them: unauthenticated input must not be able to grow the log without bound.
    /// </summary>
    public int Anonymous { get; init; } = 30;

    public int MaxNameLength { get; init; } = 200;

    public int MaxFieldLength { get; init; } = 4000;

    public int MaxPathLength { get; init; } = 512;
}

/// <summary>
/// The client-error sink (I-4): account-scoped, rate-limited, and nothing but a structured log line
/// plus a counter. No table: a browser error is an operational signal with the same retention as
/// the rest of the logs, and storing untrusted text per user would need its own purge and erasure
/// story for no extra use.
///
/// <para>What is kept is bounded and stripped before it is written: fields are clipped, control
/// characters leave the one-line fields, and the path loses any query string or fragment (where an
/// Ask Reel question could sit), whatever the browser sent.</para>
/// </summary>
public sealed class ClientErrorSink(
    ILogger<ClientErrorSink> logger,
    ReelMetrics metrics,
    ClientErrorSinkOptions? options = null,
    TimeProvider? clock = null)
{
    private const string AnonymousKey = "anonymous";
    private const int MaxTrackedCallers = 10_000;

    private static readonly string[] Origins = ["caught", "uncaught"];

    private readonly ClientErrorSinkOptions _options = options ?? new ClientErrorSinkOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime WindowStart, int Count)> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClientErrorTotals> _totals = new(StringComparer.Ordinal);

    /// <summary>When the current window for <paramref name="accountId"/> ends, for a Retry-After.</summary>
    public TimeSpan RetryAfter(Guid? accountId)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            return _windows.TryGetValue(Key(accountId), out var window) && window.WindowStart + _options.Window > now
                ? window.WindowStart + _options.Window - now
                : TimeSpan.Zero;
        }
    }

    /// <summary>The caller's totals since this process started (zero for a caller never seen).</summary>
    public ClientErrorTotals TotalsFor(Guid? accountId) => _totals.GetValueOrDefault(Key(accountId), new ClientErrorTotals(0, 0));

    public ClientErrorOutcome Record(Guid? accountId, ClientErrorReport? report)
    {
        if (report is null
            || string.IsNullOrWhiteSpace(report.Name)
            || report.Message is null
            || report.Origin is null || !Origins.Contains(report.Origin, StringComparer.Ordinal)
            || report.Path is null || !report.Path.StartsWith('/'))
        {
            metrics.ClientErrorRejected("invalid");
            return ClientErrorOutcome.Invalid;
        }

        var key = Key(accountId);
        if (!TryTake(key, accountId is null ? _options.Anonymous : _options.PerAccount, out var firstRefusal))
        {
            Count(key, rateLimited: true);
            metrics.ClientErrorRejected("rate_limited");
            if (firstRefusal)
            {
                // One line per caller per window says reports are being dropped; the drops themselves
                // are only counted, or the limit would protect nothing.
                logger.LogWarning(
                    "Client error reports from {Caller} are rate-limited for the rest of this {Minutes}-minute window.",
                    key, _options.Window.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture));
            }

            return ClientErrorOutcome.RateLimited;
        }

        var path = OneLine(StripQuery(report.Path), _options.MaxPathLength);
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["AccountId"] = accountId,
            ["ClientErrorStack"] = Clip(report.Stack, _options.MaxFieldLength),
            ["ClientComponentStack"] = Clip(report.ComponentStack, _options.MaxFieldLength),
            ["ClientOccurredAt"] = report.OccurredAt,
        }))
        {
            logger.LogWarning(
                "Client error ({ClientErrorOrigin}) at {ClientPath}: {ClientErrorName}: {ClientErrorMessage}",
                report.Origin,
                path,
                OneLine(report.Name, _options.MaxNameLength),
                OneLine(report.Message, _options.MaxFieldLength));
        }

        Count(key, rateLimited: false);
        metrics.ClientErrorAccepted(report.Origin);
        return ClientErrorOutcome.Accepted;
    }

    private static string Key(Guid? accountId) =>
        accountId is { } id ? id.ToString("D", CultureInfo.InvariantCulture) : AnonymousKey;

    /// <summary>Takes one report from the caller's budget; <paramref name="firstRefusal"/> marks the first drop of a window.</summary>
    private bool TryTake(string key, int budget, out bool firstRefusal)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_windows.Count >= MaxTrackedCallers)
            {
                foreach (var stale in _windows.Where(w => w.Value.WindowStart + _options.Window <= now).Select(w => w.Key).ToList())
                {
                    _windows.Remove(stale);
                }
            }

            var window = _windows.TryGetValue(key, out var current) && current.WindowStart + _options.Window > now
                ? current
                : (WindowStart: now, Count: 0);
            window.Count++;
            _windows[key] = window;
            firstRefusal = window.Count == budget + 1;
            return window.Count <= budget;
        }
    }

    private void Count(string key, bool rateLimited) =>
        _totals.AddOrUpdate(
            key,
            _ => rateLimited ? new ClientErrorTotals(0, 1) : new ClientErrorTotals(1, 0),
            (_, t) => rateLimited ? t with { RateLimited = t.RateLimited + 1 } : t with { Accepted = t.Accepted + 1 });

    private static string StripQuery(string path)
    {
        var cut = path.IndexOfAny(['?', '#']);
        return cut < 0 ? path : path[..cut];
    }

    private static string? Clip(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Clipped, with every control character (a forged second log line) turned into a space.</summary>
    private static string OneLine(string value, int max) =>
        string.Create(Math.Min(value.Length, max), value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
}
