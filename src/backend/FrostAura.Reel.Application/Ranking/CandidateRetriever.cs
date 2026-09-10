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
///   • ANN arm — pgvector cosine kNN over plot embeddings against the user's taste. Since B1 that
///     is a MULTI-CENTROID pull: one kNN per <see cref="TasteClusterer"/> cluster, budgeted by the
///     cluster's weight, plus the unchanged drift pull vs
///     <see cref="FeatureVectorBuilder.TasteState.RecentCentroid"/>.
///
/// Retrieval only widens WHICH titles enter scoring — the FastTree model still decides the order.
/// Cold start (no loved title carries an embedding ⇒ null LovedCentroid) skips the ANN arm and
/// degrades to pure popularity, so a fresh account is never worse off than the old behaviour.
///
/// <para><b>Why the ANN arm stopped being one centroid</b> (measured on the live database,
/// 2026-09-10). A leave-one-out test of the single all-time loved centroid put only 93 of the
/// founder's 652 loved titles — <b>14%</b> — inside its own top-500 among 4,963 embedded titles;
/// 33% inside the top-1,200 and 75% inside the top-3,000. At <c>FirstPassAnnPool = 500</c> the arm
/// therefore could not reach 86% of his own loves, because the mean of holiday comedies and
/// <i>Ad Astra</i> points at neither of them. Nothing was being FILTERED out — the taste state has
/// no window on ratings at all — it was being averaged into a direction nobody has. Splitting the
/// same budget across the pockets a spherical k-means actually finds is the whole fix, and
/// <c>FeedBuildStat.AnnPerClusterJson</c> is how it is judged: the recall rise has to concentrate in
/// the LOW-weight clusters, because a uniform rise would mean the budget bought it and the honest
/// response is to raise the pool and delete the clusterer.</para>
///
/// <para><see cref="MinPerClusterPull"/> is the single most important constant here. Without a floor
/// under each cluster's pull, a weight-proportional split hands a small pocket four slots — and
/// multi-centroid retrieval degenerates into the current single centroid plus overhead.</para>
/// </summary>
/// <param name="clusters">
/// Optional. Null — or a partition with no clusters — takes today's EXACT two-centroid path, which
/// is what every construction site predating B1 does and what a cold account gets.
/// </param>
public sealed class CandidateRetriever(
    IReelDbContext db, EligibilityQueryBuilder eligibility, TasteClusterProvider? clusters = null)
{
    // Certification ceilings can't order in SQL, so maturity is an in-memory gate applied AFTER the
    // fetch (see EligibilityQueryBuilder.PassesMaturity). Over-fetch each arm so a restricted profile
    // — whose ceiling can drop a large slice of the popular head — still lands ~target survivors
    // instead of a starved pool.
    private const double MaturityOverFetch = 1.3d;

    // The drift pull vs RecentCentroid is a smaller slice of the ANN budget than the all-time loved
    // pull: it nudges a recent taste shift into the pool without letting one recent binge dominate it.
    public const double DriftFraction = 0.35d;

    /// <summary>
    /// The floor on one cluster's ANN pull. A weight-proportional split of 325 slots across eight
    /// clusters gives the smallest pocket single digits, which is not a retrieval arm — it is
    /// rounding error with a name. Twenty-five is enough for a pocket to put a handful of survivors
    /// past the maturity gate and the shortlist cut, and it is what bounds k at
    /// <see cref="TasteClusterer.MaxK"/>: twelve of them already claim 300 of a 500-slot budget.
    /// <para>The number of clusters actually used is reduced until the floor fits the budget, rather
    /// than the floor being relaxed — a pocket served four slots is worse than a pocket not served.</para>
    /// </summary>
    public const int MinPerClusterPull = 25;

    /// <summary>Pass this as <c>maxClusters</c> to use every cluster the partition holds.</summary>
    public const int AllClusters = int.MaxValue;

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
    /// <param name="maxClusters">
    /// How many taste clusters the ANN arm may pull from. The feed build passes
    /// <see cref="AllClusters"/>; <c>FeedPaginator.FetchWindowAsync</c> passes 3, because a home load
    /// fires every rail at once and six rails × nine exact pgvector scans is not a request-path cost.
    /// </param>
    public async Task<IReadOnlyList<Title>> RetrieveAsync(
        Guid accountId,
        FeatureVectorBuilder.TasteState taste,
        Func<IQueryable<Title>, IQueryable<Title>>? lane,
        int popCount,
        int annCount,
        string? maturityCeiling,
        CancellationToken ct,
        bool ignoreGenreFilters = false,
        int maxClusters = AllClusters)
    {
        var attributed = await RetrieveWithArmsAsync(
            accountId, taste, lane, popCount, annCount, maturityCeiling, ct, ignoreGenreFilters, maxClusters: maxClusters);
        return attributed.Hits.Select(hit => hit.Title).ToList();
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
    /// <inheritdoc cref="RetrieveAsync" path="/param[@name='maxClusters']"/>
    public async Task<RetrievalResult> RetrieveWithArmsAsync(
        Guid accountId,
        FeatureVectorBuilder.TasteState taste,
        Func<IQueryable<Title>, IQueryable<Title>>? lane,
        int popCount,
        int annCount,
        string? maturityCeiling,
        CancellationToken ct,
        bool ignoreGenreFilters = false,
        DateTime? asOf = null,
        int maxClusters = AllClusters)
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

        // Add up to `target` not-yet-seen, maturity-passing titles from an arm, and report how many
        // it actually placed. Cross-arm dedup lands here: a title already taken by an earlier arm is
        // skipped and does NOT consume this arm's budget, so overlap between popular and ANN never
        // shrinks the personalized contribution.
        int AddArm(IEnumerable<Title> arm, int target, RetrievalArm which, int? clusterIndex = null)
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
                ordered.Add(new RetrievedTitle(title, which, clusterIndex));
                added++;
            }

            return added;
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
        var pulls = new List<ClusterPull>();
        IReadOnlyList<TasteClusterer.Cluster> partition = [];
        if (taste.LovedCentroid is { } loved && annCount > 0)
        {
            // Restrict the kNN to eligible+lane ids BEFORE ordering by cosine. The catalog is
            // dominated by the user's own seen library; a "global nearest-N then filter to unseen"
            // query returns almost nothing — the unseen matches rarely crack the global nearest set.
            var laneIds = Lane().Select(t => t.Id);

            // The drift arm is untouched by B1. It answers a different question from the clusters —
            // "where is this taste MOVING" against "where does it live" — and it is a layer over the
            // all-time signal, never a filter on it.
            var driftTarget = taste.RecentCentroid is null ? 0 : (int)Math.Ceiling(annCount * DriftFraction);
            var clusterBudget = annCount - driftTarget;

            partition = clusters is null
                ? []
                : await clusters.GetAsync(accountId, asOf ?? DateTime.UtcNow, ct);

            var targets = SplitClusterBudget(
                partition.Select(c => c.Weight).ToList(), clusterBudget, maxClusters);

            if (targets.Count == 0)
            {
                // No partition — today's exact single-centroid pull over the whole cluster budget.
                AddArm(
                    await AnnPullAsync(laneIds, new Vector(loved), OverFetch(clusterBudget), ct),
                    clusterBudget,
                    RetrievalArm.AnnLoved);
            }
            else
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    var cluster = partition[i];
                    var returned = AddArm(
                        await AnnPullAsync(laneIds, new Vector(cluster.Centroid), OverFetch(targets[i]), ct),
                        targets[i],
                        RetrievalArm.AnnCluster,
                        cluster.Ordinal);
                    pulls.Add(new ClusterPull(cluster.Ordinal, cluster.Weight, targets[i], returned));
                }
            }

            if (taste.RecentCentroid is { } recent && driftTarget > 0)
            {
                AddArm(await AnnPullAsync(laneIds, new Vector(recent), OverFetch(driftTarget), ct), driftTarget, RetrievalArm.AnnRecent);
            }
        }

        return new RetrievalResult(ordered, pulls, partition);
    }

    /// <summary>
    /// Splits <paramref name="clusterBudget"/> ANN slots across cluster weights, exactly.
    ///
    /// <para>Three properties, all load-bearing and all asserted by a property test over
    /// k ∈ 3..12 × budget ∈ 0..500. <b>The sum is exact</b> — a floor-and-hope split leaves slots on
    /// the table, and a round-and-hope split overspends the pool the shortlist cut is sized against.
    /// <b>Every served cluster clears <see cref="MinPerClusterPull"/></b>, which is achieved by
    /// serving FEWER clusters rather than by relaxing the floor: a pocket given four slots is a
    /// pocket that was not served. <b>Remainders go in weight order</b>, so the residue after
    /// integer division lands on the pockets the account cares most about and the result cannot
    /// depend on enumeration order.</para>
    ///
    /// <para>An empty list means "no cluster arms" — the caller falls back to the single all-time
    /// centroid, which is the pre-B1 behaviour byte for byte.</para>
    /// </summary>
    public static IReadOnlyList<int> SplitClusterBudget(
        IReadOnlyList<double> weights, int clusterBudget, int maxClusters)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var usable = Math.Min(weights.Count, Math.Max(0, maxClusters));
        if (usable <= 0 || clusterBudget <= 0)
        {
            return [];
        }

        // Serve as many clusters as the floor affords, never more. Below one floor's worth of budget
        // there is exactly one cluster to serve and it takes what there is.
        var k = Math.Clamp(clusterBudget / MinPerClusterPull, 1, usable);
        var floorPer = Math.Min(MinPerClusterPull, clusterBudget / k);

        var served = weights.Take(k).ToList();
        var totalWeight = served.Sum();
        var targets = new int[k];
        var free = clusterBudget - (floorPer * k);
        for (var i = 0; i < k; i++)
        {
            var share = totalWeight > 0d ? (int)Math.Floor(free * served[i] / totalWeight) : 0;
            targets[i] = floorPer + share;
        }

        // Hand the rounding residue out in weight order — descending, ties to the lower ordinal —
        // so Σ targets == clusterBudget on the nose.
        var remainder = clusterBudget - targets.Sum();
        var order = Enumerable.Range(0, k)
            .OrderByDescending(i => served[i])
            .ThenBy(i => i)
            .ToList();
        for (var step = 0; remainder > 0; step++)
        {
            targets[order[step % k]]++;
            remainder--;
        }

        return targets;
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

    /// <summary>
    /// pgvector kNN against the all-time loved centroid — the SINGLE-centroid pull. Since B1 this
    /// arm only fires when the account has no taste partition (a thin history, or nothing loved
    /// carries an embedding), so a hit attributed here is a statement that clustering was skipped.
    /// </summary>
    AnnLoved,

    /// <summary>pgvector kNN against the 365-day recent-loved centroid (the drift pull).</summary>
    AnnRecent,

    /// <summary>
    /// pgvector kNN against one <see cref="TasteClusterer"/> centroid. Which one is on
    /// <see cref="RetrievedTitle.ClusterIndex"/> — without it a cluster hit cannot be read back
    /// against the pocket that paid for it, which is the whole measurement.
    /// </summary>
    AnnCluster,
}

