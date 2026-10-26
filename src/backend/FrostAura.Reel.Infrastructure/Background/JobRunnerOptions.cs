using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>Which runner lane a slot belongs to.</summary>
public enum JobLane
{
    /// <summary>Runs any kind. Catalogue and LLM work lives here, capped per kind.</summary>
    General,

    /// <summary>
    /// Runs only <see cref="JobRunnerOptions.InteractiveKinds"/> — the work a user is waiting on —
    /// so a backlog of catalogue or LLM jobs can never queue a sync or a feed build behind it (J-5).
    /// </summary>
    Reserved,
}

/// <summary>What the runner does when a job's attempt runs past its kind's deadline.</summary>
public enum DeadlineAction
{
    /// <summary>
    /// The handler checkpoints as it goes (its cursor or its own data is the resume state), so the
    /// slice is not a failure: the job goes back to the end of the queue with its attempt refunded
    /// and resumes where it stopped, up to <see cref="JobRunnerOptions.MaxDeadlineYields"/> times.
    /// </summary>
    Yield,

    /// <summary>
    /// The handler starts over on every run, so a slice would only be thrown away: running past
    /// the deadline consumes the attempt like any other failure, and the third one fails the job
    /// visibly instead of holding a slot for hours.
    /// </summary>
    Retry,
}

/// <summary>One kind's runner policy.</summary>
/// <param name="Deadline">How long one attempt may run before the runner cancels its token.</param>
/// <param name="OnDeadline">What a cancelled-at-deadline attempt becomes: a slice or a failed attempt.</param>
/// <param name="StopsEarlyOnCancellation">
/// The handler checks its token at checkpoints and RETURNS (rather than throws) when it fires, so a
/// normal return after a runner cancellation means "stopped early", not "finished". For every other
/// kind a normal return is a finished run whatever the token says, and is completed — throwing away
/// a run that finished a moment after its deadline (and re-running it, re-spending on an LLM kind)
/// helps nobody. <c>JobRunnerPolicyTests</c> keeps this flag equal to what the handler sources do.
/// </param>
/// <param name="SlowAfter">
/// When set, an attempt still running past this logs one warning naming its last progress report
/// and how long ago it moved — the F+1 signal (a build whose work ended long before its job did).
/// Well inside the deadline, so the warning arrives while the stall can still be looked at.
/// </param>
public sealed record JobKindPolicy(
    TimeSpan Deadline, DeadlineAction OnDeadline, bool StopsEarlyOnCancellation = false, TimeSpan? SlowAfter = null);

/// <summary>
/// The job runner's tunables. Production runs on <see cref="Default"/>; the runner tests shrink the
/// intervals so the real loop can be driven in seconds. Not bound from configuration on purpose: the
/// status API reads <see cref="JobRunnerLimits"/> to tell live work from dead, and a runner tuned
/// differently from the reader would make the status payload lie.
/// </summary>
public sealed record JobRunnerOptions
{
    public static JobRunnerOptions Default { get; } = new();

    /// <summary>Slots that run any kind.</summary>
    public int GeneralSlots { get; init; } = 3;

    /// <summary>Slots that run only <see cref="InteractiveKinds"/>.</summary>
    public int ReservedSlots { get; init; } = 2;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan StaleAfter { get; init; } = JobRunnerLimits.StaleAfter;

    public int MaxAttempts { get; init; } = JobRunnerLimits.MaxAttempts;

    /// <summary>
    /// How long a stopping host waits for a running handler to observe cancellation. Past it the
    /// execution is abandoned: written off as a failed attempt (never refunded) and left fenced.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The hard limit past a kind's deadline (and past a lost lease). A handler that ignores its
    /// cancelled token is abandoned here: its heartbeat stops, the attempt is written off as timed
    /// out through the fence, its slot is freed, and its late writes match no row. Without it a
    /// deadline was only as good as the handler's manners, and a deaf handler held a slot — and a
    /// live-looking Running row — for as long as it liked.
    /// </summary>
    public TimeSpan OverrunGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many deadline slices a <see cref="DeadlineAction.Yield"/> kind gets before a further
    /// overrun counts as a failed attempt. Sized from the live history: the longest single-attempt
    /// hydration and enrichment runs took 9.7 and 9.8 hours of slot time, which is 59 ten-minute
    /// slices; 72 (twelve hours) clears both. Past that, "still going" is a defect to surface, not
    /// progress to wait for.
    /// </summary>
    public int MaxDeadlineYields { get; init; } = 72;

    /// <summary>
    /// How many times a stopping host may refund a job's attempt. Past it an interruption counts as
    /// a failed attempt, so a job whose own run keeps restarting the host reaches its attempt cap.
    /// </summary>
    public int MaxInterruptions { get; init; } = 5;

    /// <summary>The kinds a user starts or waits on: they get the reserved lane (J-5).</summary>
    public IReadOnlySet<JobKind> InteractiveKinds { get; init; } =
        new HashSet<JobKind> { JobKind.DeltaSync, JobKind.BuildFeed, JobKind.Train };

