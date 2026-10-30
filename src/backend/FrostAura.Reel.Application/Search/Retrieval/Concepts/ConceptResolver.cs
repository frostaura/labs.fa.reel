using System.Runtime.CompilerServices;
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
/// belongs to, or its singular belongs to ("heists" → the heist group). Genre words ("horror",
/// "romances", "sci-fi", "Sci-Fi &amp; Fantasy", and phrases made only of them: "romantic comedy")
/// add genre slugs INSTEAD of stems: a genre is its tag (plus aliases and stand-ins where a taxonomy
/// cannot tag it), never a keyword that merely contains the word ("teen comedy" is not the comedy
/// genre).</item>
/// <item>Expand through <see cref="KeywordVocabulary"/>. A stem that reaches nothing is retried
/// singular ("witches" → "witch"); a phrase that reaches nothing falls back to its words, each with
/// its synonyms ("ai robot" → AI, robot, android), OR-ed — except its genre words, which stay
/// genres.</item>
/// <item>Several genres named by one phrase AND: resolving a request's constraints splits them into
/// a concept each ("romantic comedy" → romance, comedy; "romantic heist" → heist, romance). "Or"
/// keeps them in one concept, OR-ed.</item>
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

    /// <summary>
    /// The concepts this resolver produced, by reference. Resolving is not idempotent (a resolved
    /// concept's exact labels, read again as stems, can reach further labels; its nearest-label
    /// fallback depends on whether an embedding arrived in time), so a concept it already resolved is
    /// passed through untouched, and the counts a reply quotes are counted with the very labels the
    /// cards were found with, however many callers resolve the same constraints.
    /// </summary>
    private static readonly ConditionalWeakTable<ConceptTerm, object> Produced = [];

    private static readonly object Marker = new();

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
    /// <remarks>
    /// A phrase whose genre words were peeled off in the phrase fallback becomes one concept per genre
    /// word beside its topic, so they AND: "romantic comedy" is a romance AND a comedy (the reducer's
    /// own one-concept-per-genre reading of "romcoms"), never every comedy plus every romance, and
    /// "romantic heist" is a heist that is also a romance. The first concept keeps the user's words, so
    /// the reply still names the topic as they said it.
    /// </remarks>
    public async Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hard);
        if (!hard.HasConcepts)
        {
            return hard;
        }

        if (hard.Concepts.All(IsResolved))
        {
            return hard;
        }

        var resolved = new List<ConceptTerm>(hard.Concepts.Count);
        foreach (var concept in hard.Concepts)
        {
            if (IsResolved(concept))
            {
                resolved.Add(concept);
                continue;
            }

            var resolution = await ResolveCoreAsync(concept, ct);
            resolved.AddRange(Split(resolution).Select(Mark));
        }

        return hard with { Concepts = Merge(resolved) };
    }

    /// <summary>
    /// True when <paramref name="concept"/> is one this resolver produced (see <see cref="Produced"/>);
    /// any other concept — the planner's, the stored conversation state's, a test's — is unresolved,
    /// whatever its fields hold.
    /// </summary>
    public static bool IsResolved(ConceptTerm concept) =>
        concept is not null && Produced.TryGetValue(concept, out _);

    /// <summary>
    /// <paramref name="concepts"/> with every concept that requires exactly what an earlier one does
    /// dropped (HREV2-RET-03). Concepts AND, so a duplicate changes no count — but "romantic shows" planned as
    /// [romantic, romance] is ONE requirement, the romance genre, and a reply, a knockout or a cost
    /// that reads two concepts reads two requirements. The first concept keeps the user's words.
    /// </summary>
    public static IReadOnlyList<ConceptTerm> Merge(IReadOnlyList<ConceptTerm> concepts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ConceptTerm>(concepts.Count);
        foreach (var concept in concepts)
        {
            var genres = string.Join('\u001e', HardConstraintFilter.GenresOf(concept).Order(StringComparer.Ordinal));
            var labels = string.Join('\u001e', HardConstraintFilter.LabelsOf(concept).Order(StringComparer.Ordinal));
            var exact = concept.ExactLabels is { } set ? string.Join('\u001e', set.Order(StringComparer.Ordinal)) : "\u0000";
            var key = $"{genres}\u001f{labels}\u001f{exact}";
            if (seen.Add(key))
            {
                merged.Add(concept);
            }
        }

        return merged;
    }

    /// <summary>
    /// What a concept no resolver has touched means when its words are only genre words — the static
    /// half of <see cref="ResolveAsync(HardConstraints, CancellationToken)"/>'s genre reading, for the
    /// synchronous predicate (<see cref="HardConstraintFilter"/>), so a caller that counts with the
    /// stored, unresolved constraints still counts "romantic" as the romance genre and "soap operas"
    /// as the soap genre instead of as keywords containing the words (HREV2-RET-03). The same rules as
    /// resolution: one genre key ("sci-fi &amp; fantasy", "soap operas") is one concept OR-ing its
    /// slugs; a phrase of several genre words ("romantic comedy") is a concept per word, AND-ed, unless
    /// it says "or" or carries planner labels that are not genre words. Null when the words are not
    /// all genre words, or the concept already carries genres or exact labels.
    /// </summary>
    public static IReadOnlyList<ConceptTerm>? GenreReading(ConceptTerm concept)
    {
        ArgumentNullException.ThrowIfNull(concept);
        if (concept.Genres.Count > 0 || concept.ExactLabels is not null || IsResolved(concept))
        {
            return null;
        }

        var words = Normalise(concept.UserWords);
        if (ConceptSynonyms.GenreSlugsOf(words) is { } slugs)
        {
            return [concept with { Genres = [.. slugs] }];
        }

        var named = GenreWordsIn(words, pluralAware: true, out var others);
        if (named.Count == 0 || !others.All(IsConjunction))
        {
            return null;
        }

        var all = named.SelectMany(n => n.Slugs).Distinct(StringComparer.Ordinal).ToList();
        if (others.Contains("or") || named.Count == 1 || Labelled(concept))
        {
            return [concept with { Genres = all }];
        }

        return [concept with { Genres = [.. named[0].Slugs] }, .. named.Skip(1).Select(n => new ConceptTerm(n.Word, [], [.. n.Slugs], StandInWords: concept.StandInWords))];
    }

    /// <summary>
    /// The genre phrase in a user's turn that names exactly <paramref name="genres"/>: the words of the
    /// one run of genre words (and "and"/"&amp;"/"or" between them) whose slugs are that set ("a 90s
    /// romantic comedy" with romance, comedy → "romantic comedy"). Runs naming other genres ("but no
    /// horror") are passed over. Null when no run, or more than one, names exactly those genres — the
    /// turn is then no evidence of how the user joined them (HREV8-ASK-01).
    /// </summary>
    public static string? GenrePhraseIn(string? message, IReadOnlyCollection<string> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);
        var tokens = Normalise(message).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<string>(1);
        var run = new List<string>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        void Close()
        {
            while (run.Count > 0 && IsConjunction(run[^1]))
            {
                run.RemoveAt(run.Count - 1);
            }

            if (slugs.Count > 0 && slugs.SetEquals(genres))
            {
                matches.Add(string.Join(' ', run));
            }

            run.Clear();
            slugs.Clear();
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (i + 1 < tokens.Length && ConceptSynonyms.GenreWords.TryGetValue($"{tokens[i]} {tokens[i + 1]}", out var pair))
            {
                run.Add(tokens[i]);
                run.Add(tokens[++i]);
                slugs.UnionWith(pair);
            }
            else if (ConceptSynonyms.GenreSlugsOf(tokens[i]) is { } single)
            {
                run.Add(tokens[i]);
                slugs.UnionWith(single);
            }
            else if (run.Count > 0 && IsConjunction(tokens[i]))
            {
                run.Add(tokens[i]);
            }
            else
            {
                Close();
            }
        }

        Close();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// True when the planner gave <paramref name="concept"/> a label that is not made only of genre words —
    /// a real topic beside the phrase, which keeps the phrase's genres OR-ed inside the one concept. A
    /// label that merely restates the genre phrase ("romantic comedies", the v1 keyword the user's own
    /// words anchored) is no such topic: reading it as one OR-ed the two genres, so v1 counted every
    /// comedy and every romance as a romantic comedy and offered lifting either exclusion alone as a
    /// relaxation that unlocked nothing of what was asked (HREV8-ASK-01). The static reading
    /// (<see cref="GenreReading"/>) and resolution apply the one rule.
    /// </summary>
    private static bool Labelled(ConceptTerm concept) =>
        concept.CatalogueLabels.Any(l => ConceptSynonyms.GenresNamedBy(Normalise(l)) is null);

    private static ConceptTerm Mark(ConceptTerm concept)
    {
        Produced.AddOrUpdate(concept, Marker);
        return concept;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One concept ORs everything it resolved to, so a phrase's genre words are among its
    /// <see cref="ConceptTerm.Genres"/>; <see cref="ResolveAsync(HardConstraints, CancellationToken)"/>
    /// splits them into concepts of their own.
    /// </remarks>
    public async Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct) =>
        IsResolved(concept) ? concept : (await ResolveCoreAsync(concept, ct)).Term;

    private static IEnumerable<ConceptTerm> Split(Resolution resolution)
    {
        var (term, phraseGenres) = resolution;
        if (phraseGenres.Count == 0)
        {
            yield return term;
            yield break;
        }

        var rest = phraseGenres.AsEnumerable();
        if (term.CatalogueLabels.Count > 0)
        {
            yield return term with { Genres = [] };
        }
        else
        {
            yield return term with { Genres = phraseGenres[0].Slugs };
            rest = rest.Skip(1);
        }

        foreach (var genre in rest)
        {
            yield return new ConceptTerm(genre.Word, [], genre.Slugs, [], term.StandInWords);
        }
    }

    private async Task<Resolution> ResolveCoreAsync(ConceptTerm concept, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(concept);
        var words = Normalise(concept.UserWords);
        var seeds = concept.CatalogueLabels.Select(Normalise).Append(words)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var genres = concept.Genres.Select(g => g.Trim().ToLowerInvariant()).Where(g => g.Length > 0).ToList();
        var givenGenres = genres.Count > 0;
        var genreSeeds = new HashSet<string>(StringComparer.Ordinal);
        var phraseGenres = new List<GenreWord>();
        foreach (var seed in seeds)
        {
            if (ConceptSynonyms.GenreSlugsOf(seed) is { } slugs)
            {
                genres.AddRange(slugs);
                genreSeeds.Add(seed);
                continue;
            }

            // A phrase made only of genre words ("romantic comedy", "comedy romance", "horror western")
            // is those genres whether or not some label happens to spell it: a keyword "romantic comedy"
            // on a title eligibility does not call a comedy is the two-definitions defect again. A
            // concept that already carries its genres (one this resolver split, resolved again) keeps
            // them; the words add nothing.
            var named = GenreWordsIn(seed, pluralAware: true, out var others);
            if (named.Count > 0 && others.All(IsConjunction))
            {
                genreSeeds.Add(seed);
                if (!givenGenres)
                {
                    genres.AddRange(named.SelectMany(n => n.Slugs));
                    if (seed == words && !Labelled(concept) && !others.Contains("or"))
                    {
                        phraseGenres.AddRange(named);
                    }
                }
            }
        }

        // A seed that names a genre is matched as the genre — the tag, and the alias genres and
        // stand-ins on the media that cannot tag it, exactly as an exclusion reads it — never as a
        // keyword stem. As a stem "comedy" reached every label containing the word, so "teen comedy"
        // made a family sci-fi film one of the founder's "unseen comedies" while eligibility (rightly)
        // did not call it a comedy, and one reply counted with two definitions (HREV1-RET-06).
        var stems = seeds.Where(s => !genreSeeds.Contains(s))
            .SelectMany(ConceptSynonyms.GroupOfAnyForm)
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
        // not filler; never a single-word concept, which has nothing to split. A word that names a
        // genre is the genre here too, never a stem — "romantic heist" is a heist that is a romance
        // (only exact genre words: "wars" in "star wars" is not the war genre).
        var lookFor = genres.Count == 0 ? words : string.Empty;
        if (exact.Count == 0 && genres.Count == 0 && words.Contains(' ', StringComparison.Ordinal))
        {
            var named = GenreWordsIn(words, pluralAware: false, out var rest);
            foreach (var word in rest.Where(w => (w.Length >= 3 || ConceptSynonyms.Groups.Any(g => g.Contains(w, StringComparer.Ordinal)))
                         && !LeadingFiller.Contains(w) && !PhraseFiller.Contains(w)))
            {
                foreach (var stem in ConceptSynonyms.GroupOfAnyForm(word))
                {
                    var reached = ReachedOrSingular(labels, stem, out var used);
                    if (reached.Count > 0)
                    {
                        matchedStems.Add(used);
                        exact.UnionWith(reached);
                    }
                }
            }

            if (named.Count > 0)
            {
                genres.AddRange(named.SelectMany(n => n.Slugs));
                lookFor = string.Join(' ', rest.Where(w => !LeadingFiller.Contains(w) && !PhraseFiller.Contains(w)));

                // "romance or heist" asks for either: its genre stays OR-ed inside the one concept.
                if (!rest.Contains("or"))
                {
                    phraseGenres.AddRange(named);
                }
            }
        }

        // The embedding fallback reads what is left to find labels for: the whole phrase, or the words
        // beside its genre words ("romantic lighthouse" → "lighthouse"). A genre concept has nothing left.
        if (exact.Count == 0 && lookFor.Length > 0)
        {
            foreach (var label in await NearestLabelsAsync(lookFor, ct))
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
        var pureGenre = genres.Count > 0 && (genreSeeds.Count == seeds.Count || (lookFor.Length == 0 && phraseGenres.Count > 0));
        var term = new ConceptTerm(
            concept.UserWords,
            finalStems.Count > 0 ? finalStems : pureGenre ? [] : [lookFor.Length > 0 ? lookFor : words],
            [.. genres.Distinct(StringComparer.Ordinal)],
            exactLabels,
            concept.StandInWords);
        return new Resolution(term, [.. phraseGenres.DistinctBy(p => string.Join(',', p.Slugs), StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The genre words in <paramref name="phrase"/>, two-word names first ("science fiction", "soap
    /// opera"), with every other token in <paramref name="others"/>. <paramref name="pluralAware"/>
    /// also reads a plural of a genre word ("romances").
    /// </summary>
    private static List<GenreWord> GenreWordsIn(string phrase, bool pluralAware, out List<string> others)
    {
        var named = new List<GenreWord>();
        others = [];
        var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i + 1 < tokens.Length && ConceptSynonyms.GenreWords.TryGetValue($"{tokens[i]} {tokens[i + 1]}", out var pair))
            {
                named.Add(new GenreWord($"{tokens[i]} {tokens[i + 1]}", pair));
                i++;
            }
            else if ((pluralAware ? ConceptSynonyms.GenreSlugsOf(tokens[i]) : ConceptSynonyms.GenreWords.GetValueOrDefault(tokens[i])) is { } slugs)
            {
                named.Add(new GenreWord(tokens[i], slugs));
            }
            else
            {
                others.Add(tokens[i]);
            }
        }

        return named;
    }

    private static bool IsConjunction(string token) => token is "and" or "&" or "or";

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

        foreach (var singular in ConceptSynonyms.Singulars(stem))
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

    /// <summary>A word of a phrase that names genres ("romantic" → romance).</summary>
    private sealed record GenreWord(string Word, IReadOnlyList<string> Slugs);

    /// <summary>A resolved concept and the genre words of its phrase, each a requirement of its own.</summary>
    private sealed record Resolution(ConceptTerm Term, IReadOnlyList<GenreWord> PhraseGenres);
}
