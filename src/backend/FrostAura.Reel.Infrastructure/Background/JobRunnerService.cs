using System.Collections.Concurrent;
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

/// <summary>How one execution of a claimed job ended. Returned for the tests that drive the real runner.</summary>
public enum JobRunOutcome
{
    /// <summary>The handler finished; the row is Succeeded with <c>CompletedAt</c>.</summary>
    Succeeded,

    /// <summary>The attempt failed and attempts remain: the row is Pending again, its error kept.</summary>
    Retrying,

    /// <summary>The attempt failed for good (the last attempt, or a non-retryable failure): Failed with <c>CompletedAt</c>.</summary>
    Failed,

    /// <summary>A resumable job reached its kind's deadline: back in the queue, attempt refunded.</summary>
    Yielded,

    /// <summary>The host is stopping: back in the queue, attempt refunded.</summary>
    Interrupted,

    /// <summary>
    /// Another runner owns the row now (this execution's heartbeat went stale and it was
    /// reclaimed): nothing was written, and the execution stopped at the first heartbeat that saw it.
    /// </summary>
    LeaseLost,
}

/// <summary>
/// The durable job spine. Claims Pending SyncJobs with FOR UPDATE SKIP LOCKED under a claim lock,
/// runs each in a fresh DI scope with the job's account pinned, heartbeats on a side context, and
/// reclaims jobs whose heartbeat went stale (crash recovery — handlers resume from CursorJson).
/// Config gate: <c>Background:JobRunner</c>.
///
/// <para><b>One live execution per job (fencing).</b> A claim bumps <c>AttemptCount</c>, and that
/// number is the execution's lease: the heartbeat and every terminal write are conditional on
/// <c>(Id, AttemptCount = claimed, Status = 'Running')</c>. An execution that was reclaimed while it
/// was wedged therefore can neither keep the row alive nor write its result over the new
/// execution's — its first heartbeat finds no row, cancels its handler and stops. Jobs this process
/// is running are never reclaimed by this process. The claim refuses a row whose attempts are spent,
/// so <c>AttemptCount</c> never passes <see cref="JobRunnerOptions.MaxAttempts"/> (NEW-2, J+2).</para>
///
/// <para><b>Fairness (J-5).</b> General slots run any kind; reserved slots run only the kinds a user
/// is waiting on (DeltaSync, BuildFeed, Train). EnrichCatalog, CrossReference and HydrateCatalog run
/// one at a time each. Every kind has a deadline: a resumable kind yields its slot and goes to the
/// back of the queue to resume from its checkpoint; a kind that restarts from nothing retries. LLM
/// kinds are not claimed while the provider-health reader reports their breaker open.</para>
///
/// <para><b>Honest terminal states (J-6, K-12).</b> A handler's cancellation is a failure unless the
/// host is stopping (or the deadline or a lost lease caused it); <c>CompletedAt</c> is stamped on
/// every terminal state; every message is built in C# and passed as a parameter; and a stopping host
/// puts its running jobs back to Pending with the interrupted attempt refunded.</para>
/// </summary>
public sealed class JobRunnerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<JobRunnerService> logger,
    JobRunnerOptions options) : BackgroundService
{
    /// <summary>Transaction-scoped advisory lock that serialises claims across replicas, so the per-kind caps are exact.</summary>
    private const long ClaimLockKey = 0x5245_454C_4A4F_4253; // "REELJOBS"

    private const int MaxErrorLength = 4000;

    private readonly ConcurrentDictionary<Guid, LiveJob> _live = new();
    private int _generalBusy;
    private int _reservedBusy;
    private IReadOnlyList<string> _handledKinds = Enum.GetNames<JobKind>();
    private string _lastPaused = string.Empty;

    /// <summary>A claimed execution: the row, the attempt number that is its lease, and its kind.</summary>
    public sealed record ClaimedJob(Guid Id, int Attempt, JobKind Kind);

    private sealed record LiveJob(ClaimedJob Claim, Task Run);

    /// <summary>The shape the claim statement returns.</summary>
    private sealed class ClaimRow
    {
        public Guid Id { get; set; }

        public int AttemptCount { get; set; }

        public string Kind { get; set; } = string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:JobRunner", true))
        {
            logger.LogInformation("JobRunner disabled via Background:JobRunner=false.");
            return;
        }

        _handledKinds = ResolveHandledKinds();
        await RepairHistoryAsync(stoppingToken);

        logger.LogInformation(
            "JobRunner online — {General} general and {Reserved} reserved slot(s), {Kinds} job kind(s).",
            options.GeneralSlots, options.ReservedSlots, _handledKinds.Count);

        using var timer = new PeriodicTimer(options.PollInterval);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
                await FillSlotsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "JobRunner loop error; continuing.");
            }
        }

        await DrainAsync();
    }

    // ── Claiming ─────────────────────────────────────────────────────────────────────────────

    private async Task FillSlotsAsync(CancellationToken stoppingToken)
    {
        var paused = PausedKinds();

        while (true)
        {
            var generalFree = options.GeneralSlots - Volatile.Read(ref _generalBusy) > 0;
            var reservedFree = options.ReservedSlots - Volatile.Read(ref _reservedBusy) > 0;
            if (!generalFree && !reservedFree)
            {
                return;
            }

            var kinds = _handledKinds
                .Where(k => !paused.Contains(k))
                .Where(k => generalFree || options.InteractiveKinds.Contains(Enum.Parse<JobKind>(k)))
                .ToList();
            if (kinds.Count == 0)
            {
                return;
            }

            var claim = await ClaimNextAsync(kinds, stoppingToken);
            if (claim is null)
            {
                return;
            }

            // An interactive job takes a reserved slot when one is free, keeping the general slots
            // for everything else; anything else (or an interactive job when the reserved lane is
            // full) takes a general slot, which the claim only allowed if one was free.
            var lane = reservedFree && options.InteractiveKinds.Contains(claim.Kind) ? JobLane.Reserved : JobLane.General;
            Interlocked.Increment(ref Busy(lane));

            // Registered as live BEFORE it can start, so a job that finishes at once can never
            // leave a stale entry behind (which would exempt its id from the stale-job reclaim).
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = Task.Run(async () =>
            {
                try
                {
                    await registered.Task;
                    await RunAsync(claim, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {JobId} ({Kind}) escaped the runner's handling.", claim.Id, claim.Kind);
                }
                finally
                {
                    _live.TryRemove(claim.Id, out _);
                    Interlocked.Decrement(ref Busy(lane));
                }
            }, CancellationToken.None);
            _live[claim.Id] = new LiveJob(claim, run);
            registered.SetResult();
        }
    }

    private ref int Busy(JobLane lane) => ref lane == JobLane.Reserved ? ref _reservedBusy : ref _generalBusy;

    /// <summary>
    /// Claims the next runnable job among <paramref name="kinds"/>, or null. Serialised by a
    /// transaction-scoped advisory lock so two claims (two replicas) cannot both see a capped kind
    /// as free.
    /// </summary>
    internal async Task<ClaimedJob?> ClaimNextAsync(IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
        var rows = await db.Database
            .SqlQuery<ClaimRow>(ClaimNextSql(DateTime.UtcNow, kinds, options))
            .ToListAsync(ct);
        await tx.CommitAsync(ct);

        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        return new ClaimedJob(row.Id, row.AttemptCount, Enum.Parse<JobKind>(row.Kind));
    }

    /// <summary>
    /// The claim, as one statement. Priority then age, among <paramref name="kinds"/> (the kinds this
    /// process has a handler for, minus paused ones, minus non-interactive ones when only a reserved
    /// slot is free), with four rules on top:
    /// <list type="bullet">
    /// <item>A row whose attempts are spent is never claimed (the sweep fails it instead), so
    /// <c>AttemptCount</c> cannot pass the cap.</item>
    /// <item>A capped kind is not claimed while its cap's worth of that kind is Running.</item>
    /// <item>A <c>BuildFeed</c> is not claimable while the same account has a <c>DeltaSync</c> pending
    /// or running: the refresh control enqueues both, and a build that starts beside the sync scores
    /// the stale library and is then redone by the sync's own reconcile build.</item>
    /// <item>A kind this build does not know (written by a newer image) is never claimed: its row would
    /// read back as the enum's fallback and run the wrong handler.</item>
    /// </list>
    /// The claim also clears the previous attempt's progress: a retry that inherited "100%" from the
    /// attempt that failed reported a finished job as live work for the whole retry (H-1).
    /// </summary>
    internal static FormattableString ClaimNextSql(DateTime now, IReadOnlyCollection<string> kinds, JobRunnerOptions options)
    {
        var kindArray = kinds.ToArray();
        var cappedKinds = options.KindCaps.Keys.Select(k => k.ToString()).ToArray();
        var caps = options.KindCaps.Values.ToArray();
        var maxAttempts = options.MaxAttempts;

        return $"""
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
               AND j."AttemptCount" < {maxAttempts}
               AND j."Kind" = ANY({kindArray})
               AND NOT EXISTS (
                   SELECT 1 FROM unnest({cappedKinds}::text[], {caps}::integer[]) AS c("Kind", "Cap")
                   WHERE c."Kind" = j."Kind"
                     AND (SELECT count(*) FROM "SyncJobs" r
                          WHERE r."Kind" = c."Kind" AND r."Status" = 'Running') >= c."Cap")
               AND NOT (j."Kind" = 'BuildFeed' AND EXISTS (
                   SELECT 1 FROM "SyncJobs" d
                   WHERE d."AccountId" = j."AccountId"
                     AND d."Kind" = 'DeltaSync'
                     AND d."Status" IN ('Pending', 'Running')))
             ORDER BY j."Priority", j."EnqueuedAt"
             LIMIT 1
             FOR UPDATE SKIP LOCKED)
         RETURNING "Id", "AttemptCount", "Kind"
         """;
    }

    /// <summary>
    /// The LLM kinds not to claim right now. A provider that is Down, or an open breaker on the
    /// provider as a whole, pauses every LLM kind; an open breaker on one purpose pauses the kinds
    /// that spend under it.
    /// </summary>
    private HashSet<string> PausedKinds()
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetService<IProviderHealthReader>();
        var paused = PausedKinds(reader?.Current(), options);

        var signature = string.Join(",", paused.Order(StringComparer.Ordinal));
        if (!string.Equals(signature, _lastPaused, StringComparison.Ordinal))
        {
            if (paused.Count > 0)
            {
                logger.LogWarning("JobRunner paused LLM job kind(s) while the provider breaker is open: {Kinds}.", signature);
            }
            else
            {
                logger.LogInformation("JobRunner resumed LLM job kinds: the provider breaker closed.");
            }

            _lastPaused = signature;
        }

        return paused;
    }

    internal static HashSet<string> PausedKinds(ProviderHealthSnapshot? health, JobRunnerOptions options)
    {
        var paused = new HashSet<string>(StringComparer.Ordinal);
        if (health is null)
        {
            return paused;
        }

        foreach (var (kind, purposes) in options.LlmKinds)
        {
            var off = health.State == ProviderHealthState.Down
                || health.Breakers.Any(b =>
                    b.State == ProviderBreakerState.Open
                    && b.Provider == ApiProvider.OpenRouter
                    && (b.Purpose is null || purposes.Contains(b.Purpose, StringComparer.Ordinal)));
            if (off)
            {
                paused.Add(kind.ToString());
            }
        }

        return paused;
    }

    // ── Executing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one claimed execution to a terminal write. Internal so the tests can drive the real
    /// execution path on a job they claimed themselves.
    /// </summary>
    internal async Task<JobRunOutcome> RunAsync(ClaimedJob claim, CancellationToken stoppingToken)
    {
        var policy = options.PolicyFor(claim.Kind);
        using var lease = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(policy.Deadline);
        using var handlerToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token, deadline.Token);
        using var beatStop = new CancellationTokenSource();
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = claim.Id,
            ["JobKind"] = claim.Kind.ToString(),
            ["JobAttempt"] = claim.Attempt,
        });

        var heartbeat = HeartbeatLoopAsync(claim, lease, beatStop.Token);
        try
        {
            return await ExecuteClaimAsync(claim, policy, handlerToken.Token, stoppingToken, lease.Token, deadline.Token);
        }
        finally
        {
            await beatStop.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<JobRunOutcome> ExecuteClaimAsync(
        ClaimedJob claim,
        JobKindPolicy policy,
        CancellationToken handlerToken,
        CancellationToken stoppingToken,
        CancellationToken leaseToken,
        CancellationToken deadlineToken)
    {
        Guid? accountId = null;
        Exception? failure = null;

        using (var scope = scopeFactory.CreateScope())
        {
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                var job = await db.SyncJobs.FirstAsync(j => j.Id == claim.Id, handlerToken);
                accountId = job.AccountId;
                if (job.AccountId is { } owner)
                {
                    scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(owner);
                }

                var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind)
                    ?? throw new NonRetryableJobException($"No handler registered for job kind {job.Kind}.");

                logger.LogInformation("Job {JobId} ({Kind}, account {AccountId}, attempt {Attempt}) starting.",
                    claim.Id, claim.Kind, job.AccountId, claim.Attempt);

                await handler.ExecuteAsync(job, handlerToken);

                if (!handlerToken.IsCancellationRequested)
                {
                    // Whatever the handler left unsaved goes in before the job is marked done — the
                    // runner's own completion used to carry it in the same SaveChanges.
                    await db.SaveChangesAsync(CancellationToken.None);
                    return await CompleteAsync(claim);
                }

                // The handler noticed the cancellation and returned instead of throwing: it was
                // interrupted all the same, and the reason decides what happens below.
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (leaseToken.IsCancellationRequested)
        {
            // Another runner reclaimed this row while it looked dead; that execution owns it now.
            return JobRunOutcome.LeaseLost;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return await RequeueAsync(claim, JobRunOutcome.Interrupted, "host stopping");
        }

        if (deadlineToken.IsCancellationRequested)
        {
            return await DeadlineAsync(claim, accountId, policy);
        }

        return await FailAsync(claim, accountId, failure ?? new InvalidOperationException("The handler stopped without finishing."));
    }

    private async Task<JobRunOutcome> CompleteAsync(ClaimedJob claim)
    {
        var now = DateTime.UtcNow;
        var rows = await WithContextAsync(db => Fenced(db, claim).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Succeeded)
            .SetProperty(j => j.CompletedAt, (DateTime?)now)
            .SetProperty(j => j.HeartbeatAt, (DateTime?)now)
            .SetProperty(j => j.ProgressPct, (decimal?)100)
            .SetProperty(j => j.Error, (string?)null)));

        if (rows == 0)
        {
            return LeaseLost(claim);
        }

        logger.LogInformation("Job {JobId} ({Kind}) succeeded.", claim.Id, claim.Kind);
        return JobRunOutcome.Succeeded;
    }

    private async Task<JobRunOutcome> FailAsync(ClaimedJob claim, Guid? accountId, Exception failure)
    {
        var permanent = failure is NonRetryableJobException || claim.Attempt >= options.MaxAttempts;
        var error = Truncate(failure.Message);
        var now = DateTime.UtcNow;

        var rows = permanent
            ? await WithContextAsync(db => Fenced(db, claim).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.CompletedAt, (DateTime?)now)
                .SetProperty(j => j.HeartbeatAt, (DateTime?)now)
                .SetProperty(j => j.Error, error)))
            : await WithContextAsync(db => Fenced(db, claim).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.Error, error)
                // Waiting for a retry is not progress: the status API must never show the failed
                // attempt's percentage as live work while the job sits in the queue.
                .SetProperty(j => j.ProgressPct, (decimal?)null)
                .SetProperty(j => j.ProgressMessage, (string?)null)));

        if (rows == 0)
        {
            return LeaseLost(claim);
        }

        logger.LogError(failure, "Job {JobId} ({Kind}) failed on attempt {Attempt} of {Max}; {Next}.",
            claim.Id, claim.Kind, claim.Attempt, options.MaxAttempts, permanent ? "failed for good" : "queued to retry");

        if (permanent && accountId is { } owner)
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IPipelineEventHub>().Publish(
                owner, PipelineEventTypes.Failed, new Dictionary<string, object?>
                {
                    ["error"] = $"{claim.Kind} failed after {claim.Attempt} attempts.",
                });
        }

        return permanent ? JobRunOutcome.Failed : JobRunOutcome.Retrying;
    }

    private async Task<JobRunOutcome> DeadlineAsync(ClaimedJob claim, Guid? accountId, JobKindPolicy policy)
    {
        var minutes = policy.Deadline.TotalMinutes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        if (policy.OnDeadline == DeadlineAction.Yield)
        {
            var now = DateTime.UtcNow;
            var maxYields = options.MaxDeadlineYields;
            var rows = await WithContextAsync(db => Fenced(db, claim)
                .Where(j => j.DeadlineYields < maxYields)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Pending)
                    .SetProperty(j => j.AttemptCount, j => j.AttemptCount - 1)
                    .SetProperty(j => j.DeadlineYields, j => j.DeadlineYields + 1)
                    // To the back of its priority band, so the slice it held goes to someone else.
                    .SetProperty(j => j.EnqueuedAt, now)
                    .SetProperty(j => j.ProgressPct, (decimal?)null)
                    .SetProperty(j => j.ProgressMessage, (string?)null)));

            if (rows == 1)
            {
                logger.LogInformation(
                    "Job {JobId} ({Kind}) reached its {Minutes}-minute deadline; requeued to resume from its checkpoint.",
                    claim.Id, claim.Kind, minutes);
                return JobRunOutcome.Yielded;
            }

            // Either the slices are spent (fail below, fenced) or the lease is gone (the fenced
            // failure finds no row and reports it).
            return await FailAsync(claim, accountId, new TimeoutException(
                $"Timed out: still running after {options.MaxDeadlineYields} {minutes}-minute slices."));
        }

        return await FailAsync(claim, accountId, new TimeoutException(
            $"Timed out: ran past its {minutes}-minute deadline."));
    }

    /// <summary>Back to Pending with the interrupted attempt refunded — it did not fail, it was stopped.</summary>
    private async Task<JobRunOutcome> RequeueAsync(ClaimedJob claim, JobRunOutcome outcome, string why)
    {
        var rows = await WithContextAsync(db => Fenced(db, claim).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Pending)
            .SetProperty(j => j.AttemptCount, j => j.AttemptCount - 1)
            .SetProperty(j => j.ProgressPct, (decimal?)null)
            .SetProperty(j => j.ProgressMessage, (string?)null)));

        if (rows == 0)
        {
            return LeaseLost(claim);
        }

        logger.LogInformation("Job {JobId} ({Kind}) requeued: {Why}.", claim.Id, claim.Kind, why);
        return outcome;
    }

    private JobRunOutcome LeaseLost(ClaimedJob claim)
    {
        logger.LogWarning(
            "Job {JobId} ({Kind}) attempt {Attempt} no longer owns its row (reclaimed or already settled); nothing written.",
            claim.Id, claim.Kind, claim.Attempt);
        return JobRunOutcome.LeaseLost;
    }

    /// <summary>The row as this execution claimed it — the fence every write after the claim goes through.</summary>
    private static IQueryable<SyncJob> Fenced(ReelDbContext db, ClaimedJob claim) =>
        db.SyncJobs.Where(j => j.Id == claim.Id && j.AttemptCount == claim.Attempt && j.Status == JobStatus.Running);

    /// <summary>
    /// Runs a write on a fresh, unpinned context. Never the handler's token: a terminal write must
    /// land even when the handler was cancelled.
    /// </summary>
    private async Task<int> WithContextAsync(Func<ReelDbContext, Task<int>> write)
    {
        using var scope = scopeFactory.CreateScope();
        return await write(scope.ServiceProvider.GetRequiredService<ReelDbContext>());
    }

    private async Task HeartbeatLoopAsync(ClaimedJob claim, CancellationTokenSource lease, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stop))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var rows = await WithContextAsync(db => Fenced(db, claim)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.HeartbeatAt, (DateTime?)now), stop));
                if (rows == 0)
                {
                    logger.LogWarning(
                        "Job {JobId} ({Kind}) attempt {Attempt} lost its lease — reclaimed by another runner; stopping this execution.",
                        claim.Id, claim.Kind, claim.Attempt);
                    await lease.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return; // normal end of the beat
            }
            catch (Exception ex)
            {
                // A transient DB hiccup must not kill the beat — a dead beat gets the job
                // reclaimed mid-run (and, without the fence, executed twice).
                logger.LogWarning(ex, "Heartbeat tick failed for job {JobId}; retrying next tick.", claim.Id);
            }
        }
    }

    // ── Sweeping ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Crash recovery and cap enforcement, every poll. A stale Running row this process is not
    /// running is a dead execution: past the attempt cap it is Failed (reviving it is what let a
    /// stuck job respawn forever), otherwise it goes back to Pending. A Pending row whose attempts
    /// are already spent (rows from before the claim cap) is Failed rather than stranded, because the
    /// claim will never take it and a stranded DeltaSync would hold every build of its account back.
    /// So is a queued row of a known kind this process has no handler for.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var now = DateTime.UtcNow;
        var cutoff = now - options.StaleAfter;
        var max = options.MaxAttempts;
        var local = _live.Keys.ToArray();
        var stalled = StalledMessage(max);
        var exhausted = $"Gave up: all {max} attempts were spent before this runner could retry it.";

        var failed = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs"
             SET "Status" = 'Failed', "CompletedAt" = {now}, "Error" = COALESCE("Error", {stalled})
             WHERE "Status" = 'Running' AND "HeartbeatAt" < {cutoff} AND "AttemptCount" >= {max}
               AND NOT ("Id" = ANY({local}))
             """, ct);

        var reclaimed = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs" SET "Status" = 'Pending', "ProgressPct" = NULL, "ProgressMessage" = NULL
             WHERE "Status" = 'Running' AND "HeartbeatAt" < {cutoff} AND "AttemptCount" < {max}
               AND NOT ("Id" = ANY({local}))
             """, ct);

        var stranded = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs"
             SET "Status" = 'Failed', "CompletedAt" = {now}, "Error" = COALESCE("Error", {exhausted})
             WHERE "Status" = 'Pending' AND "AttemptCount" >= {max}
             """, ct);

        // A kind this build knows but has no handler for would otherwise wait forever, because the
        // claim only takes kinds it can run. (A kind it does not know at all belongs to a newer
        // image and is left alone for it.)
        var unhandled = Enum.GetNames<JobKind>().Except(_handledKinds, StringComparer.Ordinal).ToArray();
        var noHandler = unhandled.Length == 0
            ? 0
            : await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "SyncJobs"
                 SET "Status" = 'Failed', "CompletedAt" = {now}, "Error" = 'No handler is registered for this job kind.'
                 WHERE "Status" = 'Pending' AND "Kind" = ANY({unhandled})
                 """, ct);

        if (noHandler > 0)
        {
            logger.LogError("Failed {Count} queued job(s) of a kind with no registered handler ({Kinds}).", noHandler, string.Join(",", unhandled));
        }

        if (failed > 0)
        {
            logger.LogWarning("Failed {Count} stalled job(s) past the {Max}-attempt cap.", failed, max);
        }

        if (reclaimed > 0)
        {
            logger.LogWarning("Reclaimed {Count} stale Running job(s); they resume from their cursors.", reclaimed);
        }

        if (stranded > 0)
        {
            logger.LogWarning("Failed {Count} queued job(s) whose {Max} attempts were already spent.", stranded, max);
        }
    }

    internal static string StalledMessage(int maxAttempts) =>
        $"Stalled with no heartbeat after {maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} attempts.";

    /// <summary>
    /// One-off, idempotent repair of rows written before this runner: terminal rows without a
    /// <c>CompletedAt</c> get their last sign of life (the instant the status API already used for
    /// them), and the stall message that persisted a literal "@p1" gets its number.
    /// </summary>
    private async Task RepairHistoryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var broken = "Stalled with no heartbeat after @p1 attempts.";
            var fixedMessage = StalledMessage(options.MaxAttempts);

            var completed = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "SyncJobs" SET "CompletedAt" = COALESCE("HeartbeatAt", "StartedAt", "EnqueuedAt")
                 WHERE "Status" IN ('Succeeded', 'Failed', 'Cancelled') AND "CompletedAt" IS NULL
                 """, ct);
            var messages = await db.Database.ExecuteSqlAsync(
                $"""UPDATE "SyncJobs" SET "Error" = {fixedMessage} WHERE "Error" = {broken}""", ct);

            if (completed + messages > 0)
            {
                logger.LogInformation(
                    "JobRunner repaired history: {Completed} terminal job(s) given a CompletedAt, {Messages} stall message(s) given their attempt count.",
                    completed, messages);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "JobRunner history repair failed; the runner starts anyway.");
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shutdown: the stopping token has already cancelled every handler, and each execution requeues
    /// itself. Wait for them up to the grace period, then requeue whatever is still running — the
    /// fence makes a late write from one of those harmless.
    /// </summary>
    private async Task DrainAsync()
    {
        var running = _live.Values.ToList();
        if (running.Count == 0)
        {
            return;
        }

        await Task.WhenAny(Task.WhenAll(running.Select(l => l.Run)), Task.Delay(options.ShutdownGrace));

        foreach (var live in _live.Values)
        {
            try
            {
                await RequeueAsync(live.Claim, JobRunOutcome.Interrupted, "host stopped before the handler did");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not requeue job {JobId} at shutdown; the stale-job sweep will.", live.Claim.Id);
            }
        }
    }

    private IReadOnlyList<string> ResolveHandledKinds()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            return scope.ServiceProvider.GetServices<IJobHandler>()
                .Select(h => h.Kind.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "JobRunner could not resolve its handlers; claiming every known kind.");
            return Enum.GetNames<JobKind>();
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];

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
}