    /// <summary>
    /// At most this many Running at once, across every account and replica. Each of these walks the
    /// shared catalogue or a large LLM pool for minutes to hours; two of the same kind only contend for
    /// the same rows and the same budget, and used to hold every slot between them (J-5).
    /// </summary>
    public IReadOnlyDictionary<JobKind, int> KindCaps { get; init; } = new Dictionary<JobKind, int>
    {
        [JobKind.EnrichCatalog] = 1,
        [JobKind.CrossReference] = 1,
        [JobKind.HydrateCatalog] = 1,
    };

    /// <summary>
    /// The model-backed kinds and the ledger purposes they spend under. While the provider-health
    /// reader reports the provider down, or an open breaker on the provider as a whole or on one of
    /// a kind's purposes, that kind is not claimed: a paused queue costs nothing, a run against an
    /// open breaker burns an attempt (and, before the breaker, a 402) per job.
    /// </summary>
    public IReadOnlyDictionary<JobKind, IReadOnlyList<string>> LlmKinds { get; init; } =
        new Dictionary<JobKind, IReadOnlyList<string>>
        {
            [JobKind.EnrichCatalog] = [ApiUsagePurpose.ExtractAttributes, ApiUsagePurpose.Embed],
            [JobKind.CrossReference] = [ApiUsagePurpose.CrossReference],
            [JobKind.PersonaliseTitles] = [ApiUsagePurpose.Lens],
            [JobKind.BuildTasteNarrative] = [ApiUsagePurpose.TasteNarrative],
        };

    /// <summary>
    /// Every kind's deadline, calibrated against the live job history (every single-attempt success
    /// on record, 2026-07-24 to 2026-10-22; <c>JobRunnerPolicyTests</c> pins the figures).
    /// <list type="bullet">
    /// <item>Resumable kinds (a cursor or their own data is the checkpoint) get a slice and yield;
    /// the slice length only decides how often they give their slot away, never whether they finish.
    /// Refresh-availability skips titles fetched inside its TTL and the lens pass skips titles
    /// already written at the same profile hash, so both resume rather than restart.</item>
    /// <item>Kinds that restart from nothing get a box above the longest healthy run on record, so
    /// the deadline catches a wedge and never a slow success: Train's longest was 132 minutes, the
    /// taste narrative's 4. DeltaSync's box is 90 minutes: two runs on record took longer (the
    /// longest 22 hours), and those are the wedges the deadline exists to end.</item>
    /// <item>BuildFeed is boxed on its WORK, not its job wall time (F+1). The jobs' 141-minute
    /// "longest run" was the stall itself: across the 288 builds on the 2026-10-23 snapshot the
    /// build's own stopwatch (<c>FeedBuildStats.DurationMs</c>) never passed 117 s (p95 93 s), while
    /// 18 of 53 jobs since 10-14 stayed Running 13 to 237 minutes after it stopped. 30 minutes is 15×
    /// the longest build; a stalled one is cancelled and retried instead of holding the pill and every
    /// coalesced rebuild for hours, and warns at 10 minutes with its last progress.</item>
    /// <item>The cross-reference pass boxes itself at 8 minutes soft / 10 hard, so its runner
    /// deadline sits just above that and is never the thing that ends a healthy run.</item>
    /// </list>
    /// </summary>
    public IReadOnlyDictionary<JobKind, JobKindPolicy> Kinds { get; init; } = new Dictionary<JobKind, JobKindPolicy>
    {
        [JobKind.FullIngest] = new(TimeSpan.FromMinutes(20), DeadlineAction.Yield),
        [JobKind.DeltaSync] = new(TimeSpan.FromMinutes(90), DeadlineAction.Retry),
        [JobKind.FullReconcile] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.HydrateCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield, StopsEarlyOnCancellation: true),
        [JobKind.EnrichCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield, StopsEarlyOnCancellation: true),
        [JobKind.Train] = new(TimeSpan.FromMinutes(180), DeadlineAction.Retry),
        [JobKind.Evaluate] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.BuildFeed] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry, SlowAfter: TimeSpan.FromMinutes(10)),
        [JobKind.RefreshAvailability] = new(TimeSpan.FromMinutes(20), DeadlineAction.Yield, StopsEarlyOnCancellation: true),
        [JobKind.BuildDigest] = new(TimeSpan.FromMinutes(10), DeadlineAction.Retry),
        [JobKind.BuildTasteNarrative] = new(TimeSpan.FromMinutes(10), DeadlineAction.Retry),
        [JobKind.PersonaliseTitles] = new(TimeSpan.FromMinutes(30), DeadlineAction.Yield),
        [JobKind.SeedCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield, StopsEarlyOnCancellation: true),
        [JobKind.CrossReference] = new(TimeSpan.FromMinutes(12), DeadlineAction.Yield),
    };

    /// <summary>The policy for a kind; a kind added without one gets the most conservative box.</summary>
    public JobKindPolicy PolicyFor(JobKind kind) =>
        Kinds.TryGetValue(kind, out var policy) ? policy : new JobKindPolicy(TimeSpan.FromMinutes(30), DeadlineAction.Retry);

    public int TotalSlots => GeneralSlots + ReservedSlots;
}
