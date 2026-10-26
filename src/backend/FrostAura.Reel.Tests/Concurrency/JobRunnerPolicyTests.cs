using System.Reflection;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Background;

namespace FrostAura.Reel.Tests.Concurrency;

/// <summary>
/// The runner's fairness policy as data (J-5): lanes, caps, deadlines and the breaker pause. The
/// behaviour on a real database is in <c>JobRunnerServiceTests</c>; these pin the table itself so a
/// kind added later cannot slip in without a decision.
/// </summary>
public class JobRunnerPolicyTests
{
    private static readonly JobRunnerOptions Options = JobRunnerOptions.Default;

    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_job_kind_has_an_explicit_deadline_policy()
    {
        var missing = Enum.GetValues<JobKind>().Where(k => !Options.Kinds.ContainsKey(k)).ToList();
        Assert.Empty(missing);
        Assert.All(Options.Kinds.Values, p => Assert.True(p.Deadline > TimeSpan.Zero));
    }

    [Fact]
    public void The_reserved_lane_is_for_the_work_a_user_waits_on_and_nothing_else()
    {
        Assert.Equal(
            new[] { JobKind.DeltaSync, JobKind.Train, JobKind.BuildFeed }.Order(),
            Options.InteractiveKinds.Order());
        Assert.True(Options.ReservedSlots >= 1);
        Assert.True(Options.GeneralSlots >= Options.KindCaps.Count, "every capped kind can run at once beside the others");
    }

    [Fact]
    public void Enrichment_cross_reference_and_hydration_run_one_at_a_time()
    {
        Assert.Equal(1, Options.KindCaps[JobKind.EnrichCatalog]);
        Assert.Equal(1, Options.KindCaps[JobKind.CrossReference]);
        Assert.Equal(1, Options.KindCaps[JobKind.HydrateCatalog]);
        Assert.DoesNotContain(Options.KindCaps.Keys, Options.InteractiveKinds.Contains);
    }

    [Fact]
    public void The_cross_reference_deadline_sits_above_the_handlers_own_hard_ceiling()
    {
        // The pass boxes itself; the runner's deadline must never be what ends a healthy run.
        var policy = Options.PolicyFor(JobKind.CrossReference);
        Assert.True(policy.Deadline > CrossReferenceJobHandler.HardCeiling);
        Assert.Equal(DeadlineAction.Yield, policy.OnDeadline);
    }

    [Theory]
    [InlineData(JobKind.BuildFeed)]
    [InlineData(JobKind.Train)]
    [InlineData(JobKind.DeltaSync)]
    [InlineData(JobKind.Evaluate)]
    public void Kinds_that_restart_from_nothing_retry_at_their_deadline_instead_of_slicing(JobKind kind) =>
        Assert.Equal(DeadlineAction.Retry, Options.PolicyFor(kind).OnDeadline);

