using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Everything the catalogue knows about a title, in the shape the re-rank prompt reads it —
/// extracted tone / era / themes, the seven attribute scalars, the people, and optionally the
/// model's own top contributions.
///
/// <para>It lives here because the feed build was the only surface that ever assembled it, inline,
/// while Ask Reel — the surface where a person is actually waiting on the verdict — sent the model
/// a name, a year, a genre list and an overview and got correspondingly shallow answers. One
/// builder, two callers, so the two cannot describe the same title differently.</para>
///
/// <para>Static, taking its <see cref="IReelDbContext"/> as an argument, deliberately: it holds no
/// state, and a registered service would mean editing composition-root wiring that this change has
/// no other reason to touch.</para>
/// </summary>
public static class RerankContextBuilder
{
    /// <summary>
    /// Builds one context per distinct id in <paramref name="titleIds"/>.
    ///
    /// <para><paramref name="topContributions"/> is the feed build's per-title explanation of why
    /// ITS model ranked the title where it did, so the judge can agree or disagree with a reason.
    /// Ask Reel passes null: its judge is weighing a request, not a feed build's ranking, and an
    /// empty list is the honest answer rather than a fabricated one.</para>
    ///
    /// <para><b>Absence is null, never zero.</b> A title whose attribute extraction has not
    /// completed carries nulls through every scalar — a 0.0 darkness reads to the model as
    /// "measured, and bright", which is a claim the catalogue is not entitled to make. Only rows at
    /// <see cref="AttributeExtractionStatus.Done"/> are read, and only a closed-vocabulary tone is
    /// trusted, because rows written before that vocabulary was enforced carry polluted free-text
    /// labels.</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, RerankTitleContext>> BuildAsync(
        IReelDbContext db,
        IReadOnlyList<Guid> titleIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? topContributions,
        CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, RerankTitleContext>();
        }

        var attributes = await db.TitleAttributes
            .Where(a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .ToDictionaryAsync(a => a.TitleId, ct);

        var credits = (await db.TitleCredits
                .Where(c => ids.Contains(c.TitleId))
                .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.Role, c.CastOrder, p.Name })
                .ToListAsync(ct))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.TitleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var contexts = new Dictionary<Guid, RerankTitleContext>();
        foreach (var id in ids)
        {
            attributes.TryGetValue(id, out var attrs);
            var titleCredits = credits.GetValueOrDefault(id) ?? [];
            contexts[id] = new RerankTitleContext(
                // Only a closed-vocabulary tone is trusted: rows written before enforcement carry
                // polluted free-text labels (see AttributeVocabulary).
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
                    .Select(c => c.Name).Take(3).ToList(),
                topContributions?.GetValueOrDefault(id) ?? []);
        }

        return contexts;
    }
}
