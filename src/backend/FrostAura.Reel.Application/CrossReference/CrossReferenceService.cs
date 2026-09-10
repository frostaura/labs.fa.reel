using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// Reads and writes cross-reference verdicts — the one place the resume anti-join, the stale
/// ladder, the duplicate absorber and the factor rollup live, so a caller cannot accidentally skip
/// one.
///
/// <para><b>There is no cursor, and that is the design.</b> The job's resume state is the
/// <c>IX_…_Sweep</c> index: "which eligible titles have no row at the current key" is a COUNT
/// anti-join, and it is correct after a crash, after a budget stop, after a partial batch and after
/// a concurrent run, none of which a stored cursor survives. A cursor would additionally have to be
/// invalidated whenever the pool changed, and the pool changes on every catalogue sweep.</para>
///
/// <para><b>A fingerprint move demotes, it never deletes.</b> Rows at a superseded ProfileHash are
/// stale evidence at half strength and are re-judged FIRST — the lens's Fresh → Stale → None ladder,
/// for the same reason: re-judging ten thousand titles on every fingerprint step would cost more
/// than the whole feature is worth.</para>
/// </summary>
public sealed class CrossReferenceService(
    IReelDbContext db,
    ILogger<CrossReferenceService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Of <paramref name="candidateTitleIds"/>, the ones with NO verdict at this exact key — the
    /// warm-skip anti-join, and the job's entire resume state.
    ///
    /// <para>Returned in the caller's own order, so a caller that handed in a deterministic order
    /// gets a deterministic work list back and two runs over an unchanged pool judge the same
    /// titles in the same batches.</para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> UnjudgedAsync(
        Guid accountId, IReadOnlyList<Guid> candidateTitleIds, string profileHash, int promptVersion,
        CrossReferenceRound round, CancellationToken ct)
    {
        if (candidateTitleIds.Count == 0)
        {
            return [];
        }

        var judged = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId
                && v.ProfileHash == profileHash
                && v.PromptVersion == promptVersion
                && v.Round == round
                && candidateTitleIds.Contains(v.TitleId))
            .Select(v => v.TitleId)
            .ToListAsync(ct);

        var warm = judged.ToHashSet();
        return candidateTitleIds.Where(id => !warm.Contains(id)).ToList();
    }

    /// <summary>
    /// How many of <paramref name="candidateTitleIds"/> still need judging. The COUNT the enqueue
    /// point asks before queuing a job at all — one of the three brakes on the job, and the only
    /// one that can answer "there is nothing to do" without loading a work list.
    /// </summary>
    public async Task<int> UnjudgedCountAsync(
        Guid accountId, IReadOnlyList<Guid> candidateTitleIds, string profileHash, int promptVersion,
        CrossReferenceRound round, CancellationToken ct)
    {
        if (candidateTitleIds.Count == 0)
        {
            return 0;
        }

        var judged = await db.TitleCrossReferenceVerdicts
            .CountAsync(
                v => v.AccountId == accountId
                    && v.ProfileHash == profileHash
                    && v.PromptVersion == promptVersion
                    && v.Round == round
                    && candidateTitleIds.Contains(v.TitleId),
                ct);
        return candidateTitleIds.Distinct().Count() - judged;
    }

    /// <summary>
    /// Does <paramref name="candidateTitleIds"/> contain anything with no verdict at this key? The
    /// question the enqueue gate asks before queuing a run at all, answered in ONE round trip over
    /// <c>IX_TitleCrossReferenceVerdicts_Sweep</c> without materialising a pool of thousands of ids.
    ///
    /// <para>Deliberately <c>EXISTS</c> rather than the COUNT the design note called for: the
    /// predicate is identical and the answer is the same boolean, but a semi-join stops at the
    /// first unjudged row instead of scanning the whole pool on a feed build that runs every few
    /// minutes. The COUNT still exists — see <see cref="UnjudgedCountAsync"/> — for the caller that
    /// genuinely needs the number.</para>
    /// </summary>
    public Task<bool> AnyUnjudgedAsync(
        Guid accountId, IQueryable<Guid> candidateTitleIds, string profileHash, int promptVersion,
        CrossReferenceRound round, CancellationToken ct) =>
        candidateTitleIds
            .AnyAsync(id => !db.TitleCrossReferenceVerdicts.Any(v =>
                v.AccountId == accountId
                && v.ProfileHash == profileHash
                && v.PromptVersion == promptVersion
                && v.Round == round
                && v.TitleId == id), ct);

    /// <summary>
    /// The stale ladder: titles carrying a verdict at a SUPERSEDED fingerprint, newest first.
    ///
    /// <para>These are judged before never-judged titles, because a stale verdict is the one place
    /// the instrument is actively out of date — a Keep written against a taste the account has since
    /// moved off is still shaping the order at half strength until something replaces it. A title
    /// nobody has ever judged is merely unknown, which is the state everything starts in.</para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> StaleFirstAsync(
        Guid accountId, IReadOnlyList<Guid> unjudgedTitleIds, string currentProfileHash, CancellationToken ct)
    {
        if (unjudgedTitleIds.Count == 0)
        {
            return [];
        }

        // Reads IX_…_AccountId_TitleId_GeneratedAt desc — the stale ladder's index.
        var stale = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId
                && v.ProfileHash != currentProfileHash
                && unjudgedTitleIds.Contains(v.TitleId))
            .GroupBy(v => v.TitleId)
            .Select(g => new { TitleId = g.Key, Newest = g.Max(v => v.GeneratedAt) })
            .ToListAsync(ct);

        var rank = stale
            .OrderByDescending(s => s.Newest)
            .ThenBy(s => s.TitleId)
            .Select((s, index) => (s.TitleId, index))
            .ToDictionary(x => x.TitleId, x => x.index);

        // Stale titles in recency order, then everything else in the caller's order. Stable: two
        // runs over the same inputs produce the same work list.
        return unjudgedTitleIds
            .OrderBy(id => rank.TryGetValue(id, out var r) ? r : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Writes one batch's entries and saves, absorbing the duplicate this design provokes on
    /// purpose. Returns how many rows landed.
    /// </summary>
    public async Task<int> RecordBatchAsync(
        Guid accountId,
        string profileHash,
        int promptVersion,
        CrossReferenceRound round,
        string modelId,
        Guid? modelArtifactId,
        int batchIndex,
        IReadOnlyList<CrossReferenceEntry> entries,
        IReadOnlyDictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>> neighbours,
        DateTime now,
        CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        foreach (var entry in entries)
        {
            var shown = neighbours.TryGetValue(entry.TitleId, out var list) ? list : [];
            db.TitleCrossReferenceVerdicts.Add(new TitleCrossReferenceVerdict
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = entry.TitleId,
                ProfileHash = profileHash,
                PromptVersion = promptVersion,
                Round = round,
                Verdict = entry.Verdict,
                ReasonCode = entry.Reason,
                Confidence = Math.Round((decimal)entry.Confidence, 3, MidpointRounding.AwayFromZero),
                NeighbourIdsJson = JsonSerializer.Serialize(shown.Select(n => n.TitleId), JsonOptions),
                ModelId = modelId,
                ModelArtifactId = modelArtifactId,
                BatchIndex = batchIndex,
                GeneratedAt = now,
            });
        }

        await SaveAbsorbingDuplicateAsync(ct);
        return entries.Count;
    }

    /// <summary>
    /// The ranking factor per title — what the feed multiplies <c>RankKey</c> by, and whether the
    /// hero is barred. Titles with no verdict are simply absent; callers read a miss as
    /// <see cref="VerdictFactor.Neutral"/>, so a provider outage orders the feed exactly as today.
    ///
    /// <para>Unused by the feed in PR1 by design — the instrument is measured before a single pick
    /// moves — but tested here, because the aggregation rule is the part a later PR must not
    /// silently change.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, VerdictFactor>> VerdictFactorsAsync(
        Guid accountId, IReadOnlyList<Guid> titleIds, string currentProfileHash, int promptVersion,
        CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<Guid, VerdictFactor>();
        }

        // Every prompt version is read, not just the current one: a stale-fingerprint verdict from
        // the SAME prompt generation is half-strength evidence, and a verdict from an older prompt
        // generation is a different question entirely and is ignored.
        var rows = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId
                && v.PromptVersion == promptVersion
                && titleIds.Contains(v.TitleId))
            .Select(v => new { v.TitleId, v.Round, v.Verdict, v.Confidence, v.ProfileHash })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.TitleId)
            .ToDictionary(
                g => g.Key,
                g => VerdictAggregator.Aggregate(g
                    .Select(r => new VerdictObservation(
                        r.Round, r.Verdict, (double)r.Confidence, r.ProfileHash == currentProfileHash))
                    .ToList()));
    }

    /// <summary>
    /// Saves, absorbing the one duplicate this design provokes on purpose: two runners at the same
    /// prompt version, or a re-run racing a batch that has already landed, both hit the unique key
    /// on (account, title, hash, version, round). Losing that race means the verdict already
    /// exists — which is the outcome both callers wanted. Detached rather than deleted, because the
    /// row was never inserted.
    ///
    /// <para>Copied in shape from <c>LensService.SaveAbsorbingDuplicateAsync</c> deliberately: this
    /// is the same race with the same resolution, and two spellings of it would drift.</para>
    /// </summary>
    private async Task SaveAbsorbingDuplicateAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueRaceSave.IsUniqueViolation(ex))
        {
            foreach (var entry in ex.Entries
                .Where(e => e is { State: EntityState.Added, Entity: TitleCrossReferenceVerdict })
                .ToList())
            {
                entry.State = EntityState.Detached;
            }

            logger.LogInformation(
                "A concurrent pass had already written one of these cross-reference verdicts; the duplicate was dropped.");
            await db.SaveChangesAsync(ct);
        }
    }
}
