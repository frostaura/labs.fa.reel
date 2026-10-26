using FrostAura.Reel.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>One distinct keyword label in the catalogue and how many titles carry it.</summary>
public sealed record VocabularyLabel(string Label, int Titles);

/// <summary>
/// The catalogue's keyword vocabulary: every distinct TMDB keyword label with its title count, read
/// straight from <c>Titles.Keywords</c> and held in memory for <see cref="Window"/>. It is the ground
/// the concept resolver stands on — a user's word is only a concept the catalogue can answer once it
/// has been mapped to labels titles actually carry.
///
/// <para><b>Why live, not a stored table.</b> The resolver turns "ninja" into the exact labels
/// "ninja", "female ninja", "ninja warrior"… and the facet arm then matches those labels with an
/// indexed array overlap. A stored snapshot would miss a label ingested after it was taken, and that
/// title would silently drop out of the concept; the aggregate over 270k keyword rows takes ~175 ms
/// and is re-read at most every <see cref="Window"/>, so a new label is missed for minutes, never
/// for good. The stored <c>KeywordLabels</c> table holds only what is expensive to recompute — the
/// label embeddings.</para>
/// </summary>
public sealed class KeywordVocabulary(IReelDbContext db, IMemoryCache cache)
{
    /// <summary>How long one read of the vocabulary is reused.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A stem this short or shorter matches only as a whole word (optionally pluralised): "mob" must
    /// not expand to "mobile phone", nor "spy" to "spyware". Longer stems match from a word start and
    /// stay open at the end ("heist" → "heists", "heist gone wrong"), as the facet regex always has.
    /// </summary>
    public const int WholeWordStemLength = 4;

    private const string CacheKey = "keyword-vocabulary:v1";

    /// <summary>Every label, most-carried first.</summary>
    public async Task<IReadOnlyList<VocabularyLabel>> LabelsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<IReadOnlyList<VocabularyLabel>>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var rows = await db.Titles
            .SelectMany(t => t.Keywords)
            .GroupBy(k => k)
            .Select(g => new { Label = g.Key, Titles = g.Count() })
            .ToListAsync(ct);
        IReadOnlyList<VocabularyLabel> labels = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Label))
            .Select(r => new VocabularyLabel(r.Label, r.Titles))
            .OrderByDescending(l => l.Titles)
            .ThenBy(l => l.Label, StringComparer.Ordinal)
            .ToList();
        cache.Set(CacheKey, labels, Window);
        return labels;
    }

    /// <summary>
    /// The labels <paramref name="stem"/> reaches: the stem from a word start (open at the end), or as
    /// a whole word when it is <see cref="WholeWordStemLength"/> characters or shorter.
    /// </summary>
    public static IEnumerable<VocabularyLabel> Expand(IReadOnlyList<VocabularyLabel> vocabulary, string stem)
    {
        var needle = stem.Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return [];
        }

        var whole = needle.Length <= WholeWordStemLength;
        return vocabulary.Where(l => Reaches(l.Label, needle, whole));
    }

    /// <summary>True when <paramref name="stem"/> reaches <paramref name="label"/> (see <see cref="Expand"/>).</summary>
    public static bool Reaches(string label, string stem, bool wholeWord)
    {
        var from = 0;
        while (from <= label.Length - stem.Length)
        {
            var at = label.IndexOf(stem, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (at == 0 || !IsWordChar(label[at - 1]))
            {
                if (!wholeWord)
                {
                    return true;
                }

                var end = at + stem.Length;
                var rest = label.AsSpan(end);
                if (rest.IsEmpty || !IsWordChar(rest[0])
                    || (rest.StartsWith("s") && (rest.Length == 1 || !IsWordChar(rest[1])))
                    || (rest.StartsWith("es") && (rest.Length == 2 || !IsWordChar(rest[2]))))
                {
                    return true;
                }
            }

            from = at + 1;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
