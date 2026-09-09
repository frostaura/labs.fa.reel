using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The compact taste line every LLM prompt rides on — "favours thriller, drama; loves X, Y".
///
/// <para>
/// One definition, because there were three: the feed build, the paginator and the live search
/// expansion each carried a hand-copied version, and all three were wrong in the same two ways.
/// </para>
///
/// <para>
/// <b>Count is not affinity.</b> Ordering genres by how MANY titles the user rated in them
/// describes what they have watched, not what they like — a genre rated widely and mediocrely
/// outranked one rated rarely and highly. The order is now the shrunken mean, the same
/// empirical-Bayes affinity <see cref="FeatureVectorBuilder"/> feeds the model, so the sentence the
/// model reads and the feature it scores on agree.
/// </para>
///
/// <para>
/// <b>A banned genre is not a favourite.</b> No copy subtracted the account's ExcludeGenre rules,
/// so the rerank prompt could open with "favours drama, comedy, action" while comedy is a genre the
/// eligibility layer refuses to show — asking the model to score for a taste the surface forbids.
/// </para>
///
/// <para>
/// The loved list had the same unordered-Take defect as the candidate seeds: a Where/Take(4) with
/// no ORDER BY returns any four loved titles. It now follows the TopLovedTitleIds ranking.
/// </para>
/// </summary>
public static class TasteSummary
{
    private const int TopGenres = 4;

    private const int LovedTitles = 4;

    /// <summary>The sentence, or "no strong signal yet" when the account has nothing to say.</summary>
    public static async Task<string> BuildAsync(
        IReelDbContext db, Guid accountId, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var excludedGenres = (await db.ContentFilters
                .Where(f => f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre)
                .Select(f => f.Value)
                .ToListAsync(ct))
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .ToHashSet();

        var topGenres = taste.GenreRatings
            .Where(kv => !excludedGenres.Contains(kv.Key))
            .Select(kv => new
            {
                Genre = kv.Key,
                Affinity = TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean),
                kv.Value.Count,
            })
            // Count and then name as tie-breakers: shrunken means collide readily on small pools,
            // and a summary that reshuffles between builds churns the prompt cache for nothing.
            .OrderByDescending(g => g.Affinity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .Take(TopGenres)
            .Select(g => g.Genre)
            .ToList();

        var lovedRank = taste.TopLovedTitleIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        var loved = (await db.Titles
                .Where(t => taste.TopLovedTitleIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(ct))
            .OrderBy(t => lovedRank[t.Id])
            .Take(LovedTitles)
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var parts = new List<string>();
        if (topGenres.Count > 0)
        {
            parts.Add("favours " + string.Join(", ", topGenres));
        }

        if (loved.Count > 0)
        {
            parts.Add("loves " + string.Join(", ", loved));
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "no strong signal yet";
    }
}
