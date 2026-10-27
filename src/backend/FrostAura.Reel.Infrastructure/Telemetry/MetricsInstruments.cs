using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>
/// Reel's in-process instruments (meter <see cref="MeterName"/>), readable live with
/// <c>dotnet-counters monitor --counters FrostAura.Reel</c> and by any exporter added later. The
/// durable record is the database: <see cref="JobMetricsQuery"/> reads the same facts back from the
/// M5 columns, so a restart loses a counter but never a fact.
///
/// <para>Tags are closed vocabularies (job kind, reason code, origin), never free text: a tag value
/// per error message is how a metrics backend runs out of series.</para>
/// </summary>
public sealed class ReelMetrics : IDisposable
{
    public const string MeterName = "FrostAura.Reel";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _jobsSucceeded;
    private readonly Counter<long> _jobsFailed;
    private readonly Counter<long> _deadlineYields;
    private readonly Counter<long> _interruptions;
    private readonly Counter<long> _clientErrorsAccepted;
    private readonly Counter<long> _clientErrorsRejected;
    private readonly Counter<long> _alertsRaised;

    /// <summary>
    /// Failed attempts — retrying ones included — newest last, for the alert monitor's hourly count.
    /// The database records only a job's LAST failure, so attempts that were retried and then
    /// succeeded are visible here and nowhere else. Trimmed to <see cref="FailureMemory"/>.
    /// </summary>
    private readonly ConcurrentQueue<(DateTime At, JobKind Kind)> _recentFailures = new();

    /// <summary>How long failed attempts are remembered for the alert window.</summary>
    public static readonly TimeSpan FailureMemory = TimeSpan.FromHours(2);

    /// <summary>A bound on the remembered attempts, so a failure storm cannot grow memory without limit.</summary>
    private const int FailureMemoryCap = 20_000;

    public ReelMetrics(IMeterFactory? meterFactory = null)
    {
        _ownsMeter = meterFactory is null;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _jobsSucceeded = _meter.CreateCounter<long>("reel.jobs.succeeded", "{job}", "Jobs that finished.");
        _jobsFailed = _meter.CreateCounter<long>(
            "reel.jobs.failed", "{attempt}", "Failed job attempts, by kind, reason code and whether the job will be retried.");
        _deadlineYields = _meter.CreateCounter<long>(
            "reel.jobs.deadline_yields", "{slice}", "Resumable jobs that reached their kind's deadline and went back to the queue.");
        _interruptions = _meter.CreateCounter<long>(
            "reel.jobs.interruptions", "{job}", "Jobs put back to the queue, attempt refunded, because the host was stopping.");
        _clientErrorsAccepted = _meter.CreateCounter<long>(
            "reel.client_errors.accepted", "{report}", "Browser error reports written to the log.");
        _clientErrorsRejected = _meter.CreateCounter<long>(
            "reel.client_errors.rejected", "{report}", "Browser error reports refused (rate_limited or invalid).");
        _alertsRaised = _meter.CreateCounter<long>(
            "reel.alerts.raised", "{alert}", "Operator alerts raised (and hourly reminders), by alert code.");
    }

    /// <summary>The meter, for listeners that must tell this instance from another one in the same process.</summary>
    public Meter Meter => _meter;

    public void JobSucceeded(JobKind kind) => _jobsSucceeded.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));

    /// <param name="kind">The job's kind.</param>
    /// <param name="reasonCode">A <c>JobFailureReason</c> code, never the message.</param>
    /// <param name="retrying">Whether the job went back to the queue for another attempt.</param>
    public void JobFailed(JobKind kind, string reasonCode, bool retrying)
    {
        _jobsFailed.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind.ToString()),
            new KeyValuePair<string, object?>("reason", reasonCode),
            new KeyValuePair<string, object?>("retrying", retrying));
        RememberFailure(DateTime.UtcNow, kind);
    }

    /// <summary>Failed attempts since <paramref name="since"/>, per kind, from this process's memory.</summary>
    public IReadOnlyDictionary<JobKind, int> FailedAttemptsSince(DateTime since) =>
        _recentFailures
            .Where(f => f.At >= since)
            .GroupBy(f => f.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The earliest failed attempt at or after <paramref name="since"/> in this process's memory, or null.</summary>
    public DateTime? EarliestFailedAttemptSince(DateTime since)
    {
        DateTime? earliest = null;
        foreach (var (at, _) in _recentFailures)
        {
            if (at >= since && (earliest is null || at < earliest))
            {
                earliest = at;
            }
        }

        return earliest;
    }

    /// <summary>Records a failed attempt at a given time. Internal for tests that need a past attempt.</summary>
    internal void RememberFailure(DateTime at, JobKind kind)
    {
        _recentFailures.Enqueue((at, kind));
        var horizon = at - FailureMemory;
        while ((_recentFailures.Count > FailureMemoryCap
                || (_recentFailures.TryPeek(out var oldest) && oldest.At < horizon))
            && _recentFailures.TryDequeue(out _))
        {
        }
    }

    /// <param name="code">An <c>OperatorAlertCodes</c> value.</param>
    public void AlertRaised(string code) => _alertsRaised.Add(1, new KeyValuePair<string, object?>("code", code));

    public void JobYielded(JobKind kind) => _deadlineYields.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));

    public void JobInterrupted(JobKind kind) => _interruptions.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));

    public void ClientErrorAccepted(string origin) =>
        _clientErrorsAccepted.Add(1, new KeyValuePair<string, object?>("origin", origin));

    /// <param name="reason"><c>rate_limited</c> or <c>invalid</c>.</param>
    public void ClientErrorRejected(string reason) =>
        _clientErrorsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose()
    {
        // A factory-made meter belongs to the factory, which disposes it with the container.
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
