using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// One verifiable thing a card shares with the title its row is anchored on, shaped for a chip.
/// </summary>
/// <param name="Kind"><c>person</c>, <c>keyword</c> or <c>theme</c>.</param>
/// <param name="Label">The display text — a person's name, a keyword, a theme.</param>
/// <param name="Role">For a person: <c>director</c>, <c>writer</c> or <c>cast</c>. Null otherwise.</param>
public sealed record SharedEvidence(string Kind, string Label, string? Role = null);

/// <summary>
/// The admission rule for an ANCHORED row — "Because you loved X" on Home, and the paginated pages
/// of the same rails. A candidate is admitted only when BOTH hold:
/// <list type="number">
/// <item>its plot-embedding similarity to the anchor is at least the anchor's own
/// <see cref="NeighbourRank"/>th-nearest-neighbour similarity among COMPARABLE titles — same medium,
/// no name twin (<see cref="NeighbourFloorAsync"/>) — or, when it shares
/// <see cref="StrongEvidenceItems"/> or more items with the anchor, at least
/// <see cref="StrongEvidenceFloor"/>; and</item>
/// <item>it shares at least one concrete, checkable item with the anchor: a credited person in the
/// same capacity, a TMDB keyword, or an extracted theme.</item>
/// </list>
///
/// <para><b>Why the floor is over comparable titles (HREV2-RET-05).</b> The embedding text leads with
/// the title's NAME (<c>EmbeddingText</c>), so an anchor's nearest neighbours are crowded with titles
/// that merely share a word of it: 33 of House's 50 nearest were name twins (Life as a House, Down
/// House, Dream House, Hell House LLC …) and 35 were movies. Their similarity set House's floor at
/// 0.433 and cut Grey's Anatomy (0.429, hospital, doctor, medical drama) and Private Practice (0.413).
/// Over shows with no name twin the 50th neighbour sits at 0.390. The Dark Knight's floor fell from
/// 0.542 to 0.507, Breaking Bad's from 0.414 to 0.390; Heat and John Wick barely moved.</para>
///
/// <para><b>Why both.</b> Measured on the founder's snapshot: anchor-to-member similarity averaged
/// 0.386 against 0.260 for random pairs (90th percentile 0.364) and 0.549 for the anchor's real
/// top-20 neighbours — so the rows were barely closer to their anchor than chance, and the
/// templater then invented the link out of a shared genre and one tone word. A similarity floor
/// alone admits plot-adjacent titles nobody could explain; shared evidence alone admits every film
/// with the same lead actor. The evidence that admitted a card is what the card shows as chips, so
/// the row's claim is checkable on its face.</para>
///
/// <para>Static and parameterised on the context and the cache rather than injected, so the feed
/// build, the paginator, the feed endpoint and the startup sweep all apply literally the same rule
/// without a constructor change to the hand-built handlers the tests construct.</para>
/// </summary>
public static partial class AnchorRowGate
{
    /// <summary>The neighbour whose similarity is the floor: the anchor's 50th nearest comparable title.</summary>
    public const int NeighbourRank = 50;

    /// <summary>
    /// How many nearest titles the floor reads before setting aside the incomparable ones (another
    /// medium, a name twin). 400 holds 50 comparable titles for every anchor measured; an anchor
    /// whose neighbourhood does not falls back to the 50th of everything, the stricter floor.
    /// </summary>
    public const int NeighbourScan = 400;

    /// <summary>
    /// How many shared items let a candidate in below the neighbour floor: two concrete overlaps (two
    /// story keywords, a director and a theme) are a reason a viewer would recognise even where the
    /// plot summaries are worded differently.
    /// </summary>
    public const int StrongEvidenceItems = 2;

    /// <summary>
    /// The least similarity strong evidence can carry a candidate in at: the 90th percentile of
    /// RANDOM title pairs on the founder's catalogue (0.364, measured 2026-10-24 — see the remarks).
    /// Below it the plots are no closer than chance and the shared items are coincidence.
    /// </summary>
    public const double StrongEvidenceFloor = 0.364;

    /// <summary>Most chips a card carries — the row is a glance, not a dossier.</summary>
    public const int MaxChips = 3;

    /// <summary>Top-billed cast only: a shared extra is not a reason anyone would recognise.</summary>
    public const int MaxCastOrder = 8;

