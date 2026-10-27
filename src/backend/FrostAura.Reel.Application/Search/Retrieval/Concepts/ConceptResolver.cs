using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>Maps a request's concepts onto the catalogue's own vocabulary.</summary>
public interface IConceptResolver
{
    /// <summary><paramref name="hard"/> with every concept resolved (see <see cref="ConceptResolver"/>).</summary>
    Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct);

    /// <summary>One concept resolved to stems, genres and the exact vocabulary labels they reach.</summary>
    Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct);
}

/// <summary>
/// Turns what the user said into what the catalogue can match (program §4.7, L-8, E-9): a concept's
/// words become label STEMS (its own words, the planner's labels and their curated synonyms), the
/// GENRES its words name, and the EXACT vocabulary labels the stems reach — so "ninja movies" becomes
/// stems {ninja, shinobi, kunoichi} reaching {ninja, ninja fighter, female ninja, ninja warrior,
/// kunoichi, shinobi, shinobi no mono, ninja training}, and the facet arm matches those exact labels
/// with an indexed array overlap.
///
/// <para><b>The steps, in order, each only when the previous ones found nothing to stand on:</b></para>
/// <list type="number">
/// <item>Normalise: lower-case, drop the media words ("movies", "shows", "series") and leading
/// articles. "Ninja movies" and "a ninja film" are the same concept.</item>
/// <item>Stems: the words, the planner's labels, and every <see cref="ConceptSynonyms"/> group either
/// belongs to. Genre words ("horror", "sci-fi", "Sci-Fi &amp; Fantasy") add genre slugs INSTEAD of
/// stems: a genre is its tag (plus aliases and stand-ins where a taxonomy cannot tag it), never a
/// keyword that merely contains the word ("teen comedy" is not the comedy genre).</item>
/// <item>Expand through <see cref="KeywordVocabulary"/>. A stem that reaches nothing is retried
/// singular ("heists" → "heist", "witches" → "witch"); a phrase that reaches nothing falls back to
/// its words, each with its synonyms ("ai robot" → AI, robot, android), OR-ed.</item>
/// <item>Only when no stem reached any label and no genre was named: the nearest labels by
/// embedding (the precomputed <c>KeywordLabels</c> table), within <see cref="NearestLabelDistance"/>,
/// bounded by <see cref="EmbedBudget"/>.</item>
/// </list>
///
/// <para>A concept whose stems reach more than <see cref="MaxExactLabels"/> labels ("love", "war")
/// keeps the containment regex instead (<see cref="ConceptTerm.ExactLabels"/> null): an overlap with
/// hundreds of labels is no faster and the regex is complete by construction.</para>
/// </summary>
public sealed partial class ConceptResolver(
    KeywordVocabulary vocabulary,
    ConceptEmbeddings embeddings,
    IReelDbContext db,
    ILogger<ConceptResolver> logger) : IConceptResolver
{
    /// <summary>The most exact labels a concept is matched by overlap; beyond it the regex runs.</summary>
    public const int MaxExactLabels = 300;

    /// <summary>Cosine distance within which a label counts as naming the concept (similarity ≥ 0.5).</summary>
    public const double NearestLabelDistance = 0.5;

    /// <summary>How many nearest labels the embedding fallback adds at most.</summary>
    public const int NearestLabelCount = 6;

    /// <summary>A label must be carried by at least this many titles to be a nearest-label match.</summary>
    public const int NearestLabelMinTitles = 2;

    /// <summary>How long the fallback waits for an uncached embedding.</summary>
    public static readonly TimeSpan EmbedBudget = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> MediaWords = new(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "films", "show", "shows", "series", "tv", "television", "flick", "flicks",
    };

    private static readonly HashSet<string> PhraseFiller = new(StringComparer.Ordinal)
    {
        "and", "or", "of", "in", "on", "at", "for", "from", "to", "into", "set", "like", "where", "who", "that", "but",
    };

    private static readonly HashSet<string> LeadingFiller = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "some", "any", "about", "with", "good", "great", "best", "more",
    };

    /// <inheritdoc />
    public async Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hard);
        if (!hard.HasConcepts)
        {
            return hard;
        }

        var resolved = new List<ConceptTerm>(hard.Concepts.Count);
        foreach (var concept in hard.Concepts)
        {
            resolved.Add(await ResolveAsync(concept, ct));
        }

        return hard with { Concepts = resolved };
    }

    /// <inheritdoc />
    public async Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(concept);
        var words = Normalise(concept.UserWords);
        var seeds = concept.CatalogueLabels.Select(Normalise).Append(words)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var genres = concept.Genres.Select(g => g.Trim().ToLowerInvariant()).Where(g => g.Length > 0).ToList();
        var genreSeeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            if (ConceptSynonyms.GenreWords.TryGetValue(seed, out var slugs))
            {
                genres.AddRange(slugs);
                genreSeeds.Add(seed);
            }
        }

        // A seed that names a genre is matched as the genre — the tag, and the alias genres and
        // stand-ins on the media that cannot tag it, exactly as an exclusion reads it — never as a
        // keyword stem. As a stem "comedy" reached every label containing the word, so "teen comedy"
        // made a family sci-fi film one of the founder's "unseen comedies" while eligibility (rightly)
        // did not call it a comedy, and one reply counted with two definitions (HREV1-RET-06).
        var stems = seeds.Where(s => !genreSeeds.Contains(s))
            .SelectMany(ConceptSynonyms.GroupOf)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var labels = await vocabulary.LabelsAsync(ct);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var matchedStems = new List<string>(stems.Count);
        foreach (var stem in stems)
        {
            var reached = ReachedOrSingular(labels, stem, out var used);
            matchedStems.Add(used);
            exact.UnionWith(reached);
        }

        // A phrase no label carries ("ai robot", "haunted lighthouse") falls back to its words, each
        // with its synonym group, OR-ed: the facet arm then finds titles about any of them and the
        // judge decides which answer the whole phrase. Only words of three or more letters that are
        // not filler; never a single-word concept, which has nothing to split.
        if (exact.Count == 0 && genres.Count == 0 && words.Contains(' ', StringComparison.Ordinal))
        {
            foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Where(w => (w.Length >= 3 || ConceptSynonyms.Groups.Any(g => g.Contains(w, StringComparer.Ordinal)))
                             && !LeadingFiller.Contains(w) && !PhraseFiller.Contains(w)))
            {
                foreach (var stem in ConceptSynonyms.GroupOf(word))
                {
                    var reached = ReachedOrSingular(labels, stem, out var used);
                    if (reached.Count > 0)
                    {
                        matchedStems.Add(used);
                        exact.UnionWith(reached);
                    }
                }
            }
        }

        if (exact.Count == 0 && genres.Count == 0 && words.Length > 0)
        {
            foreach (var label in await NearestLabelsAsync(words, ct))
            {
                matchedStems.Add(label);
                exact.Add(label);
            }
        }

        var finalStems = matchedStems.Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyList<string>? exactLabels = exact.Count is > 0 and <= MaxExactLabels
            ? [.. exact.Order(StringComparer.Ordinal)]
            : exact.Count == 0 ? [] : null;

        // A concept the catalogue has no label for and no genre for keeps its own words as the stem,
        // so the name and full-text arms can still try; its exact set is empty, not null, so the
        // keyword arm matches nothing rather than every label containing a stray stem.
        // A pure genre concept has no stems at all: its words are the genre, which the genre arm
        // matches, so they are not offered to the name or full-text arms as a phrase either.
        return new ConceptTerm(
            concept.UserWords,
            finalStems.Count > 0 ? finalStems : genres.Count > 0 && genreSeeds.Count == seeds.Count ? [] : [words],
            [.. genres.Distinct(StringComparer.Ordinal)],
            exactLabels);
    }

    /// <summary>
    /// The user's words as a stem: lower-cased, punctuation other than in-word hyphens, apostrophes and
    /// ampersands dropped, media words removed, leading filler removed.
    /// </summary>
    public static string Normalise(string? words)
    {
        if (string.IsNullOrWhiteSpace(words))
        {
            return string.Empty;
        }

        var cleaned = NonWord().Replace(words.ToLowerInvariant(), " ");
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !MediaWords.Contains(t))
            .ToList();
        while (tokens.Count > 1 && LeadingFiller.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        return string.Join(' ', tokens);
    }

    private static List<string> ReachedOrSingular(IReadOnlyList<VocabularyLabel> labels, string stem, out string used)
    {
        used = stem;
        var reached = KeywordVocabulary.Expand(labels, stem).Select(l => l.Label).ToList();
        if (reached.Count > 0)
        {
            return reached;
        }

        foreach (var singular in Singulars(stem))
        {
            reached = KeywordVocabulary.Expand(labels, singular).Select(l => l.Label).ToList();
            if (reached.Count > 0)
            {
                used = singular;
                return reached;
            }
        }

        return reached;
    }

    private static IEnumerable<string> Singulars(string stem)
    {
        if (stem.EndsWith("ies", StringComparison.Ordinal) && stem.Length > 4)
        {
            yield return stem[..^3] + "y";
        }

        if (stem.EndsWith("es", StringComparison.Ordinal) && stem.Length > 3)
        {
            yield return stem[..^2];
        }

        if (stem.EndsWith('s') && stem.Length > 2)
        {
            yield return stem[..^1];
        }
    }

    private async Task<IReadOnlyList<string>> NearestLabelsAsync(string words, CancellationToken ct)
    {
        try
        {
            var vector = await embeddings.EmbedAsync(words, EmbedBudget, ct);
            if (vector is null)
            {
                return [];
            }

            var query = new Vector(vector);
            var nearest = await db.KeywordLabels
                .Where(l => l.Embedding != null && l.EmbeddingModel == EmbeddingText.Model && l.TitleCount >= NearestLabelMinTitles)
                .OrderBy(l => l.Embedding!.CosineDistance(query))
                .Take(NearestLabelCount)
                .Select(l => new { l.Label, Distance = l.Embedding!.CosineDistance(query) })
                .ToListAsync(ct);
            return [.. nearest.Where(n => n.Distance <= NearestLabelDistance).Select(n => n.Label)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Concept resolver: the nearest-label fallback failed for one concept; it stays unresolved.");
            return [];
        }
    }

    [GeneratedRegex(@"[^\p{L}\p{N}&'\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();
}
