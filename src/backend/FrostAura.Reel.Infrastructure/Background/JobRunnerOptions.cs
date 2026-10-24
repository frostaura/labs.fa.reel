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
public sealed record JobKindPolicy(TimeSpan Deadline, DeadlineAction OnDeadline);

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

    /// <summary>How long a stopping host waits for running handlers to observe cancellation and requeue themselves.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many deadline slices a <see cref="DeadlineAction.Yield"/> kind gets before a further
    /// overrun counts as a failed attempt. 24 ten-minute slices is four hours of one job's work —
    /// past that, "still going" is a defect to surface, not progress to wait for.
    /// </summary>
    public int MaxDeadlineYields { get; init; } = 24;

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
    /// Every kind's deadline. Resumable kinds (a cursor or their own data is the checkpoint) get a
    /// short slice and yield; kinds that restart from nothing get a generous box and retry. The
    /// cross-reference pass boxes itself at 8 minutes soft / 10 hard, so its runner deadline sits just
    /// above that and is never the thing that ends a healthy run.
    /// </summary>
    public IReadOnlyDictionary<JobKind, JobKindPolicy> Kinds { get; init; } = new Dictionary<JobKind, JobKindPolicy>
    {
        [JobKind.FullIngest] = new(TimeSpan.FromMinutes(20), DeadlineAction.Yield),
        [JobKind.DeltaSync] = new(TimeSpan.FromMinutes(20), DeadlineAction.Retry),
        [JobKind.FullReconcile] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.HydrateCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield),
        [JobKind.EnrichCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield),
        [JobKind.Train] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.Evaluate] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.BuildFeed] = new(TimeSpan.FromMinutes(30), DeadlineAction.Retry),
        [JobKind.RefreshAvailability] = new(TimeSpan.FromMinutes(20), DeadlineAction.Retry),
        [JobKind.BuildDigest] = new(TimeSpan.FromMinutes(10), DeadlineAction.Retry),
        [JobKind.BuildTasteNarrative] = new(TimeSpan.FromMinutes(10), DeadlineAction.Retry),
        [JobKind.PersonaliseTitles] = new(TimeSpan.FromMinutes(10), DeadlineAction.Retry),
        [JobKind.SeedCatalog] = new(TimeSpan.FromMinutes(10), DeadlineAction.Yield),
        [JobKind.CrossReference] = new(TimeSpan.FromMinutes(12), DeadlineAction.Yield),
    };

    /// <summary>The policy for a kind; a kind added without one gets the most conservative box.</summary>
    public JobKindPolicy PolicyFor(JobKind kind) =>
        Kinds.TryGetValue(kind, out var policy) ? policy : new JobKindPolicy(TimeSpan.FromMinutes(30), DeadlineAction.Retry);

    public int TotalSlots => GeneralSlots + ReservedSlots;
}