    [Fact]
    public void Every_llm_kind_names_purposes_the_ledger_actually_uses()
    {
        var purposes = typeof(ApiUsagePurpose)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Options.LlmKinds.Values.SelectMany(p => p), p => Assert.Contains(p, purposes));
        Assert.DoesNotContain(Options.LlmKinds.Keys, Options.InteractiveKinds.Contains);
    }

    [Fact]
    public void A_provider_that_is_down_pauses_every_llm_kind_and_nothing_else()
    {
        var paused = JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Down), Options, Now);
        Assert.Equal(Options.LlmKinds.Keys.Select(k => k.ToString()).Order(), paused.Order());
    }

    [Fact]
    public void A_provider_wide_open_breaker_pauses_every_llm_kind()
    {
        var paused = JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(purpose: null, ProviderBreakerState.Open)), Options, Now);
        Assert.Equal(Options.LlmKinds.Count, paused.Count);
    }

    [Fact]
    public void An_open_breaker_on_one_purpose_pauses_only_the_kinds_that_spend_under_it()
    {
        var paused = JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(ApiUsagePurpose.CrossReference, ProviderBreakerState.Open)), Options, Now);
        Assert.Equal([nameof(JobKind.CrossReference)], paused);
    }

    [Fact]
    public void A_degraded_but_closed_breaker_an_unmonitored_reader_or_a_trakt_breaker_pauses_nothing()
    {
        Assert.Empty(JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(ApiUsagePurpose.Lens, ProviderBreakerState.Degraded)), Options, Now));
        Assert.Empty(JobRunnerService.PausedKinds(ProviderHealthSnapshot.Unmonitored, Options, Now));
        Assert.Empty(JobRunnerService.PausedKinds(null, Options, Now));
        Assert.Empty(JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded,
                new ProviderBreaker(ApiProvider.Trakt, null, ProviderBreakerState.Open, "unauthorized", Now, null)),
            Options,
            Now));
    }

    [Fact]
    public void A_half_open_provider_breaker_lets_llm_kinds_be_claimed_so_the_first_call_can_probe()
    {
        // Past its RetryAt the provider-health service admits one probe; background work is often
        // the only caller. Pausing here kept the LLM queues shut until something else probed.
        var halfOpen = Breaker(purpose: null, ProviderBreakerState.Open, retryAt: Now.AddSeconds(-1));
        Assert.Empty(JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Down, halfOpen), Options, Now));

        // The same breaker one second earlier is still open, and pauses every LLM kind.
        var open = Breaker(purpose: null, ProviderBreakerState.Open, retryAt: Now.AddSeconds(1));
        Assert.Equal(Options.LlmKinds.Count, JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Down, open), Options, Now).Count);
    }

    [Fact]
    public void A_purpose_breaker_past_its_retry_time_pauses_nothing_and_one_before_it_pauses_its_kinds()
    {
        var expired = Breaker(ApiUsagePurpose.Lens, ProviderBreakerState.Open, retryAt: Now);
        Assert.Empty(JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Degraded, expired), Options, Now));

        var live = Breaker(ApiUsagePurpose.Lens, ProviderBreakerState.Open, retryAt: Now.AddMinutes(5));
        Assert.Equal([nameof(JobKind.PersonaliseTitles)], JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Degraded, live), Options, Now));
    }

    [Fact]
    public void A_down_reading_that_names_no_provider_breaker_still_pauses_every_llm_kind()
    {
        // A reader that cannot say when to retry gets the conservative reading, even beside an
        // expired purpose breaker that would otherwise let its kind through.
        var expired = Breaker(ApiUsagePurpose.CrossReference, ProviderBreakerState.Open, retryAt: Now.AddMinutes(-1));
        Assert.Equal(Options.LlmKinds.Count, JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Down, expired), Options, Now).Count);
    }

    [Fact]
    public void A_non_retryable_failure_is_recognised_however_it_is_wrapped_and_nothing_else_is()
    {
        var typed = new NonRetryableJobException("402 (Payment Required)");
        Assert.True(JobRunnerService.IsNonRetryable(typed));
        Assert.True(JobRunnerService.IsNonRetryable(new InvalidOperationException("outer", typed)));
        Assert.True(JobRunnerService.IsNonRetryable(new AggregateException(new TimeoutException(), typed)));
        Assert.True(JobRunnerService.IsNonRetryable(
            new InvalidOperationException("outer", new AggregateException(new InvalidOperationException("mid", typed)))));

        Assert.False(JobRunnerService.IsNonRetryable(new InvalidOperationException("402 (Payment Required)")));
        Assert.False(JobRunnerService.IsNonRetryable(new AggregateException(new TimeoutException(), new HttpRequestException("503"))));
        Assert.False(JobRunnerService.IsNonRetryable(new TaskCanceledException()));
    }

    [Fact]
    public void The_claim_puts_every_value_in_a_parameter_never_inside_a_sql_literal()
    {
        // The @p1 bug (J-6): an interpolation inside quotes became the literal text "@p1".
        var sql = JobRunnerService.ClaimNextSql(DateTime.UtcNow, Enum.GetNames<JobKind>(), Options);
        Assert.DoesNotContain("'{", sql.Format, StringComparison.Ordinal);
        Assert.Equal("Stalled with no heartbeat after 3 attempts.", JobRunnerService.StalledMessage(3));
    }

    /// <summary>
    /// The longest single-attempt success per restarting kind on the live history
    /// (2026-07-24 to 2026-10-22, <c>Status = 'Succeeded' AND AttemptCount = 1</c>, <c>CompletedAt - StartedAt</c>).
    /// A Retry deadline below one of these turns a slow success into three restarts and a failure.
    /// DeltaSync is the exception on purpose: its two runs past 90 minutes (the longest 22 hours)
    /// are the wedges the deadline exists to end, against 6,066 runs that finished inside it.
    /// BuildFeed is measured on its WORK (F+1): its jobs' 141-minute wall time was a post-build stall,
    /// and the build's own stopwatch (<c>FeedBuildStats.DurationMs</c>, 288 builds) never passed 117 s.
    /// </summary>
    [Theory]
    [InlineData(JobKind.BuildFeed, 1.95)]
    [InlineData(JobKind.Train, 131.5)]
    [InlineData(JobKind.BuildTasteNarrative, 3.8)]
    [InlineData(JobKind.Evaluate, 0.1)]
    [InlineData(JobKind.BuildDigest, 0.1)]
    [InlineData(JobKind.DeltaSync, 5.2)] // p99; see above
    public void A_restarting_kinds_deadline_clears_its_longest_healthy_run_on_record(JobKind kind, double longestMinutes)
    {
        var policy = Options.PolicyFor(kind);
        Assert.Equal(DeadlineAction.Retry, policy.OnDeadline);
        Assert.True(policy.Deadline.TotalMinutes > longestMinutes * 1.05, $"{kind}: {policy.Deadline} against {longestMinutes} min on record");
    }

    [Fact]
    public void A_stalled_build_warns_well_inside_its_deadline_and_is_ended_within_half_an_hour()
    {
        // F+1: 18 of 53 BuildFeed jobs since 10-14 held the pill 13 to 237 minutes past a build that
        // took at most 117 s. The warning must come while the stall can still be looked at.
        var policy = Options.PolicyFor(JobKind.BuildFeed);
        Assert.Equal(TimeSpan.FromMinutes(30), policy.Deadline);
        Assert.NotNull(policy.SlowAfter);
        Assert.InRange(policy.SlowAfter.Value, TimeSpan.FromMinutes(2), policy.Deadline / 2);
    }

    [Fact]
    public void The_slow_run_watch_warns_once_with_how_long_progress_has_not_moved()
    {
        var watch = new JobRunnerService.SlowRunWatch(TimeSpan.FromMinutes(10));
        var job = new SyncJob { ProgressPct = 70, ProgressMessage = "Final ranking" };

        Assert.Null(watch.Observe(job, TimeSpan.FromMinutes(1)));
        job.ProgressPct = 85;
        job.ProgressMessage = "Assembling tonight's picks";
        Assert.Null(watch.Observe(job, TimeSpan.FromMinutes(2)));
        Assert.Null(watch.Observe(job, TimeSpan.FromMinutes(9)));

        var slow = watch.Observe(job, TimeSpan.FromMinutes(12));
        Assert.NotNull(slow);
        Assert.Equal(85, slow.Pct);
        Assert.Equal("Assembling tonight's picks", slow.Message);
        Assert.Equal(TimeSpan.FromMinutes(10), slow.Unchanged);
        Assert.Null(watch.Observe(job, TimeSpan.FromMinutes(40)));

        // A kind with no SlowAfter never warns, and a run whose job row was never read reports no progress.
        Assert.Null(new JobRunnerService.SlowRunWatch(null).Observe(job, TimeSpan.FromHours(5)));
        var unread = new JobRunnerService.SlowRunWatch(TimeSpan.FromMinutes(1)).Observe(null, TimeSpan.FromMinutes(2));
        Assert.NotNull(unread);
        Assert.Null(unread.Message);
    }

    [Theory]
    [InlineData(JobKind.PersonaliseTitles)] // skips titles already written at the same profile hash
    [InlineData(JobKind.RefreshAvailability)] // skips titles fetched inside the TTL
    public void Kinds_that_skip_work_already_done_slice_instead_of_restarting(JobKind kind) =>
        Assert.Equal(DeadlineAction.Yield, Options.PolicyFor(kind).OnDeadline);

    [Fact]
    public void Slices_cover_the_longest_resumable_run_on_record()
    {
        // Hydration and enrichment: 581 and 589 minutes of slot time on record, in ten-minute slices.
        var slices = Math.Ceiling(589.3 / Options.PolicyFor(JobKind.EnrichCatalog).Deadline.TotalMinutes);
        Assert.True(Options.MaxDeadlineYields > slices, $"{Options.MaxDeadlineYields} slices against {slices} needed");
    }

    [Fact]
    public void The_hard_limit_ends_a_deaf_handler_long_before_the_stale_sweep_would()
    {
        Assert.True(Options.OverrunGrace > TimeSpan.Zero);
        Assert.True(Options.OverrunGrace < Options.StaleAfter);
        Assert.True(Options.ShutdownGrace < TimeSpan.FromSeconds(30), "inside the host's own shutdown timeout");
        Assert.True(Options.MaxInterruptions > 0);
    }

    /// <summary>
    /// A handler that reads <c>IsCancellationRequested</c> returns at a checkpoint when its token
    /// fires, and the runner must not mistake that return for a finished run; every other handler's
    /// return is treated as finished. The flag is data, so this keeps it equal to the handler sources.
    /// </summary>
    [Fact]
    public void Kinds_declared_to_stop_early_are_exactly_the_handlers_that_return_on_cancellation()
    {
        var jobs = HandlerSourceDirectory();
        var handlers = typeof(IJobHandler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IJobHandler).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(handlers);

        var stopsEarly = new List<JobKind>();
        foreach (var handler in handlers)
        {
            Assert.EndsWith("JobHandler", handler.Name, StringComparison.Ordinal);
            var kind = Enum.Parse<JobKind>(handler.Name[..^"JobHandler".Length]);
            var source = File.ReadAllText(Path.Combine(jobs, handler.Name + ".cs"));
            if (source.Contains("IsCancellationRequested", StringComparison.Ordinal))
            {
                stopsEarly.Add(kind);
            }
        }

        Assert.Equal(
            stopsEarly.Order(),
            Options.Kinds.Where(k => k.Value.StopsEarlyOnCancellation).Select(k => k.Key).Order());
    }

    private static string HandlerSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "backend", "FrostAura.Reel.Application", "Jobs");
            if (File.Exists(Path.Combine(candidate, "IJobHandler.cs")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The handler sources were not found above the test output directory.");
    }

    private static ProviderHealthSnapshot Snapshot(ProviderHealthState state, params ProviderBreaker[] breakers) =>
        new(state, DateTime.UtcNow, breakers);

    private static ProviderBreaker Breaker(string? purpose, ProviderBreakerState state, DateTime? retryAt = null) =>
        new(ApiProvider.OpenRouter, purpose, state, "payment_required", Now.AddMinutes(-20), retryAt);
}
