using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One title a mood found, with what carried the mood.</summary>
/// <param name="Evidence"><c>mood:&lt;mood&gt;</c> first, then <c>tone:</c>, <c>keyword:</c> and <c>genre:</c> strings for what matched.</param>
public sealed record MoodHit(Guid TitleId, IReadOnlyList<string> Evidence);

/// <summary>What the mood arm found for a request's moods.</summary>
/// <param name="Lists">One ranked list per mood the catalogue could read, best first.</param>
/// <param name="Implied">The attribute steers the moods imply, for <see cref="AttributeSteer.Merge"/>.</param>
/// <param name="Labels">Every exact vocabulary label the moods reached (for the semantic arm's label-embedding fallback).</param>
public sealed record MoodArmResult(
    IReadOnlyDictionary<string, IReadOnlyList<MoodHit>> Lists,
    IReadOnlyList<AttributeDirection> Implied,
    IReadOnlyList<string> Labels)
{
    /// <summary>No mood, or none the catalogue could read.</summary>
    public static MoodArmResult Empty { get; } = new(new Dictionary<string, IReadOnlyList<MoodHit>>(), [], []);
}

/// <summary>
/// The mood arm (WS-2's request: "mood and soft-preference search"): a request's moods ("feel-good",
/// "cosy", "tense") read through <see cref="MoodLexicon"/> — or, for a word it does not know, through
/// the concept resolver like any topic — and matched against the extracted TONE (trusted rows only),
/// keyword labels and genres, inside the caller's pool. Each mood yields its own ranked list for
/// fusion: a title carrying the mood's tone outranks one carrying only a label, which outranks a
/// genre-only match, and the crowd breaks ties.
///
/// <para>A mood never filters. With a hard concept the lists only re-rank the titles the concept
/// admits; without one they are the request's candidates. Before this arm, a mood-only request had
/// nothing to stand on but an uncached embedding (which misses its 1.0 s budget on first sight), so it
/// was answered with the catalogue's best-rated titles of any kind.</para>
/// </summary>
public sealed class MoodArm(IReelDbContext db, IConceptResolver resolver)
{
    /// <summary>How many titles each source (tone, labels and genres) reads per mood before scoring.</summary>
    public const int ReadDepth = 300;

    private const double ToneWeight = 2.0;
    private const double LabelWeight = 1.5;
    private const double GenreWeight = 0.5;

    /// <summary>
    /// The pool's titles carrying each of <paramref name="moods"/>, at most <paramref name="take"/> per
    /// mood. Moods the catalogue cannot read are left out of <see cref="MoodArmResult.Lists"/>.
    /// </summary>
    public async Task<MoodArmResult> FindAsync(IQueryable<Title> pool, IReadOnlyList<string> moods, int take, CancellationToken ct)
    {
        var distinct = moods.Select(ConceptResolver.Normalise).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0 || take <= 0)
        {
            return MoodArmResult.Empty;
        }

        var lists = new Dictionary<string, IReadOnlyList<MoodHit>>(StringComparer.Ordinal);
        var implied = new List<AttributeDirection>();
        var allLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mood in distinct)
        {
            var reading = await ReadAsync(mood, ct);
            implied.AddRange(reading.Directions);
            allLabels.UnionWith(reading.Labels);
            var hits = await HitsAsync(pool, mood, reading, take, ct);
            if (hits.Count > 0)
            {
                lists[mood] = hits;
            }
        }

        return new MoodArmResult(lists, implied, [.. allLabels.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The mood's reading with its labels resolved to exact vocabulary labels.</summary>
    internal async Task<MoodReading> ReadAsync(string mood, CancellationToken ct)
    {
        var reading = MoodLexicon.Read(mood);
        if (reading is null)
        {
            // A mood the lexicon does not know ("christmassy", "noir") is read like a topic: its words
            // through the synonyms and the vocabulary. Never its tone: tones are a closed set.
            var asConcept = await resolver.ResolveAsync(new ConceptTerm(mood, [], []), ct);
            return new MoodReading(mood, asConcept.Genres, asConcept.ExactLabels ?? asConcept.CatalogueLabels, [], []);
        }

        // The lexicon's stems reach their labels through the vocabulary, as a concept's do. No user
        // words: a mood is never searched for in a title's name.
        var resolved = reading.Labels.Count == 0
            ? null
            : await resolver.ResolveAsync(new ConceptTerm(string.Empty, reading.Labels, []), ct);
        // A stem reaching more labels than an overlap is worth (ConceptResolver.MaxExactLabels) keeps
        // its literal stems as exact labels: "love" then means the label "love", never the 400 it reaches.
        return reading with { Labels = resolved is null ? [] : resolved.ExactLabels ?? reading.Labels };
    }

    private async Task<IReadOnlyList<MoodHit>> HitsAsync(
        IQueryable<Title> pool, string mood, MoodReading reading, int take, CancellationToken ct)
    {
        var scored = new Dictionary<Guid, (double Score, int Order, List<string> Evidence)>();
        var order = 0;

        if (reading.Tones.Count > 0)
        {
            var tones = reading.Tones.ToList();
            var byTone = await CrowdVotes.BestShrunkFirst(pool.Where(t => db.TitleAttributes.Any(a =>
                    a.TitleId == t.Id && a.Status == AttributeExtractionStatus.Done && a.Tone != null && tones.Contains(a.Tone))))
                .Select(t => new { t.Id, Tone = db.TitleAttributes.Where(a => a.TitleId == t.Id).Select(a => a.Tone).FirstOrDefault() })
                .Take(ReadDepth)
                .ToListAsync(ct);
            foreach (var row in byTone)
            {
                Credit(scored, row.Id, ToneWeight, $"tone:{row.Tone}", ref order);
            }
        }

        if (HardConstraintFilter.SoftPredicate(reading.Labels, reading.Genres) is { } predicate)
        {
            var labels = reading.Labels.ToHashSet(StringComparer.Ordinal);
            var byLabel = await CrowdVotes.BestShrunkFirst(pool.Where(predicate))
                .Select(t => new Title { Id = t.Id, Name = t.Name, MediaType = t.MediaType, Language = t.Language, Keywords = t.Keywords, Genres = t.Genres })
                .Take(ReadDepth)
                .ToListAsync(ct);
            foreach (var row in byLabel)
            {
                var keyword = row.Keywords.FirstOrDefault(labels.Contains);
                if (keyword is not null)
                {
                    Credit(scored, row.Id, LabelWeight, $"keyword:{keyword}", ref order);
                }

                // The genre as an exclusion of it reads it (aliases, the anime composite, TV stand-ins),
                // so the evidence names the genre that actually matched.
                var genre = reading.Genres.FirstOrDefault(g => GenreStandIns.ExcludedBy(row, g));
                if (genre is not null)
                {
                    Credit(scored, row.Id, GenreWeight, $"genre:{genre}", ref order);
                }
            }
        }

        return [.. scored
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Value.Order)
            .Take(take)
            .Select(kv => new MoodHit(kv.Key, [$"mood:{mood}", .. kv.Value.Evidence]))];
    }

    private static void Credit(
        Dictionary<Guid, (double Score, int Order, List<string> Evidence)> scored, Guid id, double weight, string evidence, ref int order)
    {
        if (!scored.TryGetValue(id, out var entry))
        {
            entry = (0d, order++, []);
        }

        entry.Evidence.Add(evidence);
        scored[id] = (entry.Score + weight, entry.Order, entry.Evidence);
    }
}
