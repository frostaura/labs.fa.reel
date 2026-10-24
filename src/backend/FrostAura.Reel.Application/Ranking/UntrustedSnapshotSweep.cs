using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The deploy-time purge (WS-5 task 3): every Active snapshot that carries something this build no
/// longer serves gets one forced Fresh build. "Something" is exactly two things, both read from the
/// stored rows rather than inferred:
/// <list type="bullet">
/// <item>a why-line <see cref="FeedWhyGuard.IsServable"/> refuses — the stub sentence a failed
/// rerank wrote and the cache and Preserve path then carried for days, the retired era template,
/// the "A + vowel" defect;</item>
/// <item>a because-you-loved card that shares no person, keyword or theme with its anchor — a card
/// dealt before <see cref="AnchorRowGate"/> existed.</item>
/// </list>
///
/// <para><b>Why a forced build and not just the serve-time guard.</b> The feed endpoint already
/// hides a refused line, but it can only hide — the card then shows no reason at all, and a
/// Preserve rebuild never re-reads a kept card's caption (its no-op suppression returns before the
/// why pass). Only a Fresh build re-captions and re-gates the page.</para>
///
/// <para><b>Idempotent by content, not by a marker.</b> A snapshot the purge rebuilt passes both
/// checks, so the next start-up finds nothing to do; there is no "has this deploy run" flag to lose
/// or to retain past its usefulness. The similarity half of the gate is deliberately NOT re-checked
/// here: the neighbour floor moves whenever the catalogue grows near an anchor, and re-gating on
/// it would re-deal tonight's page on any restart. The shared-evidence half is catalogue fact and
/// does not drift.</para>
/// </summary>
public static class UntrustedSnapshotSweep
{
    /// <summary>Accounts whose Active snapshot fails either check, with the reason, for the log.</summary>
    public sealed record Finding(Guid AccountId, int RefusedWhys, int UnexplainedAnchoredCards);

    /// <summary>Reads every Active snapshot (the caller's context must see every tenant).</summary>
    public static async Task<IReadOnlyList<Finding>> FindAsync(IReelDbContext db, CancellationToken ct)
    {
        var items = await db.FeedItems
            .Where(i => db.FeedSnapshots.Any(s => s.Id == i.FeedSnapshotId && s.Status == SnapshotStatus.Active))
            .Select(i => new { i.AccountId, i.Row, i.AnchorTitleId, i.TitleId, i.WhyThisSentence })
            .ToListAsync(ct);

        var anchored = items
            .Where(i => i.Row == FeedRowKind.BecauseYouLoved && i.AnchorTitleId != null)
            .Select(i => (i.AnchorTitleId!.Value, i.TitleId))
            .Distinct()
            .ToList();
        var evidence = await AnchorRowGate.EvidenceAsync(db, anchored, ct);

        return items
            .GroupBy(i => i.AccountId)
            .Select(g => new Finding(
                g.Key,
                // An empty caption is the generic-once guard's honest blank, not a defect.
                g.Count(i => !string.IsNullOrEmpty(i.WhyThisSentence) && !FeedWhyGuard.IsServable(i.WhyThisSentence)),
                g.Count(i => i.Row == FeedRowKind.BecauseYouLoved && i.AnchorTitleId != null
                    && evidence.GetValueOrDefault((i.AnchorTitleId.Value, i.TitleId)) is not { Count: > 0 })))
            .Where(f => f.RefusedWhys > 0 || f.UnexplainedAnchoredCards > 0)
            .ToList();
    }

    /// <summary>
    /// Stages one Fresh BuildFeed (<see cref="FeedBuildPolicy.TrustPurgeReason"/>) per finding — a new
    /// job, or the account's pending one re-aimed. Saves nothing; returns how many were staged.
    /// </summary>
    public static async Task<int> StageRebuildsAsync(
        IReelDbContext db, IReadOnlyList<Finding> findings, DateTime now, CancellationToken ct)
    {
        var staged = 0;
        foreach (var finding in findings)
        {
            var queued = await db.SyncJobs
                .Where(j => j.AccountId == finding.AccountId && j.Kind == JobKind.BuildFeed
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
                .ToListAsync(ct);
            if (queued.Count > 0)
            {
                // A build already waiting would land as Preserve and keep the very captions this
                // purge exists to replace, so a PENDING one is re-aimed at the purge instead of
                // being joined by a second build. A running one is left alone; the next start-up
                // re-checks whatever it wrote.
                foreach (var pending in queued.Where(j => j.Status == JobStatus.Pending))
                {
                    pending.TriggerReason = FeedBuildPolicy.TrustPurgeReason;
                    staged++;
                }

                continue;
            }

            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = finding.AccountId,
                Kind = JobKind.BuildFeed,
                Priority = 1,
                EnqueuedAt = now,
                TriggerReason = FeedBuildPolicy.TrustPurgeReason,
            });
            staged++;
        }

        return staged;
    }
}
