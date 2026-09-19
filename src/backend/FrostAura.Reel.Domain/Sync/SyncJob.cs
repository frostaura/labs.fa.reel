using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Sync;

public enum JobKind
{
    FullIngest,
    DeltaSync,
    FullReconcile,
    HydrateCatalog,
    EnrichCatalog,
    Train,
    Evaluate,
    BuildFeed,
    RefreshAvailability,
    BuildDigest,

    /// <summary>Writes the account's second-person taste narrative. Weekly, and whenever its profile hash moves.</summary>
    BuildTasteNarrative,

    /// <summary>
    /// Writes "through your lens" copy for the titles that carry the day. Priority 3, enqueued
    /// AFTER a Fresh build's snapshot is written — never inside the build, whose why pass is
    /// already time-boxed at 20s soft / 60s hard and would turn into a timeout.
    /// </summary>
    PersonaliseTitles,

    /// <summary>
    /// The one-off catalogue bulk seed: walks TMDB discover by <c>vote_count.desc</c>, partitioned
    /// by release year, upserting summary rows for the whole modern high-signal corpus. Global
    /// (null account) — the rows belong to every tenant — and priority 4, BELOW everything else,
    /// because it is supply acquisition and nothing waits on it. Resumable from
    /// <see cref="SyncJob.CursorJson"/> and re-enqueues itself while the plan is unfinished.
    /// </summary>
    SeedCatalog,

    /// <summary>
    /// The cross-reference pass: judges the eligible pool in batches against the k nearest titles
    /// the viewer has actually rated, and persists a closed-enum verdict per candidate.
    ///
    /// <para>Priority 2 — above <see cref="PersonaliseTitles"/> (3), because lens copy for a card
    /// the cross-reference is about to demote is waste, and below a reactive
    /// <see cref="BuildFeed"/> (1), because a rating that just landed reshapes the page first.</para>
    ///
    /// <para><b>It never re-enqueues itself.</b> The partial unique index on (AccountId, Kind)
    /// WHERE Pending/Running guarantees a 23505 for a per-account kind, and the absorbing save at
    /// the enqueue sites would swallow the continuation silently. A run boxes itself at 8 minutes
    /// soft / 10 hard, saves per batch, and stops; the enqueue point in <c>BuildFeedJobHandler</c>
    /// picks the remainder up on the next build. <see cref="SyncJob.CursorJson"/> carries
    /// {round, judgedSoFar, ruledOutSoFar} for COPY only — the resume state is the verdict table's
    /// own sweep index, which is correct after a crash, a budget stop or a partial batch.</para>
    /// </summary>
    CrossReference,
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Durable job queue row. JobRunnerService claims Pending rows with FOR UPDATE SKIP LOCKED,
/// heartbeats while running, and reclaims rows whose heartbeat went stale (crash recovery —
/// handlers resume from <see cref="CursorJson"/>). A partial unique index forbids duplicate
/// in-flight jobs per (account, kind).
/// </summary>
public class SyncJob
{
    public Guid Id { get; set; }

    /// <summary>Null for global jobs (catalog refresh); per-account otherwise.</summary>
    public Guid? AccountId { get; set; }

    public JobKind Kind { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>Lower runs first within the same enqueue window.</summary>
    public short Priority { get; set; }

    public DateTime EnqueuedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? HeartbeatAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Resumability checkpoint (jsonb), e.g. {"phase":"shows","page":3}.</summary>
    public string? CursorJson { get; set; }

    public decimal? ProgressPct { get; set; }

    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Why this job was enqueued — "delta-sync", "manual-refresh", "in-app rating", … Null for jobs
    /// that predate the column or have no interesting provenance. BuildFeed reads it to decide
    /// between preserving tonight's picks and dealing fresh ones (see FeedBuildPolicy): a background
    /// poll must not churn the page, while an explicit user action must visibly change it.
    /// </summary>
    public string? TriggerReason { get; set; }

    public string? Error { get; set; }
}
