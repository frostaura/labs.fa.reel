using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
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
    /// Sharing ALL the anchors' genres is worth this fraction of an average shared keyword, split
    /// evenly across the anchors' genres; it orders titles that already share specific evidence and
    /// never admits one on its own.
    /// </summary>
    public const double GenreWeight = 0.5;

    /// <summary>
    /// Up to <paramref name="want"/> eligible titles sharing specific keywords or themes with
    /// <paramref name="anchorTitleId"/>, best first; never the anchor itself or anything in
    /// <paramref name="exclude"/>. Empty when the anchor carries no specific evidence.
    /// </summary>
    public Task<IReadOnlyList<KeywordNeighbour>> ForAsync(
        Guid accountId, Guid anchorTitleId, int want, IReadOnlyCollection<Guid> exclude, CancellationToken ct) =>
        ForPoolAsync(eligibility.EligibleTitles(accountId), [anchorTitleId], want, exclude, creditGenres: false, ct);

    /// <summary>
    /// The same scoring over a caller's own <paramref name="pool"/> (already eligible and narrowed by
    /// the request's hard constraints) and around several <paramref name="anchors"/> at once — Ask
    /// Reel's "more like the second one" (HREV2-RET-02 / HREV2-ASK-02). A label any anchor carries
    /// counts once; with <paramref name="creditGenres"/>, a genre shared with an anchor adds a little
    /// (<see cref="GenreWeight"/>), only to
    /// a title that already shares specific evidence, so a genre alone never makes a neighbour. The
    /// anchors and <paramref name="exclude"/> are never returned. <paramref name="creditGenres"/> is
    /// off for the Similar rail, whose evidence chips and gate read keyword and theme evidence only.
    /// </summary>
    public async Task<IReadOnlyList<KeywordNeighbour>> ForPoolAsync(
        IQueryable<Title> pool,
        IReadOnlyList<Guid> anchors,
        int want,
        IReadOnlyCollection<Guid> exclude,
        bool creditGenres,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(anchors);
        if (want <= 0 || anchors.Count == 0)
        {
            return [];
        }

        var anchorIds = anchors.Distinct().ToList();
        var anchorRows = await db.Titles.AsNoTracking()
            .Where(t => anchorIds.Contains(t.Id))
            .Select(t => new { t.Keywords, t.Genres, Themes = db.TitleAttributes.Where(a => a.TitleId == t.Id).Select(a => a.Themes).FirstOrDefault() })
            .ToListAsync(ct);
        if (anchorRows.Count == 0)
        {
            return [];
        }

        var labels = await vocabulary.LabelsAsync(ct);
        var counts = labels.ToDictionary(l => l.Label, l => l.Titles, StringComparer.Ordinal);
        var total = Math.Max(1, await db.Titles.CountAsync(ct));
        var weights = anchorRows.SelectMany(a => a.Keywords)
            .Distinct(StringComparer.Ordinal)
            .Where(k => counts.TryGetValue(k, out var n) && n is >= 2 and <= MaxLabelTitles)
            .ToDictionary(k => k, k => Math.Log((double)total / counts[k]), StringComparer.Ordinal);
        var themes = anchorRows.SelectMany(a => a.Themes ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
        if (weights.Count == 0 && themes.Length == 0)
        {
            return [];
        }

        var anchorGenres = anchorRows.SelectMany(a => a.Genres).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var excluded = exclude.Concat(anchorIds).Distinct().ToList();
        var shared = new LabelHolder([.. weights.Keys]);
        var candidates = pool.Where(t => !excluded.Contains(t.Id));

        // Read the titles sharing the MOST specific labels first, so the depth cut keeps the best
        // overlaps rather than whichever rows the index happened to return.
        var byKeyword = shared.Labels.Length == 0
            ? []
            : await candidates.Where(t => t.Keywords.Any(k => shared.Labels.Contains(k)))
                .OrderByDescending(t => t.Keywords.Count(k => shared.Labels.Contains(k)))
                .ThenBy(t => t.Id)
                .Select(t => new { t.Id, t.Keywords, t.Genres })
                .Take(ReadDepth)
                .ToListAsync(ct);
        var byTheme = themes.Length == 0
            ? []
            : await db.TitleAttributes
                .Where(a => a.Themes.Any(x => themes.Contains(x)) && candidates.Select(t => t.Id).Contains(a.TitleId))
                .OrderBy(a => a.TitleId)
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

        if (creditGenres && anchorGenres.Count > 0)
        {
            var genreWeight = (weights.Count > 0 ? weights.Values.Average() : 1d) * GenreWeight / anchorGenres.Count;
            foreach (var row in byKeyword)
            {
                foreach (var genre in row.Genres.Distinct(StringComparer.Ordinal).Where(anchorGenres.Contains))
                {
                    Credit(scored, row.Id, $"genre:{genre}", genreWeight);
                }
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

    /// <summary>A closure-shaped holder so EF Core sends the shared labels as one array parameter.</summary>
    private sealed class LabelHolder(string[] labels)
    {
        public readonly string[] Labels = labels;
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
