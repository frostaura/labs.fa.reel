using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Assembles feature vectors for (account, title) pairs from raw persisted events filtered to
/// <c>asOf</c>. THE leakage guarantee of the M2 gate lives here: training and evaluation pass
/// <c>asOf = SplitAt</c>, serving passes now — affinity/taste cache tables are never read.
/// Account-level taste state is computed once per call and reused across every title.
/// </summary>
/// <remarks>
/// <b>Leave-one-out is the second half of that guarantee.</b> <c>asOf</c> filtering stops the FUTURE
/// leaking in; it does nothing about a title's OWN label leaking sideways. Every user-side statistic
/// here — genre and decade affinity, cast/director/writer affinity, the contrarian offset, the loved
/// centroids and the top-loved similarities — is a pool built from rated titles, and a training row
/// IS one of those rated titles. Without subtraction, <c>simTopLovedMax</c> for a title in the user's
/// own loved set is <c>Cosine(a, a) = 1.0</c> exactly: a perfect proxy for the label. At serving time
/// that is structurally impossible (a scored candidate is by definition unrated), so it is pure
/// train/serve skew. Every feature below therefore subtracts the title's own contribution from the
/// pool that describes it. For an unrated candidate there is nothing to subtract and the code path is
/// identical — one path, no divergence between train and serve.
/// </remarks>
public class FeatureVectorBuilder(IReelDbContext db)
{
    private const decimal LovedThreshold = 8m;
    private const int TopLovedCount = 10;

    /// <summary>
    /// How many loved titles the "because you loved" ANCHOR rotation may draw from.
    ///
    /// Deliberately a SEPARATE, wider pool from <see cref="TopLovedCount"/>. That constant sizes
    /// <see cref="TasteState.TopLovedTitleIds"/> / <see cref="TasteState.LovedEmbeddings"/>, which
    /// feed the <c>simTopLovedMax</c> / <c>simTopLovedMean</c> FEATURES — widening it would silently
    /// change every vector the model has ever been trained on. Anchor choice is presentation, not
    /// features, so it gets its own pool and leaves the schema alone.
    /// </summary>
    private const int LovedAnchorPoolCount = 40;

    /// <summary>Prior used when leave-one-out leaves no observations at all (matches the cold-start mean).</summary>
    private const decimal NeutralUserMean = 7m;

    /// <summary>The user's own label on a title, plus the crowd rating it was paired against.</summary>
    public readonly record struct SelfRating(short Rating, DateTime RatedAt, decimal? GlobalRating);

    /// <summary>
    /// The aggregates needed to subtract a title's own label back out of the taste state before that
    /// taste state is used to describe it. Sums and counts rather than lists, so the subtraction is
    /// O(1) per title and the whole taste state is still built exactly once per batch.
    /// </summary>
    public sealed record SelfExclusion(
        IReadOnlyDictionary<Guid, SelfRating> Ratings,
        decimal RatingSum,
        int RatingCount,
        IReadOnlySet<Guid> LovedTitleIds,
        IReadOnlySet<Guid> RecentLovedTitleIds,
        int LovedEmbeddingCount,
        int RecentEmbeddingCount,
        IReadOnlySet<Guid> ExplicitPersonIds,
        int ContrarianCount,
        decimal ContrarianSum,
        IReadOnlyDictionary<string, (int Count, decimal Sum)> GenreContrarian)
    {
        /// <summary>An account with no history — nothing to exclude.</summary>
        public static SelfExclusion Empty { get; } = new(
            new Dictionary<Guid, SelfRating>(),
            0m,
            0,
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            0,
            0,
            new HashSet<Guid>(),
            0,
            0m,
            new Dictionary<string, (int, decimal)>());

        /// <summary>
        /// The user's overall mean rating with <paramref name="self"/>'s own rating removed — the prior
        /// every shrunken affinity below is pulled toward. Leaving the label in the prior is a small
        /// leak (1/N) but it is still a leak, and it costs one subtraction to remove.
        /// </summary>
        public decimal MeanExcluding(SelfRating? self, decimal fallback)
        {
            if (self is not { } own)
            {
                return RatingCount > 0 ? RatingSum / RatingCount : fallback;
            }

            return RatingCount > 1 ? (RatingSum - own.Rating) / (RatingCount - 1) : fallback;
        }
    }

