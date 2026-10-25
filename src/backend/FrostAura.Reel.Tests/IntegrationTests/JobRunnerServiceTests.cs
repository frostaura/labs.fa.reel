using System.Diagnostics;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The real runner loop on its own database (J-6, NEW-2, J-5, K-12): claim cap, fencing, honest
/// terminal states, cancellation, shutdown, lanes, caps, deadlines and the provider breaker. Every
/// test starts from an empty queue, and the collection runs serially.
/// </summary>
[Collection("job-runner")]
public sealed class JobRunnerServiceTests(JobRunnerDatabaseFixture fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // ── Cancellation, attempts and terminal states ─────────────────────────────────────────────

    [Fact]
    public async Task A_handler_throwing_TaskCanceledException_is_settled_within_two_seconds_and_fails_after_its_attempts()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = new ScriptedHandler(JobKind.SeedCatalog, (_, _, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            // The first throw is a failed attempt, written at once — not a Running row left for the
            // 12-minute stall reclaim.
            var firstSettled = await WaitForAsync(id, j => j.Error != null && j.Status != JobStatus.Running, TimeSpan.FromSeconds(2));
            Assert.Contains("canceled", firstSettled.Error!, StringComparison.OrdinalIgnoreCase);

            var failed = await WaitForAsync(id, j => j.Status == JobStatus.Failed, Patience);
            Assert.Equal(JobRunnerLimits.MaxAttempts, failed.AttemptCount);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal(JobFailureReason.TimedOut, JobFailureReason.Classify(failed.Error));
            Assert.Equal(JobRunnerLimits.MaxAttempts, handler.Executions);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_row_whose_attempts_are_spent_is_never_claimed_and_the_sweep_fails_it_with_a_completion_time()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Succeeds(JobKind.SeedCatalog);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        // Rows like the 22 the assessment found on live: queued again with more attempts than the cap.
        var atCap = await AddJobAsync(JobKind.SeedCatalog, attempts: 3);
        var pastCap = await AddJobAsync(JobKind.SeedCatalog, attempts: 5);

        Assert.Null(await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None));
        await harness.Runner.SweepAsync(CancellationToken.None);

        foreach (var id in new[] { atCap, pastCap })
        {
            var job = await ReadAsync(id);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.NotNull(job.CompletedAt);
            Assert.DoesNotContain("@p", job.Error);
        }

        Assert.Equal(0, handler.Executions);
    }

    [Fact]
    public async Task A_stalled_row_past_the_cap_is_failed_with_its_attempt_count_in_the_message_not_a_placeholder()
    {
        if (!await ResetAsync())
        {
            return;
        }

        await using var harness = Harness(JobRunnerHarness.FastOptions(), ScriptedHandler.Succeeds(JobKind.SeedCatalog));
        var stale = DateTime.UtcNow - JobRunnerLimits.StaleAfter - TimeSpan.FromMinutes(1);
        var dead = await AddJobAsync(JobKind.SeedCatalog, attempts: 3, status: JobStatus.Running, heartbeat: stale);
        var revivable = await AddJobAsync(JobKind.HydrateCatalog, attempts: 1, status: JobStatus.Running, heartbeat: stale, progress: 100);

        await harness.Runner.SweepAsync(CancellationToken.None);

        var failed = await ReadAsync(dead);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.NotNull(failed.CompletedAt);
        Assert.Equal("Stalled with no heartbeat after 3 attempts.", failed.Error);
        Assert.Equal(JobFailureReason.Stalled, JobFailureReason.Classify(failed.Error));

        var revived = await ReadAsync(revivable);
        Assert.Equal(JobStatus.Pending, revived.Status);
        Assert.Equal(1, revived.AttemptCount);
        Assert.Null(revived.ProgressPct);
    }

    [Fact]
    public async Task A_non_retryable_failure_fails_on_its_first_attempt_and_reaches_last_failures_on_the_next_poll()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var handler = new ScriptedHandler(JobKind.CrossReference, (_, _, _) =>
            throw new NonRetryableJobException("Response status code does not indicate success: 402 (Payment Required)."));
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.CrossReference, account.Id);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var failed = await WaitForAsync(id, j => j.Status == JobStatus.Failed, Patience);
            Assert.Equal(1, failed.AttemptCount);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal(1, handler.Executions);
            Assert.Contains(harness.Events.Published, e => e.AccountId == account.Id && e.Type == PipelineEventTypes.Failed);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }

        // One status poll after the failure landed.
        await using var scoped = fixture.CreateContext();
        var status = await new SyncStatusQuery(scoped, new UnmonitoredProviderHealthReader())
            .GetAsync(account, retrainThreshold: 10, DateTime.UtcNow, CancellationToken.None);
        var failure = Assert.Single(status.LastFailures, f => f.Kind == JobKind.CrossReference);
        Assert.Equal(JobFailureReason.PaymentRequired, failure.ReasonCode);
        Assert.Equal(1, failure.Attempts);
    }

    // ── Fencing: one live execution per job ────────────────────────────────────────────────────

    [Fact]
    public async Task A_job_paused_past_the_stale_window_runs_exactly_once()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // A paused process: the handler is wedged for longer than the stale window and its beat
        // never fires. Its own runner must not reclaim it from under itself.
        var handler = new ScriptedHandler(JobKind.SeedCatalog, (_, _, ct) => Task.Delay(TimeSpan.FromSeconds(3), ct));
        var options = JobRunnerHarness.FastOptions() with
        {
            StaleAfter = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromHours(1),
        };
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var done = await WaitForAsync(id, j => j.Status == JobStatus.Succeeded, Patience);
            Assert.Equal(1, done.AttemptCount);
            Assert.NotNull(done.CompletedAt);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, handler.Executions);
        Assert.Equal(1, handler.MaxConcurrent);
    }

    [Fact]
    public async Task A_reclaimed_execution_cannot_write_its_result_over_the_new_one()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // Runner A (a paused replica: its beat never fires) holds attempt 1 until released. Runner B
        // sees the row stale, reclaims it and finishes attempt 2. A then finishes too, late.
        var release = new TaskCompletionSource();
        var handlerA = new ScriptedHandler(JobKind.SeedCatalog, (_, _, _) => release.Task);
        var handlerB = ScriptedHandler.Succeeds(JobKind.SeedCatalog);
        await using var a = Harness(JobRunnerHarness.FastOptions() with { HeartbeatInterval = TimeSpan.FromHours(1) }, handlerA);
        await using var b = Harness(JobRunnerHarness.FastOptions() with { StaleAfter = TimeSpan.FromSeconds(1) }, handlerB);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        var claimA = await a.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimA);
        var runA = a.Runner.RunAsync(claimA, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await b.Runner.SweepAsync(CancellationToken.None);
        var claimB = await b.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimB);
        Assert.Equal(2, claimB.Attempt);
        Assert.Equal(JobRunOutcome.Succeeded, await b.Runner.RunAsync(claimB, CancellationToken.None));
        var settled = await ReadAsync(id);

        release.SetResult();
        Assert.Equal(JobRunOutcome.LeaseLost, await runA);

        var final = await ReadAsync(id);
        Assert.Equal(JobStatus.Succeeded, final.Status);
        Assert.Equal(2, final.AttemptCount);
        Assert.Equal(settled.CompletedAt, final.CompletedAt);
    }

    [Fact]
    public async Task An_execution_that_lost_its_lease_is_cancelled_at_its_next_heartbeat_and_writes_nothing()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);
        var claim = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claim);

        var run = harness.Runner.RunAsync(claim, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(handler.Running == 1), Patience);

        // Another replica's sweep took the row back.
        await using (var db = fixture.CreateContext())
        {
            await db.SyncJobs.Where(j => j.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Pending));
        }

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(JobRunOutcome.LeaseLost, await run.WaitAsync(Patience));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"lease loss took {stopwatch.Elapsed} to stop the handler");

        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.Error);
    }

    // ── Shutdown ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_puts_a_running_job_back_to_pending_with_the_interrupted_attempt_refunded()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        await harness.Runner.StartAsync(CancellationToken.None);
        await WaitForAsync(id, j => j.Status == JobStatus.Running, Patience);
        await harness.Runner.StopAsync(CancellationToken.None);

        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.Error);
        Assert.Null(row.CompletedAt);

        // And the next process runs it as a first attempt.
        var next = ScriptedHandler.Succeeds(JobKind.SeedCatalog);
        await using var restarted = Harness(JobRunnerHarness.FastOptions(), next);
        await restarted.Runner.StartAsync(CancellationToken.None);
        try
        {
            var done = await WaitForAsync(id, j => j.Status == JobStatus.Succeeded, Patience);
            Assert.Equal(1, done.AttemptCount);
        }
        finally
        {
            await restarted.Runner.StopAsync(CancellationToken.None);
        }
    }

    // ── Fairness: caps, lanes, deadlines, the breaker ──────────────────────────────────────────

    [Fact]
    public async Task Capped_kinds_run_one_at_a_time_while_other_kinds_still_get_slots()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var enrich = ScriptedHandler.Blocks(JobKind.EnrichCatalog);
        var seed = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), enrich, seed);
        for (var i = 0; i < 3; i++)
        {
            await AddJobAsync(JobKind.EnrichCatalog);
        }

        await AddJobAsync(JobKind.SeedCatalog, priority: 5);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(enrich.Running == 1 && seed.Running == 1), Patience);
            await Task.Delay(TimeSpan.FromMilliseconds(600)); // several polls with a general slot free
            Assert.Equal(1, enrich.MaxConcurrent);
            Assert.Equal(2, await CountAsync(JobKind.EnrichCatalog, JobStatus.Pending));
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_reserved_lane_runs_a_sync_while_every_general_slot_is_held_and_never_runs_background_work()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var seed = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        var sync = ScriptedHandler.Blocks(JobKind.DeltaSync);
        var options = JobRunnerHarness.FastOptions();
        await using var harness = Harness(options, seed, sync);
        for (var i = 0; i < options.GeneralSlots + 1; i++)
        {
            await AddJobAsync(JobKind.SeedCatalog);
        }

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(seed.Running == options.GeneralSlots), Patience);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            // The reserved slots are free, but background work never takes them.
            Assert.Equal(options.GeneralSlots, seed.Running);

            var enqueued = DateTime.UtcNow;
            var id = await AddJobAsync(JobKind.DeltaSync, account.Id);
            var running = await WaitForAsync(id, j => j.Status == JobStatus.Running, TimeSpan.FromSeconds(5));
            Assert.True(running.StartedAt - enqueued < TimeSpan.FromSeconds(2), "a sync waited behind background work");
            Assert.Equal(options.GeneralSlots, seed.Running);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_resumable_kind_yields_at_its_deadline_and_resumes_from_its_checkpoint_with_the_attempt_refunded()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var log = new CheckpointLog();
        var options = Deadline(JobKind.SeedCatalog, TimeSpan.FromMilliseconds(400), DeadlineAction.Yield);
        await using var harness = Harness(options, [], services =>
        {
            services.AddSingleton(log);
            services.AddScoped<IJobHandler, CheckpointingHandler>();
        });
        var id = await AddJobAsync(JobKind.SeedCatalog);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var done = await WaitForAsync(id, j => j.Status == JobStatus.Succeeded, Patience);
            Assert.Equal(1, done.AttemptCount);
            Assert.Equal(1, done.DeadlineYields);
            Assert.Null(done.Error);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, log.Executions);
        using var cursor = System.Text.Json.JsonDocument.Parse(log.CursorSeenOnResume!);
        Assert.Equal(7, cursor.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task A_resumable_kind_that_never_finishes_runs_out_of_slices_and_fails_as_timed_out()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        var options = Deadline(JobKind.SeedCatalog, TimeSpan.FromMilliseconds(250), DeadlineAction.Yield) with { MaxDeadlineYields = 2 };
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var failed = await WaitForAsync(id, j => j.Status == JobStatus.Failed, Patience);
            Assert.Equal(2, failed.DeadlineYields);
            Assert.Equal(JobRunnerLimits.MaxAttempts, failed.AttemptCount);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal(JobFailureReason.TimedOut, JobFailureReason.Classify(failed.Error));
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }

        // Two refunded slices, then the three attempts the cap allows.
        Assert.Equal(2 + JobRunnerLimits.MaxAttempts, handler.Executions);
    }

    [Fact]
    public async Task A_restarting_kind_that_overruns_its_deadline_consumes_attempts_and_fails_as_timed_out()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var handler = ScriptedHandler.Blocks(JobKind.Train);
        var options = Deadline(JobKind.Train, TimeSpan.FromMilliseconds(250), DeadlineAction.Retry);
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.Train, account.Id);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var failed = await WaitForAsync(id, j => j.Status == JobStatus.Failed, Patience);
            Assert.Equal(JobRunnerLimits.MaxAttempts, failed.AttemptCount);
            Assert.Equal(0, failed.DeadlineYields);
            Assert.StartsWith("Timed out: ran past its", failed.Error, StringComparison.Ordinal);
            Assert.NotNull(failed.CompletedAt);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }

        Assert.Equal(JobRunnerLimits.MaxAttempts, handler.Executions);
    }

    [Fact]
    public async Task Llm_kinds_wait_while_the_provider_breaker_is_open_and_other_kinds_keep_running()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var health = new SwitchableProviderHealthReader
        {
            Snapshot = new ProviderHealthSnapshot(ProviderHealthState.Down, DateTime.UtcNow,
            [
                new ProviderBreaker(ApiProvider.OpenRouter, null, ProviderBreakerState.Open, "payment_required", DateTime.UtcNow, null),
            ]),
        };
        var crossReference = ScriptedHandler.Succeeds(JobKind.CrossReference);
        var sync = ScriptedHandler.Succeeds(JobKind.DeltaSync);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), [crossReference, sync], services =>
            services.AddSingleton<IProviderHealthReader>(health));
        var llm = await AddJobAsync(JobKind.CrossReference, account.Id);
        var plain = await AddJobAsync(JobKind.DeltaSync, account.Id, priority: 9);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(plain, j => j.Status == JobStatus.Succeeded, Patience);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var held = await ReadAsync(llm);
            Assert.Equal(JobStatus.Pending, held.Status);
            Assert.Equal(0, held.AttemptCount);
            Assert.Equal(0, crossReference.Executions);

            health.Snapshot = new ProviderHealthSnapshot(ProviderHealthState.Healthy, DateTime.UtcNow, []);
            await WaitForAsync(llm, j => j.Status == JobStatus.Succeeded, Patience);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Behind_the_real_provider_breaker_llm_kinds_wait_unspent_and_are_claimed_once_it_half_opens()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // WS-1's breaker as production registers it, on a clock the test moves.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var breaker = new Infrastructure.Llm.ProviderHealthService(clock: clock);
        breaker.ReportRefused(ApiProvider.OpenRouter, 402);

        var account = await SeedAccountAsync();
        var lens = ScriptedHandler.Succeeds(JobKind.PersonaliseTitles);
        var sync = ScriptedHandler.Succeeds(JobKind.DeltaSync);
        await using var harness = Harness(JobRunnerHarness.FastOptions(), [lens, sync], services =>
            services.AddSingleton<IProviderHealthReader>(breaker));
        var llm = await AddJobAsync(JobKind.PersonaliseTitles, account.Id);
        var plain = await AddJobAsync(JobKind.DeltaSync, account.Id, priority: 9);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            // Open: the plain kind runs behind it, the LLM kind is not claimed and spends nothing.
            await WaitForAsync(plain, j => j.Status == JobStatus.Succeeded, Patience);
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            var held = await ReadAsync(llm);
            Assert.Equal(JobStatus.Pending, held.Status);
            Assert.Equal(0, held.AttemptCount);
            Assert.Null(held.Error);
            Assert.Equal(0, lens.Executions);

            // Past the cool-off nothing else calls the provider; the queue itself must probe.
            clock.Advance(Infrastructure.Llm.ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));
            var ran = await WaitForAsync(llm, j => j.Status == JobStatus.Succeeded, Patience);
            Assert.Equal(1, ran.AttemptCount);
            Assert.Equal(1, lens.Executions);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    // ── Non-retryable failures (WS-1 CR-5) ─────────────────────────────────────────────────────

    public static TheoryData<string, Func<Exception>> TypedNonRetryable => new()
    {
        { "thrown as is", () => new NonRetryableJobException("401 (Unauthorized)") },
        { "wrapped by a helper", () => new InvalidOperationException("judge failed", new NonRetryableJobException("401 (Unauthorized)")) },
        { "inside an aggregate", () => new AggregateException(new TimeoutException("slow"), new NonRetryableJobException("401 (Unauthorized)")) },
    };

    [Theory]
    [MemberData(nameof(TypedNonRetryable))]
    public async Task A_typed_non_retryable_failure_is_dead_lettered_on_its_first_attempt_with_its_attempts_unspent(
        string because, Func<Exception> failure)
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var handler = new ScriptedHandler(JobKind.CrossReference, (_, _, _) => throw failure());
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.CrossReference, account.Id);

        var outcome = await RunOnceAsync(harness, id, JobKind.CrossReference);

        var row = await ReadAsync(id);
        Assert.True(outcome == JobRunOutcome.Failed, $"{because}: {outcome}");
        Assert.Equal(JobStatus.Failed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(1, handler.Executions);
        Assert.Contains(harness.Events.Published, e => e.AccountId == account.Id && e.Type == PipelineEventTypes.Failed);
    }

    [Fact]
    public async Task An_untyped_failure_with_the_same_message_is_still_retried()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // The control for the theory above: the TYPE decides, never the text.
        var account = await SeedAccountAsync();
        var handler = new ScriptedHandler(JobKind.CrossReference, (_, _, _) =>
            throw new InvalidOperationException("judge failed", new HttpRequestException("401 (Unauthorized)")));
        await using var harness = Harness(JobRunnerHarness.FastOptions(), handler);
        var id = await AddJobAsync(JobKind.CrossReference, account.Id);

        Assert.Equal(JobRunOutcome.Retrying, await RunOnceAsync(harness, id, JobKind.CrossReference));
        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.CompletedAt);
    }

    [Theory]
    [InlineData(0)] // the first of three attempts
    [InlineData(1)] // the second: one would still have remained
    public async Task A_handler_that_raises_its_own_attempt_count_and_throws_is_failed_without_another_attempt(int priorAttempts)
    {
        if (!await ResetAsync())
        {
            return;
        }

        // How the cross-reference handler asks for no retry today: the write was never saved
        // (it throws), so the runner used to retry the deterministic failure anyway.
        var account = await SeedAccountAsync();
        await using var harness = Harness(JobRunnerHarness.FastOptions(), [], ContextHandler.For(JobKind.CrossReference, (job, _, _) =>
        {
            job.AttemptCount = Math.Max(job.AttemptCount, JobRunnerLimits.MaxAttempts);
            throw new InvalidOperationException("Cross-reference answered none of 3 batch(es): truncated.");
        }));
        var id = await AddJobAsync(JobKind.CrossReference, account.Id, attempts: priorAttempts);

        Assert.Equal(JobRunOutcome.Failed, await RunOnceAsync(harness, id, JobKind.CrossReference));
        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Failed, row.Status);
        Assert.Equal(priorAttempts + 1, row.AttemptCount);
        Assert.NotNull(row.CompletedAt);
        Assert.Contains("answered none", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handler_that_moves_the_runners_columns_and_returns_is_completed_on_its_own_fence()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // Saving those columns would move the row off this execution's fence, and the completion
        // would find no row and report a lease it never lost.
        await using var harness = Harness(JobRunnerHarness.FastOptions(), [], ContextHandler.For(JobKind.SeedCatalog, (job, _, _) =>
        {
            job.AttemptCount = 7;
            job.Status = JobStatus.Failed;
            job.CursorJson = """{"page":3}""";
            return Task.CompletedTask;
        }));
        var id = await AddJobAsync(JobKind.SeedCatalog);

        Assert.Equal(JobRunOutcome.Succeeded, await RunOnceAsync(harness, id, JobKind.SeedCatalog));
        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Succeeded, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("""{"page": 3}""", row.CursorJson);
    }

    [Fact]
    public async Task A_kind_this_build_does_not_know_is_never_claimed()
    {
        if (!await ResetAsync())
        {
            return;
        }

        await using var harness = Harness(JobRunnerHarness.FastOptions(), ScriptedHandler.Succeeds(JobKind.SeedCatalog));
        var id = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            // A row a newer image wrote, with a kind added after this build.
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO "SyncJobs" ("Id", "Kind", "Status", "Priority", "EnqueuedAt", "AttemptCount")
                 VALUES ({id}, 'RepairAvailability', 'Pending', 0, {DateTime.UtcNow}, 0)
                 """);
        }

        Assert.Null(await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None));
        await using var check = fixture.CreateContext();
        var status = await check.Database
            .SqlQuery<string>($"""SELECT "Status" AS "Value" FROM "SyncJobs" WHERE "Id" = {id}""")
            .SingleAsync();
        Assert.Equal("Pending", status);
    }

    [Fact]
    public async Task A_queued_job_of_a_known_kind_with_no_handler_is_failed_rather_than_left_waiting()
    {
        if (!await ResetAsync())
        {
            return;
        }

        await using var harness = Harness(JobRunnerHarness.FastOptions(), ScriptedHandler.Succeeds(JobKind.SeedCatalog));
        var id = await AddJobAsync(JobKind.FullReconcile);

        await harness.Runner.StartAsync(CancellationToken.None);
        try
        {
            var failed = await WaitForAsync(id, j => j.Status == JobStatus.Failed, Patience);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal(0, failed.AttemptCount);
            Assert.Contains("No handler", failed.Error!, StringComparison.Ordinal);
        }
        finally
        {
            await harness.Runner.StopAsync(CancellationToken.None);
        }
    }

    // ── Review round: fenced handler writes, hard deadlines, bounded refunds ──────────────────

    [Fact]
    public async Task A_reclaimed_execution_cannot_save_its_checkpoint_or_progress_over_the_live_execution()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // A is a paused replica: its beat never fires, so B reclaims the row and starts attempt 2.
        // While B is still running, A wakes up and saves its stale cursor and progress the way every
        // real handler does — through the tracked job on its own context.
        var releaseA = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();
        var bSaved = new TaskCompletionSource();
        await using var a = JobRunnerHarness.Create(
            fixture.ConnectionString,
            JobRunnerHarness.FastOptions() with { HeartbeatInterval = TimeSpan.FromHours(1) },
            ContextHandler.For(JobKind.SeedCatalog, async (job, db, _) =>
            {
                await releaseA.Task;
                job.CursorJson = """{"from":"stale-attempt-1"}""";
                job.ProgressPct = 42;
                job.ProgressMessage = "stale";
                await db.SaveChangesAsync(CancellationToken.None);
            }));
        await using var b = JobRunnerHarness.Create(
            fixture.ConnectionString,
            JobRunnerHarness.FastOptions() with { StaleAfter = TimeSpan.FromSeconds(1) },
            ContextHandler.For(JobKind.SeedCatalog, async (job, db, _) =>
            {
                job.CursorJson = """{"from":"attempt-2"}""";
                job.ProgressPct = 10;
                job.ProgressMessage = "live";
                await db.SaveChangesAsync(CancellationToken.None);
                bSaved.SetResult();
                await releaseB.Task;
            }));
        var id = await AddJobAsync(JobKind.SeedCatalog);

        var claimA = await a.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimA);
        var runA = a.Runner.RunAsync(claimA, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await b.Runner.SweepAsync(CancellationToken.None);
        var claimB = await b.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimB);
        var runB = b.Runner.RunAsync(claimB, CancellationToken.None);
        await bSaved.Task.WaitAsync(Patience);

        releaseA.SetResult();
        Assert.Equal(JobRunOutcome.LeaseLost, await runA.WaitAsync(Patience));

        var live = await ReadAsync(id);
        Assert.Equal(JobStatus.Running, live.Status);
        Assert.Equal(2, live.AttemptCount);
        Assert.Equal("""{"from": "attempt-2"}""", live.CursorJson);
        Assert.Equal(10, live.ProgressPct);
        Assert.Equal("live", live.ProgressMessage);

        releaseB.SetResult();
        Assert.Equal(JobRunOutcome.Succeeded, await runB.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_reclaimed_execution_that_finishes_while_its_successor_is_still_running_writes_nothing()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // Guards the lease NUMBER, not only the status: B's row is Running, exactly what A's own
        // fence would match if it checked status alone.
        var releaseA = new TaskCompletionSource();
        var handlerA = new ScriptedHandler(JobKind.SeedCatalog, (_, _, _) => releaseA.Task);
        var handlerB = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        await using var a = Harness(JobRunnerHarness.FastOptions() with { HeartbeatInterval = TimeSpan.FromHours(1) }, handlerA);
        await using var b = Harness(JobRunnerHarness.FastOptions() with { StaleAfter = TimeSpan.FromSeconds(1) }, handlerB);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        var claimA = await a.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimA);
        var runA = a.Runner.RunAsync(claimA, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await b.Runner.SweepAsync(CancellationToken.None);
        var claimB = await b.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimB);
        using var stopB = new CancellationTokenSource();
        var runB = b.Runner.RunAsync(claimB, stopB.Token);
        await WaitUntilAsync(() => Task.FromResult(handlerB.Running == 1), Patience);

        releaseA.SetResult();
        Assert.Equal(JobRunOutcome.LeaseLost, await runA.WaitAsync(Patience));

        var live = await ReadAsync(id);
        Assert.Equal(JobStatus.Running, live.Status);
        Assert.Equal(2, live.AttemptCount);
        Assert.Null(live.CompletedAt);
        Assert.Null(live.ProgressPct);

        await stopB.CancelAsync();
        Assert.Equal(JobRunOutcome.Interrupted, await runB.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_restarting_kind_whose_handler_returns_after_its_deadline_has_finished_and_is_completed()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        // Finishes its work 1.5 s in, never looking at the token that fired at 250 ms.
        var handler = new ScriptedHandler(JobKind.Train, (_, _, _) => Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken.None));
        var options = Deadline(JobKind.Train, TimeSpan.FromMilliseconds(250), DeadlineAction.Retry);
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.Train, account.Id);

        var claim = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(JobRunOutcome.Succeeded, await harness.Runner.RunAsync(claim, CancellationToken.None).WaitAsync(Patience));

        var done = await ReadAsync(id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Equal(1, done.AttemptCount);
        Assert.Null(done.Error);
        Assert.Equal(1, handler.Executions);
    }

    [Fact]
    public async Task A_kind_that_returns_early_at_its_deadline_is_sliced_not_completed()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // The seed walks pages while its token is live and RETURNS when it fires: that return is a
        // checkpoint, not a finished plan.
        var handler = new ScriptedHandler(JobKind.SeedCatalog, async (_, execution, ct) =>
        {
            while (execution == 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
            }
        });
        var options = Deadline(JobKind.SeedCatalog, TimeSpan.FromMilliseconds(250), DeadlineAction.Yield);
        Assert.True(options.PolicyFor(JobKind.SeedCatalog).StopsEarlyOnCancellation);
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        var first = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(JobRunOutcome.Yielded, await harness.Runner.RunAsync(first, CancellationToken.None).WaitAsync(Patience));

        var second = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(JobRunOutcome.Succeeded, await harness.Runner.RunAsync(second, CancellationToken.None).WaitAsync(Patience));

        var done = await ReadAsync(id);
        Assert.Equal(1, done.AttemptCount);
        Assert.Equal(1, done.DeadlineYields);
        Assert.Equal(2, handler.Executions);
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_is_abandoned_at_the_hard_limit_and_its_late_save_is_fenced()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var account = await SeedAccountAsync();
        var orphanEnded = new TaskCompletionSource();
        var options = Deadline(JobKind.Train, TimeSpan.FromMilliseconds(300), DeadlineAction.Retry) with
        {
            OverrunGrace = TimeSpan.FromMilliseconds(300),
        };
        await using var deaf = JobRunnerHarness.Create(fixture.ConnectionString, options, ContextHandler.For(JobKind.Train, async (job, db, _) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
                job.ProgressMessage = "late";
                job.CursorJson = """{"late":true}""";
                await db.SaveChangesAsync(CancellationToken.None);
            }
            finally
            {
                orphanEnded.SetResult();
            }
        }));
        var id = await AddJobAsync(JobKind.Train, account.Id);

        var claim = await deaf.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claim);
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(JobRunOutcome.Retrying, await deaf.Runner.RunAsync(claim, CancellationToken.None).WaitAsync(Patience));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5), $"the slot was held for {stopwatch.Elapsed}");

        var abandoned = await ReadAsync(id);
        Assert.Equal(JobStatus.Pending, abandoned.Status);
        Assert.Equal(1, abandoned.AttemptCount);
        Assert.StartsWith("Timed out: ran past its", abandoned.Error, StringComparison.Ordinal);
        Assert.Contains("abandoned", abandoned.Error, StringComparison.Ordinal);
        Assert.Equal(JobFailureReason.TimedOut, JobFailureReason.Classify(abandoned.Error));

        // Its beat stopped with it: nothing keeps the row looking alive.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(abandoned.HeartbeatAt, (await ReadAsync(id)).HeartbeatAt);

        // The next attempt runs while the deaf handler is still going, and the deaf handler's late
        // save lands on nothing.
        var next = ScriptedHandler.Blocks(JobKind.Train);
        await using var successor = Harness(JobRunnerHarness.FastOptions(), next);
        var claimNext = await successor.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claimNext);
        Assert.Equal(2, claimNext.Attempt);
        using var stopNext = new CancellationTokenSource();
        var runNext = successor.Runner.RunAsync(claimNext, stopNext.Token);

        await orphanEnded.Task.WaitAsync(Patience);
        var live = await ReadAsync(id);
        Assert.Equal(JobStatus.Running, live.Status);
        Assert.Equal(2, live.AttemptCount);
        Assert.Null(live.ProgressMessage);
        Assert.Null(live.CursorJson);

        await stopNext.CancelAsync();
        await runNext.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stopping_host_refunds_an_attempt_a_bounded_number_of_times_then_counts_it_as_a_failure()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        var options = JobRunnerHarness.FastOptions() with { MaxInterruptions = 2 };
        await using var harness = Harness(options, handler);
        var id = await AddJobAsync(JobKind.SeedCatalog);

        var outcomes = new List<JobRunOutcome>();
        for (var restart = 0; restart < 3; restart++)
        {
            var claim = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
            Assert.NotNull(claim);
            using var stopping = new CancellationTokenSource();
            var run = harness.Runner.RunAsync(claim, stopping.Token);
            await WaitUntilAsync(() => Task.FromResult(handler.Running == 1), Patience);
            await stopping.CancelAsync();
            outcomes.Add(await run.WaitAsync(Patience));
        }

        Assert.Equal(new[] { JobRunOutcome.Interrupted, JobRunOutcome.Interrupted, JobRunOutcome.Retrying }, outcomes);
        var row = await ReadAsync(id);
        Assert.Equal(JobStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(2, row.Interruptions);
        Assert.StartsWith("Interrupted:", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_succeeds_never_reports_a_lease_it_did_not_lose()
    {
        if (!await ResetAsync())
        {
            return;
        }

        // A beat every 5 ms against handlers that finish in ~30 ms: under the old ordering a tick
        // regularly landed between the terminal write and the beat's stop and cried "reclaimed".
        var log = new RecordingLogger<JobRunnerService>();
        var handler = new ScriptedHandler(JobKind.SeedCatalog, (_, _, _) => Task.Delay(30, CancellationToken.None));
        await using var harness = JobRunnerHarness.Create(
            fixture.ConnectionString,
            JobRunnerHarness.FastOptions() with { HeartbeatInterval = TimeSpan.FromMilliseconds(5) },
            services => services.AddSingleton<IJobHandler>(handler),
            log);

        for (var i = 0; i < 20; i++)
        {
            await AddJobAsync(JobKind.SeedCatalog);
            var claim = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
            Assert.NotNull(claim);
            Assert.Equal(JobRunOutcome.Succeeded, await harness.Runner.RunAsync(claim, CancellationToken.None).WaitAsync(Patience));
        }

        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_slice_keeps_the_first_enqueue_time_and_goes_behind_work_queued_before_it()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var handler = ScriptedHandler.Blocks(JobKind.SeedCatalog);
        var options = Deadline(JobKind.SeedCatalog, TimeSpan.FromMilliseconds(200), DeadlineAction.Yield);
        await using var harness = Harness(options, handler);
        var firstQueued = DateTime.UtcNow.AddSeconds(-10);
        var sliced = await AddJobAsync(JobKind.SeedCatalog, enqueuedAt: firstQueued);
        var queuedLater = await AddJobAsync(JobKind.SeedCatalog, enqueuedAt: DateTime.UtcNow.AddSeconds(-5));

        var claim = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(sliced, claim.Id);
        Assert.Equal(JobRunOutcome.Yielded, await harness.Runner.RunAsync(claim, CancellationToken.None).WaitAsync(Patience));

        var row = await ReadAsync(sliced);
        Assert.Equal(firstQueued, row.EnqueuedAt, TimeSpan.FromMilliseconds(1));
        Assert.NotNull(row.RequeuedAt);
        Assert.True(row.StartedAt >= row.EnqueuedAt, "a queue wait must never be negative");

        var next = await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal(queuedLater, next.Id);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────

    private JobRunnerHarness Harness(JobRunnerOptions options, params ScriptedHandler[] handlers) =>
        Harness(options, handlers, configure: null);

    private JobRunnerHarness Harness(JobRunnerOptions options, IJobHandler[] handlers, Action<IServiceCollection>? configure) =>
        JobRunnerHarness.Create(fixture.ConnectionString, options, services =>
        {
            foreach (var handler in handlers)
            {
                services.AddSingleton(handler);
            }

            configure?.Invoke(services);
        });

    private static JobRunnerOptions Deadline(JobKind kind, TimeSpan deadline, DeadlineAction action)
    {
        var defaults = JobRunnerHarness.FastOptions();
        var kinds = defaults.Kinds.ToDictionary(k => k.Key, k => k.Value);
        kinds[kind] = kinds[kind] with { Deadline = deadline, OnDeadline = action };
        return defaults with { Kinds = kinds };
    }

    /// <summary>One execution through the real claim and the real execution path, without the loop.</summary>
    private static async Task<JobRunOutcome> RunOnceAsync(JobRunnerHarness harness, Guid id, JobKind kind)
    {
        var claim = await harness.Runner.ClaimNextAsync([kind.ToString()], CancellationToken.None, id);
        Assert.NotNull(claim);
        return await harness.Runner.RunAsync(claim, CancellationToken.None);
    }

    /// <summary>A clock the test moves by hand, for the provider-health service's cool-off.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>Empties the queue; false when there is no database (the collection no-ops).</summary>
    private async Task<bool> ResetAsync()
    {
        if (!fixture.Available)
        {
            return false;
        }

        await using var db = fixture.CreateContext();
        await db.SyncJobs.ExecuteDeleteAsync();
        return true;
    }

    private async Task<Guid> AddJobAsync(
        JobKind kind,
        Guid? accountId = null,
        int attempts = 0,
        JobStatus status = JobStatus.Pending,
        DateTime? heartbeat = null,
        short priority = 0,
        decimal? progress = null,
        DateTime? enqueuedAt = null)
    {
        await using var db = fixture.CreateContext();
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Status = status,
            Priority = priority,
            EnqueuedAt = enqueuedAt ?? DateTime.UtcNow,
            StartedAt = status == JobStatus.Running ? heartbeat : null,
            HeartbeatAt = heartbeat,
            AttemptCount = attempts,
            ProgressPct = progress,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<SyncJob> ReadAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private async Task<int> CountAsync(JobKind kind, JobStatus status)
    {
        await using var db = fixture.CreateContext();
        return await db.SyncJobs.CountAsync(j => j.Kind == kind && j.Status == status);
    }

    private async Task<SyncJob> WaitForAsync(Guid id, Func<SyncJob, bool> condition, TimeSpan timeout)
    {
        SyncJob? last = null;
        await WaitUntilAsync(async () =>
        {
            last = await ReadAsync(id);
            return condition(last);
        }, timeout, () => last is null ? "no row" : $"last seen {last.Status}, attempt {last.AttemptCount}, error {last.Error}");
        return last!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<string>? describe = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                Assert.Fail($"Condition not met within {timeout}: {describe?.Invoke() ?? "timed out"}");
            }

            await Task.Delay(50);
        }
    }

    private async Task<Account> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"runner-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStage = PipelineStage.FeedReady,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class CheckpointLog
    {
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public string? CursorSeenOnResume { get; set; }

        public int Next() => Interlocked.Increment(ref _executions);
    }

    /// <summary>
    /// Checkpoints the way a real handler does — the cursor saved in its own SaveChanges before it
    /// can be interrupted — so the resume test exercises the checkpoint, not a runner flush.
    /// </summary>
    private sealed class CheckpointingHandler(Infrastructure.Persistence.ReelDbContext db, CheckpointLog log) : IJobHandler
    {
        public JobKind Kind => JobKind.SeedCatalog;

        public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
        {
            if (log.Next() == 1)
            {
                job.CursorJson = """{"page":7}""";
                await db.SaveChangesAsync(ct);
                await Task.Delay(Timeout.Infinite, ct);
            }

            log.CursorSeenOnResume = job.CursorJson;
        }
    }
}