    /// <summary>
    /// The floor is a catalogue property (the anchor's neighbourhood), not an account one, and it
    /// moves only when the catalogue's embeddings do — so it is cached for a while and shared.
    /// </summary>
    private static readonly TimeSpan FloorTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// TMDB keywords that describe the PRODUCTION, not the story — two films sharing a post-credits
    /// scene or a woman director share nothing a viewer would call a reason.
    /// </summary>
    private static readonly HashSet<string> MetaKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "duringcreditsstinger",
        "aftercreditsstinger",
        "woman director",
        "female director",
        "short film",
        "3d",
        "imax",
        "black and white",
        "silent film",
        "live action remake",
        "remake",
        "sequel",
        "prequel",
        "anime",
        "miniseries",
        "tv movie",
        "independent film",
        "biography",
        "3d animation",
        "2d animation",
        "stop motion",
        "adult animation",
        "live action and animation",
    };

    /// <summary>
    /// Story words so common they link a title to a tenth of the catalogue: carried by more than
    /// 2.5% of the titles that have any (measured 2026-10-24 on a copy of the founder's catalogue —
    /// 33,260 keyworded titles, 14,161 completed extractions). "Friendship" sat on 13.8% of
    /// extracted titles and "identity" on 9.7%, and the latter admitted Doctor Who to a Sherlock
    /// rail. Shared, they are not a reason anyone would recognise. A fixed list rather than a live
    /// document-frequency read so the gate stays a pure, testable rule; the counts are stable
    /// because the theme vocabulary is closed and the keyword head moves slowly.
    /// </summary>
    private static readonly HashSet<string> CommonKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "murder", "romance", "friendship", "revenge", "lgbt", "christmas", "new york city",
    };

    /// <inheritdoc cref="CommonKeywords"/>
    private static readonly HashSet<string> CommonThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "friendship", "survival", "identity", "family", "revenge", "grief", "coming of age", "romance",
        "redemption", "loyalty", "justice", "teamwork", "ambition", "betrayal", "family bonds", "isolation",
        "belonging", "deception", "trauma",
    };

    /// <summary>
    /// True when a keyword says something about the story that few titles share — not a production
    /// fact ("woman director", "sequel", any "based on …" source) and not a near-universal one.
    /// </summary>
    public static bool IsDistinctiveKeyword(string keyword) =>
        !MetaKeywords.Contains(keyword)
        && !CommonKeywords.Contains(keyword)
        && !keyword.StartsWith("based on ", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an extracted theme is specific enough to be a reason (see <see cref="CommonThemes"/>).</summary>
    public static bool IsDistinctiveTheme(string theme) => !CommonThemes.Contains(theme);

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The evidence as the jsonb array <c>FeedItem.EvidenceJson</c> stores — <c>[{kind, label, role}]</c>.</summary>
    public static string Serialize(IReadOnlyList<SharedEvidence> evidence) =>
        JsonSerializer.Serialize(evidence, EvidenceJsonOptions);

    /// <summary>
    /// Reads <c>FeedItem.EvidenceJson</c> back. Null for a null column (a row written before the
    /// evidence was stored); an unreadable document reads as no evidence rather than throwing,
    /// because a serving path must never fail on a stored row.
    /// </summary>
    public static IReadOnlyList<SharedEvidence>? Deserialize(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<SharedEvidence>>(json, EvidenceJsonOptions)?
                .Where(e => !string.IsNullOrWhiteSpace(e.Kind) && !string.IsNullOrWhiteSpace(e.Label))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A candidate's standing against one anchor.</summary>
    /// <param name="Similarity">Plot-embedding cosine to the anchor; null when either side has no embedding.</param>
    /// <param name="Evidence">What it verifiably shares with the anchor, strongest first, at most <see cref="MaxChips"/>.</param>
    /// <param name="Admitted">Whether it may sit on the anchor's row.</param>
    public sealed record Admission(double? Similarity, IReadOnlyList<SharedEvidence> Evidence, bool Admitted);

    /// <summary>
    /// The anchor's <see cref="NeighbourRank"/>th-nearest COMPARABLE neighbour's similarity: among the
    /// <see cref="NeighbourScan"/> nearest titles, those of the anchor's own medium whose names do not
    /// contain the anchor's or sit inside it (<see cref="IsNameTwin"/>). Falls back to the 50th of all
    /// of them when fewer than <see cref="NeighbourRank"/> are comparable. Null when the anchor has no
    /// embedding (no floor can be stated, so nothing is admitted). Cached per anchor; pass a null cache
    /// to always compute.
    /// </summary>
    public static async Task<double?> NeighbourFloorAsync(
        IReelDbContext db, IMemoryCache? cache, Guid anchorId, CancellationToken ct)
    {
        var key = $"anchorfloor:v2:{anchorId}";
        if (cache is not null && cache.TryGetValue<double?>(key, out var cached))
        {
            return cached;
        }

        var anchor = await db.TitleEmbeddings
            .Where(e => e.TitleId == anchorId)
            .Select(e => e.Embedding)
            .FirstOrDefaultAsync(ct);

        double? floor = null;
        if (anchor is not null)
        {
            // Ordered by distance with a stable tiebreak. The id tiebreak is the same planner nudge
            // CandidateRetriever relies on, and the connection's hnsw.iterative_scan makes the pull
            // whole whichever plan is chosen. The comparability filter runs on the pulled rows, not
            // in the ANN query, so the index is never asked to under-fill a filtered scan.
            var nearest = await db.TitleEmbeddings
                .Where(e => e.TitleId != anchorId)
                .OrderBy(e => e.Embedding.CosineDistance(anchor))
                .ThenBy(e => e.TitleId)
                .Take(NeighbourScan)
                .Select(e => new { e.TitleId, Distance = e.Embedding.CosineDistance(anchor) })
                .ToListAsync(ct);
            var nearestIds = nearest.Select(n => n.TitleId).Append(anchorId).ToList();
            var titles = await db.Titles
                .Where(t => nearestIds.Contains(t.Id))
                .Select(t => new { t.Id, t.MediaType, t.Name })
                .ToDictionaryAsync(t => t.Id, ct);
            var comparable = titles.TryGetValue(anchorId, out var self)
                ? nearest
                    .Where(n => titles.TryGetValue(n.TitleId, out var t)
                        && t.MediaType == self.MediaType && !IsNameTwin(self.Name, t.Name))
                    .ToList()
                : [];
            var rankedFrom = comparable.Count >= NeighbourRank ? comparable : nearest;
            floor = rankedFrom.Count == 0 ? null : 1d - rankedFrom[Math.Min(NeighbourRank, rankedFrom.Count) - 1].Distance;
        }

        cache?.Set(key, floor, FloorTtl);
        return floor;
    }

    /// <summary>
    /// Admission for every candidate against one anchor: similarity computed in SQL (no vectors
    /// leave the database), evidence from the catalogue. A candidate missing from the result was
    /// not found at all and is not admitted.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Admission>> AdmitAsync(
        IReelDbContext db, IMemoryCache? cache, Guid anchorId, IReadOnlyCollection<Guid> candidateIds, CancellationToken ct)
    {
        var ids = candidateIds.Where(id => id != anchorId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Admission>();
        }

        var floor = await NeighbourFloorAsync(db, cache, anchorId, ct);
        var anchorVector = await db.TitleEmbeddings
            .Where(e => e.TitleId == anchorId)
            .Select(e => e.Embedding)
            .FirstOrDefaultAsync(ct);

        var similarity = anchorVector is null
            ? new Dictionary<Guid, double>()
            : (await db.TitleEmbeddings
                    .Where(e => ids.Contains(e.TitleId))
                    .Select(e => new { e.TitleId, Distance = e.Embedding.CosineDistance(anchorVector) })
                    .ToListAsync(ct))
                .ToDictionary(x => x.TitleId, x => 1d - x.Distance);

        var facts = await LoadFactsAsync(db, ids.Append(anchorId).ToList(), ct);
        var anchorFacts = facts.GetValueOrDefault(anchorId) ?? Facts.None;

        var result = new Dictionary<Guid, Admission>(ids.Count);
        foreach (var id in ids)
        {
            double? sim = similarity.TryGetValue(id, out var s) ? s : null;
            var evidence = Between(anchorFacts, facts.GetValueOrDefault(id) ?? Facts.None);
            var admitted = floor is { } f && sim is { } value && evidence.Count > 0
                && (value >= f || (evidence.Count >= StrongEvidenceItems && value >= StrongEvidenceFloor));
            result[id] = new Admission(sim, evidence, admitted);
        }

        return result;
    }

    /// <summary>
    /// True when one title's name contains the other's as a whole run of words ("House" in "Life as a
    /// House", "The Dark Knight" in "The Dark Knight Rises"): a neighbour by its name, which leads the
    /// embedding text, rather than by its story. Case- and punctuation-blind; a word inside a longer
    /// word ("Madhouse") is not a twin.
    /// </summary>
    public static bool IsNameTwin(string? anchorName, string? otherName)
    {
        var a = Words(anchorName);
        var b = Words(otherName);
        return a.Length > 0 && b.Length > 0 && (ContainsRun(b, a) || ContainsRun(a, b));

        static string[] Words(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? []
                : [.. NonWord().Split(name.ToLowerInvariant()).Where(w => w.Length > 0)];

        static bool ContainsRun(string[] haystack, string[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    return true;
                }
            }

            return false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial System.Text.RegularExpressions.Regex NonWord();

    /// <summary>
    /// The shared evidence for many (anchor, member) pairs in one pass — the read the feed endpoint
    /// uses to put chips on cards a build already admitted. No floor, no similarity: admission was
    /// decided when the row was dealt.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(Guid AnchorId, Guid TitleId), IReadOnlyList<SharedEvidence>>> EvidenceAsync(
        IReelDbContext db, IReadOnlyCollection<(Guid AnchorId, Guid TitleId)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return new Dictionary<(Guid, Guid), IReadOnlyList<SharedEvidence>>();
        }

        var facts = await LoadFactsAsync(
            db, pairs.SelectMany(p => new[] { p.AnchorId, p.TitleId }).Distinct().ToList(), ct);
        return pairs.Distinct().ToDictionary(
            p => p,
            p => Between(facts.GetValueOrDefault(p.AnchorId) ?? Facts.None, facts.GetValueOrDefault(p.TitleId) ?? Facts.None));
    }

    /// <summary>
    /// What two titles verifiably share, strongest first: people in the same capacity (director,
    /// writer, then top-billed cast), then story keywords, then extracted themes. Pure, so it is
    /// unit-tested directly.
    /// </summary>
    public static IReadOnlyList<SharedEvidence> Between(Facts anchor, Facts candidate)
    {
        var chips = new List<SharedEvidence>(MaxChips);

        foreach (var role in new[] { "director", "writer", "cast" })
        {
            foreach (var person in candidate.People.Where(p => p.Role == role))
            {
                if (chips.Count >= MaxChips)
                {
                    return chips;
                }

                if (anchor.People.Any(a => a.PersonId == person.PersonId && a.Role == role)
                    && chips.All(c => c.Label != person.Name))
                {
                    chips.Add(new SharedEvidence("person", person.Name, role));
                }
            }
        }

        foreach (var keyword in candidate.Keywords)
        {
            if (chips.Count >= MaxChips)
            {
                return chips;
            }

            if (IsDistinctiveKeyword(keyword) && anchor.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)
                && chips.All(c => !string.Equals(c.Label, keyword, StringComparison.OrdinalIgnoreCase)))
            {
                chips.Add(new SharedEvidence("keyword", keyword));
            }
        }

        foreach (var theme in candidate.Themes)
        {
            if (chips.Count >= MaxChips)
            {
                return chips;
            }

            if (IsDistinctiveTheme(theme) && anchor.Themes.Contains(theme, StringComparer.OrdinalIgnoreCase)
                && chips.All(c => !string.Equals(c.Label, theme, StringComparison.OrdinalIgnoreCase)))
            {
                chips.Add(new SharedEvidence("theme", theme));
            }
        }

        return chips;
    }

    /// <summary>One credited person in the shape the gate compares: a stable id, a display name, a capacity.</summary>
    public sealed record PersonFact(Guid PersonId, string Name, string Role);

    /// <summary>Everything the gate compares about one title.</summary>
    public sealed record Facts(IReadOnlyList<PersonFact> People, IReadOnlyList<string> Keywords, IReadOnlyList<string> Themes)
    {
        /// <summary>A title the catalogue knows nothing checkable about.</summary>
        public static Facts None { get; } = new([], [], []);
    }

    /// <summary>
    /// Credits (directors, writers, top-billed cast), keywords and themes for the given titles, in
    /// four reads. Themes come only from a completed extraction — a pending or failed row's themes
    /// are not facts.
    /// </summary>
    private static async Task<Dictionary<Guid, Facts>> LoadFactsAsync(IReelDbContext db, List<Guid> titleIds, CancellationToken ct)
    {
        var keywords = await db.Titles
            .Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Keywords })
            .ToDictionaryAsync(t => t.Id, t => t.Keywords, ct);

        var themes = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .Select(a => new { a.TitleId, a.Themes })
            .ToDictionaryAsync(a => a.TitleId, a => a.Themes, ct);

        var credits = await db.TitleCredits
            .Where(c => titleIds.Contains(c.TitleId)
                && (c.Role == CreditRole.Director || c.Role == CreditRole.Writer
                    || (c.Role == CreditRole.Actor && c.CastOrder != null && c.CastOrder < MaxCastOrder)))
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.PersonId, c.Role, c.CastOrder, p.Name })
            .ToListAsync(ct);

        var people = credits
            .GroupBy(c => c.TitleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PersonFact>)g
                    .OrderBy(c => c.Role)
                    .ThenBy(c => c.CastOrder ?? int.MaxValue)
                    .ThenBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => new PersonFact(c.PersonId, c.Name, RoleName(c.Role)))
                    .DistinctBy(p => (p.PersonId, p.Role))
                    .ToList());

        return titleIds.Distinct().ToDictionary(
            id => id,
            id => new Facts(
                people.GetValueOrDefault(id) ?? [],
                keywords.GetValueOrDefault(id) ?? [],
                themes.GetValueOrDefault(id) ?? []));
    }

    private static string RoleName(CreditRole role) => role switch
    {
        CreditRole.Director => "director",
        CreditRole.Writer => "writer",
        _ => "cast",
    };
}
