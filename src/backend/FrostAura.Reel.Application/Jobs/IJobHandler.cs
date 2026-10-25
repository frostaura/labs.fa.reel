using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// One job kind's executor. Resolved per execution in a fresh DI scope with the job's account
/// pinned on IAccountContext. Contract:
///   • Idempotent — upsert by natural keys; a replayed job must be harmless.
///   • Resumable — persist checkpoints to <see cref="SyncJob.CursorJson"/> alongside data
///     writes (same SaveChanges), so a crash-reclaimed job continues, never restarts.
///   • Successors — enqueue follow-up SyncJobs inside the final SaveChanges.
/// The runner owns Status transitions and heartbeats; handlers own everything else.
/// </summary>
public interface IJobHandler
{
    JobKind Kind { get; }

    Task ExecuteAsync(SyncJob job, CancellationToken ct);
}

/// <summary>
/// Thrown by a handler whose failure is deterministic — a refused key, a spent budget, a prompt the
/// model answers with nothing every time. The runner fails the job on this attempt instead of
/// spending its remaining attempts (and their calls) on the same answer; the failure reaches the
/// status API's <c>lastFailures</c> on the next poll. The message is persisted as the job's error,
/// so it should carry what <c>JobFailureReason.Classify</c> reads (a status code, "budget", …).
/// </summary>
public sealed class NonRetryableJobException(string message, Exception? innerException = null)
    : Exception(message, innerException);
