using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The deploy-time purge (WS-5 task 3): every Active snapshot that carries something this build no
/// longer serves gets one forced Fresh build. Every check reads what the snapshot's rows STORE —
/// nothing is re-derived from catalogue facts that move:
/// <list type="bullet">
/// <item>a row with no stored why-source — written before provenance was stored, i.e. by an image
/// whose fallback and stub captions this one cannot tell apart from real ones;</item>
/// <item>a why-line <see cref="FeedWhyGuard.IsServable"/> refuses — the stub sentence a failed
/// rerank wrote, the retired era template, the "A + vowel" defect, a hedge against the pick, the
/// prompt's "the anchor" jargon;</item>
/// <item>a because-you-loved card with no stored shared evidence — a card dealt before
/// <see cref="AnchorRowGate"/> existed;</item>
/// <item>a caption on a non-anchored card whose model pass failed (stored source Fallback) — the
/// soft templater copy that now shows as no line at all.</item>
/// </list>
///
/// <para><b>Why a forced build and not just the serve-time guard.</b> The feed endpoint already
/// hides a refused line, but it can only hide — the card then shows no reason at all, and a
/// Preserve rebuild never re-reads a kept card's caption (its no-op suppression returns before the
/// why pass). Only a Fresh build re-captions and re-gates the page.</para>
///
/// <para><b>Idempotent by content, and drift-free.</b> A snapshot written by this image stores its
/// why-source on every row and its admitting evidence on every anchored row, and every line it
/// writes has passed the guard, so the next start-up finds nothing to do; there is no "has this
/// deploy run" flag to lose. The earlier version re-read each anchored card's evidence from the
/// catalogue, and a theme re-extraction or a keyword refresh could strip a kept card of it and
/// force another paid build on every restart. Stored evidence cannot drift.</para>
/// </summary>
public static class UntrustedSnapshotSweep
{
    /// <summary>
    /// Is the purge enabled on this host? <c>Background:TrustPurge</c> when set; otherwise it
    /// follows <c>Background:NightlyReconcile</c> (default on). A slot or eval host started on a copy
    /// of an account's data turns its background services off, and with them the purge — before
    /// this it enqueued a paid Fresh build (and the jobs it chains) on every such start.
    /// </summary>
    public static bool Enabled(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration["Background:TrustPurge"] is { Length: > 0 } explicitValue
            ? bool.TryParse(explicitValue, out var on) && on
            : !bool.TryParse(configuration["Background:NightlyReconcile"], out var reconcile) || reconcile;

    /// <summary>Accounts whose Active snapshot fails any check, with the counts, for the log.</summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="RefusedWhys">Persisted lines the serve-time guard refuses.</param>
    /// <param name="UnexplainedAnchoredCards">Because-you-loved cards with no stored evidence.</param>
    /// <param name="UnattributedCards">Rows with no stored why-source (written before it was stored).</param>
    /// <param name="FallbackCaptions">Non-anchored cards captioned although their model pass failed.</param>
    public sealed record Finding(
        Guid AccountId, int RefusedWhys, int UnexplainedAnchoredCards, int UnattributedCards = 0, int FallbackCaptions = 0);

    /// <summary>Reads every Active snapshot (the caller's context must see every tenant).</summary>
    public static async Task<IReadOnlyList<Finding>> FindAsync(IReelDbContext db, CancellationToken ct)
    {
        var items = await db.FeedItems
            .Where(i => db.FeedSnapshots.Any(s => s.Id == i.FeedSnapshotId && s.Status == SnapshotStatus.Active))
            .Select(i => new { i.AccountId, i.Row, i.AnchorTitleId, i.WhyThisSentence, i.WhySource, i.EvidenceJson })
            .ToListAsync(ct);

        return items
            .GroupBy(i => i.AccountId)
            .Select(g => new Finding(
                g.Key,
                // An empty caption is the generic-once guard's honest blank, not a defect.
                g.Count(i => !string.IsNullOrEmpty(i.WhyThisSentence) && !FeedWhyGuard.IsServable(i.WhyThisSentence)),
                g.Count(i => i.Row == FeedRowKind.BecauseYouLoved && i.AnchorTitleId != null
                    && AnchorRowGate.Deserialize(i.EvidenceJson) is not { Count: > 0 }),
                g.Count(i => string.IsNullOrWhiteSpace(i.WhySource)),
                g.Count(i => i.AnchorTitleId == null && !string.IsNullOrEmpty(i.WhyThisSentence)
                    && ExposureWhySource.Parse(i.WhySource) == ExposureWhySource.Fallback)))
            .Where(f => f.RefusedWhys > 0 || f.UnexplainedAnchoredCards > 0 || f.UnattributedCards > 0 || f.FallbackCaptions > 0)
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
