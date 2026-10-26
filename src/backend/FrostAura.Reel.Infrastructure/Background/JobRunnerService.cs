using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
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

    /// <summary>
    /// The host is stopping and the handler stopped when asked: back in the queue, attempt refunded
    /// (up to <see cref="JobRunnerOptions.MaxInterruptions"/> times; past that it is a failed attempt).
    /// </summary>
    Interrupted,

    /// <summary>
    /// Another runner owns the row now (this execution's heartbeat went stale and it was
    /// reclaimed): nothing was written — neither by the runner nor by the handler's own saves, which
    /// the row's concurrency tokens refuse — and the execution stopped at the first heartbeat that
    /// saw it (or was abandoned, if its handler ignored the cancellation).
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
/// <para><b>Handler writes are fenced too.</b> <c>AttemptCount</c> and <c>Status</c> are concurrency
/// tokens on <see cref="SyncJob"/>, and the handler gets its row tracked from a fenced read, so its own
/// cursor and progress saves match no row once the execution has been reclaimed. A refund
/// (<c>AttemptCount - 1</c>) only ever happens after the execution holding that lease has
/// returned, so a lease number is never handed out twice while its holder is alive.</para>
///
/// <para><b>Honest terminal states (J-6, K-12).</b> A handler that returns has finished — and is
/// completed, whatever its token says — unless its kind is declared to return early at a
/// checkpoint when cancelled; a handler that throws has failed unless the host is stopping, the
/// deadline fired or the lease was lost. Deadlines are hard: a handler that ignores its token past
/// <see cref="JobRunnerOptions.OverrunGrace"/> is abandoned with its attempt written off.
/// <c>CompletedAt</c> is stamped on every terminal state; every message is built in C# and passed as
/// a parameter; and a stopping host puts its running jobs back to Pending with the interrupted
/// attempt refunded, a bounded number of times.</para>
/// </summary>
public sealed class JobRunnerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<JobRunnerService> logger,
    JobRunnerOptions options,
    ReelMetrics? metrics = null) : BackgroundService
{
    /// <summary>Transaction-scoped advisory lock that serialises claims across replicas, so the per-kind caps are exact.</summary>
    private const long ClaimLockKey = 0x5245_454C_4A4F_4253; // "REELJOBS"

    private const int MaxErrorLength = 4000;

    /// <summary>Time past the shutdown grace for abandoned executions to write their outcome.</summary>
    private static readonly TimeSpan DrainMargin = TimeSpan.FromSeconds(5);

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
    /// <param name="kinds">The kinds this caller may run.</param>
    /// <param name="onlyId">
    /// Restricts the claim to one row. For tests on a shared database, which must run the real claim
    /// (cap, progress reset, attempt cap, claim lock) on their own job without taking another test's.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    internal async Task<ClaimedJob?> ClaimNextAsync(IReadOnlyCollection<string> kinds, CancellationToken ct, Guid? onlyId = null)
    {
        using var scope = CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
        var rows = await db.Database
            .SqlQuery<ClaimRow>(ClaimNextSql(DateTime.UtcNow, kinds, options, onlyId))
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
    internal static FormattableString ClaimNextSql(DateTime now, IReadOnlyCollection<string> kinds, JobRunnerOptions options, Guid? onlyId = null)
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
               AND ({onlyId}::uuid IS NULL OR j."Id" = {onlyId}::uuid)
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
             ORDER BY j."Priority", COALESCE(j."RequeuedAt", j."EnqueuedAt")
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
        var health = reader?.Current();

        // Half-open is judged on the reader's own clock: its RetryAt and CheckedAt come from it.
        var paused = PausedKinds(health, options, health?.CheckedAt ?? DateTime.UtcNow);

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

    /// <remarks>
    /// A breaker whose <c>RetryAt</c> has passed is half-open, not open: the provider-health service
    /// admits exactly one probe call, and background work is often the only caller there is. Pausing
    /// on it kept every LLM kind off the queue until something else happened to probe — for the
    /// cross-reference and lens queues, possibly until the process restarted. So a half-open breaker
    /// lets its kinds be claimed, and the first call of the first job is the probe. A "Down" reading
    /// that names no provider-wide breaker (a reader that cannot say when to retry) still pauses.
    /// Nothing here touches a job row: a paused kind is simply not claimed, so no attempt is spent.
    /// </remarks>
    internal static HashSet<string> PausedKinds(ProviderHealthSnapshot? health, JobRunnerOptions options, DateTime now)
    {
        var paused = new HashSet<string>(StringComparer.Ordinal);
        if (health is null)
        {
            return paused;
        }

        var openRouter = health.Breakers.Where(b => b.Provider == ApiProvider.OpenRouter).ToList();
        var open = openRouter
            .Where(b => b.State == ProviderBreakerState.Open && (b.RetryAt is null || b.RetryAt > now))
            .ToList();
        var downWithoutABreaker = health.State == ProviderHealthState.Down && !openRouter.Any(b => b.Purpose is null);

        foreach (var (kind, purposes) in options.LlmKinds)
        {
            var off = downWithoutABreaker
                || open.Any(b => b.Purpose is null || purposes.Contains(b.Purpose, StringComparer.Ordinal));
            if (off)
            {
                paused.Add(kind.ToString());
            }
        }

        return paused;
    }

    // ── Executing ────────────────────────────────────────────────────────────────────────────

    /// <summary>What the handler half of an execution reports to the half that settles it.</summary>
    private sealed class Execution
    {
        public Guid? AccountId { get; set; }

        /// <summary>The handler returned as finished and its unsaved changes were flushed through the fence.</summary>
        public bool Finished { get; set; }

        public Exception? Failure { get; set; }

        /// <summary>The failure is deterministic (typed by the handler): fail now, keep the remaining attempts unspent.</summary>
        public bool NonRetryable { get; set; }
    }

    /// <summary>
    /// Runs one claimed execution to a terminal write. Internal so the tests can drive the real
    /// execution path on a job they claimed themselves.
    ///
    /// <para>The handler runs beside its heartbeat, bounded by a hard limit: its kind's deadline
    /// plus <see cref="JobRunnerOptions.OverrunGrace"/> (or the grace after a lost lease or a
    /// stopping host). The beat stops before ANY terminal write, so a tick can never find the row
    /// already settled by this same execution and report a lease it did not lose.</para>
    /// </summary>
    internal async Task<JobRunOutcome> RunAsync(ClaimedJob claim, CancellationToken stoppingToken)
    {
        var policy = options.PolicyFor(claim.Kind);
        var clock = Stopwatch.StartNew();
        var hardLimit = policy.Deadline + options.OverrunGrace;
        using var lease = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(policy.Deadline);
        using var handlerToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token, deadline.Token);
        using var beatStop = new CancellationTokenSource();
        using var abandon = new CancellationTokenSource(hardLimit);
        using var abandonOnLeaseLoss = lease.Token.Register(() => ShortenTo(abandon, options.OverrunGrace, hardLimit - clock.Elapsed));
        using var abandonOnStop = stoppingToken.Register(() => ShortenTo(abandon, options.ShutdownGrace, hardLimit - clock.Elapsed));
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = claim.Id,
            ["JobKind"] = claim.Kind.ToString(),
            ["JobAttempt"] = claim.Attempt,
        });

        var state = new Execution();
        var heartbeat = HeartbeatLoopAsync(claim, lease, beatStop.Token);
        var execution = ExecuteHandlerAsync(claim, policy, state, handlerToken.Token, lease.Token);
        bool returned;
        try
        {
            returned = await Task.WhenAny(execution, WhenCancelled(abandon.Token)) == execution;
        }
        finally
        {
            await beatStop.CancelAsync();
            await heartbeat;
        }

        return returned
            ? await SettleAsync(claim, policy, state, stoppingToken, lease.Token, deadline.Token)
            : await AbandonAsync(claim, policy, state, execution, stoppingToken, lease.Token);
    }

    /// <summary>
    /// The handler half, in its own DI scope, which the task owns: an abandoned execution keeps its
    /// scope until it finally returns, and everything it then tries to save is fenced. Never throws.
    /// </summary>
    private async Task ExecuteHandlerAsync(
        ClaimedJob claim, JobKindPolicy policy, Execution state, CancellationToken handlerToken, CancellationToken leaseToken)
    {
        // Declared outside the try: the failure path reads what the handler did to its tracked row,
        // so the scope is disposed only after the catch has run.
        IServiceScope? scope = null;
        ReelDbContext? db = null;
        SyncJob? job = null;
        try
        {
            scope = scopeFactory.CreateScope();
            db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var tenancy = scope.ServiceProvider.GetRequiredService<IAccountContext>();

            // Read through the fence: a row reclaimed before the handler even started is not this
            // execution's to run. Tracked, so the row's AttemptCount and Status concurrency tokens
            // fence every save the handler makes of it. Read in system scope: whose job it is is
            // what this read finds out.
            var reading = tenancy.EnterSystemScope();
            job = await Fenced(db, claim).FirstOrDefaultAsync(handlerToken)
                ?? throw new InvalidOperationException("The job no longer belongs to this execution.");
            state.AccountId = job.AccountId;
            if (job.AccountId is { } owner)
            {
                // An account job runs pinned to its account and nothing else (J-11).
                reading.Dispose();
                tenancy.SetAccount(owner);
            }

            // A global job (no account) keeps the system scope for the whole run: reading across
            // accounts is what a global job is. It ends with the execution's DI scope.

            var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind)
                ?? throw new NonRetryableJobException($"No handler registered for job kind {job.Kind}.");

            logger.LogInformation("Job {JobId} ({Kind}, account {AccountId}, attempt {Attempt}) starting.",
                claim.Id, claim.Kind, job.AccountId, claim.Attempt);

            await handler.ExecuteAsync(job, handlerToken);

            if (leaseToken.IsCancellationRequested
                || (handlerToken.IsCancellationRequested && policy.StopsEarlyOnCancellation))
            {
                // Not this execution's row any more; or a handler that returns at a checkpoint when
                // its token fires did exactly that, and stopped early rather than finished.
                return;
            }

            // A normal return is a finished run, whatever the token says now. Whatever the handler
            // left unsaved goes in before the job is marked done, through the concurrency tokens —
            // except the runner's own columns, which a handler never moves.
            KeepRunnerColumns(db, job, claim);
            await db.SaveChangesAsync(CancellationToken.None);
            state.Finished = true;
        }
        catch (Exception ex)
        {
            state.Failure = ex;
            state.NonRetryable = IsNonRetryable(ex) || SpentItsOwnAttempts(db, job);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <summary>
    /// Whether a failure is typed as deterministic: a <see cref="NonRetryableJobException"/> anywhere
    /// in the chain, so a handler (or a helper it calls) may wrap it, or let it surface inside an
    /// <see cref="AggregateException"/>, without the runner retrying the same answer.
    /// </summary>
    internal static bool IsNonRetryable(Exception failure)
    {
        var pending = new Stack<Exception>();
        pending.Push(failure);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 64)
        {
            var current = pending.Pop();
            if (current is NonRetryableJobException)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }

    /// <summary>
    /// The older way a handler asked not to be retried: raising its own tracked row's
    /// <c>AttemptCount</c> to the cap and throwing. That write was never saved (the handler threw, so
    /// nothing flushed it), so the runner retried the deterministic failure anyway (WS-1 CR-5).
    /// The runner owns <c>AttemptCount</c>; it reads the request off the tracked entry instead and
    /// honours it as a non-retryable failure, with no attempt spent beyond the one that ran.
    /// </summary>
    private bool SpentItsOwnAttempts(ReelDbContext? db, SyncJob? job)
    {
        if (db is null || job is null)
        {
            return false;
        }

        try
        {
            var attempts = db.Entry(job).Property(j => j.AttemptCount);
            if (!attempts.IsModified || attempts.CurrentValue <= attempts.OriginalValue)
            {
                return false;
            }

            logger.LogWarning(
                "Job {JobId} ({Kind}) raised its own AttemptCount to {Attempts}; treated as a non-retryable failure. "
                + "Handlers should throw NonRetryableJobException instead.",
                job.Id, job.Kind, attempts.CurrentValue);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The entry is no longer tracked (the handler detached or cleared it): nothing to read.
            return false;
        }
    }

    /// <summary>
    /// <c>AttemptCount</c> and <c>Status</c> belong to the runner: a handler's change to either is
    /// undone before the finishing flush. Saving them would move the row off this execution's fence,
    /// so the completion that follows would find no row and report a lease it never lost.
    /// </summary>
    private void KeepRunnerColumns(ReelDbContext db, SyncJob job, ClaimedJob claim)
    {
        var entry = db.Entry(job);
        var attempts = entry.Property(j => j.AttemptCount);
        var status = entry.Property(j => j.Status);
        if (!attempts.IsModified && !status.IsModified)
        {
            return;
        }

        logger.LogWarning(
            "Job {JobId} ({Kind}) changed runner-owned columns (AttemptCount {Attempts}, Status {Status}); kept the runner's values.",
            claim.Id, claim.Kind, attempts.CurrentValue, status.CurrentValue);
        attempts.CurrentValue = attempts.OriginalValue;
        attempts.IsModified = false;
        status.CurrentValue = status.OriginalValue;
        status.IsModified = false;
    }

    /// <summary>Decides the outcome of an execution whose handler returned (or threw) inside its hard limit.</summary>
    private async Task<JobRunOutcome> SettleAsync(
        ClaimedJob claim,
        JobKindPolicy policy,
        Execution state,
        CancellationToken stoppingToken,
        CancellationToken leaseToken,
        CancellationToken deadlineToken)
    {
        if (state.Finished)
        {
            return await CompleteAsync(claim);
        }

        if (leaseToken.IsCancellationRequested)
        {
            // Another runner reclaimed this row while it looked dead; that execution owns it now.
            return JobRunOutcome.LeaseLost;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return await InterruptAsync(claim, state.AccountId);
        }

        if (deadlineToken.IsCancellationRequested)
        {
            return await DeadlineAsync(claim, state.AccountId, policy);
        }

        // Includes a stale save the concurrency tokens refused: the fenced write then finds no row
        // and reports the lease as lost.
        return await FailAsync(
            claim,
            state.AccountId,
            state.Failure ?? new InvalidOperationException("The handler stopped without finishing."),
            state.NonRetryable);
    }

    /// <summary>
    /// The handler ignored its cancelled token past the hard limit. It is left running, fenced off:
    /// the attempt is written off (never refunded — a refund would hand the next claim the same lease
    /// number the deaf handler still holds), and the slot goes back to the runner.
    /// </summary>
    private async Task<JobRunOutcome> AbandonAsync(
        ClaimedJob claim,
        JobKindPolicy policy,
        Execution state,
        Task execution,
        CancellationToken stoppingToken,
        CancellationToken leaseToken)
    {
        _ = execution.ContinueWith(
            _ => logger.LogWarning(
                "Abandoned execution of job {JobId} ({Kind}) attempt {Attempt} finally returned; anything it saved was fenced.",
                claim.Id, claim.Kind, claim.Attempt),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        if (leaseToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Job {JobId} ({Kind}) attempt {Attempt} lost its lease and its handler ignored cancellation; abandoned.",
                claim.Id, claim.Kind, claim.Attempt);
            return JobRunOutcome.LeaseLost;
        }

        var reason = stoppingToken.IsCancellationRequested
            ? $"Timed out: the handler did not stop within {Seconds(options.ShutdownGrace)} s of the host stopping; abandoned."
            : $"Timed out: ran past its {Minutes(policy.Deadline)}-minute deadline and ignored cancellation for {Seconds(options.OverrunGrace)} s; abandoned.";
        logger.LogError("Job {JobId} ({Kind}) attempt {Attempt}: {Reason}", claim.Id, claim.Kind, claim.Attempt, reason);
        return await FailAsync(claim, state.AccountId, new TimeoutException(reason));
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
        metrics?.JobSucceeded(claim.Kind);
        return JobRunOutcome.Succeeded;
    }

    private async Task<JobRunOutcome> FailAsync(ClaimedJob claim, Guid? accountId, Exception failure, bool nonRetryable = false)
    {
        // A typed deterministic failure is dead-lettered on this attempt: Failed with CompletedAt,
        // AttemptCount left at the attempt that ran. Retrying would only buy the same answer again.
        nonRetryable = nonRetryable || IsNonRetryable(failure);
        var permanent = nonRetryable || claim.Attempt >= options.MaxAttempts;
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

        metrics?.JobFailed(claim.Kind, JobFailureReason.Classify(error), retrying: !permanent);
        logger.LogError(failure, "Job {JobId} ({Kind}) failed on attempt {Attempt} of {Max}; {Next}.",
            claim.Id, claim.Kind, claim.Attempt, options.MaxAttempts,
            nonRetryable ? "not retryable, failed for good" : permanent ? "failed for good" : "queued to retry");

        if (permanent && accountId is { } owner)
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IPipelineEventHub>().Publish(
                owner, PipelineEventTypes.Failed, new Dictionary<string, object?>
                {
                    ["error"] = nonRetryable && claim.Attempt < options.MaxAttempts
                        ? $"{claim.Kind} failed and will not be retried."
                        : $"{claim.Kind} failed after {claim.Attempt} attempts.",
                });
        }

        return permanent ? JobRunOutcome.Failed : JobRunOutcome.Retrying;
    }

    private async Task<JobRunOutcome> DeadlineAsync(ClaimedJob claim, Guid? accountId, JobKindPolicy policy)
    {
        var minutes = Minutes(policy.Deadline);

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
                    // EnqueuedAt keeps the first enqueue, so queue-wait figures stay honest.
                    .SetProperty(j => j.RequeuedAt, (DateTime?)now)
                    .SetProperty(j => j.ProgressPct, (decimal?)null)
                    .SetProperty(j => j.ProgressMessage, (string?)null)));

            if (rows == 1)
            {
                metrics?.JobYielded(claim.Kind);
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

    /// <summary>
    /// The host is stopping and the handler stopped when asked: back to Pending with the attempt
    /// refunded — it did not fail, it was stopped. Bounded by <see cref="JobRunnerOptions.MaxInterruptions"/>:
    /// past it the interruption is a failed attempt, so a job that keeps taking its host down
    /// still reaches its cap.
    /// </summary>
    private async Task<JobRunOutcome> InterruptAsync(ClaimedJob claim, Guid? accountId)
    {
        var max = options.MaxInterruptions;
        var rows = await WithContextAsync(db => Fenced(db, claim)
            .Where(j => j.Interruptions < max)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount - 1)
                .SetProperty(j => j.Interruptions, j => j.Interruptions + 1)
                .SetProperty(j => j.ProgressPct, (decimal?)null)
                .SetProperty(j => j.ProgressMessage, (string?)null)));

        if (rows == 1)
        {
            metrics?.JobInterrupted(claim.Kind);
            logger.LogInformation("Job {JobId} ({Kind}) requeued: host stopping.", claim.Id, claim.Kind);
            return JobRunOutcome.Interrupted;
        }

        // The refunds are spent (fail below, fenced) or the lease is gone (no row, reported).
        return await FailAsync(claim, accountId, new InvalidOperationException(
            $"Interrupted: the host stopped during this job {max.ToString(CultureInfo.InvariantCulture)} times before; this interruption counts as a failed attempt."));
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
    /// A DI scope for the runner's own bookkeeping — claims, fences, sweeps, terminal writes. Those
    /// read and write the queue across every account by design, so they say so (J-11); the scope's
    /// system-scope handle ends with the scope.
    /// </summary>
    private IServiceScope CreateSystemScope()
    {
        var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        return scope;
    }

    /// <summary>
    /// Runs a write on a fresh context in system scope. Never the handler's token: a terminal write must
    /// land even when the handler was cancelled.
    /// </summary>
    private async Task<int> WithContextAsync(Func<ReelDbContext, Task<int>> write)
    {
        using var scope = CreateSystemScope();
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

    /// <summary>Brings a pending cancellation forward to <paramref name="grace"/> from now, never later than it already is.</summary>
    private static void ShortenTo(CancellationTokenSource source, TimeSpan grace, TimeSpan remaining)
    {
        if (grace >= remaining)
        {
            return;
        }

        try
        {
            source.CancelAfter(grace);
        }
        catch (ObjectDisposedException)
        {
            // The execution already ended.
        }
    }

    private static Task WhenCancelled(CancellationToken token)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), signal);
        return signal.Task;
    }

    private static string Minutes(TimeSpan span) => span.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

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
        using var scope = CreateSystemScope();
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
        $"Stalled with no heartbeat after {maxAttempts.ToString(CultureInfo.InvariantCulture)} attempts.";

    /// <summary>
    /// One-off, idempotent repair of rows written before this runner: terminal rows without a
    /// <c>CompletedAt</c> get their last sign of life (the instant the status API already used for
    /// them), and the stall message that persisted a literal "@p1" gets its number.
    /// </summary>
    private async Task RepairHistoryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = CreateSystemScope();
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
    /// Shutdown: the stopping token has already cancelled every handler, and each execution settles
    /// itself — requeued with its attempt refunded if the handler stopped, written off and abandoned
    /// if it did not within <see cref="JobRunnerOptions.ShutdownGrace"/>. This only waits for that,
    /// with a margin for the terminal writes; a row still Running after it (the database went away
    /// mid-write) is left for the stale-job sweep of the next process.
    /// </summary>
    private async Task DrainAsync()
    {
        var running = _live.Values.ToList();
        if (running.Count == 0)
        {
            return;
        }

        var all = Task.WhenAll(running.Select(l => l.Run));
        if (await Task.WhenAny(all, Task.Delay(options.ShutdownGrace + DrainMargin)) != all)
        {
            logger.LogWarning(
                "JobRunner stopped with {Count} execution(s) still settling; the stale-job sweep will recover them.",
                _live.Count);
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
