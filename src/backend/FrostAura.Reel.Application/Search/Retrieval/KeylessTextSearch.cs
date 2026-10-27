using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One keyless match: the title, its coverage-first score, and the evidence it matched on.</summary>
/// <param name="MatchedOn">Evidence strings as the retriever writes them: <c>keyword:heist</c>, <c>genre:comedy</c>, <c>name:ninja</c>, <c>fts:overview</c>.</param>
public sealed record KeylessHit(Title Title, double MatchScore, IReadOnlyList<string> MatchedOn);

/// <summary>
/// Search with no model key (program task 12, C-9): the full-text arm and the facet SQL over the
/// resolved concepts, inside the account's eligibility — the same arms and the same concept resolver
/// the hybrid retriever runs, so a keyless install and a keyed one agree on what "heist" means. It
/// replaced <c>LexicalSearchService</c>, whose hand-written 30-word concept table and unindexed
/// <c>ILIKE</c> scans over names, overviews and keywords answered "ninja movies" with American Ninja
/// Warrior.
///
/// <para><b>Units.</b> The words are split into units: the longest run of up to three words that
/// names a synonym group, a genre, a mood or a catalogue label ("time loop", "survival at sea", "feel
/// good"), else one word. Stop words and words under three letters are dropped.</para>
///
/// <para><b>Each unit</b> is resolved like a concept (synonyms, genre words, the vocabulary) — or, for
/// a mood word, through <see cref="MoodLexicon"/>'s genres and labels. A unit that reaches nothing is
/// corrected against the vocabulary, the genre words and the mood words by edit distance, keeping its
/// first letter ("medevil" → "medieval"), and resolved again.</para>
///
/// <para><b>Ranking is coverage first</b>: a title matching more units always outranks one matching
/// fewer ("medieval" and "fun" beats "fun" alone, however popular), and within the same coverage the
/// units' reciprocal-rank scores over their facet and full-text lists decide. Nothing here is a pick:
/// no model read the request, so the caller shows these as unchecked.</para>
/// </summary>
public sealed class KeylessTextSearch(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    IConceptResolver resolver,
    KeywordVocabulary vocabulary,
    FullTextArm fullText)
{
    /// <summary>How deep each unit's facet and full-text lists read.</summary>
    public const int ReadDepth = 200;

    /// <summary>The most units one query is split into.</summary>
    public const int MaxUnits = 8;

    /// <summary>Added per unit matched beyond the first: larger than any sum of reciprocal ranks, so coverage always leads.</summary>
    private const double CoverageStep = 1.0;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "i", "a", "an", "and", "or", "the", "to", "of", "in", "on", "for", "with", "want", "wanna", "something",
        "anything", "some", "watch", "watching", "movie", "movies", "film", "films", "show", "shows", "series",
        "tonight", "please", "me", "my", "that", "is", "it", "like", "but", "really", "very", "kind", "sort",
        "feel", "feels", "bit", "about", "set", "where", "who", "has", "have", "can", "you", "find", "give",
        "recommend", "good", "great", "best",
    };

    /// <summary>
    /// A search with no container: the concept resolver over a private vocabulary cache and no
    /// embedding provider (a keyless install has none). For hand-built services in tests and tools.
    /// </summary>
    public static KeylessTextSearch Offline(IReelDbContext db, EligibilityQueryBuilder eligibility)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var vocabulary = new KeywordVocabulary(db, cache);
        var resolver = new ConceptResolver(
            vocabulary,
            new ConceptEmbeddings(NoEmbeddings.Instance, cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
        return new KeylessTextSearch(db, eligibility, resolver, vocabulary, new FullTextArm(db));
    }

    /// <summary>The account's eligible titles matching <paramref name="query"/>, coverage first, at most <paramref name="take"/>.</summary>
    public async Task<List<KeylessHit>> SearchAsync(Guid accountId, string query, int take, CancellationToken ct)
    {
        if (take <= 0)
        {
            return [];
        }

        var labels = await vocabulary.LabelsAsync(ct);
        var units = Units(query, labels);
        if (units.Count == 0)
        {
            return [];
        }

        var concepts = new List<ConceptTerm>(units.Count);
        foreach (var unit in units)
        {
            concepts.Add(await ResolveUnitAsync(unit, labels, ct));
        }

        // Every unit's lists are read inside eligibility (conditions and footprint); maturity is a
        // ceiling on the materialised titles below, as everywhere else.
        var pool = eligibility.EligibleTitles(accountId);
        var perUnit = new List<Dictionary<Guid, double>>(concepts.Count);
        var textEvidence = new Dictionary<Guid, List<string>>();
        foreach (var concept in concepts)
        {
            var hard = new HardConstraints([concept], [], null, null, MediaScope.Any);
            var facet = await CrowdVotes.BestShrunkFirst(HardConstraintFilter.Apply(pool, db, hard))
                .Select(t => t.Id)
                .Take(ReadDepth)
                .ToListAsync(ct);
            var text = await fullText.SearchAsync(pool, [concept], ReadDepth, ct);
            foreach (var hit in text)
            {
                if (!textEvidence.TryGetValue(hit.TitleId, out var list))
                {
                    textEvidence[hit.TitleId] = list = [];
                }

                list.AddRange(hit.Evidence());
            }

            perUnit.Add(HybridRetriever.Fuse(
                new Dictionary<string, List<Guid>> { ["facet"] = facet, ["fulltext"] = [.. text.Select(h => h.TitleId)] },
                admitted: null).ToDictionary(f => f.TitleId, f => f.Score));
        }

        // Coverage is judged on the titles themselves, not on list membership: each unit's lists stop
        // at ReadDepth, so a title matching "medieval" AND "fun" can sit below the top of the broad
        // "fun" list and would otherwise count as matching one unit.
        var candidateIds = perUnit.SelectMany(scores => scores.Keys).Distinct().ToList();
        var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);
        var titles = EligibilityQueryBuilder.WithinMaturity(
                await db.Titles.AsNoTracking().Where(t => candidateIds.Contains(t.Id)).ToListAsync(ct), ceiling)
            .ToDictionary(t => t.Id);
        var unitHards = concepts.Select(c => new HardConstraints([c], [], null, null, MediaScope.Any)).ToList();
        var all = new HardConstraints(concepts, [], null, null, MediaScope.Any);
        return [.. titles.Values
            .Select(title =>
            {
                var matched = 0;
                for (var u = 0; u < unitHards.Count; u++)
                {
                    if (perUnit[u].ContainsKey(title.Id) || HardConstraintFilter.EvidenceFor(title, unitHards[u]).Count > 0)
                    {
                        matched++;
                    }
                }

                var score = perUnit.Sum(scores => scores.GetValueOrDefault(title.Id)) + (CoverageStep * (matched - 1));
                return (Title: title, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title.Id)
            .Take(take)
            .Select(x => new KeylessHit(
                x.Title,
                x.Score,
                [.. HardConstraintFilter.EvidenceFor(x.Title, all)
                    .Concat(textEvidence.GetValueOrDefault(x.Title.Id, []))
                    .Distinct(StringComparer.Ordinal)]))];
    }

    /// <summary>
    /// The query's units: the longest run of up to three words that names a synonym group, a genre, a
    /// mood or reaches a catalogue label, else one word; stop words and words under three letters
    /// dropped, at most <see cref="MaxUnits"/>.
    /// </summary>
    internal static List<string> Units(string query, IReadOnlyList<VocabularyLabel> labels)
    {
        var words = ConceptResolver.Normalise(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var units = new List<string>();
        var i = 0;
        while (i < words.Count && units.Count < MaxUnits)
        {
            var taken = 0;
            for (var length = Math.Min(3, words.Count - i); length >= 2; length--)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(length));
                if (Names(phrase, labels))
                {
                    units.Add(phrase);
                    taken = length;
                    break;
                }
            }

            if (taken == 0)
            {
                var word = words[i];
                if (word.Length >= 3 && !Stopwords.Contains(word))
                {
                    units.Add(word);
                }

                taken = 1;
            }

            i += taken;
        }

        return [.. units.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Plain Levenshtein, bounded: lengths more than three apart are <see cref="int.MaxValue"/>.</summary>
    public static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// The closest known word to a <paramref name="word"/> that reached nothing — a genre word, a mood
    /// word or a one-word catalogue label — within the typo budget (1 edit under five letters, 2 under
    /// eight, else 3) and sharing its first letter; genre and mood words win ties over labels, then the
    /// closer length, then the longer word, then the more-carried label. Null for a word under four letters or with no match.
    /// </summary>
    internal static string? Correct(string word, IReadOnlyList<VocabularyLabel> labels)
    {
        if (word.Length < 4 || word.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        var curated = ConceptSynonyms.GenreWords.Keys
            .Concat(MoodLexicon.Words)
            .Concat(ConceptSynonyms.Groups.SelectMany(g => g))
            .Where(w => !w.Contains(' ', StringComparison.Ordinal))
            .Select(w => (Word: w, Curated: true, Titles: int.MaxValue));
        var catalogue = labels
            .Where(l => !l.Label.Contains(' ', StringComparison.Ordinal))
            .Select(l => (Word: l.Label, Curated: false, Titles: l.Titles));

        return curated.Concat(catalogue)
            .Where(c => c.Word.Length > 0 && c.Word[0] == word[0] && c.Word != word)
            .Select(c => (c.Word, c.Curated, c.Titles, Distance: EditDistance(word, c.Word)))
            .Where(c => c.Distance <= MaxDistance(c.Word))
            .OrderBy(c => c.Distance)
            .ThenByDescending(c => c.Curated)
            .ThenBy(c => Math.Abs(c.Word.Length - word.Length))
            // A typo drops a letter more often than it adds one: "funy" is "funny", not "fun".
            .ThenByDescending(c => c.Word.Length)
            .ThenByDescending(c => c.Titles)
            .ThenBy(c => c.Word, StringComparer.Ordinal)
            .Select(c => c.Word)
            .FirstOrDefault();
    }

    private static int MaxDistance(string word) => word.Length >= 8 ? 3 : word.Length >= 5 ? 2 : 1;

    private static bool Names(string phrase, IReadOnlyList<VocabularyLabel> labels) =>
        ConceptSynonyms.GenreWords.ContainsKey(phrase)
        || ConceptSynonyms.Groups.Any(g => g.Contains(phrase, StringComparer.Ordinal))
        || MoodLexicon.Words.Contains(phrase)
        || labels.Any(l => string.Equals(l.Label, phrase, StringComparison.Ordinal));

    private async Task<ConceptTerm> ResolveUnitAsync(string unit, IReadOnlyList<VocabularyLabel> labels, CancellationToken ct)
    {
        var concept = await ResolveOnceAsync(unit, ct);
        if (Reached(concept) || Correct(unit, labels) is not { } corrected)
        {
            return concept;
        }

        var retried = await ResolveOnceAsync(corrected, ct);
        return Reached(retried) ? retried : concept;
    }

    private async Task<ConceptTerm> ResolveOnceAsync(string unit, CancellationToken ct)
    {
        // A mood word ("fun", "cosy") stands for the genres and labels that carry it; the unit's own
        // words still match names and text, as any concept's do.
        var mood = MoodLexicon.Words.Contains(unit) ? MoodLexicon.Read(unit) : null;
        return await resolver.ResolveAsync(
            mood is null ? new ConceptTerm(unit, [], []) : new ConceptTerm(unit, [unit, .. mood.Labels], mood.Genres),
            ct);
    }

    private static bool Reached(ConceptTerm concept) =>
        concept.Genres.Count > 0 || concept.ExactLabels is null || concept.ExactLabels.Count > 0;

    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public static readonly NoEmbeddings Instance = new();

        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());
    }
}
