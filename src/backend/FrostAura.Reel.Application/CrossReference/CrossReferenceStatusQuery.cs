using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// What the home band is allowed to say about the cross-reference pass, in numbers only.
///
/// <para><b>Every field is counted, never estimated.</b> The band's whole licence to exist is that
/// it quotes real figures — "cross-referenced 1,840 titles against your history" is worth reading
/// precisely because it is not a progress bar's guess. <see cref="Judged"/> and <see cref="Total"/>
/// are two counts over the same key, so a running pass's "640 of 2,242" is arithmetic rather than a
/// percentage rendered back into a count.</para>
///
/// <para><b>Progress is derived from the verdict table, not from the job row.</b> The job's
/// persisted message is a sentence, and parsing a number back out of a sentence is how two surfaces
/// start disagreeing. The table is also correct across a crash, a budget stop and a partial batch,
/// which the job row is not.</para>
/// </summary>
/// <param name="Running">A pass is queued or in flight for this account right now.</param>
/// <param name="Judged">Distinct titles carrying a verdict at the current fingerprint and prompt generation.</param>
/// <param name="Total">Judgeable titles in the pool — the denominator the counter runs against.</param>
/// <param name="Kept">Of the judged, the ones standing as a Keep (confirmed or provisional).</param>
/// <param name="RuledOut">Of the judged, the ones standing as an Out. Recorded, not acted on.</param>
/// <param name="Unsure">Of the judged, the ones it looked at and could not call.</param>
/// <param name="NoSignal">Eligible, scored titles with neither an embedding nor attributes — unjudgeable.</param>
/// <param name="Gating">
/// The newest pooled backtest's decision, or <c>unmeasured</c>. It is what decides which sentence
/// the band shows, so a band can never promise a demotion the measurement has not earned.
/// </param>
public sealed record CrossReferenceStatus(
    bool Running,
    int Judged,
    int Total,
    int Kept,
    int RuledOut,
    int Unsure,
    int NoSignal,
    string Gating);

/// <inheritdoc cref="CrossReferenceStatus"/>
public sealed class CrossReferenceStatusQuery(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    CrossReferenceService verdicts,
    ICrossReferenceJudge judge)
{
    /// <summary>What the completion frame and the band call a gating decision nobody has measured.</summary>
    public const string Unmeasured = "unmeasured";

    /// <summary>
    /// The status, or null when there is nothing honest to say — no active artifact, or a pool
    /// nothing has ever judged and nothing is judging. The endpoint answers 204 on a null and the
    /// band renders nothing at all, which is the correct output for "no news".
    /// </summary>
    public async Task<CrossReferenceStatus?> GetAsync(Guid accountId, CancellationToken ct)
    {
        var running = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId
                && j.Kind == JobKind.CrossReference
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
            ct);

        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } artifact)
        {
            return null;
        }

        var pool = await CrossReferencePool.Judgeable(db, eligibility, accountId, artifact).ToListAsync(ct);
        var noSignal = await CrossReferencePool.WithoutSignal(db, eligibility, accountId, artifact).CountAsync(ct);

        // The fingerprint the current verdicts were written against — the same one the read path
        // uses, for the same reason: reading at any other key returns an empty map and reads as
        // "nothing judged yet".
        var profileHash = await db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.GeneratedAt)
            .Select(n => n.ProfileHash)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var factors = await verdicts.VerdictFactorsAsync(
            accountId, pool, profileHash, judge.PromptVersion, ct);

        var kept = factors.Values.Count(f =>
            f.Verdict is AggregateVerdict.ConfirmedKeep or AggregateVerdict.ProvisionalKeep);
        var ruledOut = factors.Values.Count(f => f.Verdict == AggregateVerdict.Out);
        var unsure = factors.Values.Count(f => f.Verdict == AggregateVerdict.Unsure);

        if (factors.Count == 0 && !running)
        {
            return null;
        }

        return new CrossReferenceStatus(
            running,
            factors.Count,
            pool.Count,
            kept,
            ruledOut,
            unsure,
            noSignal,
            await LatestGatingDecisionAsync(accountId, ct));
    }

    /// <summary>The newest pooled backtest verdict for this account, or "unmeasured".</summary>
    private async Task<string> LatestGatingDecisionAsync(Guid accountId, CancellationToken ct) =>
        await db.CrossReferenceEvaluations
            .Where(e => e.AccountId == accountId && e.SplitFraction == null)
            .OrderByDescending(e => e.EvaluatedAt)
            .Select(e => e.GatingDecision)
            .FirstOrDefaultAsync(ct) ?? Unmeasured;
}
