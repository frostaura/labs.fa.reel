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
