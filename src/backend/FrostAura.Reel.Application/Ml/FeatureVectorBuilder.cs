using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
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
///
/// <para><b>For the rating-mean pools the subtraction is a whole fold, not one rating.</b> Taking
/// only a row's own rating out of a large pool leaves a value that orders the pool's rows exactly
/// opposite to their labels, which the trees learned to read back (HREV2-RET-02). See
/// <see cref="FoldPools"/>. The embedding features keep the per-title exclusion. An artifact trained
/// before that recipe learned from leave-one-out rows, and is served its rated titles that way until
/// it is retrained (<see cref="OutOfFoldPools"/>).</para>
/// </remarks>
public class FeatureVectorBuilder(IReelDbContext db)
{
    /// <summary>
    /// Whether shows rated only season by season or episode by episode join the taste pools at their
    /// rolled-up rating (task 12, N-8).
    /// <list type="bullet">
    /// <item><b>Null (DI's default) — follow the account's Active artifact.</b> A serving taste state
    /// describes the user the way the model scoring it was trained to read one: with rollups only if
    /// that artifact's <see cref="TrainingRecipe"/> had them, and as the current recipe has them when
    /// there is no artifact yet (its first one will be). HREV2-RET-02: serving rollups to an artifact
    /// trained without them moved every 2020s prediction from about 6 to about 3.</item>
    /// <item><b>Training</b> (<see cref="ForTraining"/>) — the current recipe's
    /// <see cref="TrainingRecipe.ShowRollups"/>: a new artifact learns the current recipe.</item>
    /// <item><b>Explicit either way</b> — the offline ablation (<c>scripts/ml/show-rollup-ablation.sh</c>),
    /// which measures the rollup against the same data and code with it off.</item>
    /// </list>
    /// </summary>
    public bool? IncludeShowRollups { get; init; }

    /// <summary>
    /// Whether a rated title's rating-mean pools exclude its whole fold (<see cref="FoldPools"/>) or
    /// only its own rating (leave-one-out). An unrated candidate excludes nothing either way.
    /// <list type="bullet">
    /// <item><b>Null (DI's default) — follow the account's Active artifact</b>, exactly as
    /// <see cref="IncludeShowRollups"/> does: out-of-fold only if its <see cref="TrainingRecipe"/>
    /// trained that way (<see cref="TrainingRecipe.UsesOutOfFoldPools"/>), and as the current recipe
    /// when there is no artifact yet. HREV2-RET-02: an unstamped artifact trained on leave-one-out
    /// rows and handed out-of-fold ones put Boss Level, rated 7, at 2.0.</item>
    /// <item><b>Training</b> (<see cref="ForTraining"/>) — <see cref="TrainingRecipe.OutOfFoldPools"/>.</item>
    /// <item><b>Explicit either way</b> — the offline ablation, which measures out-of-fold pools against
    /// the leave-one-out they replaced on the same data and code.</item>
    /// </list>
    /// </summary>
    public bool? OutOfFoldPools { get; init; }

    /// <summary>
    /// This builder as training must use it: the current <see cref="TrainingRecipe"/> — its rollups
    /// and its pools — rather than whatever the soon-to-be-replaced Active artifact was trained with.
    /// An explicit setting (the ablation's) is kept.
    /// </summary>
    public FeatureVectorBuilder ForTraining() =>
        IncludeShowRollups is not null && OutOfFoldPools is not null
            ? this
            : new FeatureVectorBuilder(db)
            {
                IncludeShowRollups = IncludeShowRollups ?? TrainingRecipe.ShowRollups,
                OutOfFoldPools = OutOfFoldPools ?? TrainingRecipe.OutOfFoldPools,
            };

    /// <summary>
    /// How many folds the rating-mean pools are split into for a RATED title's own vector
    /// (<see cref="TrainingFold"/>). See <see cref="FoldPools"/> for why leave-one-out alone was not
    /// enough.
    /// </summary>
    public const int TrainingFolds = 5;

    /// <summary>
    /// The fold a rated title's own label sits in: a pure function of its id, so a training matrix is
    /// reproducible from its inputs and two runs over the same ratings agree row for row.
    /// </summary>
    public static int TrainingFold(Guid titleId) => TasteMath.StableBucket(titleId.ToString("N"), TrainingFolds);

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