    /// <summary>Everything user-side the features need, derived strictly from events ≤ asOf.</summary>
    public sealed record TasteState(
        decimal UserMean,
        decimal OverallContrarianOffset,
        IReadOnlyDictionary<string, List<(short Rating, DateTime RatedAt)>> GenreRatings,
        IReadOnlyDictionary<string, decimal> GenreContrarianOffset,
        IReadOnlyDictionary<int, List<short>> DecadeRatings,
        IReadOnlyDictionary<Guid, List<short>> PersonRatings,
        IReadOnlyDictionary<Guid, decimal> ShowEngagement,
        IReadOnlyList<Guid> TopLovedTitleIds,
        float[]? LovedCentroid,
        float[]? RecentCentroid,
        IReadOnlyDictionary<Guid, float[]> LovedEmbeddings,
        SelfExclusion Self,
        IReadOnlyList<Guid> LovedAnchorPool);

    public async Task<TasteState> BuildTasteStateAsync(Guid accountId, DateTime asOf, CancellationToken ct)
    {
        // Movie/show-level ratings are the label-bearing events; everything user-side derives
        // from them (episode/season ratings feed only the showEngagement auxiliary signal).
        var titleRatings = await db.UserRatings
            .Where(r => r.AccountId == accountId && r.RatedAt <= asOf
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating, r.RatedAt })
            .ToListAsync(ct);

