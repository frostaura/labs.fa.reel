using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Reactive feed freshness. Turns "the user's taste just changed" — a new watch/rating pulled from
/// Trakt, or an in-app rate / not-interested / save — into a fresh feed within seconds by enqueuing
/// a <see cref="JobKind.BuildFeed"/> that REUSES the current Active model artifact. No retrain sits
/// in the hot path (Train stays threshold-gated and chains its own rebuild); reusing the artifact
/// means the rebuild is the seconds-long serving pass, not a minutes-long training pass, yet it
/// still re-scores against fresh taste features (person/genre affinity, embeddings, drift update the
/// instant a rating lands) and drops anything newly seen.
///
/// Coalescing is free: the caller commits the enqueue in the same transaction as the change, and the
/// (AccountId, BuildFeed Pending||Running) guard collapses a rating burst — or a hot delta poll that
/// lands three changed categories at once — into exactly one in-flight build. The build publishes
/// feed-ready, which the client turns into a live reseed, so new picks lazy-load in as sync arrives.
/// </summary>
public sealed class FeedRefreshCoordinator(
    IReelDbContext db,
    ILogger<FeedRefreshCoordinator> logger)
{
    /// <summary>
    /// Requests a reactive rebuild for the account. Adds a BuildFeed job to the tracked context — the
    /// caller's <c>SaveChangesAsync</c> commits it alongside the change that triggered it — when an
    /// Active model exists and no BuildFeed is already queued or running. Returns true iff a job was
    /// added. Best-effort and side-effect-only: it never throws on the "nothing to do" paths and never
    /// calls SaveChanges itself, so it composes inside any endpoint or job transaction.
    /// </summary>
    public async Task<bool> RequestRebuildAsync(Guid accountId, string reason, CancellationToken ct)
    {
        // No model yet → the onboarding Train → Evaluate → BuildFeed chain owns the first feed; a
        // reactive build with no artifact would no-op inside BuildFeedJobHandler anyway.
        var hasArtifact = await db.ModelArtifacts
            .AnyAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active, ct);
        if (!hasArtifact)
        {
            return false;
        }

        // Coalesce: an already Pending build will read the just-committed change when it runs; an
        // already Running one is re-triggered by the next change after it completes. Either way a
        // burst collapses to one build.
        var inFlight = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (inFlight)
        {
            return false;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            // Above the nightly reconcile (2) and its Train (3), below a user-forced sync-now (0), so
            // an interactive rating's rebuild jumps ahead of background maintenance but never preempts
            // an explicit refresh.
            Kind = JobKind.BuildFeed,
            Priority = 1,
            EnqueuedAt = DateTime.UtcNow,
            // Persisted, not just logged: BuildFeedJobHandler reads it to decide Preserve vs Fresh
            // (FeedBuildPolicy) — "delta-sync" must keep tonight's picks, "content filters changed"
            // must re-deal them.
            TriggerReason = reason,
        });
        logger.LogInformation("Reactive feed rebuild enqueued for {AccountId} ({Reason}).", accountId, reason);
        return true;
    }
}
