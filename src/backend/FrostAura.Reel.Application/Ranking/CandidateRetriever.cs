using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Hybrid personalized candidate retrieval — THE fix for popularity-gated recall. Every feed
/// surface used to seed its pool with <c>OrderByDescending(TmdbPopularity).Take(N)</c> BEFORE the
/// FastTree model scored anything, so the model only ever re-ranked the globally-popular head: a
/// title the user would rate 9 that simply isn't popular was never even scored, and "deep cuts"
/// was just the least-popular of the popular. This composes two arms over the SAME eligibility rule
/// and hands the union back to the existing scorer:
///
///   • Popularity arm — the coverage slice; keeps the familiar popular head everyone expects.
///   • ANN arm — pgvector cosine kNN over plot embeddings against the user's taste centroids
///     (all-time <see cref="FeatureVectorBuilder.TasteState.LovedCentroid"/> plus a smaller drift
///     pull vs <see cref="FeatureVectorBuilder.TasteState.RecentCentroid"/>), surfacing the
///     personalized titles the popularity sort buried.
///
/// Retrieval only widens WHICH titles enter scoring — the FastTree model still decides the order.
/// Cold start (no loved title carries an embedding ⇒ null LovedCentroid) skips the ANN arm and
/// degrades to pure popularity, so a fresh account is never worse off than the old behaviour.
/// </summary>
public sealed class CandidateRetriever(IReelDbContext db, EligibilityQueryBuilder eligibility)
{
    // Certification ceilings can't order in SQL, so maturity is an in-memory gate applied AFTER the
    // fetch (see EligibilityQueryBuilder.PassesMaturity). Over-fetch each arm so a restricted profile
    // — whose ceiling can drop a large slice of the popular head — still lands ~target survivors
    // instead of a starved pool.
    private const double MaturityOverFetch = 1.3d;

    // The drift pull vs RecentCentroid is a smaller slice of the ANN budget than the all-time loved
    // pull: it nudges a recent taste shift into the pool without letting one recent binge dominate it.
    private const double DriftFraction = 0.35d;

    /// <summary>
    /// The deduped, maturity-filtered hybrid pool for one lane. <paramref name="lane"/> is the
    /// caller's extra predicate (the poster/tmdbId gate, a genre rail, new-for-you recency, the
    /// paginated path's already-seen exclusions) applied to BOTH arms so neither can leak past
    /// eligibility. Ordering is the popularity head first, then ANN hits best-similarity first — a
    /// downstream <c>Take()</c> keeps the whole coverage slice plus the strongest personalized hits.
    /// </summary>
    /// <param name="popCount">Target popularity-arm survivors after maturity filtering.</param>
    /// <param name="annCount">Target ANN-arm survivors (loved + drift combined) after maturity filtering.</param>
    /// <param name="maturityCeiling">The account's certification ceiling, or null for no ceiling.</param>
    /// <param name="ignoreGenreFilters">
    /// Genre rails only — drops the account's ExcludeGenre/IncludeGenre rules while every other
    /// content rule still binds. See <see cref="EligibilityQueryBuilder.EligibleTitles"/>.
    /// </param>
    public async Task<IReadOnlyList<Title>> RetrieveAsync(
        Guid accountId,
        FeatureVectorBuilder.TasteState taste,
        Func<IQueryable<Title>, IQueryable<Title>>? lane,
        int popCount,
        int annCount,
        string? maturityCeiling,
        CancellationToken ct,
        bool ignoreGenreFilters = false)
    {
        var attributed = await RetrieveWithArmsAsync(
            accountId, taste, lane, popCount, annCount, maturityCeiling, ct, ignoreGenreFilters);
        return attributed.Select(hit => hit.Title).ToList();
    }