        var ratedTitleIds = titleRatings.Select(r => r.TitleId).Distinct().ToList();
        var ratedTitles = await db.Titles
            .Where(t => ratedTitleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Genres, t.Year, t.TraktRating })
            .ToDictionaryAsync(t => t.Id, ct);

        var userMean = titleRatings.Count > 0 ? (decimal)titleRatings.Average(r => (double)r.Rating) : NeutralUserMean;

        // Genre & decade affinities + contrarian pairs.
        var genreRatings = new Dictionary<string, List<(short, DateTime)>>();
        var decadeRatings = new Dictionary<int, List<short>>();
        var contrarianPairs = new List<(short User, decimal Global)>();
        var genrePairs = new Dictionary<string, List<(short User, decimal Global)>>();

        // Per-title self contribution, so any single title can be taken back out of the pools above.
        // The (AccountId, SubjectType, TitleId, Season, Episode) unique index means at most one
        // movie/show-level rating row per title, but keep the highest defensively.
        var selfRatings = new Dictionary<Guid, SelfRating>();

        foreach (var rating in titleRatings)
        {
            if (!ratedTitles.TryGetValue(rating.TitleId, out var title))
            {
                continue;
            }

            if (!selfRatings.TryGetValue(rating.TitleId, out var seen) || rating.Rating > seen.Rating)
            {
                selfRatings[rating.TitleId] = new SelfRating(rating.Rating, rating.RatedAt, title.TraktRating);
            }

            foreach (var genre in title.Genres)
            {
                (genreRatings.TryGetValue(genre, out var list) ? list : genreRatings[genre] = []).Add((rating.Rating, rating.RatedAt));
            }

            if (title.Year is { } year)
            {
                var decade = (year / 10) * 10;
                (decadeRatings.TryGetValue(decade, out var dList) ? dList : decadeRatings[decade] = []).Add(rating.Rating);
            }

            if (title.TraktRating is { } globalRating)
            {
                contrarianPairs.Add((rating.Rating, globalRating));
                foreach (var genre in title.Genres)
                {
                    (genrePairs.TryGetValue(genre, out var gList) ? gList : genrePairs[genre] = []).Add((rating.Rating, globalRating));
                }
            }
        }

        var genreContrarian = genrePairs.ToDictionary(kv => kv.Key, kv => TasteMath.ContrarianOffset(kv.Value));

        // Person affinities: ratings joined through credits of the rated titles.
        var credits = await db.TitleCredits
            .Where(c => ratedTitleIds.Contains(c.TitleId))
            .Select(c => new { c.TitleId, c.PersonId })
            .ToListAsync(ct);
        var ratingByTitle = titleRatings
            .GroupBy(r => r.TitleId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Rating));
        var personRatings = new Dictionary<Guid, List<short>>();
        foreach (var credit in credits)
        {
            if (ratingByTitle.TryGetValue(credit.TitleId, out var rating))
            {
                (personRatings.TryGetValue(credit.PersonId, out var list) ? list : personRatings[credit.PersonId] = []).Add(rating);
            }
        }

        // Explicit person ratings (time-filtered like every other label, so the M2 gate stays
        // leakage-clean) OVERRIDE the derived signal for that person: inject the rating as k
        // identical pseudo-observations (k = shrinkage constant), which dominates ShrunkenMean
        // (≈ a 50/50 blend toward the explicit value) and rides the existing cast/director/
        // writer-affinity features with no schema change. Replace, not append → no double-count.
        var explicitPersonRatings = await db.UserPersonRatings
            .Where(r => r.AccountId == accountId && r.RatedAt <= asOf)
            .Select(r => new { r.PersonId, r.Rating })
            .ToListAsync(ct);
        foreach (var pr in explicitPersonRatings)
        {
            personRatings[pr.PersonId] = Enumerable.Repeat(pr.Rating, TasteMath.ShrinkageK).ToList();
        }

        // These persons' pools are the explicit rating, NOT a sum of title ratings — so there is no
        // per-title contribution to subtract and leave-one-out must skip them.
        var explicitPersonIds = explicitPersonRatings.Select(pr => pr.PersonId).ToHashSet();

        // Show engagement: episode/season ratings averaged per parent show.
        var showEngagement = (await db.UserRatings
                .Where(r => r.AccountId == accountId && r.RatedAt <= asOf
                    && (r.SubjectType == RatingSubjectType.Episode || r.SubjectType == RatingSubjectType.Season))
                .GroupBy(r => r.TitleId)
                .Select(g => new { g.Key, Mean = g.Average(r => (double)r.Rating) })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => (decimal)x.Mean);

        // Loved set for embedding similarity features (centroids tolerate missing embeddings).
        var topLoved = titleRatings
            .Where(r => r.Rating >= LovedThreshold)
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.RatedAt)
            .Select(r => r.TitleId)
            .Distinct()
            .Take(TopLovedCount)
            .ToList();

        // The anchor pool: the same loved set, but ordered by rating WEIGHTED BY RECENCY rather than
        // rating alone. A user with thousands of ratings has a strict top-10 that is frozen for ever
        // — which is exactly why "Because you loved X" showed the same three titles every build. This
        // ordering lets a recently-loved 8 outrank a decade-old 10, and it is deterministic (a pure
        // function of the ratings and asOf), so a build is reproducible from its own timestamp.
        var lovedAnchorPool = titleRatings
            .Where(r => r.Rating >= LovedThreshold)
            .GroupBy(r => r.TitleId)
            .Select(g => g.OrderByDescending(r => r.Rating).ThenByDescending(r => r.RatedAt).First())
            .OrderByDescending(r => r.Rating * TasteMath.RecencyWeight(r.RatedAt, asOf))
            .ThenByDescending(r => r.RatedAt)
            .Select(r => r.TitleId)
            .Take(LovedAnchorPoolCount)
            .ToList();

        var lovedIds = titleRatings.Where(r => r.Rating >= LovedThreshold).Select(r => r.TitleId).Distinct().ToList();
        var recentCutoff = asOf.AddDays(-365);
        var recentLovedIds = titleRatings
            .Where(r => r.Rating >= LovedThreshold && r.RatedAt >= recentCutoff)
            .Select(r => r.TitleId).Distinct().ToList();

        var lovedEmbeddings = await LoadEmbeddingsAsync(lovedIds, ct);
        var lovedCentroid = Centroid(lovedIds.Select(id => lovedEmbeddings.GetValueOrDefault(id)));
        var recentCentroid = Centroid(recentLovedIds.Select(id => lovedEmbeddings.GetValueOrDefault(id)));
        var topLovedEmbeddings = topLoved
            .Where(lovedEmbeddings.ContainsKey)
            .ToDictionary(id => id, id => lovedEmbeddings[id]);

        var self = new SelfExclusion(
            selfRatings,
            titleRatings.Sum(r => (decimal)r.Rating),
            titleRatings.Count,
            lovedIds.ToHashSet(),
            recentLovedIds.ToHashSet(),
            // Only members that actually HAD an embedding moved the centroid, so only they can be
            // subtracted back out of it.
            lovedIds.Count(lovedEmbeddings.ContainsKey),
            recentLovedIds.Count(lovedEmbeddings.ContainsKey),
            explicitPersonIds,
            contrarianPairs.Count,
            contrarianPairs.Sum(p => p.User - p.Global),
            genrePairs.ToDictionary(kv => kv.Key, kv => (kv.Value.Count, kv.Value.Sum(p => p.User - p.Global))));

        return new TasteState(
            userMean,
            TasteMath.ContrarianOffset(contrarianPairs),
            genreRatings,
            genreContrarian,
            decadeRatings,
            personRatings,
            showEngagement,
            topLoved,
            lovedCentroid,
            recentCentroid,
            topLovedEmbeddings,
            self,
            lovedAnchorPool);
    }

    /// <summary>Builds vectors for the given titles against a prebuilt taste state.</summary>
    public async Task<Dictionary<Guid, float[]>> BuildAsync(
        TasteState taste, IReadOnlyCollection<Guid> titleIds, DateTime asOf, CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        var titles = await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var attributes = await db.TitleAttributes
            .Where(a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .ToDictionaryAsync(a => a.TitleId, ct);
        var embeddings = await LoadEmbeddingsAsync(ids, ct);
        var creditsByTitle = (await db.TitleCredits
                .Where(c => ids.Contains(c.TitleId))
                .Select(c => new CreditRow(c.TitleId, c.PersonId, c.Role, c.CastOrder))
                .ToListAsync(ct))
            .GroupBy(c => c.TitleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Leave-one-out aggregates ───────────────────────────────────────────────────────
        // Rolled up ONCE per batch, not once per title: subtracting a title's own contribution is
        // then arithmetic on (count, sum) rather than a rebuild of the whole taste state.
        var genreStats = new Dictionary<string, GenreStat>(taste.GenreRatings.Count);
        foreach (var (genre, ratings) in taste.GenreRatings)
        {
            decimal sum = 0m, weightTotal = 0m, weightedSum = 0m;
            foreach (var (rating, ratedAt) in ratings)
            {
                var weight = (decimal)TasteMath.RecencyWeight(ratedAt, asOf);
                sum += rating;
                weightTotal += weight;
                weightedSum += weight * rating;
            }

            genreStats[genre] = new GenreStat(ratings.Count, sum, weightTotal, weightedSum);
        }

        var decadeStats = taste.DecadeRatings.ToDictionary(
            kv => kv.Key, kv => new Stat(kv.Value.Count, kv.Value.Sum(r => (decimal)r)));
        var personStats = taste.PersonRatings.ToDictionary(
            kv => kv.Key, kv => new Stat(kv.Value.Count, kv.Value.Sum(r => (decimal)r)));

        var result = new Dictionary<Guid, float[]>(ids.Count);
        foreach (var id in ids)
        {
            if (!titles.TryGetValue(id, out var title))
            {
                continue;
            }

            var vector = new float[FeatureSchema.VectorLength];
            var s = 0;

            // This title's own label, if the user has already rated it (training rows always have
            // one; scored candidates never do). Everything below subtracts it back out.
            SelfRating? self = taste.Self.Ratings.TryGetValue(id, out var own) ? own : null;
            var prior = taste.Self.MeanExcluding(self, NeutralUserMean);
            var selfWeight = self is { } weighted ? (decimal)TasteMath.RecencyWeight(weighted.RatedAt, asOf) : 0m;

            // ── Title scalars ──────────────────────────────────────────────────────────
            var released = title.ReleasedAt ?? title.FirstAiredAt;
            var ageYears = released is { } rel ? Math.Max(0d, (asOf - rel).TotalDays / 365.25) : 0d;
            vector[s++] = (float)Math.Log(1d + ageYears);
            // Missing crowd data is imputed NEUTRAL and flagged (hasRuntime / hasTraktRating /
            // hasPopularity, appended below). A literal 0 here told the trainer "0-minute film,
            // crowd score 0/10, zero popularity" for half the catalogue.
            vector[s++] = title.RuntimeMinutes is { } runtime ? runtime : FeatureSchema.NeutralRuntimeMinutes(title.MediaType);
            vector[s++] = title.MediaType == MediaType.Show ? 1f : 0f;
            vector[s++] = (float)(title.TraktRating ?? prior);
            vector[s++] = TasteMath.Log1p(title.TraktVotes);
            vector[s++] = TasteMath.Log1p(title.TmdbPopularity ?? FeatureSchema.NeutralPopularity(title.MediaType));
            vector[s++] = TasteMath.CertificationOrdinal(title.Certification);

            // ── User-genre interaction ─────────────────────────────────────────────────
            var (genreAllTime, genreRecent) = GenreAffinities(genreStats, title.Genres, prior, self, selfWeight);
            vector[s++] = (float)genreAllTime;
            vector[s++] = (float)(genreRecent - genreAllTime);
            vector[s++] = (float)DecadeAffinity(decadeStats, title.Year, prior, self);

            // ── Cast & crew affinities ─────────────────────────────────────────────────
            var credits = creditsByTitle.GetValueOrDefault(id);
            var (director, cast, writer, lovedCast, hasCredits) = CreditAffinities(taste, personStats, credits, prior, self);
            vector[s++] = (float)director;
            vector[s++] = (float)cast;
            vector[s++] = (float)writer;
            vector[s++] = lovedCast;
            vector[s++] = hasCredits;

            // ── Contrarian calibration ─────────────────────────────────────────────────
            vector[s++] = (float)((title.TraktRating ?? prior) + ContrarianOffset(taste, title.Genres, self));

            // ── Show engagement ────────────────────────────────────────────────────────
            // showEngagement is the mean of the user's own episode/season ratings for THIS show, so
            // it is non-zero only for a show they have already watched — and the founder-locked
            // zero-interaction-footprint rule guarantees a scored candidate never has. It is
            // therefore always 0 at serving time and only ever non-zero during training: the same
            // train/serve skew as the embedding and affinity leaks, just quieter, because a
            // train-only signal teaches the model to lean on something that can never fire in
            // production.
            //
            // Held at 0 on both paths rather than deleted, so v2 feature indices stay put — the
            // slot is dead weight FastTree will simply never split on, and removing it is a
            // schema-version decision to take deliberately, not a side effect of this fix.
            vector[s++] = 0f;

            // ── LLM attributes ─────────────────────────────────────────────────────────
            if (attributes.TryGetValue(id, out var attrs))
            {
                vector[s++] = (float)attrs.Darkness;
                vector[s++] = (float)attrs.Pacing;
                vector[s++] = (float)attrs.Complexity;
                vector[s++] = (float)attrs.EmotionalIntensity;
                vector[s++] = (float)attrs.Humor;
                vector[s++] = (float)attrs.Optimism;
                vector[s++] = (float)attrs.EnsembleVsSolo;
                vector[s++] = 1f;
            }
            else
            {
                s += 8; // zeros + hasAttributes = 0
            }

            // ── Embedding similarities ─────────────────────────────────────────────────
            if (embeddings.TryGetValue(id, out var embedding))
            {
                // The title's own vector is pulled out of both centroids and skipped in the
                // top-loved scan. Without this, a loved title scores Cosine(a, a) = 1.0 against
                // itself and simTopLovedMax becomes a perfect label proxy that serving can
                // never reproduce.
                var lovedCentroid = taste.Self.LovedTitleIds.Contains(id)
                    ? CentroidExcluding(taste.LovedCentroid, taste.Self.LovedEmbeddingCount, embedding)
                    : taste.LovedCentroid;
                var recentCentroid = taste.Self.RecentLovedTitleIds.Contains(id)
                    ? CentroidExcluding(taste.RecentCentroid, taste.Self.RecentEmbeddingCount, embedding)
                    : taste.RecentCentroid;

                vector[s++] = Cosine(embedding, lovedCentroid);
                vector[s++] = Cosine(embedding, recentCentroid);

                float max = 0f, simSum = 0f;
                var simCount = 0;
                foreach (var (lovedId, lovedVector) in taste.LovedEmbeddings)
                {
                    if (lovedId == id)
                    {
                        continue;
                    }

                    var similarity = Cosine(embedding, lovedVector);
                    max = simCount == 0 ? similarity : Math.Max(max, similarity);
                    simSum += similarity;
                    simCount++;
                }

                vector[s++] = simCount > 0 ? max : 0f;
                vector[s++] = simCount > 0 ? simSum / simCount : 0f;
                vector[s++] = 1f;
            }
            else
            {
                s += 5; // zeros + hasEmbedding = 0
            }

            // ── Crowd-data presence (schema v2) ────────────────────────────────────────
            vector[s++] = title.RuntimeMinutes is null ? 0f : 1f;
            vector[s++] = title.TraktRating is null ? 0f : 1f;
            vector[s++] = title.TmdbPopularity is null ? 0f : 1f;

            // ── Genre multi-hot ────────────────────────────────────────────────────────
            var genreSet = title.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var g = 0; g < FeatureSchema.Genres.Length; g++)
            {
                vector[s + g] = genreSet.Contains(FeatureSchema.Genres[g]) ? 1f : 0f;
            }

            s += FeatureSchema.Genres.Length;

            // ── Hashed subgenres/themes ────────────────────────────────────────────────
            foreach (var subgenre in title.Subgenres)
            {
                vector[s + TasteMath.StableBucket(subgenre, FeatureSchema.SubgenreBuckets)] = 1f;
            }

            if (attributes.TryGetValue(id, out var themed))
            {
                foreach (var theme in themed.Themes)
                {
                    vector[s + TasteMath.StableBucket(theme, FeatureSchema.SubgenreBuckets)] = 1f;
                }
            }

            result[id] = vector;
        }

        return result;
    }

    /// <summary>Rolled-up ratings for one pool: enough to shrink, and enough to un-count one member.</summary>
    private readonly record struct Stat(int Count, decimal Sum);

    /// <summary>As <see cref="Stat"/>, plus the recency-weighted totals the drift feature needs.</summary>
    private readonly record struct GenreStat(int Count, decimal Sum, decimal WeightTotal, decimal WeightedSum);

    /// <summary>
    /// All-time and recency-weighted genre affinity over the title's genres, with the title's own
    /// rating removed from each. BOTH sides are shrunken with the same k against the same prior, so
    /// their difference measures change over time — previously the all-time side was shrunken and the
    /// recent side was not, and a genre with a single rating reported a large "drift" that was
    /// nothing but the gap between two formulas.
    /// </summary>
    private static (decimal AllTime, decimal Recent) GenreAffinities(
        IReadOnlyDictionary<string, GenreStat> genreStats,
        string[] genres,
        decimal prior,
        SelfRating? self,
        decimal selfWeight)
    {
        if (genres.Length == 0)
        {
            return (prior, prior);
        }

        decimal allTimeTotal = 0m, recentTotal = 0m;
        foreach (var genre in genres)
        {
            if (!genreStats.TryGetValue(genre, out var stat))
            {
                allTimeTotal += prior;
                recentTotal += prior;
                continue;
            }

            var count = stat.Count;
            var sum = stat.Sum;
            var weightTotal = stat.WeightTotal;
            var weightedSum = stat.WeightedSum;
            if (self is { } own)
            {
                // A rated title contributed its rating to every one of its OWN genres, exactly once.
                count -= 1;
                sum -= own.Rating;
                weightTotal -= selfWeight;
                weightedSum -= selfWeight * own.Rating;
            }

            allTimeTotal += TasteMath.ShrunkenMean(sum, count, prior);
            recentTotal += TasteMath.ShrunkenWeightedMean(weightedSum, weightTotal, prior);
        }

        return (allTimeTotal / genres.Length, recentTotal / genres.Length);
    }

    private static decimal DecadeAffinity(
        IReadOnlyDictionary<int, Stat> decadeStats, int? year, decimal prior, SelfRating? self)
    {
        if (year is null)
        {
            return prior;
        }

        var decade = (year.Value / 10) * 10;
        if (!decadeStats.TryGetValue(decade, out var stat))
        {
            return prior;
        }

        var count = stat.Count;
        var sum = stat.Sum;
        if (self is { } own)
        {
            count -= 1;
            sum -= own.Rating;
        }

        return TasteMath.ShrunkenMean(sum, count, prior);
    }

    private sealed record CreditRow(Guid TitleId, Guid PersonId, CreditRole Role, int? CastOrder);

    private static (decimal Director, decimal Cast, decimal Writer, float LovedCast, float HasCredits) CreditAffinities(
        TasteState taste,
        IReadOnlyDictionary<Guid, Stat> personStats,
        IReadOnlyList<CreditRow>? credits,
        decimal prior,
        SelfRating? self)
    {
        if (credits is null || credits.Count == 0)
        {
            return (prior, prior, prior, 0f, 0f);
        }

        // A person credited on this title in two roles was handed this title's rating twice, so the
        // subtraction removes one copy per CREDIT ROW, not one per person.
        var selfCredits = new Dictionary<Guid, int>();
        if (self is not null)
        {
            foreach (var credit in credits)
            {
                selfCredits[credit.PersonId] = selfCredits.GetValueOrDefault(credit.PersonId) + 1;
            }
        }

        decimal directorSum = 0m, writerSum = 0m;
        int directorCount = 0, writerCount = 0, lovedCast = 0;
        var cast = new List<decimal>();

        foreach (var credit in credits)
        {
            if (!personStats.TryGetValue(credit.PersonId, out var stat))
            {
                continue;
            }

            var count = stat.Count;
            var sum = stat.Sum;
            // An EXPLICIT person rating replaced the derived pool with k pseudo-observations, so this
            // title never contributed to it and there is nothing to take out.
            if (self is { } own && !taste.Self.ExplicitPersonIds.Contains(credit.PersonId))
            {
                var copies = selfCredits.GetValueOrDefault(credit.PersonId);
                count -= copies;
                sum -= copies * own.Rating;
            }

            if (count <= 0)
            {
                continue;
            }

            switch (credit.Role)
            {
                case CreditRole.Director:
                    directorCount += count;
                    directorSum += sum;
                    break;
                case CreditRole.Writer:
                    writerCount += count;
                    writerSum += sum;
                    break;
                default:
                    if (credit.CastOrder is null or < 5)
                    {
                        cast.Add(TasteMath.ShrunkenMean(sum, count, prior));
                    }

                    if (sum / count >= LovedThreshold)
                    {
                        lovedCast++;
                    }

                    break;
            }
        }

        return (
            TasteMath.ShrunkenMean(directorSum, directorCount, prior),
            cast.Count > 0 ? cast.Average() : prior,
            TasteMath.ShrunkenMean(writerSum, writerCount, prior),
            lovedCast,
            1f);
    }

    /// <summary>
    /// The user's genre-level contrarian offset for this title, with the title's own (user, crowd)
    /// pair removed — otherwise <c>contrarianAdjustedRating</c> for a once-rated genre is a rearranged
    /// copy of the label.
    /// </summary>
    private static decimal ContrarianOffset(TasteState taste, string[] genres, SelfRating? self)
    {
        decimal? selfPair = self is { GlobalRating: { } global } own ? own.Rating - global : null;

        var overallCount = taste.Self.ContrarianCount - (selfPair is null ? 0 : 1);
        var overallSum = taste.Self.ContrarianSum - (selfPair ?? 0m);
        var overall = overallCount > 0 ? overallSum / overallCount : 0m;

        if (genres.Length == 0)
        {
            return overall;
        }

        var total = 0m;
        foreach (var genre in genres)
        {
            if (taste.Self.GenreContrarian.TryGetValue(genre, out var stat))
            {
                var count = stat.Count - (selfPair is null ? 0 : 1);
                if (count > 0)
                {
                    total += (stat.Sum - (selfPair ?? 0m)) / count;
                    continue;
                }
            }

            total += overall;
        }

        return total / genres.Length;
    }

    private async Task<Dictionary<Guid, float[]>> LoadEmbeddingsAsync(IReadOnlyCollection<Guid> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return [];
        }

        var ids = titleIds.Distinct().ToList();
        var rows = await db.TitleEmbeddings
            .Where(e => ids.Contains(e.TitleId))
            .Select(e => new { e.TitleId, e.Embedding })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.TitleId, r => r.Embedding.ToArray());
    }

    private static float[]? Centroid(IEnumerable<float[]?> vectors)
    {
        float[]? sum = null;
        var count = 0;
        foreach (var vector in vectors)
        {
            if (vector is null)
            {
                continue;
            }

            sum ??= new float[vector.Length];
            for (var i = 0; i < vector.Length; i++)
            {
                sum[i] += vector[i];
            }

            count++;
        }

        if (sum is null || count == 0)
        {
            return null;
        }

        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] /= count;
        }

        return sum;
    }

    /// <summary>
    /// The centroid with one member taken back out: (mean·n − own) / (n − 1). Null when the member
    /// WAS the centroid (n = 1) — a one-title centroid minus that title is not a taste signal, and
    /// <see cref="Cosine"/> treats null as "no similarity known" (0).
    /// </summary>
    private static float[]? CentroidExcluding(float[]? centroid, int count, float[] own)
    {
        if (centroid is null || count <= 1 || centroid.Length != own.Length)
        {
            return null;
        }

        var result = new float[centroid.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ((centroid[i] * count) - own[i]) / (count - 1);
        }

        return result;
    }

    private static float Cosine(float[] a, float[]? b)
    {
        if (b is null || a.Length != b.Length)
        {
            return 0f;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0f : (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
