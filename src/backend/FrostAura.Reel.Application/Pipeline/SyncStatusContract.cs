using System.Text.Json.Serialization;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Application.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// GET /api/sync/status — contract v2.
//
// Shape-compatible with v1: every v1 field keeps its name and meaning, except that `activeJob` now
// only ever holds user-visible work (or the named cross-reference pass), and `pipelineStage` can
// read `NotEnoughRatings`. Everything else is additive. Enums travel as strings; a client must map
// a value it does not know to its own fallback, because new values are appended without notice.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Why the account is not in a healthy, progressing state. Null on the wire when it is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DegradedReason>))]
public enum DegradedReason
{
    /// <summary>The Trakt link is revoked (or was never made). The fix is to reconnect Trakt.</summary>
    TraktRevoked,

    /// <summary>The Trakt token refresh failed. The fix is to reconnect Trakt.</summary>
    TraktRefreshFailed,

    /// <summary>Writes to Trakt (ratings, lists) exhausted their retries and are dead-lettered.</summary>
    TraktWritesDeadLettered,

    /// <summary>Too few usable ratings to train a model; see <c>notEnoughRatings</c> for the counts.</summary>
    NotEnoughRatings,

    /// <summary>The model provider refuses every call (<c>providerHealth.state</c> is <c>Down</c>).</summary>
    ProviderDown,

    /// <summary>The pipeline is parked as Degraded and none of the causes above applies.</summary>
    PipelineFailed,

    /// <summary>
    /// The account's pipeline stage or Trakt connection holds a value a NEWER version of Reel wrote
    /// and this one cannot read (a rollback). Nothing is known to be broken and nothing is asked of
    /// the user: this build reads the value as its inert fallback (no Trakt calls on an unreadable
    /// connection), so <c>pipelineStage</c> and <c>connectionStatus</c> carry that fallback, and this
    /// reason says not to act on them. A client shows no reconnect or failure copy for it.
    /// </summary>
    UnrecognisedState,
}

/// <summary>One job as the header sees it.</summary>
/// <param name="Id">The SyncJob id, so a client can tell a new run of a kind from the previous one.</param>
/// <param name="Kind">The backend job kind.</param>
/// <param name="SseKind">The short kind its SSE frames carry; apply a live frame only when it matches.</param>
/// <param name="Visibility">How the header treats it — computed by <see cref="JobVisibilityPolicy"/>.</param>
/// <param name="Status">Queue status.</param>
/// <param name="ProgressPct">
/// Null whenever no honest percentage exists: a queued job, a job waiting for a retry, and always
/// for the <see cref="JobVisibility.NamedPass"/> (it has no denominator).
/// </param>
/// <param name="ProgressMessage">The handler's own progress line; null whenever <c>progressPct</c> is cleared for a retry.</param>
/// <param name="EnqueuedAt">When it was queued.</param>
/// <param name="StartedAt">When the first attempt was claimed.</param>
/// <param name="CompletedAt">When it reached a terminal state.</param>
/// <param name="Attempt">Attempts claimed so far (0 while queued for the first time).</param>
/// <param name="MaxAttempts">The runner's attempt cap.</param>
/// <param name="Retrying">True once an attempt has failed and the job is queued or running again.</param>
/// <param name="RetryReasonCode">When retrying, why the last attempt failed — a <see cref="JobFailureReason"/> code.</param>
public sealed record SyncJobSummary(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobKind>))] JobKind Kind,
    string SseKind,
    JobVisibility Visibility,
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))] JobStatus Status,
    decimal? ProgressPct,
    string? ProgressMessage,
    DateTime EnqueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int Attempt,
    int MaxAttempts,
    bool Retrying,
    string? RetryReasonCode);

/// <summary>
/// The latest unrecovered failure of one job kind: a kind appears here when its most recent
/// terminal run inside <see cref="SyncStatusQuery.FailureWindow"/> failed, and drops out as soon as
/// a run of that kind succeeds.
/// </summary>
/// <param name="Kind">The job kind that failed.</param>
/// <param name="SseKind">Its SSE short kind (the key for per-kind copy).</param>
/// <param name="Visibility">Its header visibility at the time of reading.</param>
/// <param name="FailedAt">When the latest failed run ended.</param>
/// <param name="ConsecutiveFailures">Failed runs since the last success of this kind (inside the window).</param>
/// <param name="Attempts">Attempts the latest failed run made.</param>
/// <param name="ReasonCode">A <see cref="JobFailureReason"/> code — never free text.</param>
public sealed record JobFailure(
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobKind>))] JobKind Kind,
    string SseKind,
    JobVisibility Visibility,
    DateTime FailedAt,
    int ConsecutiveFailures,
    int Attempts,
    string ReasonCode);

/// <summary>Progress toward the next drift retrain (unchanged from v1).</summary>
public sealed record RetrainProgress(
    int MinNewRatings,
    int NewRatingsSinceTrain,
    bool HasActiveModel,
    DateTime? LastTrainedAt);

/// <summary>The training floor, when the account is below it.</summary>
/// <param name="UsableRatings">Distinct rated movies and shows (seasons and episodes do not train).</param>
/// <param name="Required">The trainer's floor.</param>
public sealed record RatingsFloor(int UsableRatings, int Required);

/// <summary>
/// The v2 status payload. <c>alerts</c> (additive) carries the active operator alerts — job failures
/// over the hourly threshold, a provider refusing calls — for Founder-tier accounts, and is empty
/// for everyone else: they are about the system, not about the caller's own pipeline.
/// </summary>
public sealed record SyncStatusResponse(
    int ContractVersion,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PipelineStage>))] PipelineStage PipelineStage,
    RetrainProgress Retrain,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ConnectionStatus>))] ConnectionStatus ConnectionStatus,
    DateTime? LastDeltaSyncAt,
    DateTime? LastFullReconcileAt,
    SyncJobSummary? ActiveJob,
    IReadOnlyList<SyncJobSummary> RecentJobs,
    int OutboxPending,
    int OutboxDeadLetters,
    DateTime? LastListSyncAt,
    string? OutboxLastError,
    IReadOnlyList<JobFailure> LastFailures,
    ProviderHealthSnapshot ProviderHealth,
    DegradedReason? DegradedReason,
    RatingsFloor? NotEnoughRatings,
    IReadOnlyList<OperatorAlert> Alerts)
{
    public const int CurrentContractVersion = 2;
}
