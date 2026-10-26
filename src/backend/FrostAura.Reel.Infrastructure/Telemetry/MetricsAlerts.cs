using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>The alert rules (K-7, E-12). Code constants: changing a threshold is a reviewed change, not a knob.</summary>
public sealed record OperatorAlertOptions
{
    public static OperatorAlertOptions Default { get; } = new();

    /// <summary>How often the conditions are evaluated. A forced 402 must alert within 5 minutes.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The window failed job attempts are counted over.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Failed attempts per <see cref="FailureWindow"/>, across every account and kind, that raise
    /// <see cref="OperatorAlertCodes.JobFailures"/>. Five: one job failing all three attempts is
    /// visible in the status API already; five failures in an hour is a pattern.
    /// </summary>
    public int JobFailuresPerHour { get; init; } = 5;

    /// <summary>How often a still-active alert is repeated at the destination.</summary>
    public TimeSpan ReminderInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Where a raised alert goes. FD-20 names the destination; until it does, the one sink is the
/// host-local log (<see cref="LogOperatorAlertSink"/>), and the alert is also on the status API.
/// A web-push or email sink registers beside it.
/// </summary>
public interface IOperatorAlertSink
{
    Task DeliverAsync(OperatorAlert alert, OperatorAlertEvent kind, CancellationToken ct);
}

/// <summary>What happened to an alert this evaluation.</summary>
public enum OperatorAlertEvent
{
    Raised,
    Reminder,
    Resolved,
}

/// <summary>
/// The host-local alert log: one line per raise, reminder and resolve, under the category
/// <see cref="Category"/>, each beginning with <c>ALERT</c> so it can be found in any log store.
/// </summary>
public sealed class LogOperatorAlertSink(ILoggerFactory loggers) : IOperatorAlertSink
{
    public const string Category = "FrostAura.Reel.Alerts";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    public Task DeliverAsync(OperatorAlert alert, OperatorAlertEvent kind, CancellationToken ct)
    {
        var verb = kind switch
        {
            OperatorAlertEvent.Raised => "raised",
            OperatorAlertEvent.Reminder => "still active",
            _ => "resolved",
        };
        var level = kind == OperatorAlertEvent.Resolved ? LogLevel.Warning : LogLevel.Error;
        if (alert.Code == OperatorAlertCodes.LlmAlarm)
        {
            _logger.Log(
                level,
                "ALERT {Event} {Code}: model purpose {Purpose} is above the {Reason} alarm line over its last {Calls} calls. Since {Since:o}.",
                verb, alert.Code, alert.Purpose, alert.ReasonCode, alert.Count, alert.Since);
        }
        else if (alert.Code == OperatorAlertCodes.ProviderRefused)
        {
            _logger.Log(
                level,
                "ALERT {Event} {Code}: {Provider} refuses every call ({Reason}); every model purpose is off. Since {Since:o}.",
                verb, alert.Code, alert.Provider, alert.ReasonCode, alert.Since);
        }
        else
        {
            var kinds = alert.Kinds is { Count: > 0 } k ? string.Join(", ", k.Select(x => $"{x.Kind} {x.Count}")) : "none";
            _logger.Log(
                level,
                "ALERT {Event} {Code}: {Count} failed job attempt(s) in the hour, threshold {Threshold} ({Kinds}). Since {Since:o}.",
                verb, alert.Code, alert.Count, alert.Threshold, kinds, alert.Since);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Evaluates the alert conditions once a minute and delivers each change to every
/// <see cref="IOperatorAlertSink"/>: a new alert is raised once, repeated every
/// <see cref="OperatorAlertOptions.ReminderInterval"/> while it lasts, and resolved when it clears.
///
/// <list type="bullet">
/// <item><b>Provider refused</b> — any provider-wide breaker opened by a 401, 402 or 403
/// (WS-1's <see cref="IProviderHealthReader"/>). One alert per (provider, reason) while it lasts,
/// so the breaker's 15-minute probe cycle does not re-raise it.</item>
/// <item><b>Job failures</b> — failed attempts in the last hour, per kind, at or above the threshold
/// in total. Per kind, the larger of this process's attempt memory (which sees retried attempts)
/// and the database's terminal failures (which survive a restart).</item>
/// <item><b>LLM alarm</b> — a model purpose WS-1's call counters (<see cref="ILlmCallHealthReader"/>)
/// mark as alarming: length-finish or fallback rate above 2% over its last 50 sent calls. One alert
/// per purpose; it resolves when the window falls back under the line. The counters are in process,
/// so a restart starts the window again.</item>
/// </list>
///
/// <para>The current set is what the status API and the metrics sink show. State is in memory and
/// rebuilt from the same sources within one evaluation of a restart.</para>
/// </summary>
public sealed class OperatorAlertMonitor(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IOperatorAlertSink> sinks,
    ILogger<OperatorAlertMonitor> logger,
    ReelMetrics? metrics = null,
    OperatorAlertOptions? options = null) : BackgroundService, IOperatorAlertReader
{
    private readonly OperatorAlertOptions _options = options ?? OperatorAlertOptions.Default;
    private readonly IReadOnlyList<IOperatorAlertSink> _sinks = sinks.ToList();
    private readonly Dictionary<string, (OperatorAlert Alert, DateTime LastDelivered)> _active = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private IReadOnlyList<OperatorAlert> _current = [];

    public IReadOnlyList<OperatorAlert> Current() => Volatile.Read(ref _current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                try
                {
                    await EvaluateAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Alert evaluation failed; it runs again in {Seconds} s.", _options.PollInterval.TotalSeconds);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>One evaluation at <paramref name="now"/>. Internal for tests.</summary>
    internal async Task EvaluateAsync(DateTime now, CancellationToken ct)
    {
        var observed = new List<OperatorAlert>();
        using (var scope = scopeFactory.CreateScope())
        {
            observed.AddRange(ProviderRefusals(scope.ServiceProvider.GetService<IProviderHealthReader>()?.Current(), now));
            observed.AddRange(LlmAlarms(scope.ServiceProvider.GetService<ILlmCallHealthReader>()?.Snapshot(), now));
            if (await JobFailuresAsync(scope, now, ct) is { } failures)
            {
                observed.Add(failures);
            }
        }

        var deliveries = new List<(OperatorAlert Alert, OperatorAlertEvent Event)>();
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alert in observed)
            {
                var key = Key(alert);
                seen.Add(key);
                if (_active.TryGetValue(key, out var held))
                {
                    // Keep the first raise time; carry the latest counts.
                    var refreshed = alert with { RaisedAt = held.Alert.RaisedAt, Since = Earlier(held.Alert.Since, alert.Since) };
                    var remind = now - held.LastDelivered >= _options.ReminderInterval;
                    _active[key] = (refreshed, remind ? now : held.LastDelivered);
                    if (remind)
                    {
                        deliveries.Add((refreshed, OperatorAlertEvent.Reminder));
                    }
                }
                else
                {
                    var raised = alert with { RaisedAt = now };
                    _active[key] = (raised, now);
                    deliveries.Add((raised, OperatorAlertEvent.Raised));
                }
            }

            foreach (var gone in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                deliveries.Add((_active[gone].Alert, OperatorAlertEvent.Resolved));
                _active.Remove(gone);
            }

            Volatile.Write(
                ref _current,
                _active.Values.Select(v => v.Alert).OrderBy(a => a.RaisedAt).ThenBy(a => a.Code, StringComparer.Ordinal).ToList());
        }

        foreach (var (alert, kind) in deliveries)
        {
            if (kind != OperatorAlertEvent.Resolved)
            {
                metrics?.AlertRaised(alert.Code);
            }

            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.DeliverAsync(alert, kind, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One sink failing never stops the others, least of all the host-local log.
                    logger.LogError(ex, "Alert sink {Sink} failed to deliver {Code}.", sink.GetType().Name, alert.Code);
                }
            }
        }
    }

    private static IEnumerable<OperatorAlert> ProviderRefusals(ProviderHealthSnapshot? health, DateTime now)
    {
        if (health is null)
        {
            yield break;
        }

        foreach (var breaker in health.Breakers.Where(b => b.Purpose is null && IsRefusal(b.ReasonCode)))
        {
            yield return new OperatorAlert(
                OperatorAlertCodes.ProviderRefused, breaker.Since, now, breaker.ReasonCode, breaker.Provider,
                Count: null, Threshold: null, Kinds: null);
        }
    }

    private static IEnumerable<OperatorAlert> LlmAlarms(IReadOnlyList<LlmPurposeHealth>? purposes, DateTime now) =>
        (purposes ?? [])
            .Where(p => p.Alarming)
            .Select(p => new OperatorAlert(
                OperatorAlertCodes.LlmAlarm, now, now,
                ReasonCode: p.WindowLengthRate > LlmPurposeHealth.AlarmRate ? "length" : "fallback",
                Provider: ApiProvider.OpenRouter,
                Count: p.WindowCalls,
                Threshold: null,
                Kinds: null)
            { Purpose = p.Purpose });

    private static bool IsRefusal(string reasonCode) =>
        reasonCode is JobFailureReason.PaymentRequired or JobFailureReason.Unauthorized or JobFailureReason.Forbidden;

    private async Task<OperatorAlert?> JobFailuresAsync(IServiceScope scope, DateTime now, CancellationToken ct)
    {
        var since = now - _options.FailureWindow;

        // Every account's jobs: the alert is about the system (J-11 system scope).
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var terminal = await db.SyncJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Failed && j.CompletedAt >= since && j.CompletedAt <= now)
            .GroupBy(j => j.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Kind, x => x.Count, ct);
        var attempts = metrics?.FailedAttemptsSince(since) ?? new Dictionary<JobKind, int>();

        var kinds = terminal.Keys.Union(attempts.Keys)
            .Select(kind => new AlertKindCount(
                kind, Math.Max(terminal.GetValueOrDefault(kind), attempts.GetValueOrDefault(kind))))
            .OrderByDescending(k => k.Count)
            .ThenBy(k => k.Kind.ToString(), StringComparer.Ordinal)
            .ToList();
        var total = kinds.Sum(k => k.Count);
        return total < _options.JobFailuresPerHour
            ? null
            : new OperatorAlert(
                OperatorAlertCodes.JobFailures, since, now, ReasonCode: null, Provider: null,
                total, _options.JobFailuresPerHour, kinds);
    }

    private static string Key(OperatorAlert alert) => alert.Code switch
    {
        OperatorAlertCodes.ProviderRefused => $"{alert.Code}:{alert.Provider}:{alert.ReasonCode}",
        // One per purpose: a purpose that goes from the length line to the fallback line is the same alarm.
        OperatorAlertCodes.LlmAlarm => $"{alert.Code}:{alert.Purpose}",
        _ => alert.Code,
    };

    private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;
}
