using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One title that shares specific catalogue evidence with an anchor.</summary>
/// <param name="Score">Σ of the shared labels' specificity (log of catalogue size over the label's title count), themes at half weight.</param>
/// <param name="Evidence"><c>keyword:&lt;label&gt;</c> and <c>theme:&lt;theme&gt;</c> strings for what is shared, most specific first.</param>
public sealed record KeywordNeighbour(Guid TitleId, double Score, IReadOnlyList<string> Evidence);

/// <summary>
/// The keyword / theme seeding API for the Similar rail (WS-5 task 10, F-14, E-9): eligible titles
/// that share SPECIFIC catalogue evidence with an anchor title — its TMDB keyword labels and its
/// extracted themes — scored by how specific the shared evidence is, so two films sharing "heist" and
/// "safecracker" outrank two sharing "based on novel or book". Every neighbour carries the evidence
/// it was found by, which is what the rail's evidence chips show and its gate checks.
///
/// <para>Labels carried by more than <see cref="MaxLabelTitles"/> titles are too generic to be
/// evidence and are ignored; a label only the anchor carries cannot connect anything. Eligibility is
/// the one builder's (the account's conditions and footprint); the caller still applies maturity and
/// its own timeout.</para>
/// </summary>
public sealed class KeywordNeighbours(IReelDbContext db, EligibilityQueryBuilder eligibility, KeywordVocabulary vocabulary)
{
    /// <summary>A label on more titles than this is too generic to count as shared evidence.</summary>
    public const int MaxLabelTitles = 400;

    /// <summary>How many overlapping titles are read before scoring.</summary>
    public const int ReadDepth = 600;

    /// <summary>Themes count for this fraction of a keyword's weight (extracted, so less certain than a tag).</summary>
    public const double ThemeWeight = 0.5;

    /// <summary>
    /// Up to <paramref name="want"/> eligible titles sharing specific keywords or themes with
    /// <paramref name="anchorTitleId"/>, best first; never the anchor itself or anything in
    /// <paramref name="exclude"/>. Empty when the anchor carries no specific evidence.
    /// </summary>
    public async Task<IReadOnlyList<KeywordNeighbour>> ForAsync(
        Guid accountId, Guid anchorTitleId, int want, IReadOnlyCollection<Guid> exclude, CancellationToken ct)
    {
        var anchor = await db.Titles.AsNoTracking()
            .Where(t => t.Id == anchorTitleId)
            .Select(t => new { t.Keywords, Themes = db.TitleAttributes.Where(a => a.TitleId == t.Id).Select(a => a.Themes).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (anchor is null || want <= 0)
        {
            return [];
        }

        var labels = await vocabulary.LabelsAsync(ct);
        var counts = labels.ToDictionary(l => l.Label, l => l.Titles, StringComparer.Ordinal);
        var total = Math.Max(1, await db.Titles.CountAsync(ct));
        var weights = anchor.Keywords
            .Distinct(StringComparer.Ordinal)
            .Where(k => counts.TryGetValue(k, out var n) && n is >= 2 and <= MaxLabelTitles)
            .ToDictionary(k => k, k => Math.Log((double)total / counts[k]), StringComparer.Ordinal);
        var themes = (anchor.Themes ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
        if (weights.Count == 0 && themes.Length == 0)
        {
            return [];
        }

        var excluded = exclude.Append(anchorTitleId).Distinct().ToList();
        var shared = weights.Keys.ToArray();
        var pool = eligibility.EligibleTitles(accountId).Where(t => !excluded.Contains(t.Id));
        var byKeyword = shared.Length == 0
            ? []
            : await pool.Where(t => t.Keywords.Any(k => shared.Contains(k)))
                .Select(t => new { t.Id, t.Keywords })
                .Take(ReadDepth)
                .ToListAsync(ct);
        var byTheme = themes.Length == 0
            ? []
            : await db.TitleAttributes
                .Where(a => a.Themes.Any(x => themes.Contains(x)) && pool.Select(t => t.Id).Contains(a.TitleId))
                .Select(a => new { a.TitleId, a.Themes })
                .Take(ReadDepth)
                .ToListAsync(ct);

        var scored = new Dictionary<Guid, (double Score, List<(string Evidence, double Weight)> Evidence)>();
        foreach (var row in byKeyword)
        {
            foreach (var k in row.Keywords.Distinct(StringComparer.Ordinal).Where(weights.ContainsKey))
            {
                Credit(scored, row.Id, $"keyword:{k}", weights[k]);
            }
        }

        var themeWeight = weights.Count > 0 ? weights.Values.Average() * ThemeWeight : ThemeWeight;
        foreach (var row in byTheme)
        {
            foreach (var theme in row.Themes.Select(x => x.Trim().ToLowerInvariant()).Distinct().Where(themes.Contains))
            {
                Credit(scored, row.TitleId, $"theme:{theme}", themeWeight);
            }
        }

        return [.. scored
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Key)
            .Take(want)
            .Select(kv => new KeywordNeighbour(
                kv.Key,
                kv.Value.Score,
                [.. kv.Value.Evidence.OrderByDescending(e => e.Weight).Select(e => e.Evidence)]))];
    }

    private static void Credit(
        Dictionary<Guid, (double Score, List<(string Evidence, double Weight)> Evidence)> scored, Guid id, string evidence, double weight)
    {
        if (!scored.TryGetValue(id, out var entry))
        {
            entry = (0d, []);
        }

        entry.Evidence.Add((evidence, weight));
        scored[id] = (entry.Score + weight, entry.Evidence);
    }
}
