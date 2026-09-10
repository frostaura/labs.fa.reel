using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Assembles the <see cref="TasteFacts"/> the taste narrative is written from — and, because the
/// same object is persisted beside the text, the evidence a later reader checks that text against.
///
/// <para>Everything here already existed and was already being computed; none of it had a reader
/// that could speak. The genre affinities are the shrunken means the model itself scores on (not
/// the rating COUNTS the old twenty-token summary ordered by, which described what the account had
/// watched most rather than what it rated highest); the drift is the same recency-weighted
/// estimate the feature vector carries; the people come from <see cref="VerifiedPeople"/>'s ≥2
/// rated-titles bar, the identical rule the why-line templater is held to; the texture is the mean
/// of the seven extracted scalars over the titles the account actually loved.</para>
///
/// <para><b>Excluded genres are subtracted.</b> A genre the eligibility layer refuses to show is
/// not a favourite, and the old summary would happily open with one — asking the model to write
/// warmly about a taste the surface forbids.</para>
/// </summary>
public sealed class TasteFactsBuilder(
    IReelDbContext db,
    DislikeSignal dislikes,
    FeatureVectorBuilder features,
    IMemoryCache cache,
    // Optional: a construction site without it simply produces a fact sheet with no clusters, which
    // is also what an account below TasteClusterProvider.MinClusterInputs gets.
    TasteClusterProvider? tasteClusters = null)
{
    /// <summary>
    /// How long a computed profile hash is reused on the RENDER paths.
    ///
    /// <para>Every surface that shows lens copy needs the account's current fingerprint to know
    /// which cached notes are fresh — the feed, the digest, Saved and the title modal. Computing it
    /// means building the whole taste state and running half a dozen aggregates, which is fine once
    /// a night in a job and absurd on a page load that fires four of them at once. Two minutes is
    /// well inside a browsing session and far shorter than the fingerprint's own cadence, which is
    /// measured in weeks.</para>
    ///
    /// <para>Deliberately NOT used by the jobs: a generation pass must key its writes on a
    /// fingerprint it actually computed, not on one a page load left lying around.</para>
    /// </summary>
    public static readonly TimeSpan HashCacheTtl = TimeSpan.FromMinutes(2);

    /// <summary>The affinity list length. Matches <see cref="TasteProfileHash.TopN"/> — the hash reads the same list.</summary>
    public const int TopGenres = TasteProfileHash.TopN;

    /// <summary>Genres named by VOLUME. Six is the whole shape of a library without becoming a list.</summary>
    public const int MostWatchedGenres = 6;

    /// <summary>
    /// The floor on how many ratings a genre needs before its affinity may be called a preference,
    /// as an absolute count. See <see cref="MinGenreRatings"/> for why there is also a relative one.
    /// </summary>
    public const int MinGenreRatingsFloor = 15;

    /// <summary>Recent loves named to the writer. Enough for the prose to pick two or three from.</summary>
    public const int RecentLoves = 6;

    /// <summary>Recent dislikes named to the writer. Fewer — one is usually the whole point.</summary>
    public const int RecentDislikes = 4;

    /// <summary>
    /// How far back "recently" reaches for the recent-loves and recent-dislikes facts.
    ///
    /// <para><b>Narrative prose only. No feature, score or retrieval arm reads this.</b> It exists so
    /// a sentence can say "lately" and mean something; the model's own view of the account is
    /// all-time and separately recency-WEIGHTED, never truncated.</para>
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(120);

    /// <summary>Loved anchors named to the writer. More than this and the prose starts listing.</summary>
    public const int LovedAnchors = 8;

    /// <summary>Verified people named to the writer, best affinity first.</summary>
    public const int TopPeople = 6;

    /// <summary>A genre needs this many ratings before a drift figure means anything.</summary>
    private const int MinDriftObservations = 4;

    /// <summary>
    /// The share of total taste weight a cluster needs before the fact sheet will call it something
    /// the account "keeps returning to". Below three percent it is a weekend, not a taste.
    ///
    /// <para>Paired with <see cref="MinGenreRatingsFloor"/> as a member floor, for exactly the reason
    /// <see cref="MinGenreRatings"/> is two-sided: a relative bar alone lets three percent of a thin
    /// library pass as a preference, and an absolute bar alone is a meaningful pool at two hundred
    /// ratings and noise at ten thousand. <b>This is the PROSE bar and it is deliberately stricter
    /// than the retrieval one</b> — a small pocket absolutely should get its own ANN budget (that is
    /// the entire point of the partition), and absolutely should not open a sentence about who
    /// someone is.</para>
    /// </summary>
    public const decimal MinClusterShare = 0.03m;

    /// <summary>Pockets named on the fact sheet, heaviest first. More than this and the prose starts listing.</summary>
    public const int TopClusters = 4;

    /// <summary>Drift smaller than this is formula noise, not a change of taste.</summary>
    private const decimal MinDriftDelta = 0.3m;

    /// <summary>"Loved" for the texture aggregate — the same "loved, not tolerated" bar the eval harness uses.</summary>
    private const short LovedRatingFloor = 8;

    /// <summary>"Disliked" for the recent-dislikes fact.</summary>
    private const short DislikedRatingCeiling = 4;

    /// <summary>
    /// The minimum ratings a genre needs before its affinity is allowed to be called a preference:
    /// <see cref="MinGenreRatingsFloor"/>, or one percent of the account's whole-title ratings,
    /// whichever is larger.
    ///
    /// <para><b>Why a bar exists at all.</b> The affinity is a shrunken mean, and shrinkage bounds
    /// how far a small pool can stray from the user's own average — it does not stop a pool of
    /// twenty-six from outranking a pool of eight hundred. On the founder's account that produced a
    /// narrative opening on "adventure, family, superhero, fantasy and science-fiction", with
    /// holiday and family "especially high", for a library that is thriller, action, drama, comedy
    /// and crime by a factor of thirty. Every number in it was correct and the sentence was false,
    /// which is the worst kind of generated copy: unfalsifiable-looking and wrong.</para>
    ///
    /// <para><b>Why the bar is relative as well as absolute.</b> Fifteen ratings is a meaningful
    /// pool at two hundred ratings and noise at ten thousand. Integer division is deliberate: it
    /// makes the bar a whole number of ratings, so the boundary cannot sit between two counts.</para>
    /// </summary>
    public static int MinGenreRatings(int ratingsCount) => Math.Max(MinGenreRatingsFloor, ratingsCount / 100);

    /// <summary>The facts, plus the fingerprint they hash to and the loved ids that fed it.</summary>
    public sealed record Result(
        TasteFacts Facts, string ProfileHash, string ProfileFingerprint, IReadOnlyList<Guid> TopLovedTitleIds);

    /// <summary>One genre's shrunken affinity over one genre's rating count. The input to the ranking.</summary>
    public sealed record GenreStat(string Genre, decimal Affinity, int Count);

    /// <summary>The two genre views the fact sheet carries: by affinity, and by volume.</summary>
    public sealed record GenreRanking(
        IReadOnlyList<GenreFact> TopGenres, IReadOnlyList<GenreVolumeFact> MostWatchedGenres);

    /// <summary>
    /// The genre ranking, pure, so the ORDER can be asserted against a synthetic distribution
    /// without a database. <paramref name="stats"/> must already be affinity-ordered, which is
    /// what <see cref="BuildAsync"/> hands it.
    ///
    /// <para><b>Two lists, because there are two questions.</b> <c>TopGenres</c> answers "which
    /// genres does this account rate highest", restricted to genres it has rated often enough for
    /// a mean to carry information, and it is the list the profile fingerprint reads.
    /// <c>MostWatchedGenres</c> answers "what does this account actually watch", which is the
    /// question the opening sentence of a narrative is about and which nothing here could answer
    /// before.</para>
    /// </summary>
    public static GenreRanking RankGenres(IReadOnlyList<GenreStat> stats, int ratingsCount)
    {
        // `stats` arrives affinity-ordered, so filtering preserves that order.
        var minRatings = MinGenreRatings(ratingsCount);
        var qualified = stats.Where(g => g.Count >= minRatings).ToList();

        // A young account can have nothing clear the bar. An empty affinity list is worse than a
        // thin one — it strips the fingerprint of every genre component and leaves the narrative
        // with nothing to say — so the unfiltered ranking stands in until the library is big enough
        // for the bar to mean anything.
        var top = (qualified.Count > 0 ? qualified : stats)
            .Take(TopGenres)
            .Select(g => new GenreFact(g.Genre, Math.Round(g.Affinity, 2), g.Count))
            .ToList();

        var watched = stats
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Affinity)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .Take(MostWatchedGenres)
            .Select(g => new GenreVolumeFact(
                g.Genre,
                Math.Round(g.Affinity, 2),
                g.Count,
                ratingsCount > 0 ? Math.Round((decimal)g.Count / ratingsCount, 3) : 0m))
            .ToList();

        return new GenreRanking(top, watched);
    }

    /// <summary>
    /// The account's current profile hash, cached briefly — what a RENDER path needs and all it
    /// needs. See <see cref="HashCacheTtl"/> for why this exists separately from
    /// <see cref="BuildAsync"/>.
    /// </summary>
    public async Task<string> CurrentHashAsync(Guid accountId, DateTime asOf, CancellationToken ct)
    {
        var key = $"profilehash:{accountId}";
        if (cache.TryGetValue<string>(key, out var cached) && cached is { Length: > 0 })
        {
            return cached;
        }

        var taste = await features.BuildTasteStateAsync(accountId, asOf, ct);
        var built = await BuildAsync(accountId, taste, asOf, ct);
        cache.Set(key, built.ProfileHash, HashCacheTtl);
        return built.ProfileHash;
    }

    public async Task<Result> BuildAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime asOf, CancellationToken ct)
    {
        var excludedGenres = (await db.ContentFilters
                .Where(f => f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre)
                .Select(f => f.Value)
                .ToListAsync(ct))
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .ToHashSet();

        var genreStats = taste.GenreRatings
            .Where(kv => !excludedGenres.Contains(kv.Key))
            .Select(kv => new
            {
                Genre = kv.Key,
                Affinity = TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean),
                Recent = TasteMath.RecencyWeightedMean(kv.Value, asOf, taste.UserMean),
                kv.Value.Count,
            })
            // Count then name as tie-breakers: shrunken means collide readily on small pools, and a
            // fact list that reshuffles between runs churns the profile hash for nothing.
            .OrderByDescending(g => g.Affinity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .ToList();

        var ratingsCount = await db.UserRatings
            .CountAsync(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show), ct);

        var (topGenres, mostWatchedGenres) = RankGenres(
            genreStats.Select(g => new GenreStat(g.Genre, g.Affinity, g.Count)).ToList(), ratingsCount);

        var drifting = genreStats
            .Where(g => g.Count >= MinDriftObservations && Math.Abs(g.Recent - g.Affinity) >= MinDriftDelta)
            .OrderByDescending(g => g.Recent - g.Affinity)
            .Take(3)
            .Select(g => new DriftFact(g.Genre, Math.Round(g.Recent - g.Affinity, 2)))
            .ToList();

        var lovedRank = taste.TopLovedTitleIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        var lovedIds = taste.TopLovedTitleIds.Take(LovedAnchors).ToList();
        var lovedRatings = await db.UserRatings
            .Where(r => r.AccountId == accountId && lovedIds.Contains(r.TitleId)
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating })
            .ToDictionaryAsync(r => r.TitleId, r => r.Rating, ct);
        var loved = (await db.Titles
                .Where(t => lovedIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name, t.Year })
                .ToListAsync(ct))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => lovedRank[t.Id])
            .Select(t => new LovedFact(t.Name, t.Year, lovedRatings.GetValueOrDefault(t.Id)))
            .ToList();

        var dislikeProfile = await dislikes.GetProfileAsync(accountId, ct);
        var people = await VerifiedPeopleAsync(taste, ct);
        var texture = await TextureAsync(accountId, ct);

        var contrarian = new ContrarianFact(
            Math.Round(taste.OverallContrarianOffset, 2),
            topGenres
                .Where(g => taste.GenreContrarianOffset.ContainsKey(g.Genre))
                .Select(g => new GenreOffset(g.Genre, Math.Round(taste.GenreContrarianOffset[g.Genre], 2)))
                .ToList());

        var preferenceTags = await db.UserPreferenceTags
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Text)
            .ToListAsync(ct);

        var (recentLoves, recentDislikes) = await RecentAsync(accountId, asOf, ct);
        var clusters = await ClustersAsync(accountId, asOf, ratingsCount, ct);

        var facts = new TasteFacts(
            Math.Round(taste.UserMean, 2),
            ratingsCount,
            Math.Round(taste.MovieRatedShare, 2),
            topGenres,
            mostWatchedGenres,
            drifting,
            loved,
            recentLoves,
            recentDislikes,
            DislikeSignal.DeclinedLine(dislikeProfile),
            people,
            texture,
            contrarian,
            preferenceTags,
            clusters);

        // The account's last stored fingerprint is the hysteresis anchor — see TasteProfileHash.
        // Read here, once, so every caller (the feed build, the endpoints, both jobs) computes the
        // SAME hash. Two callers anchoring differently would key the same taste two ways and halve
        // the cache hit rate silently.
        var previous = await db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.Version)
            .Select(n => n.ProfileFingerprint)
            .FirstOrDefaultAsync(ct);

        var fingerprint = TasteProfileHash.Compute(
            facts, taste.TopLovedTitleIds, dislikeProfile.Version, previous);

        return new Result(facts, fingerprint.Hash, fingerprint.Components, taste.TopLovedTitleIds);
    }

    /// <summary>
    /// The pockets of taste worth naming, heaviest first.
    ///
    /// <para>Ranked by weight and filtered by <see cref="MinClusterShare"/> plus the same absolute
    /// member floor the genre bar uses. Exemplar titles are resolved to names here — a cluster has no
    /// label (that would be an LLM call with its own budget), so the titles nearest its centre ARE
    /// its name, and a pocket whose exemplars cannot be named is dropped rather than described as an
    /// anonymous percentage.</para>
    /// </summary>
    private async Task<List<ClusterFact>> ClustersAsync(
        Guid accountId, DateTime asOf, int ratingsCount, CancellationToken ct)
    {
        if (tasteClusters is null)
        {
            return [];
        }

        var partition = await tasteClusters.GetAsync(accountId, asOf, ct);
        var totalWeight = partition.Sum(c => c.Weight);
        if (partition.Count == 0 || totalWeight <= 0d)
        {
            return [];
        }

        var memberFloor = Math.Max(MinGenreRatingsFloor, ratingsCount / 100);
        var qualifying = partition
            .Where(c => (decimal)(c.Weight / totalWeight) >= MinClusterShare && c.MemberCount >= memberFloor)
            .Take(TopClusters)
            .ToList();
        if (qualifying.Count == 0)
        {
            return [];
        }

        var exemplarIds = qualifying.SelectMany(c => c.ExemplarTitleIds).Distinct().ToList();
        var names = await db.Titles
            .Where(t => exemplarIds.Contains(t.Id) && t.Name != null && t.Name != "")
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return qualifying
            .Select(c => new ClusterFact(
                c.ExemplarTitleIds.Where(names.ContainsKey).Select(id => names[id]).ToList(),
                Math.Round((decimal)(c.Weight / totalWeight), 3),
                c.MemberCount))
            .Where(f => f.Exemplars.Count > 0)
            .ToList();
    }

    /// <summary>
    /// What this account has loved and disliked inside <see cref="RecentWindow"/>, by name and year.
    ///
    /// <para>The loved anchors are all-time and change on the order of months; these are the
    /// evidence for a sentence about NOW. Without them a narrative can only describe a person's
    /// average, and an average is the one thing nobody recognises themselves in.</para>
    ///
    /// <para>Ordered rating-then-recency-then-name, deterministically, so the fact sheet — which is
    /// persisted beside the narrative as the evidence it was checked against — is reproducible.</para>
    /// </summary>
    private async Task<(List<LovedFact> Loves, List<LovedFact> Dislikes)> RecentAsync(
        Guid accountId, DateTime asOf, CancellationToken ct)
    {
        var since = asOf - RecentWindow;
        var rows = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && r.RatedAt >= since
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
                && (r.Rating >= LovedRatingFloor || r.Rating <= DislikedRatingCeiling))
            .Join(
                db.Titles,
                r => r.TitleId,
                t => t.Id,
                (r, t) => new { t.Name, t.Year, r.Rating, r.RatedAt })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);

        var loves = rows
            .Where(x => x.Rating >= LovedRatingFloor)
            .OrderByDescending(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(RecentLoves)
            .Select(x => new LovedFact(x.Name, x.Year, x.Rating))
            .ToList();

        var dislikes_ = rows
            .Where(x => x.Rating <= DislikedRatingCeiling)
            .OrderBy(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(RecentDislikes)
            .Select(x => new LovedFact(x.Name, x.Year, x.Rating))
            .ToList();

        return (loves, dislikes_);
    }

    /// <summary>
    /// The account's taste-verified people, best affinity first. Same ≥2-rated-titles bar as
    /// <see cref="VerifiedPeople"/> — a person seen once is a coincidence, and the narrative must
    /// not present one as a preference. The role is the one the person most often holds in this
    /// account's rated library, so a director is described as a director.
    /// </summary>
    private async Task<List<PersonFact>> VerifiedPeopleAsync(FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var eligible = taste.PersonRatings
            .Where(kv => kv.Value.Count >= VerifiedPeople.MinRatedTitles)
            .Select(kv => new
            {
                PersonId = kv.Key,
                Average = TasteMath.ShrunkenMean(kv.Value, taste.UserMean),
                kv.Value.Count,
            })
            .OrderByDescending(p => p.Average)
            .ThenByDescending(p => p.Count)
            .Take(TopPeople * 3)
            .ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var ids = eligible.Select(p => p.PersonId).ToList();
        var names = await db.Persons
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var roles = (await db.TitleCredits
                .Where(c => ids.Contains(c.PersonId))
                .Select(c => new { c.PersonId, c.Role })
                .ToListAsync(ct))
            .GroupBy(c => c.PersonId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => c.Role)
                    .OrderByDescending(rg => rg.Count())
                    .ThenBy(rg => rg.Key)
                    .First().Key);

        return eligible
            .Where(p => names.ContainsKey(p.PersonId) && !string.IsNullOrWhiteSpace(names[p.PersonId]))
            .Select(p => new PersonFact(
                names[p.PersonId],
                roles.TryGetValue(p.PersonId, out var role) ? role.ToString() : nameof(CreditRole.Actor),
                Math.Round(p.Average, 2),
                p.Count))
            .Take(TopPeople)
            .ToList();
    }

    /// <summary>
    /// The mean of the seven extracted scalars over the titles this account rated ≥8 — what the
    /// person's taste feels like, rather than what any one film does. Null when nothing loved has
    /// been enriched yet, which is honest: an average over zero titles is not a neutral 0.5, it is
    /// the absence of an answer, and the extractor's own rule against fabricated scalars applies
    /// with equal force to an aggregate of them.
    /// </summary>
    private async Task<TextureFact?> TextureAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.UserRatings
            .Where(r => r.AccountId == accountId && r.Rating >= LovedRatingFloor
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Join(
                db.TitleAttributes.Where(a => a.Status == AttributeExtractionStatus.Done),
                r => r.TitleId,
                a => a.TitleId,
                (_, a) => new
                {
                    a.Darkness,
                    a.Pacing,
                    a.Complexity,
                    a.EmotionalIntensity,
                    a.Humor,
                    a.Optimism,
                    a.EnsembleVsSolo,
                })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return null;
        }

        return new TextureFact(
            Math.Round(rows.Average(r => r.Darkness), 2),
            Math.Round(rows.Average(r => r.Pacing), 2),
            Math.Round(rows.Average(r => r.Complexity), 2),
            Math.Round(rows.Average(r => r.EmotionalIntensity), 2),
            Math.Round(rows.Average(r => r.Humor), 2),
            Math.Round(rows.Average(r => r.Optimism), 2),
            Math.Round(rows.Average(r => r.EnsembleVsSolo), 2),
            rows.Count);
    }
}
