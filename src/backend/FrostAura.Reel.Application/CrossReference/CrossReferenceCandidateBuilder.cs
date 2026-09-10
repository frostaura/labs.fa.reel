using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// Assembles candidates in the shape the judge reads: catalogue row, extracted attributes, top
/// credits, and the viewer's own nearest rated neighbours.
///
/// <para><b>Everything optional is left NULL rather than defaulted.</b> The adapter omits a null
/// field from the prompt entirely, so a title whose attributes were never extracted costs the judge
/// a smaller line rather than a line full of confident zeros. Only a closed-vocabulary tone is
/// trusted — rows written before the vocabulary was enforced carry polluted free-text labels, the
/// same rule the re-rank context applies.</para>
///
/// <para><b>A candidate with neither an embedding nor extracted attributes is dropped.</b> It would
/// reach the judge as a name, a year and a genre list, and a verdict on a name is not a verdict —
/// it is the model's prior about the title's reputation, which is the one thing this whole design
/// is trying not to buy. Those titles are counted and reported, never silently skipped.</para>
/// </summary>
public sealed class CrossReferenceCandidateBuilder(IReelDbContext db, NeighbourFinder neighbours)
{
    /// <summary>Cast names carried into the prompt.</summary>
    private const int MaxCast = 3;

    /// <summary>What one batch's assembly produced, including what it could not use.</summary>
    /// <param name="Candidates">Judgeable candidates, in the caller's own order.</param>
    /// <param name="Neighbours">The evidence each candidate carries, for persistence.</param>
    /// <param name="SkippedForNoSignal">
    /// Candidates with no embedding AND no extracted attributes. Reported honestly — "1,840 of
    /// 2,242 cross-referenced against your history; 402 had no plot embedding yet" — rather than
    /// rounded into the judged count.
    /// </param>
    public sealed record Batch(
        IReadOnlyList<CrossReferenceCandidate> Candidates,
        IReadOnlyDictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>> Neighbours,
        int SkippedForNoSignal);

    /// <summary>
    /// Builds one batch. <paramref name="predictions"/> is the model's own number per title where
    /// one exists; a title with no score simply carries no <c>predicted</c> field.
    /// </summary>
    public async Task<Batch> BuildAsync(
        Guid accountId,
        IReadOnlyList<Guid> titleIds,
        IReadOnlyDictionary<Guid, decimal> predictions,
        NeighbourFinder.Options neighbourOptions,
        CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Batch([], new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>(), 0);
        }

        var titles = await db.Titles
            .Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Year, t.MediaType, t.Genres, t.Overview })
            .ToDictionaryAsync(t => t.Id, ct);

        var attributes = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .ToDictionaryAsync(a => a.TitleId, ct);

        var credits = (await db.TitleCredits
                .Where(c => titleIds.Contains(c.TitleId))
                .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.Role, c.CastOrder, p.Name })
                .ToListAsync(ct))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.TitleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var neighbourMap = await neighbours.FindAsync(accountId, titleIds, neighbourOptions, ct);

        var candidates = new List<CrossReferenceCandidate>();
        var skipped = 0;
        foreach (var id in titleIds)
        {
            if (!titles.TryGetValue(id, out var title) || string.IsNullOrWhiteSpace(title.Name))
            {
                skipped++;
                continue;
            }

            attributes.TryGetValue(id, out var attrs);
            var hasNeighbours = neighbourMap.TryGetValue(id, out var near) && near.Count > 0;
            if (!hasNeighbours && attrs is null)
            {
                // No embedding to place it near anything the viewer rated, and nothing extracted to
                // describe it. There is no evidence here, only a title.
                skipped++;
                continue;
            }

            var titleCredits = credits.GetValueOrDefault(id) ?? [];
            candidates.Add(new CrossReferenceCandidate(
                id,
                title.Name,
                title.Year,
                title.MediaType.ToString(),
                title.Genres,
                predictions.TryGetValue(id, out var predicted) ? predicted : null,
                AttributeVocabulary.IsClosedVocabTone(attrs?.Tone) ? attrs!.Tone : null,
                attrs?.Era,
                attrs?.Themes ?? [],
                attrs?.Darkness,
                attrs?.Pacing,
                attrs?.Complexity,
                attrs?.EmotionalIntensity,
                attrs?.Humor,
                attrs?.Optimism,
                attrs?.EnsembleVsSolo,
                titleCredits.Where(c => c.Role == CreditRole.Director)
                    .OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name).FirstOrDefault(),
                titleCredits.Where(c => c.Role == CreditRole.Writer)
                    .OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name).FirstOrDefault(),
                titleCredits.Where(c => c.Role == CreditRole.Actor)
                    .OrderBy(c => c.CastOrder ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => c.Name).Take(MaxCast).ToList(),
                hasNeighbours ? near! : [],
                title.Overview));
        }

        return new Batch(candidates, neighbourMap, skipped);
    }
}
