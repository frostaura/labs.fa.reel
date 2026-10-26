using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>One job kind's figures over the report window.</summary>
/// <param name="Kind">The job kind.</param>
/// <param name="Succeeded">Jobs that finished in the window.</param>
/// <param name="Failed">Jobs that failed for good in the window.</param>
/// <param name="Retrying">Jobs queued again after a failed attempt (Pending, with an error, attempts left).</param>
/// <param name="FailureReasons">Failed and retrying jobs by <c>JobFailureReason</c> code; never the message.</param>
/// <param name="DeadlineYields">Deadline slices the jobs touched in the window have taken (M5 <c>DeadlineYields</c>).</param>
/// <param name="Interruptions">Host-stop refunds the jobs touched in the window have taken (M5 <c>Interruptions</c>).</param>
/// <param name="Requeued">Jobs sent to the back of the queue at a deadline inside the window (M5 <c>RequeuedAt</c>).</param>
public sealed record JobKindMetrics(
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobKind>))] JobKind Kind,
    int Succeeded,
    int Failed,
    int Retrying,
    IReadOnlyDictionary<string, int> FailureReasons,
    int DeadlineYields,
    int Interruptions,
    int Requeued);

/// <summary>The caller's browser error reports since this process started.</summary>
public sealed record ClientErrorMetrics(long Accepted, long RateLimited, DateTime Since);

/// <summary><c>GET /api/observability/metrics</c>.</summary>
/// <param name="GeneratedAt">When the report was built.</param>
/// <param name="WindowStart">The start of the report window.</param>
/// <param name="WindowHours">The window's length.</param>
/// <param name="Jobs">Per job kind, from the database (the caller's jobs and global ones).</param>
/// <param name="ClientErrors">The caller's browser error reports since process start.</param>
/// <param name="Ask">The caller's Ask Reel turns in the window: stage latency and fallbacks, from the turn record.</param>
/// <param name="Llm">
/// Model calls per purpose since <see cref="LlmSince"/>, every account's: WS-1's call counters
/// (<see cref="ILlmCallHealthReader"/>) — sent calls, length finishes, fallbacks, retries, and the
/// rolling 50-call window the alarm reads. One tally: the metrics report, the status API and the
/// <c>reel.llm.*</c> instruments all read it.
/// </param>
/// <param name="LlmSince">When this process started counting model calls.</param>
/// <param name="Alerts">Active operator alerts; Founder-tier callers only, otherwise empty.</param>
public sealed record MetricsReport(
    DateTime GeneratedAt,
    DateTime WindowStart,
    int WindowHours,
    IReadOnlyList<JobKindMetrics> Jobs,
    ClientErrorMetrics ClientErrors,
    AskMetrics Ask,
    IReadOnlyList<LlmPurposeHealth> Llm,
    DateTime LlmSince,
    IReadOnlyList<OperatorAlert> Alerts);

/// <summary>
/// The job half of the metrics sink, read from the database rather than from counters: the M5
/// columns (<c>DeadlineYields</c>, <c>Interruptions</c>, <c>RequeuedAt</c>) and the terminal states the
/// runner writes are durable, so the figures survive a restart and agree with the status API.
///
/// <para>Scoped to the caller: their own jobs plus global ones (which carry no tenant data), and
/// only counts and reason codes leave — never an error message, which can quote provider text.
/// A job is in the window when it was enqueued, last heard from, requeued or completed inside it;
/// its slice and interruption totals are the job's whole history, not just the window's part.</para>
/// </summary>
public sealed class JobMetricsQuery(IReelDbContext db)
{
    public async Task<IReadOnlyList<JobKindMetrics>> GetAsync(Guid accountId, DateTime since, CancellationToken ct)
    {
        var rows = await db.SyncJobs
            .AsNoTracking()
            .Where(j => j.AccountId == accountId || j.AccountId == null)
            .Where(j => j.EnqueuedAt >= since
                || j.HeartbeatAt >= since
                || j.RequeuedAt >= since
                || j.CompletedAt >= since)
            .Select(j => new
            {
                j.Kind,
                j.Status,
                j.Error,
                j.DeadlineYields,
                j.Interruptions,
                j.RequeuedAt,
                j.CompletedAt,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g =>
            {
                var failed = g.Where(r => r.Status == JobStatus.Failed && r.CompletedAt >= since).ToList();
                var retrying = g.Where(r => r.Status == JobStatus.Pending && r.Error is not null).ToList();
                var reasons = failed.Concat(retrying)
                    .GroupBy(r => JobFailureReason.Classify(r.Error), StringComparer.Ordinal)
                    .OrderBy(r => r.Key, StringComparer.Ordinal)
                    .ToDictionary(r => r.Key, r => r.Count(), StringComparer.Ordinal);
                return new JobKindMetrics(
                    g.Key,
                    Succeeded: g.Count(r => r.Status == JobStatus.Succeeded && r.CompletedAt >= since),
                    Failed: failed.Count,
                    Retrying: retrying.Count,
                    reasons,
                    DeadlineYields: g.Sum(r => r.DeadlineYields),
                    Interruptions: g.Sum(r => r.Interruptions),
                    Requeued: g.Count(r => r.RequeuedAt >= since));
            })
            .ToList();
    }
}
