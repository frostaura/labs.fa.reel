using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The one door the serving paths open onto the verdict table: "for these titles, what does the
/// cross-reference currently say?"
///
/// <para><b>It exists so the three serve paths cannot disagree.</b> The feed build, the live refill
/// and <c>/feed/more</c> all need the same answer, and each of them would otherwise have to know
/// three separate things — which prompt generation is current, which taste fingerprint counts as
/// fresh, and what a missing row means. Getting any one of those wrong is silent: the feed would
/// simply order as though nothing had ever been judged, and no test that did not look for it would
/// notice.</para>
///
/// <para><b>The prompt version comes from the judge, never from a literal.</b> It is a persisted
/// key part; a serving path reading verdicts at a version the job is no longer writing would return
/// an empty map for ever and look exactly like "nothing judged yet".</para>
///
/// <para><b>Everything is keep-only.</b> See <see cref="VerdictAggregator"/> for the measurements:
/// an <c>Out</c> comes back with a factor of exactly 1.0 and the hero allowed. The verdict is still
/// returned, because the exposure log records it and the track record counts it — the product
/// observes the opinion without acting on it.</para>
/// </summary>
public sealed class CrossReferenceFactors(
    IReelDbContext db,
    CrossReferenceService verdicts,
    ICrossReferenceJudge judge)
{
    /// <summary>
    /// The factors for <paramref name="titleIds"/> at a fingerprint the caller already holds — the
    /// feed build's path, which computes the fingerprint anyway to key its own enqueue.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, VerdictFactor>> ForAsync(
        Guid accountId, IReadOnlyList<Guid> titleIds, string profileHash, CancellationToken ct) =>
        verdicts.VerdictFactorsAsync(accountId, titleIds, profileHash, judge.PromptVersion, ct);

    /// <summary>
    /// The factors for <paramref name="titleIds"/> on a path with no fingerprint in hand — the two
    /// read-path serves, which must not pay for a taste build to log a column.
    ///
    /// <para>The fingerprint is read off the account's newest taste narrative rather than rebuilt.
    /// That row is written by the same feed build that queues the cross-reference pass, so it IS
    /// the fingerprint the current verdicts were judged against; rebuilding it here would cost a
    /// full taste-state read on a GET to arrive at the same string. An account with no narrative
    /// yet has no verdicts either, so the empty hash simply marks everything stale — which is the
    /// conservative direction, and it only ever halves a keep's lift.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, VerdictFactor>> ForAsync(
        Guid accountId, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<Guid, VerdictFactor>();
        }

        // AccountId is written out explicitly as well as being covered by the global filter: this
        // is also reachable from an unpinned context, where the filter admits every tenant.
        var profileHash = await db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.GeneratedAt)
            .Select(n => n.ProfileHash)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        return await ForAsync(accountId, titleIds, profileHash, ct);
    }
}