    /// <summary>
    /// How far back <see cref="TasteState.RecentCentroid"/> reaches — <b>a drift arm layered on the
    /// all-time signal, not a filter on it.</b> See the comment at the cutoff for the full statement;
    /// in short, no rating is ever excluded from the taste state by age, and this window only decides
    /// which loved titles ALSO get a second, drift-shaped centroid of their own.
    /// </summary>
    public const int RecentCentroidWindowDays = 365;

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
    }

    /// <summary>Everything user-side the features need, derived strictly from events ≤ asOf.</summary>
    public sealed record TasteState(
        decimal UserMean,
        decimal OverallContrarianOffset,
        IReadOnlyDictionary<string, List<(short Rating, DateTime RatedAt)>> GenreRatings,
        IReadOnlyDictionary<string, decimal> GenreContrarianOffset,
        IReadOnlyDictionary<int, List<short>> DecadeRatings,
        IReadOnlyDictionary<Guid, List<short>> PersonRatings,
        IReadOnlyList<Guid> TopLovedTitleIds,
        float[]? LovedCentroid,
        float[]? RecentCentroid,
        IReadOnlyDictionary<Guid, float[]> LovedEmbeddings,
        SelfExclusion Self,
        IReadOnlyList<Guid> LovedAnchorPool,
        IReadOnlyDictionary<string, double> DislikedGenreWeights,
        float[]? NotInterestedCentroid,
        /// <summary>
        /// The share of the user's movie/show-level ratings that are MOVIES, 0..1 — 0.5 when they
        /// have rated nothing yet. It is a supply signal, not a feature: discovery uses it to ask
        /// TMDB for the two media in the proportion the user actually rates, instead of a fixed 1:1
        /// intake that produced an all-TV live feed on the founder's account.
        /// </summary>
        decimal MovieRatedShare,
        /// <summary>
        /// The same person pools as <see cref="PersonRatings"/>, split by the CAPACITY the person
        /// was credited in on each rated title — so a director's twenty-two films never count as
        /// acting evidence because of one cameo. Read by the serving side (verified people, why
        /// lines, stored affinities), never by the model features, which still read the role-blind
        /// <see cref="PersonRatings"/>: changing those is a feature-schema change and a retrain.
        /// An explicit person rating is a statement about the person, so it seeds every role.
        /// Null only where a caller built a state by hand; readers treat null as "no pools".
        /// </summary>
        IReadOnlyDictionary<(Guid PersonId, CreditRole Role), List<short>>? PersonRoleRatings = null,
        /// <summary>
        /// The rating-mean pools split by <see cref="TrainingFold"/>, so a rated title's vector can
        /// take its WHOLE fold out of every pool rather than its own rating alone. Null when the
        /// recipe is leave-one-out (<see cref="OutOfFoldPools"/>: an Active artifact trained that way,
        /// or the ablation's other arm) or a caller built the state by hand; <see cref="Build"/> then
        /// takes out the row's own rating alone.
        /// </summary>
        FoldPools? Folds = null,
        /// <summary>
        /// The user's OWN rating of each title a person was credited on, per capacity: title → rating,
        /// one entry per title, with no explicit person rating folded in. What every printed "you rate
        /// X N on average" is computed from (<see cref="Ranking.VerifiedPeople.OwnRatings(TasteState, Guid, CreditRole)"/>),
        /// because <see cref="PersonRoleRatings"/> is a model input: an explicit person rating replaces
        /// it with pseudo-observations. Null only where a caller built a state by hand.
        /// </summary>
        IReadOnlyDictionary<(Guid PersonId, CreditRole Role), IReadOnlyDictionary<Guid, short>>? PersonRoleTitleRatings = null);

    /// <summary>
    /// The rating-mean pools (the overall mean the shrinkage pulls toward, genre, decade, person and
    /// contrarian) per <see cref="TrainingFold"/>, so a training row is described by the OTHER folds.
    ///
    /// <para><b>Why leave-one-out leaked.</b> Training builds one taste state and describes every rated
    /// title from it with its own rating subtracted, so in a pool of N ratings summing to S a row
    /// rated r reads (S − r)/(N − 1): one pool, one value per rating, ordered exactly opposite to the
    /// label. FastTree found it. On the founder's copy (867 ratings of 2020s titles) a retrained model
    /// moved its 2020s mean prediction from 8.8 to 3.1 across ±0.004 of <c>decadeAffinity</c>, and a
    /// candidate — which reads S/N, the value of a row rated at the pool's mean — sat on that cliff:
    /// twelve rolled-up shows shifted the pool by 0.005 and every 2020s title fell from 6.1 to 3.0
    /// (HREV2-RET-02). A single new rating moves a decade pool by up to 0.004, so any rating could do
    /// the same until the next retrain.</para>
    ///
    /// <para><b>Out-of-fold instead.</b> A rated title's pools exclude every rating in its fold. Rows
    /// that share a fold and a pool share one value whatever their labels, so the value no longer
    /// orders labels within a pool; what is left is five fold means, which differ by sampling noise.
    /// An unrated candidate is in no fold's way and reads the full pool, exactly as before.</para>
    /// </summary>
    public sealed record FoldPools(
        IReadOnlyDictionary<int, (int Count, decimal Sum)> Overall,
        IReadOnlyDictionary<(string Genre, int Fold), List<(short Rating, DateTime RatedAt)>> Genre,
        IReadOnlyDictionary<(int Decade, int Fold), (int Count, decimal Sum)> Decade,
        IReadOnlyDictionary<(Guid PersonId, int Fold), (int Count, decimal Sum)> Person,
        IReadOnlyDictionary<int, (int Count, decimal Sum)> Contrarian,
        IReadOnlyDictionary<(string Genre, int Fold), (int Count, decimal Sum)> GenreContrarian);

    public async Task<TasteState> BuildTasteStateAsync(Guid accountId, DateTime asOf, CancellationToken ct)
    {
        // Movie/show-level ratings are the label-bearing events; everything user-side derives from
        // them. Episode and season ratings are still INGESTED, but since schema v4 deleted
        // `showEngagement` they feed no feature at all — see the note where it was computed.
        var titleRatings = await db.UserRatings
            .Where(r => r.AccountId == accountId && r.RatedAt <= asOf
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating, r.RatedAt, Rolled = false })
            .ToListAsync(ct);

        // A show rated only season by season or episode by episode joins the pools at its rolled-up
        // show-level rating (task 12, N-8): genre, decade, person and contrarian affinities and the
        // media share read it; the loved anchors below do not (an anchor is a title rated whole).
        var wholeRated = titleRatings.Select(r => r.TitleId).ToHashSet();
        var explicitMean = titleRatings.Count > 0 ? (decimal)titleRatings.Average(r => (double)r.Rating) : NeutralUserMean;
        var (includeShowRollups, outOfFoldPools) = await RecipeAsync(accountId, ct);
        if (includeShowRollups)
        {
            titleRatings.AddRange(ShowRatingRollup
                .Roll(await ShowRatingRollup.PartsAsync(db, accountId, asOf, ct), wholeRated, explicitMean)
                .Select(r => new { r.TitleId, r.Rating, r.RatedAt, Rolled = true }));
        }

        var ratedTitleIds = titleRatings.Select(r => r.TitleId).Distinct().ToList();
        var ratedTitles = await db.Titles
            .Where(t => ratedTitleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Genres, t.Year, t.TraktRating, t.MediaType })
            .ToDictionaryAsync(t => t.Id, ct);

        // Media mix over DISTINCT rated titles, not rating rows — a show rated once and a movie
        // rated once are one vote each. Neutral with nothing rated: an even split asserts nothing.
        var ratedMedia = ratedTitleIds.Where(ratedTitles.ContainsKey).ToList();
        var ratedMovies = ratedMedia.Count(id => ratedTitles[id].MediaType == MediaType.Movie);
        var movieRatedShare = ratedMedia.Count == 0
            ? 0.5m
            : (decimal)ratedMovies / ratedMedia.Count;

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

        // The same pools again, per fold (see FoldPools). Built in the same pass, from the same rows.
        var foldOverall = new Dictionary<int, (int Count, decimal Sum)>();
        var foldGenre = new Dictionary<(string, int), List<(short, DateTime)>>();
        var foldDecade = new Dictionary<(int, int), (int Count, decimal Sum)>();
        var foldContrarian = new Dictionary<int, (int Count, decimal Sum)>();
        var foldGenreContrarian = new Dictionary<(string, int), (int Count, decimal Sum)>();

        // The overall pool is every movie/show-level row, whether or not its title row is loaded —
        // SelfExclusion.RatingSum/RatingCount below count them all, so the folds must too.
        foreach (var rating in titleRatings)
        {
            Add(foldOverall, TrainingFold(rating.TitleId), rating.Rating);
        }

        foreach (var rating in titleRatings)
        {
            if (!ratedTitles.TryGetValue(rating.TitleId, out var title))
            {
                continue;
            }

            var fold = TrainingFold(rating.TitleId);

            if (!selfRatings.TryGetValue(rating.TitleId, out var seen) || rating.Rating > seen.Rating)
            {
                selfRatings[rating.TitleId] = new SelfRating(rating.Rating, rating.RatedAt, title.TraktRating);
            }

            foreach (var genre in title.Genres)
            {
                (genreRatings.TryGetValue(genre, out var list) ? list : genreRatings[genre] = []).Add((rating.Rating, rating.RatedAt));
                (foldGenre.TryGetValue((genre, fold), out var fList) ? fList : foldGenre[(genre, fold)] = []).Add((rating.Rating, rating.RatedAt));
            }

            if (title.Year is { } year)
            {
                var decade = (year / 10) * 10;
                (decadeRatings.TryGetValue(decade, out var dList) ? dList : decadeRatings[decade] = []).Add(rating.Rating);
                Add(foldDecade, (decade, fold), rating.Rating);
            }

            if (title.TraktRating is { } globalRating)
            {
                contrarianPairs.Add((rating.Rating, globalRating));
                Add(foldContrarian, fold, rating.Rating - globalRating);
                foreach (var genre in title.Genres)
                {
                    (genrePairs.TryGetValue(genre, out var gList) ? gList : genrePairs[genre] = []).Add((rating.Rating, globalRating));
                    Add(foldGenreContrarian, (genre, fold), rating.Rating - globalRating);
                }
            }
        }

        var genreContrarian = genrePairs.ToDictionary(kv => kv.Key, kv => TasteMath.ContrarianOffset(kv.Value));

        // Person affinities: ratings joined through credits of the rated titles.
        var credits = await db.TitleCredits
            .Where(c => ratedTitleIds.Contains(c.TitleId))
            .Select(c => new { c.TitleId, c.PersonId, c.Role })
            .ToListAsync(ct);
        var ratingByTitle = titleRatings
            .GroupBy(r => r.TitleId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Rating));
        var personRatings = new Dictionary<Guid, List<short>>();
        // One title can credit the same person twice in one role (two writing credits); the
        // role-blind pool has always counted each credit, the per-role pool counts each TITLE once.
        var personRoleTitles = new Dictionary<(Guid PersonId, CreditRole Role), Dictionary<Guid, short>>();
        var foldPerson = new Dictionary<(Guid, int), (int Count, decimal Sum)>();
        foreach (var credit in credits)
        {
            if (ratingByTitle.TryGetValue(credit.TitleId, out var rating))
            {
                (personRatings.TryGetValue(credit.PersonId, out var list) ? list : personRatings[credit.PersonId] = []).Add(rating);
                // One entry per credit ROW, exactly as the role-blind pool above counts them.
                Add(foldPerson, (credit.PersonId, TrainingFold(credit.TitleId)), rating);
                var key = (credit.PersonId, credit.Role);
                (personRoleTitles.TryGetValue(key, out var titles) ? titles : personRoleTitles[key] = [])[credit.TitleId] = rating;
            }
        }

        var personRoleRatings = personRoleTitles.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList());

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
            foreach (var role in Enum.GetValues<CreditRole>())
            {
                personRoleRatings[(pr.PersonId, role)] = Enumerable.Repeat(pr.Rating, TasteMath.ShrinkageK).ToList();
            }
        }

        // These persons' pools are the explicit rating, NOT a sum of title ratings — so there is no
        // per-title contribution to subtract and leave-one-out must skip them.
        var explicitPersonIds = explicitPersonRatings.Select(pr => pr.PersonId).ToHashSet();

        // NOTE: a per-show mean of episode/season ratings used to be computed here for the
        // `showEngagement` feature. That feature was pinned to 0 from Phase 8b and deleted outright
        // in schema v4, so this was a database round-trip per taste-state build feeding a field
        // nothing read. Removed with the slot.

        // Loved set for embedding similarity features (centroids tolerate missing embeddings).
        var topLoved = titleRatings
            .Where(r => r.Rating >= LovedThreshold && !r.Rolled)
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
            .Where(r => r.Rating >= LovedThreshold && !r.Rolled)
            .GroupBy(r => r.TitleId)
            .Select(g => g.OrderByDescending(r => r.Rating).ThenByDescending(r => r.RatedAt).First())
            .OrderByDescending(r => r.Rating * TasteMath.RecencyWeight(r.RatedAt, asOf))
            .ThenByDescending(r => r.RatedAt)
            .Select(r => r.TitleId)
            .Take(LovedAnchorPoolCount)
            .ToList();

        var lovedIds = titleRatings.Where(r => r.Rating >= LovedThreshold).Select(r => r.TitleId).Distinct().ToList();

        // The ONLY window in this method, and it is a DRIFT arm layered on the all-time signal, not
        // a filter on it. `lovedIds` above is every loved title ever rated and feeds LovedCentroid
        // and the similarity features; this narrower set feeds RecentCentroid, which exists so a
        // taste that has moved in the last year can pull ~35% of the ANN budget its way. Nothing is
        // dropped by it: a title outside the window still counts in full through LovedCentroid, and
        // separately carries its own TasteMath.RecencyWeight wherever weighting applies. The founder
        // asked for "every single one, not just 180 days" and this is the window people find when
        // they look — so it is named rather than left as a literal, and 2,966 of his 3,354 watched
        // titles are older than a year, which is how much a real cutoff here would have cost.
        var recentCutoff = asOf.AddDays(-RecentCentroidWindowDays);
        var recentLovedIds = titleRatings
            .Where(r => r.Rating >= LovedThreshold && r.RatedAt >= recentCutoff)
            .Select(r => r.TitleId).Distinct().ToList();

        var lovedEmbeddings = await LoadEmbeddingsAsync(lovedIds, ct);
        var lovedCentroid = Centroid(lovedIds.Select(id => lovedEmbeddings.GetValueOrDefault(id)));
        var recentCentroid = Centroid(recentLovedIds.Select(id => lovedEmbeddings.GetValueOrDefault(id)));
        var topLovedEmbeddings = topLoved
            .Where(lovedEmbeddings.ContainsKey)
            .ToDictionary(id => id, id => lovedEmbeddings[id]);

        // ── Dislike signals (schema v3) — as-of aware like every other label source ─────────
        // A reaction created after asOf does not exist yet; one revoked after asOf was still live
        // at asOf. Reactions are not labels, so no leave-one-out is needed: a scored candidate is
        // by definition unreacted-to, and a training row's own reaction cannot exist (a rating
        // implies "seen", a dislike implies "won't watch" — the interaction-footprint rule keeps
        // them disjoint). Math shared with DislikeSignal so the served demotion and the trained
        // feature agree by construction.
        var dislikeReactions = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId
                && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.Dropped)
                && r.CreatedAt <= asOf && r.CreatedAt >= asOf.AddDays(-DislikeSignal.WindowDays)
                && (r.RevokedAt == null || r.RevokedAt > asOf))
            .Select(r => new { r.TitleId, r.Kind, r.Reason, r.CreatedAt })
            .ToListAsync(ct);

        var dislikedGenreWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        float[]? notInterestedCentroid = null;
        if (dislikeReactions.Count > 0)
        {
            var newestPerDislikedTitle = dislikeReactions
                .GroupBy(r => r.TitleId)
                .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
                .ToList();

            var dislikedIds = newestPerDislikedTitle.Select(r => r.TitleId).ToList();
            var dislikedGenres = await db.Titles
                .Where(t => dislikedIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Genres })
                .ToDictionaryAsync(t => t.Id, t => t.Genres, ct);

            foreach (var reaction in newestPerDislikedTitle.Where(r => r.Reason == ReactionReason.Genre))
            {
                if (!dislikedGenres.TryGetValue(reaction.TitleId, out var genres))
                {
                    continue;
                }

                var weight = DislikeSignal.RecencyWeight(reaction.CreatedAt, asOf);
                foreach (var genre in genres)
                {
                    dislikedGenreWeights[genre] = dislikedGenreWeights.GetValueOrDefault(genre) + weight;
                }
            }

            // Recency-weighted centroid over the NOT-INTERESTED titles' embeddings (Dropped is a
            // scheduling signal, not a similarity one — it feeds the genre map above when reasoned,
            // never the centroid).
            var notInterested = newestPerDislikedTitle
                .Where(r => r.Kind == ReactionKind.NotInterested)
                .ToList();
            var niEmbeddings = await LoadEmbeddingsAsync(notInterested.Select(r => r.TitleId).ToList(), ct);
            double[]? centroidSum = null;
            var centroidWeight = 0d;
            foreach (var reaction in notInterested)
            {
                if (!niEmbeddings.TryGetValue(reaction.TitleId, out var embedding))
                {
                    continue;
                }

                var weight = DislikeSignal.RecencyWeight(reaction.CreatedAt, asOf);
                centroidSum ??= new double[embedding.Length];
                if (centroidSum.Length == embedding.Length)
                {
                    for (var i = 0; i < embedding.Length; i++)
                    {
                        centroidSum[i] += weight * embedding[i];
                    }

                    centroidWeight += weight;
                }
            }

            if (centroidSum is not null && centroidWeight > 0d)
            {
                notInterestedCentroid = new float[centroidSum.Length];
                for (var i = 0; i < centroidSum.Length; i++)
                {
                    notInterestedCentroid[i] = (float)(centroidSum[i] / centroidWeight);
                }
            }
        }

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
            topLoved,
            lovedCentroid,
            recentCentroid,
            topLovedEmbeddings,
            self,
            lovedAnchorPool,
            dislikedGenreWeights,
            notInterestedCentroid,
            movieRatedShare,
            personRoleRatings,
            outOfFoldPools ? new FoldPools(foldOverall, foldGenre, foldDecade, foldPerson, foldContrarian, foldGenreContrarian) : null,
            personRoleTitles.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<Guid, short>)kv.Value));

        static void Add<TKey>(Dictionary<TKey, (int Count, decimal Sum)> pool, TKey key, decimal value)
            where TKey : notnull
        {
            var (count, sum) = pool.GetValueOrDefault(key);
            pool[key] = (count + 1, sum + value);
        }
    }

    /// <summary>
    /// The recipe this taste state is built with: each explicit setting as set, and each null one as
    /// the account's Active artifact was trained — with no artifact at all, as the current recipe,
    /// because the first one will be trained on it (see <see cref="IncludeShowRollups"/> and
    /// <see cref="OutOfFoldPools"/>). The artifact is read once, and only when something follows it.
    /// </summary>
    private async Task<(bool ShowRollups, bool OutOfFoldPools)> RecipeAsync(Guid accountId, CancellationToken ct)
    {
        if (IncludeShowRollups is { } rollups && OutOfFoldPools is { } outOfFold)
        {
            return (rollups, outOfFold);
        }

        var active = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => new { a.FeatureSchemaJson })
            .FirstOrDefaultAsync(ct);
        return (
            IncludeShowRollups ?? (active is null ? TrainingRecipe.ShowRollups : TrainingRecipe.UsesShowRollups(active.FeatureSchemaJson)),
            OutOfFoldPools ?? (active is null ? TrainingRecipe.OutOfFoldPools : TrainingRecipe.UsesOutOfFoldPools(active.FeatureSchemaJson)));
    }

    /// <summary>
    /// Every catalogue-side input a title's vector reads — the title row, its finished attribute
    /// extraction, its embedding and its credits — read once. The eval harness loads one of these
    /// per run and builds all five origins from it, so a catalogue refresh landing mid-run cannot
    /// give two origins (or two runs it compares) different title features: the title side is
    /// FROZEN per eval, and <c>FeatureSetHash</c> fingerprints exactly what was built from it.
    /// </summary>
    public sealed class TitleSide
    {
        private readonly HashSet<Guid> _requested;

        internal TitleSide(
            IEnumerable<Guid> requested,
            Dictionary<Guid, Title> titles,
            Dictionary<Guid, TitleAttributes> attributes,
            Dictionary<Guid, float[]> embeddings,
            Dictionary<Guid, List<CreditRow>> credits)
        {
            _requested = [.. requested];
            Titles = titles;
            Attributes = attributes;
            Embeddings = embeddings;
            Credits = credits;
        }

        /// <summary>The title rows, by id. A title missing here gets no vector.</summary>
        public IReadOnlyDictionary<Guid, Title> Titles { get; }

        internal Dictionary<Guid, TitleAttributes> Attributes { get; }

        internal Dictionary<Guid, float[]> Embeddings { get; }

        internal Dictionary<Guid, List<CreditRow>> Credits { get; }

        /// <summary>True when every id in <paramref name="titleIds"/> was part of this snapshot's read.</summary>
        internal bool Covers(IEnumerable<Guid> titleIds) => titleIds.All(_requested.Contains);
    }

    /// <summary>Reads the catalogue side of <paramref name="titleIds"/> once. See <see cref="TitleSide"/>.</summary>
    public async Task<TitleSide> LoadTitleSideAsync(IReadOnlyCollection<Guid> titleIds, CancellationToken ct)
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
        return new TitleSide(ids, titles, attributes, embeddings, creditsByTitle);
    }

    /// <summary>Builds vectors for the given titles against a prebuilt taste state.</summary>
    public async Task<Dictionary<Guid, float[]>> BuildAsync(
        TasteState taste, IReadOnlyCollection<Guid> titleIds, DateTime asOf, CancellationToken ct) =>
        Build(taste, titleIds, asOf, await LoadTitleSideAsync(titleIds, ct));

    /// <summary>
    /// Builds vectors for the given titles against a prebuilt taste state and a title side read
    /// earlier (<see cref="LoadTitleSideAsync"/>), touching no database. Every id must have been part
    /// of that read — a title outside it would silently get no vector, so that is refused instead.
    /// </summary>
    public Dictionary<Guid, float[]> Build(
        TasteState taste, IReadOnlyCollection<Guid> titleIds, DateTime asOf, TitleSide titleSide)
    {
        var ids = titleIds.Distinct().ToList();
        if (!titleSide.Covers(ids))
        {
            throw new ArgumentException("Every title must be part of the frozen title side it is built from.", nameof(titleIds));
        }

        var titles = titleSide.Titles;
        var attributes = titleSide.Attributes;
        var embeddings = titleSide.Embeddings;
        var creditsByTitle = titleSide.Credits;

        // ── Leave-one-out aggregates ───────────────────────────────────────────────────────
        // Rolled up ONCE per batch, not once per title: subtracting a title's own contribution is
        // then arithmetic on (count, sum) rather than a rebuild of the whole taste state.
        var genreStats = taste.GenreRatings.ToDictionary(kv => kv.Key, kv => GenreStatOf(kv.Value, asOf));

        var decadeStats = taste.DecadeRatings.ToDictionary(
            kv => kv.Key, kv => new Stat(kv.Value.Count, kv.Value.Sum(r => (decimal)r)));

        // The per-fold genre pools need the same recency weights at the same asOf as the full pools.
        var foldGenreStats = taste.Folds is { } folds
            ? folds.Genre.ToDictionary(kv => kv.Key, kv => GenreStatOf(kv.Value, asOf))
            : [];
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
            // What this row takes back out of the rating-mean pools: its whole fold where the state
            // carries folds (see FoldPools), its own rating where a hand-built state does not, and
            // nothing for an unrated candidate.
            var excluded = self is not { } rated
                ? Exclusion.None
                : taste.Folds is { } rowFolds
                    ? Exclusion.OutOfFold(rowFolds, foldGenreStats, TrainingFold(id))
                    : Exclusion.LeaveOneOut(rated, (decimal)TasteMath.RecencyWeight(rated.RatedAt, asOf), title.Genres);
            var prior = excluded.Prior(taste.Self, NeutralUserMean);

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
            var (genreAllTime, genreRecent) = GenreAffinities(genreStats, title.Genres, prior, excluded);
            vector[s++] = (float)genreAllTime;
            vector[s++] = (float)(genreRecent - genreAllTime);
            vector[s++] = (float)DecadeAffinity(decadeStats, title.Year, prior, excluded);

            // ── Cast & crew affinities ─────────────────────────────────────────────────
            var credits = creditsByTitle.GetValueOrDefault(id);
            var creditFeatures = CreditAffinities(taste, personStats, credits, prior, excluded);
            vector[s++] = (float)creditFeatures.Director;
            vector[s++] = (float)creditFeatures.Cast;
            vector[s++] = (float)creditFeatures.Writer;
            vector[s++] = creditFeatures.LovedCast;
            // Literally "credit rows exist" — the KNOWLEDGE half moved to the two v5 scalars below.
            vector[s++] = creditFeatures.HasCredits;

            // ── Contrarian calibration ─────────────────────────────────────────────────
            vector[s++] = (float)((title.TraktRating ?? prior) + ContrarianOffset(taste, title.Genres, excluded));

            // NOTE: v1-v3 wrote a `showEngagement` slot here, pinned to 0 on both paths. It was the
            // mean of the user's own episode/season ratings for THIS show, so it could only ever be
            // non-zero for a show they had already watched — which the founder-locked
            // zero-interaction-footprint rule guarantees a scored candidate never is. Train-only by
            // construction, therefore pure train/serve skew, therefore pinned; and pinned rather than
            // deleted only because deleting it renumbers the vector. Schema v4 takes that renumber
            // deliberately and the slot is gone. Do not reinstate it without a version bump.

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

            // ── Dislike signals (schema v3) ────────────────────────────────────────────
            // Neutral 0 when the account has no dislikes or the title no embedding. No leave-one-out
            // (see BuildTasteStateAsync): a reacted-to title can never be a training row or a
            // candidate, so there is no self-contribution to subtract.
            vector[s++] = embeddings.TryGetValue(id, out var candidateEmbedding)
                ? Cosine(candidateEmbedding, taste.NotInterestedCentroid)
                : 0f;
            vector[s++] = (float)DislikeSignal.GenreOverlap(title.Genres, taste.DislikedGenreWeights);

            // ── Attribute/metadata presence (schema v4) ────────────────────────────────
            // The v2 lesson applied to the four signals that carried the SAME defect one layer up:
            // certification and subgenres are written only by Trakt ingest (IngestMapper), never by
            // TitleHydrator, so before v4 a TMDB-discovered candidate scored certificationOrdinal 0
            // and 32 zero subgenre buckets while every training row — the user's own Trakt library —
            // had them populated. The model could learn "certification 0 ⇒ library title ⇒ rated",
            // which is a label proxy that serving can never reproduce. v4 both SOURCES certification
            // from TMDB during hydration and says out loud when it is missing.
            //
            // Tone/Era are gated on the extractor version as well as on presence: see
            // FeatureSchema.MinTrustedExtractorVersion. A stale row reads exactly like no row.
            var toneIndex = -1;
            var eraIndex = -1;
            if (attributes.TryGetValue(id, out var vocab)
                && vocab.ExtractorVersion >= FeatureSchema.MinTrustedExtractorVersion)
            {
                toneIndex = AttributeVocabulary.ToneIndex(vocab.Tone);
                eraIndex = AttributeVocabulary.EraIndex(vocab.Era);
            }

            vector[s++] = string.IsNullOrWhiteSpace(title.Certification) ? 0f : 1f;
            // Both sources feed the shared hash buckets below, so the flag has to cover both — a
            // title with themes but no Trakt subgenres genuinely does light buckets.
            vector[s++] = title.Subgenres.Length > 0
                || (attributes.TryGetValue(id, out var subgenreSource) && subgenreSource.Themes.Length > 0)
                    ? 1f
                    : 0f;
            vector[s++] = toneIndex >= 0 ? 1f : 0f;
            vector[s++] = eraIndex >= 0 ? 1f : 0f;

            // ── Credit knowledge + unscripted aggregate (schema v5) ────────────────────
            // The v2/v4 lesson one more time, on the last signal still telling it: hasCredits above
            // says only that credit ROWS exist. These two say how much of the people behind them the
            // user has any record with, which is the question directorAffinity / castAffinity /
            // writerAffinity are conditioned on and which nothing was answering.
            vector[s++] = creditFeatures.KnownPeopleFraction;
            vector[s++] = creditFeatures.KnownDirectorOrWriter;
            // The aggregate of the five v5 genre slots, via the SAME predicate the fit-verdict
            // demotion uses — one vocabulary, not two. Note `soap` deliberately does NOT light it:
            // a soap is scripted, it just shares the scheduling-filler economics, so it gets its own
            // genre slot and no claim about being unscripted.
            vector[s++] = UnscriptedFormats.IsUnscripted(title.Genres) ? 1f : 0f;

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

            s += FeatureSchema.SubgenreBuckets;

            // ── Tone one-hot (schema v4) ───────────────────────────────────────────────
            // At most one slot lights; an untrusted, out-of-vocabulary or missing tone lights none,
            // which is why hasAttributesTone sits beside it — an all-zero block would otherwise be
            // indistinguishable from a tone the model has simply never seen.
            if (toneIndex >= 0)
            {
                vector[s + toneIndex] = 1f;
            }

            s += FeatureSchema.Tones.Length;

            // ── Era one-hot (schema v4) ────────────────────────────────────────────────
            if (eraIndex >= 0)
            {
                vector[s + eraIndex] = 1f;
            }

            result[id] = vector;
        }

        return result;
    }

    /// <summary>Rolled-up ratings for one pool: enough to shrink, and enough to un-count one member.</summary>
    private readonly record struct Stat(int Count, decimal Sum);

    /// <summary>As <see cref="Stat"/>, plus the recency-weighted totals the drift feature needs.</summary>
    private readonly record struct GenreStat(int Count, decimal Sum, decimal WeightTotal, decimal WeightedSum);

    /// <summary>The (count, sum, weight) totals of one genre pool at <paramref name="asOf"/>.</summary>
    private static GenreStat GenreStatOf(IEnumerable<(short Rating, DateTime RatedAt)> ratings, DateTime asOf)
    {
        int count = 0;
        decimal sum = 0m, weightTotal = 0m, weightedSum = 0m;
        foreach (var (rating, ratedAt) in ratings)
        {
            var weight = (decimal)TasteMath.RecencyWeight(ratedAt, asOf);
            count++;
            sum += rating;
            weightTotal += weight;
            weightedSum += weight * rating;
        }

        return new GenreStat(count, sum, weightTotal, weightedSum);
    }

    /// <summary>
    /// All-time and recency-weighted genre affinity over the title's genres, with the row's
    /// <see cref="Exclusion"/> taken out of each. BOTH sides are shrunken with the same k against the
    /// same prior, so their difference measures change over time — previously the all-time side was
    /// shrunken and the recent side was not, and a genre with a single rating reported a large "drift"
    /// that was nothing but the gap between two formulas.
    /// </summary>
    private static (decimal AllTime, decimal Recent) GenreAffinities(
        IReadOnlyDictionary<string, GenreStat> genreStats,
        string[] genres,
        decimal prior,
        Exclusion excluded)
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

            var own = excluded.Genre(genre);
            var count = stat.Count - own.Count;
            allTimeTotal += TasteMath.ShrunkenMean(stat.Sum - own.Sum, count, prior);
            recentTotal += count <= 0
                ? prior
                : TasteMath.ShrunkenWeightedMean(stat.WeightedSum - own.WeightedSum, stat.WeightTotal - own.WeightTotal, prior);
        }

        return (allTimeTotal / genres.Length, recentTotal / genres.Length);
    }

    private static decimal DecadeAffinity(
        IReadOnlyDictionary<int, Stat> decadeStats, int? year, decimal prior, Exclusion excluded)
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

        var own = excluded.Decade(decade);
        return TasteMath.ShrunkenMean(stat.Sum - own.Sum, stat.Count - own.Count, prior);
    }

    /// <summary>
    /// What one row takes back out of the rating-mean pools before they describe it: nothing for an
    /// unrated candidate, its whole fold for a rated title (<see cref="FoldPools"/>), or — for a taste
    /// state built by hand without folds — its own rating alone, the leave-one-out it replaced. The
    /// embedding features keep their own self-exclusion: a title's similarity to the OTHER loved
    /// titles does not encode its label, only its similarity to itself does.
    /// </summary>
    private sealed class Exclusion
    {
        private readonly FoldPools? _folds;
        private readonly IReadOnlyDictionary<(string Genre, int Fold), GenreStat>? _foldGenres;
        private readonly int _fold;
        private readonly SelfRating? _self;
        private readonly decimal _selfWeight;
        private readonly string[] _selfGenres = [];

        private Exclusion(
            FoldPools? folds,
            IReadOnlyDictionary<(string, int), GenreStat>? foldGenres,
            int fold,
            SelfRating? self,
            decimal selfWeight,
            string[] selfGenres)
        {
            _folds = folds;
            _foldGenres = foldGenres;
            _fold = fold;
            _self = self;
            _selfWeight = selfWeight;
            _selfGenres = selfGenres;
        }

        /// <summary>An unrated candidate: every pool is read whole.</summary>
        public static Exclusion None { get; } = new(null, null, 0, null, 0m, []);

        public static Exclusion OutOfFold(
            FoldPools folds, IReadOnlyDictionary<(string, int), GenreStat> foldGenres, int fold) =>
            new(folds, foldGenres, fold, null, 0m, []);

        public static Exclusion LeaveOneOut(SelfRating self, decimal selfWeight, string[] genres) =>
            new(null, null, 0, self, selfWeight, genres);

        /// <summary>True for a rated title — whether by fold or by its own rating.</summary>
        public bool Excludes => _folds is not null || _self is not null;

        /// <summary>
        /// The overall mean every shrunken affinity is pulled toward, with this row's exclusion
        /// removed. Leaving the label in the prior is a small leak, and it costs one subtraction.
        /// </summary>
        public decimal Prior(SelfExclusion pool, decimal fallback)
        {
            var (count, sum) = _folds is { } folds
                ? folds.Overall.GetValueOrDefault(_fold)
                : _self is { } own ? (1, (decimal)own.Rating) : (0, 0m);
            var remaining = pool.RatingCount - count;
            return remaining > 0 ? (pool.RatingSum - sum) / remaining : fallback;
        }

        /// <summary>This row's share of one genre pool. A rated title sat in each of its OWN genres once.</summary>
        public GenreStat Genre(string genre) =>
            _foldGenres is { } foldGenres
                ? foldGenres.GetValueOrDefault((genre, _fold))
                : _self is { } own && _selfGenres.Contains(genre)
                    ? new GenreStat(1, own.Rating, _selfWeight, _selfWeight * own.Rating)
                    : default;

        public Stat Decade(int decade) =>
            _folds is { } folds
                ? Of(folds.Decade.GetValueOrDefault((decade, _fold)))
                : _self is { } own ? new Stat(1, own.Rating) : default;

        /// <summary>
        /// This row's share of one person's pool. A person credited on the title in two roles was
        /// handed its rating twice, so leave-one-out removes one copy per credit row
        /// (<paramref name="ownCredits"/>); a fold's pool already counted every one of them.
        /// </summary>
        public Stat Person(Guid personId, int ownCredits) =>
            _folds is { } folds
                ? Of(folds.Person.GetValueOrDefault((personId, _fold)))
                : _self is { } own ? new Stat(ownCredits, ownCredits * own.Rating) : default;

        /// <summary>This row's share of the overall (user − crowd) pool.</summary>
        public Stat Contrarian() =>
            _folds is { } folds
                ? Of(folds.Contrarian.GetValueOrDefault(_fold))
                : _self is { GlobalRating: { } global } own ? new Stat(1, own.Rating - global) : default;

        /// <summary>This row's share of one genre's (user − crowd) pool.</summary>
        public Stat GenreContrarian(string genre) =>
            _folds is { } folds
                ? Of(folds.GenreContrarian.GetValueOrDefault((genre, _fold)))
                : Contrarian();

        private static Stat Of((int Count, decimal Sum) pool) => new(pool.Count, pool.Sum);
    }

    internal sealed record CreditRow(Guid TitleId, Guid PersonId, CreditRole Role, int? CastOrder);

    /// <summary>
    /// The neutral-LOW imputation for a person-affinity nothing is known about (schema v5):
    /// <c>prior − 0.5</c>, floored at 1 so it stays inside the rating scale for a user whose mean is
    /// already at the bottom of it. See <see cref="FeatureSchema.NeutralLowPersonAffinityOffset"/>
    /// for why it is not the prior itself.
    /// </summary>
    private static decimal NeutralLowAffinity(decimal prior) =>
        Math.Max(1m, prior - FeatureSchema.NeutralLowPersonAffinityOffset);

    /// <summary>
    /// Person-affinity features, and the two v5 scalars that say how much of them is actually known.
    ///
    /// <para><b>Known-and-average and know-nothing used to be the same number.</b> A title whose
    /// credited people the user has never rated fell through every branch below and returned the
    /// prior — the user's own mean — for director, cast and writer alike, behind a
    /// <c>hasCredits = 1</c> that only ever meant "credit rows exist". On the founder's live account
    /// this made <c>directorAffinity</c> and <c>writerAffinity</c> the top POSITIVE contributors on
    /// precisely the titles with nobody known in them: a reality show with twelve unknown presenters
    /// and no director or writer row read as three solidly-average signals plus a presence flag.</para>
    ///
    /// <para>v5 separates the three questions that were collapsed into one. <c>hasCredits</c> keeps
    /// its literal meaning (any credit row exists) and is now honest because it is no longer the
    /// only thing asked. <c>knownPeopleFraction</c> carries how much of the cast and crew the user
    /// has any record with. <c>knownDirectorOrWriter</c> singles out the above-the-line roles, whose
    /// affinities are the ones that were being fabricated. And an unknown affinity is imputed
    /// neutral-LOW rather than at the mean, so the fabricated value at least errs toward "we do not
    /// know" instead of toward "this is for you".</para>
    ///
    /// <para><b>Leave-one-out holds for the two new scalars by construction:</b> both are computed
    /// from the SAME post-subtraction counts the affinities are. A training row whose only shared
    /// person is that title itself has <c>count &lt;= 0</c> after subtraction, so the person is not
    /// known — exactly as they would not be at serving time, where the candidate is unrated and
    /// contributed nothing to any pool. One path, no divergence.</para>
    /// </summary>
    private static CreditFeatures CreditAffinities(
        TasteState taste,
        IReadOnlyDictionary<Guid, Stat> personStats,
        IReadOnlyList<CreditRow>? credits,
        decimal prior,
        Exclusion excluded)
    {
        if (credits is null || credits.Count == 0)
        {
            var unknown = NeutralLowAffinity(prior);
            return new CreditFeatures(unknown, unknown, unknown, 0f, 0f, 0f, 0f);
        }

        // A person credited on this title in two roles was handed this title's rating twice, so a
        // leave-one-out subtraction removes one copy per CREDIT ROW, not one per person.
        var selfCredits = new Dictionary<Guid, int>();
        if (excluded.Excludes)
        {
            foreach (var credit in credits)
            {
                selfCredits[credit.PersonId] = selfCredits.GetValueOrDefault(credit.PersonId) + 1;
            }
        }

        decimal directorSum = 0m, writerSum = 0m;
        int directorCount = 0, writerCount = 0, lovedCast = 0;
        var cast = new List<decimal>();
        // DISTINCT people, so a person credited twice on one title is one person in the fraction.
        var creditedPeople = new HashSet<Guid>();
        var knownPeople = new HashSet<Guid>();
        var knownAboveTheLine = false;

        foreach (var credit in credits)
        {
            creditedPeople.Add(credit.PersonId);
            if (!personStats.TryGetValue(credit.PersonId, out var stat))
            {
                continue;
            }

            var count = stat.Count;
            var sum = stat.Sum;
            // An EXPLICIT person rating replaced the derived pool with k pseudo-observations, so this
            // title never contributed to it and there is nothing to take out.
            if (excluded.Excludes && !taste.Self.ExplicitPersonIds.Contains(credit.PersonId))
            {
                var own = excluded.Person(credit.PersonId, selfCredits.GetValueOrDefault(credit.PersonId));
                count -= own.Count;
                sum -= own.Sum;
            }

            if (count <= 0)
            {
                continue;
            }

            // Known = the user has at least one rating that reaches this person, AFTER this title's
            // own rating is taken back out. That subtraction is what keeps the fraction honest on a
            // training row (see the leave-one-out note above).
            knownPeople.Add(credit.PersonId);

            switch (credit.Role)
            {
                case CreditRole.Director:
                    directorCount += count;
                    directorSum += sum;
                    knownAboveTheLine = true;
                    break;
                case CreditRole.Writer:
                    writerCount += count;
                    writerSum += sum;
                    knownAboveTheLine = true;
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

        // A role with nothing known behind it is imputed neutral-LOW, not at the prior. ShrunkenMean
        // still handles the KNOWN-but-thin case exactly as before — that is what shrinkage is for —
        // so this only ever fires where the alternative was inventing a number outright.
        var unknownAffinity = NeutralLowAffinity(prior);
        return new CreditFeatures(
            directorCount > 0 ? TasteMath.ShrunkenMean(directorSum, directorCount, prior) : unknownAffinity,
            cast.Count > 0 ? cast.Average() : unknownAffinity,
            writerCount > 0 ? TasteMath.ShrunkenMean(writerSum, writerCount, prior) : unknownAffinity,
            lovedCast,
            HasCredits: 1f,
            KnownPeopleFraction: creditedPeople.Count == 0
                ? 0f
                : (float)knownPeople.Count / creditedPeople.Count,
            KnownDirectorOrWriter: knownAboveTheLine ? 1f : 0f);
    }

    /// <summary>
    /// The credit block's outputs. A named record rather than a seven-place tuple: the two v5
    /// additions made the positional form a trap, and <c>HasCredits</c> vs <c>KnownPeopleFraction</c>
    /// are precisely the pair nobody should be able to transpose by accident.
    /// </summary>
    private readonly record struct CreditFeatures(
        decimal Director,
        decimal Cast,
        decimal Writer,
        float LovedCast,
        float HasCredits,
        float KnownPeopleFraction,
        float KnownDirectorOrWriter);

    /// <summary>
    /// The user's genre-level contrarian offset for this title, with the row's <see cref="Exclusion"/>
    /// (its fold's (user, crowd) pairs, or its own) removed — otherwise <c>contrarianAdjustedRating</c>
    /// for a once-rated genre is a rearranged copy of the label.
    /// </summary>
    private static decimal ContrarianOffset(TasteState taste, string[] genres, Exclusion excluded)
    {
        var ownOverall = excluded.Contrarian();
        var overallCount = taste.Self.ContrarianCount - ownOverall.Count;
        var overallSum = taste.Self.ContrarianSum - ownOverall.Sum;
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
                var own = excluded.GenreContrarian(genre);
                var count = stat.Count - own.Count;
                if (count > 0)
                {
                    total += (stat.Sum - own.Sum) / count;
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
