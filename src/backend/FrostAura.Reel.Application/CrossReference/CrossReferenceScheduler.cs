using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The one place a <see cref="JobKind.CrossReference"/> run is queued from — the feed build, on
/// both its Fresh and its Preserve exit.
///
/// <para><b>Three brakes, and not one of them is a clock.</b> A run is queued only when nothing is
/// already in flight for the account, the day's ceiling still has headroom, and the pool actually
/// contains a title with no verdict at the current (fingerprint, prompt version). A timer would
/// answer none of those questions and would spend money on the nights there is nothing to do.</para>
///
/// <para><b>It queues from the feed build rather than from delta sync</b> because delta sync never
/// computes the taste fingerprint, and the fingerprint is what the verdict key is built on. The
/// build has it in hand already.</para>
///
/// <para><b>It adds to the tracked context and never saves.</b> The caller commits it alongside the
/// build's own writes with <c>SaveAbsorbingRebuildRaceAsync</c>, exactly as every other enqueue in
/// the build does, so a lost race against the partial unique index costs the coalesced job and not
/// the build's stat row.</para>
/// </summary>
public sealed class CrossReferenceScheduler(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    CrossReferenceService verdicts,
    CrossReferenceBudget budget,
    ICrossReferenceJudge judge,
    ILogger<CrossReferenceScheduler> logger)
{
    /// <summary>
    /// The smallest unjudged set worth a run, once the account has a first pass at this key. A
    /// batch is sixty; ten is a sixth of one call's capacity and a few builds' worth of new titles.
    /// </summary>
    public const int MinUnjudgedToQueue = 10;

    /// <summary>
    /// Above <see cref="JobKind.PersonaliseTitles"/> (3) — lens copy for a card the cross-reference
    /// is about to demote is waste — and below a reactive <see cref="JobKind.BuildFeed"/> (1),
    /// because a rating that just landed reshapes the page before anything reasons about it.
    /// </summary>
    public const short Priority = 2;

    /// <summary>
    /// Queues a run when there is judgeable work at <paramref name="profileHash"/>. Returns true
    /// iff a job was added to the tracked context.
    /// </summary>
    public async Task<bool> EnqueueIfWorkPendingAsync(
        Guid accountId, string profileHash, DateTime now, CancellationToken ct, string? buildTriggerReason = null)
    {
        // Brake 0: the build this is running after was itself asked for by a finished pass. The
        // titles that build brought in wait for the next build the account earns on its own —
        // otherwise pass → rebuild → pass is a loop with no natural end, because every rebuild
        // scores a few more titles. This is the deterministic half of the anti-ping-pong; the
        // judged-count floor in the handler is the other.
        if (buildTriggerReason == Jobs.CrossReferenceJobHandler.CrossReferenceRebuildReason)
        {
            logger.LogInformation(
                "Cross-reference not queued for {AccountId}: this build was itself asked for by the last pass.", accountId);
            return false;
        }

        if (!judge.IsAvailable)
        {
            logger.LogInformation("Cross-reference not queued for {AccountId}: no judge is configured.", accountId);
            return false;
        }

        // Brake 1, and the cheapest: the partial unique index would refuse a second in-flight run
        // for this account anyway. Checking first avoids provoking the 23505 rather than relying on
        // it being absorbed.
        var inFlight = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == JobKind.CrossReference
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (inFlight)
        {
            logger.LogInformation("Cross-reference not queued for {AccountId}: a pass is already in flight.", accountId);
            return false;
        }

        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } artifact)
        {
            logger.LogInformation("Cross-reference not queued for {AccountId}: no readable Active model artifact.", accountId);
            return false;
        }

        // Brake 2: the daily ceiling. A run queued with no allowance would claim a runner slot, hold
        // the process-wide gate, and stop on its first batch — the ceiling is cheaper to read here.
        var allowance = await budget.AllowanceAsync(accountId, ct);
        if (!allowance.HasHeadroom)
        {
            logger.LogInformation(
                "Cross-reference not queued for {AccountId}: today's ceiling is spent ({Allowance}).",
                accountId, allowance.Describe());
            return false;
        }

        // Brake 3: is there anything to judge at THIS key? A fingerprint move makes every verdict a
        // different key, which is what re-judges a moved profile without ever deleting a row.
        // The pool is the scored shortlist — a few hundred ids — so it is read once here and both
        // brakes below count against the same list.
        var pool = await CrossReferencePool.Judgeable(db, eligibility, accountId, artifact).ToListAsync(ct);
        var unjudged = await verdicts.UnjudgedAsync(
            accountId, pool, profileHash, judge.PromptVersion, CrossReferenceRound.First, ct);
        if (unjudged.Count == 0)
        {
            logger.LogInformation(
                "Cross-reference not queued for {AccountId}: nothing unjudged at fingerprint {ProfileHash} (prompt v{Version}).",
                accountId, profileHash, judge.PromptVersion);
            return false;
        }

        // Brake 4: a batch's worth, or a first pass. Every build brings a few newly scored titles
        // into the pool; a run per build over three of them is a provider call per build for
        // almost nothing, and it was one half of the build ↔ cross-reference ping-pong. A key
        // nothing has been judged at is a first pass and goes regardless of size.
        var poolSize = pool.Count;
        if (unjudged.Count < MinUnjudgedToQueue && unjudged.Count < poolSize)
        {
            logger.LogInformation(
                "Cross-reference not queued for {AccountId}: {Unjudged} unjudged of {Pool}, under the {Floor} that earns a pass.",
                accountId, unjudged.Count, poolSize, MinUnjudgedToQueue);
            return false;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.CrossReference,
            Priority = Priority,
            EnqueuedAt = now,
        });
        logger.LogInformation(
            "Cross-reference queued for {AccountId} at fingerprint {ProfileHash} (prompt v{Version}); "
            + "today stands at {Allowance}.",
            accountId, profileHash, judge.PromptVersion, allowance.Describe());
        return true;
    }
}
