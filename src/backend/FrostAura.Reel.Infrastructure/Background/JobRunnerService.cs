using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// The durable job spine: claims Pending SyncJobs with FOR UPDATE SKIP LOCKED (single-replica
/// today, correct under N replicas), runs each in a fresh DI scope with the job's account
/// pinned, heartbeats every 15s on a side context, and reclaims jobs whose heartbeat went
/// stale (crash recovery — handlers resume from CursorJson). Config gate: Background:JobRunner.
/// </summary>
public sealed class JobRunnerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<JobRunnerService> logger) : BackgroundService
{
    private const int MaxConcurrentJobs = 4;
    private const int MaxAttempts = JobRunnerLimits.MaxAttempts;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleAfter = JobRunnerLimits.StaleAfter;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentJobs, MaxConcurrentJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:JobRunner", true))
        {
            logger.LogInformation("JobRunner disabled via Background:JobRunner=false.");
            return;
        }

        logger.LogInformation("JobRunner online — {Slots} worker slots.", MaxConcurrentJobs);
        using var timer = new PeriodicTimer(PollInterval);

        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await ReclaimStaleJobsAsync(stoppingToken);

                while (_slots.CurrentCount > 0)
                {
                    var jobId = await ClaimNextJobAsync(stoppingToken);
                    if (jobId is null)
                    {
                        break;
                    }

                    await _slots.WaitAsync(stoppingToken);
                    _ = RunJobAsync(jobId.Value, stoppingToken)
                        .ContinueWith(_ => _slots.Release(), CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "JobRunner loop error; continuing.");
            }
        }
    }

    private async Task ReclaimStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var cutoff = DateTime.UtcNow - StaleAfter;

        // A stale Running job (crashed or wedged) whose attempts are spent is FAILED, not revived —
        // reviving unconditionally is what let a genuinely-stuck job respawn forever and spiral into
        // concurrent duplicates. Surfacing it as Failed also lets the UI show the problem instead of
        // an eternally-spinning pill.
        var failed = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs"
             SET "Status" = 'Failed',
                 "CompletedAt" = {DateTime.UtcNow},
                 "Error" = COALESCE("Error", 'Stalled with no heartbeat after {MaxAttempts} attempts.')
             WHERE "Status" = 'Running' AND "HeartbeatAt" < {cutoff} AND "AttemptCount" >= {MaxAttempts}
             """, ct);

        var reclaimed = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs" SET "Status" = 'Pending'
             WHERE "Status" = 'Running' AND "HeartbeatAt" < {cutoff} AND "AttemptCount" < {MaxAttempts}
             """, ct);

        if (failed > 0)
        {
            logger.LogWarning("Failed {Count} stalled job(s) past the {Max}-attempt cap.", failed, MaxAttempts);
        }
        if (reclaimed > 0)
        {
            logger.LogWarning("Reclaimed {Count} stale Running job(s); they resume from their cursors.", reclaimed);
        }
    }

    private async Task<Guid?> ClaimNextJobAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var now = DateTime.UtcNow;

        var claimed = await db.Database.SqlQuery<Guid>(ClaimNextSql(now)).ToListAsync(ct);

        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <summary>
    /// The claim, as one statement. Priority then age — with one ordering rule on top: a
    /// <c>BuildFeed</c> is not claimable while the same account has a <c>DeltaSync</c> pending or
    /// running. The refresh control enqueues both at once so the build scores the freshly synced
    /// library; with two runner slots the build used to start beside the sync, finish first on
    /// stale data, and the sync's own reconcile build then did the work again. The stale-job
    /// reclaim bounds how long a wedged sync can hold a build back.
    ///
    /// <para>The claim also clears the previous attempt's progress. A retry that inherited "100% —
    /// nothing cross-referenced this run" from the attempt that failed reported a finished job as
    /// live work for the whole retry (H-1); every attempt starts from no progress and the handler
    /// writes its own.</para>
    /// </summary>
    internal static FormattableString ClaimNextSql(DateTime now) =>
        $"""
         UPDATE "SyncJobs" SET
             "Status" = 'Running',
             "StartedAt" = COALESCE("StartedAt", {now}),
             "HeartbeatAt" = {now},
             "AttemptCount" = "AttemptCount" + 1,
             "ProgressPct" = NULL,
             "ProgressMessage" = NULL
         WHERE "Id" = (
             SELECT j."Id" FROM "SyncJobs" j
             WHERE j."Status" = 'Pending'
               AND NOT (j."Kind" = 'BuildFeed' AND EXISTS (
                   SELECT 1 FROM "SyncJobs" d
                   WHERE d."AccountId" = j."AccountId"
                     AND d."Kind" = 'DeltaSync'
                     AND d."Status" IN ('Pending', 'Running')))
             ORDER BY j."Priority", j."EnqueuedAt"
             LIMIT 1
             FOR UPDATE SKIP LOCKED)
         RETURNING "Id" AS "Value"
         """;

    private async Task RunJobAsync(Guid jobId, CancellationToken ct)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatLoopAsync(jobId, heartbeatCts.Token);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var job = await db.SyncJobs.FirstAsync(j => j.Id == jobId, ct);

        try
        {
            if (job.AccountId is { } accountId)
            {
                scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(accountId);
            }

            var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind)
                ?? throw new InvalidOperationException($"No handler registered for job kind {job.Kind}.");

            logger.LogInformation("Job {JobId} ({Kind}, account {AccountId}, attempt {Attempt}) starting.",
                job.Id, job.Kind, job.AccountId, job.AttemptCount);

            await handler.ExecuteAsync(job, ct);

            job.Status = JobStatus.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            job.ProgressPct = 100;
            job.Error = null;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Job {JobId} ({Kind}) succeeded.", job.Id, job.Kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) failed on attempt {Attempt}.", job.Id, job.Kind, job.AttemptCount);
            try
            {
                // Up to MaxAttempts; the claim path bumps AttemptCount, the cursor preserves progress.
                job.Status = job.AttemptCount >= MaxAttempts ? JobStatus.Failed : JobStatus.Pending;
                job.Error = ex.Message;
                if (job.Status == JobStatus.Pending)
                {
                    // Waiting for a retry is not progress: the status API must never show the
                    // failed attempt's percentage as live work while the job sits in the queue.
                    job.ProgressPct = null;
                    job.ProgressMessage = null;
                }
                await db.SaveChangesAsync(CancellationToken.None);

                if (job.Status == JobStatus.Failed && job.AccountId is { } failedAccount)
                {
                    var events = scope.ServiceProvider.GetRequiredService<IPipelineEventHub>();
                    events.Publish(failedAccount, PipelineEventTypes.Failed, new Dictionary<string, object?>
                    {
                        ["error"] = $"{job.Kind} failed after {job.AttemptCount} attempts.",
                    });
                }
            }
            catch (Exception persistEx)
            {
                logger.LogError(persistEx, "Failed to persist failure state for job {JobId}.", jobId);
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }
    }

    private async Task HeartbeatLoopAsync(Guid jobId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                {
                    return;
                }

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "SyncJobs" SET "HeartbeatAt" = {DateTime.UtcNow} WHERE "Id" = {jobId}""", ct);
            }
            catch (OperationCanceledException)
            {
                return; // normal shutdown of the beat
            }
            catch (Exception ex)
            {
                // A transient DB hiccup must not kill the beat — a dead beat gets the job
                // reclaimed mid-run and executed twice.
                logger.LogWarning(ex, "Heartbeat tick failed for job {JobId}; retrying next tick.", jobId);
            }
        }
    }

    private static async ValueTask<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