/// <summary>
/// One retrieved title and the arm that claimed it. Arms are attributed on a FIRST-CLAIM basis,
/// matching the cross-arm dedup: a title the popularity slice already took is not re-counted for
/// ANN even when ANN would also have found it. Any recall figure derived from this is therefore a
/// statement about the pool as assembled, not about what each arm could reach in isolation.
/// <para><paramref name="ClusterIndex"/> is the ordinal of the taste cluster that pulled it, and is
/// null for every arm but <see cref="RetrievalArm.AnnCluster"/>.</para>
/// </summary>
public readonly record struct RetrievedTitle(Title Title, RetrievalArm Arm, int? ClusterIndex = null);

/// <summary>
/// What one cluster's ANN pull was asked for and what it could actually find.
/// <para><see cref="Returned"/> below <see cref="Target"/> is not a retrieval failure: it means the
/// eligible catalogue holds nothing else near that pocket, which is a per-cluster SUPPLY answer and
/// the first one this product has ever been able to give.</para>
/// </summary>
public readonly record struct ClusterPull(int Ordinal, double Weight, int Target, int Returned);

/// <summary>
/// The attributed pool, how the ANN budget was actually spent per cluster, and the partition it was
/// spent across.
///
/// <para>All three travel together deliberately. A pool without its budget plan cannot be diagnosed
/// — "recall went up" and "we bought recall with slots" look identical from the hits alone. And
/// <paramref name="Clusters"/> is here so the feed build persists the partition it ACTUALLY
/// retrieved with, structurally, rather than fetching its own copy and trusting a cache to have
/// handed both callers the same answer.</para>
/// </summary>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedTitle> Hits,
    IReadOnlyList<ClusterPull> ClusterPulls,
    IReadOnlyList<TasteClusterer.Cluster> Clusters);