    /// <summary>
    /// <see cref="RetrieveAsync"/>, plus WHICH arm claimed each title — and the single
    /// implementation both share, so the attribution can never describe a retrieval that differs
    /// from the one the feed actually runs. That mattered enough to restructure for: the offline
    /// retrieval-recall probe exists to answer "is the pool recall-limited, and by which arm", and a
    /// second copy of this composition would answer it about a pool nothing serves.
    ///
    /// <para><paramref name="asOf"/> rewinds the interaction footprint to a past instant
    /// (<see cref="EligibilityQueryBuilder.EligibleTitlesAsOf"/>) — OFFLINE MEASUREMENT ONLY. Null,
    /// which is every serving call, takes the live predicate unchanged.</para>
    /// </summary>
    public async Task<IReadOnlyList<RetrievedTitle>> RetrieveWithArmsAsync(
        Guid accountId,
        FeatureVectorBuilder.TasteState taste,
        Func<IQueryable<Title>, IQueryable<Title>>? lane,
        int popCount,
        int annCount,
        string? maturityCeiling,
        CancellationToken ct,
        bool ignoreGenreFilters = false,
        DateTime? asOf = null)
    {
        IQueryable<Title> Lane()
        {
            var q = asOf is { } instant
                ? eligibility.EligibleTitlesAsOf(accountId, instant, ignoreGenreFilters)
                : eligibility.EligibleTitles(accountId, ignoreGenreFilters);
            return lane is null ? q : lane(q);
        }

        var ordered = new List<RetrievedTitle>(popCount + annCount);
        var seen = new HashSet<Guid>();

        // Add up to `target` not-yet-seen, maturity-passing titles from an arm. Cross-arm dedup lands
        // here: a title already taken by an earlier arm is skipped and does NOT consume this arm's
        // budget, so overlap between popular and ANN never shrinks the personalized contribution.
        void AddArm(IEnumerable<Title> arm, int target, RetrievalArm which)
        {
            var added = 0;
            foreach (var title in arm)
            {
                if (added >= target)
                {
                    break;
                }

                if (seen.Contains(title.Id) || !EligibilityQueryBuilder.PassesMaturity(title, maturityCeiling))
                {
                    continue;
                }

                seen.Add(title.Id);
                ordered.Add(new RetrievedTitle(title, which));
                added++;
            }
        }

        // ── Popularity arm: the coverage slice ──────────────────────────────────────────────
        // The id tie-break is load-bearing: without it, titles tied on popularity land in whatever
        // row order Postgres feels like, so two back-to-back builds over identical data can retrieve
        // DIFFERENT pools at the Take() cutoff — which defeats the feed's no-op suppression (an
        // identical rebuild must assemble identically) and makes any build irreproducible.
        var popular = await Lane()
            .OrderByDescending(t => t.TmdbPopularity)
            .ThenBy(t => t.Id)
            .Take(OverFetch(popCount))
            .ToListAsync(ct);
        AddArm(popular, popCount, RetrievalArm.Popularity);

        // ── ANN arm: personalized cosine kNN over plot embeddings ───────────────────────────
        // Null LovedCentroid ⇒ cold start (no loved title carries an embedding): skip the ANN arm
        // and return the pure popularity slice, so retrieval never regresses a fresh account.
        if (taste.LovedCentroid is { } loved && annCount > 0)
        {
            // Restrict the kNN to eligible+lane ids BEFORE ordering by cosine. The catalog is
            // dominated by the user's own seen library; a "global nearest-N then filter to unseen"
            // query returns almost nothing — the unseen matches rarely crack the global nearest set.
            var laneIds = Lane().Select(t => t.Id);

            var driftTarget = taste.RecentCentroid is null ? 0 : (int)Math.Ceiling(annCount * DriftFraction);
            var lovedTarget = annCount - driftTarget;

            AddArm(await AnnPullAsync(laneIds, new Vector(loved), OverFetch(lovedTarget), ct), lovedTarget, RetrievalArm.AnnLoved);

            if (taste.RecentCentroid is { } recent && driftTarget > 0)
            {
                AddArm(await AnnPullAsync(laneIds, new Vector(recent), OverFetch(driftTarget), ct), driftTarget, RetrievalArm.AnnRecent);
            }
        }

        return ordered;
    }

    /// <summary>
    /// The "more like this" window: the eligible catalog nearest to ONE specific anchor embedding
    /// (the title being viewed) by pgvector cosine kNN — semantic similarity to that title, not to a
    /// taste centroid. Reuses the exact same eligibility + <paramref name="lane"/> + maturity
    /// composition as <see cref="RetrieveAsync"/>, so the "never show things I've already seen" rule
    /// and all content filters hold identically. The caller only invokes this when the anchor carries
    /// an embedding; a missing-embedding anchor degrades to a genre-overlap pool through
    /// <see cref="RetrieveAsync"/> instead.
    /// </summary>
    /// <param name="anchor">The viewed title's plot embedding — the kNN centre.</param>
    /// <param name="count">Target survivors after maturity filtering.</param>
    public async Task<IReadOnlyList<Title>> RetrieveSimilarAsync(
        Guid accountId,
        Vector anchor,
        Func<IQueryable<Title>, IQueryable<Title>>? lane,
        int count,
        string? maturityCeiling,
        CancellationToken ct)
    {
        var q = eligibility.EligibleTitles(accountId);
        var laneIds = (lane is null ? q : lane(q)).Select(t => t.Id);
        var hits = await AnnPullAsync(laneIds, anchor, OverFetch(count), ct);

        // Maturity can't order in SQL (see EligibilityQueryBuilder.PassesMaturity) — over-fetch above,
        // then keep the nearest survivors in cosine order.
        var ordered = new List<Title>(count);
        foreach (var title in hits)
        {
            if (ordered.Count >= count)
            {
                break;
            }

            if (EligibilityQueryBuilder.PassesMaturity(title, maturityCeiling))
            {
                ordered.Add(title);
            }
        }

        return ordered;
    }

    /// <summary>Nearest eligible titles to <paramref name="centroid"/> by cosine distance (HNSW index).
    /// Ties broken by id for the same reason as the popularity arm: identical inputs, identical pool.</summary>
    private Task<List<Title>> AnnPullAsync(IQueryable<Guid> laneIds, Vector centroid, int take, CancellationToken ct) =>
        db.TitleEmbeddings
            .Where(e => laneIds.Contains(e.TitleId))
            .OrderBy(e => e.Embedding.CosineDistance(centroid))
            .ThenBy(e => e.TitleId)
            .Take(take)
            .Join(db.Titles, e => e.TitleId, t => t.Id, (e, t) => t)
            .ToListAsync(ct);

    private static int OverFetch(int target) => (int)Math.Ceiling(target * MaturityOverFetch);
}

/// <summary>Which retrieval arm put a title into the first-pass pool.</summary>
public enum RetrievalArm
{
    /// <summary>The coverage slice — eligible catalogue by TMDB popularity.</summary>
    Popularity,

    /// <summary>pgvector kNN against the all-time loved centroid.</summary>
    AnnLoved,

    /// <summary>pgvector kNN against the 365-day recent-loved centroid (the drift pull).</summary>
    AnnRecent,
}

/// <summary>
/// One retrieved title and the arm that claimed it. Arms are attributed on a FIRST-CLAIM basis,
/// matching the cross-arm dedup: a title the popularity slice already took is not re-counted for
/// ANN even when ANN would also have found it. Any recall figure derived from this is therefore a
/// statement about the pool as assembled, not about what each arm could reach in isolation.
/// </summary>
public readonly record struct RetrievedTitle(Title Title, RetrievalArm Arm);
