using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Loads one account's taste inputs and hands <see cref="TasteClusterer"/> the whole history —
/// every title rated ≥ 7, plus every rewatch (<c>WatchedTitles.Plays &gt;= 2</c>) whether rated or
/// not — and caches the resulting partition for the day.
///
/// <para><b>Why 7 and not 8.</b> Every other "loved" bar in this codebase is 8 ("loved, not
/// tolerated"), and that is right for a LABEL. This is not a label set, it is a map of where the
/// account's attention goes, and at 8 the founder's holiday/family pocket — a real, repeatedly
/// revisited corner of his taste that the single centroid already loses — has too few members to
/// survive the three-member merge. A 7 is still a title someone chose to finish and rate above
/// neutral.</para>
///
/// <para><b>Why this is not on <see cref="FeatureVectorBuilder.BuildTasteStateAsync"/>.</b> Three
/// reasons, any one of which is sufficient. It sits on request paths (every feed page, every rail);
/// the eval harness builds a taste state five times per run and would pay for a partition it never
/// uses; and the feature builder is bound by the rule that it never reads a persisted table, which
/// is the guarantee that its as-of rewind is honest.</para>
///
/// <para><b>Persisting is the feed build's job alone</b> — see <see cref="PersistAsync"/>.</para>
/// </summary>
public sealed class TasteClusterProvider(
    IReelDbContext db, IMemoryCache cache, ILogger<TasteClusterProvider> logger)
{
    /// <summary>
    /// Ratings at or above this contribute a title to the clustering pool. See the class remarks for
    /// why this is 7 while every label bar in the product is 8.
    /// </summary>
    public const short ClusterRatingFloor = 7;

    /// <summary>Plays at or above this make a title a REWATCH — behaviour worth clustering on even unrated.</summary>
    public const int RewatchPlays = 2;

    /// <summary>
    /// Below this many inputs the account gets NO clusters and retrieval takes today's exact
    /// two-centroid path.
    ///
    /// <para>It is a supply bar, not a taste bar. The k rule already collapses to k = 1 below 60
    /// inputs, so this is not what stops a thin history being over-partitioned; what it stops is a
    /// young account paying for a clustering pass, a persisted row and a budget split in order to
    /// arrive at the single centroid it would have used anyway.</para>
    /// </summary>
    public const int MinClusterInputs = 30;

    /// <summary>
    /// How long a computed partition is reused. Thirty minutes is comfortably longer than a browsing
    /// session's worth of rail pagination and far shorter than the day the key is granular to, so the
    /// cache is a cost saver and never the thing that decides which partition a build sees.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>Days of partitions kept per account. Pruned by the nightly reconcile, beside the other sweeps.</summary>
    public const int RetentionDays = 14;

    /// <summary>
    /// This account's taste clusters as of <paramref name="asOf"/>, or an empty list when the
    /// history is too thin (<see cref="MinClusterInputs"/>) or nothing carries an embedding.
    ///
    /// <para>The cache key is DAY-granular — <c>tasteclusters:{account}:{yyyyMMdd}</c> — so the
    /// several rebuilds the stable-tonight contract performs inside one UTC day all see the same
    /// partition. An hourly key would let two Preserve rebuilds retrieve different pools and defeat
    /// the no-op suppression that keeps tonight's picks stable.</para>
    /// </summary>
    public async Task<IReadOnlyList<TasteClusterer.Cluster>> GetAsync(
        Guid accountId, DateTime asOf, CancellationToken ct)
    {
        var key = CacheKey(accountId, asOf);
        if (cache.TryGetValue<IReadOnlyList<TasteClusterer.Cluster>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var clusters = await ComputeAsync(accountId, asOf, ct);
        cache.Set(key, clusters, CacheTtl);
        return clusters;
    }

    /// <summary>The cache key, invariant-formatted because it is compared as an exact string.</summary>
    public static string CacheKey(Guid accountId, DateTime asOf) =>
        string.Create(CultureInfo.InvariantCulture, $"tasteclusters:{accountId}:{asOf:yyyyMMdd}");

    /// <summary>
    /// Replaces the stored partition for <paramref name="asOf"/>'s day. Called from the feed build
    /// and nowhere else.
    ///
    /// <para><b>Best effort by contract.</b> The caller already holds the partition it is going to
    /// retrieve with, so a failed write costs a diagnostic row and nothing else — losing a feed over
    /// it would be a strictly worse trade. Failures are logged at warning with the account and the
    /// day, so a systematic one is visible rather than merely absent.</para>
    /// </summary>
    public async Task PersistAsync(
        Guid accountId, DateTime asOf, IReadOnlyList<TasteClusterer.Cluster> clusters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var day = DateOnly.FromDateTime(asOf);

        try
        {
            var existing = await db.TasteClusters
                .Where(c => c.AccountId == accountId && c.ComputedForDate == day)
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                db.TasteClusters.RemoveRange(existing);
            }

            foreach (var cluster in clusters)
            {
                db.TasteClusters.Add(new TasteCluster
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ComputedForDate = day,
                    Ordinal = cluster.Ordinal,
                    Centroid = new Vector(cluster.Centroid),
                    Weight = (decimal)cluster.Weight,
                    MemberCount = cluster.MemberCount,
                    ExemplarTitleIdsJson = JsonSerializer.Serialize(cluster.ExemplarTitleIds),
                    Label = null,
                    ComputedAt = asOf,
                });
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Persisting {Count} taste cluster(s) for {AccountId} on {Day} failed; the build's own partition is unaffected.",
                clusters.Count, accountId, day);
        }
    }

    /// <summary>
    /// Deletes partitions older than <see cref="RetentionDays"/> for one account, and reports how
    /// many rows went. Called from the nightly reconcile beside the other sweeps.
    /// </summary>
    public Task<int> PruneAsync(Guid accountId, DateTime now, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(now.AddDays(-RetentionDays));
        return db.TasteClusters
            .Where(c => c.AccountId == accountId && c.ComputedForDate < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<IReadOnlyList<TasteClusterer.Cluster>> ComputeAsync(
        Guid accountId, DateTime asOf, CancellationToken ct)
    {
        // Every movie/show rating the account had made by asOf. The mean is taken over ALL of them,
        // not just the pool below: the weight expresses "how far above your own bar is this", and a
        // mean taken over the ≥7 slice alone would put that bar somewhere around 8.
        var ratings = await db.UserRatings
            .Where(r => r.AccountId == accountId && r.RatedAt <= asOf
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating, r.RatedAt })
            .ToListAsync(ct);

        if (ratings.Count == 0)
        {
            return [];
        }

        var userMean = (decimal)ratings.Average(r => (double)r.Rating);

        // At most one row per title — the unique index already guarantees it, but a defensive
        // grouping keeps the pool a set rather than trusting a constraint from a different layer.
        var pool = ratings
            .Where(r => r.Rating >= ClusterRatingFloor)
            .GroupBy(r => r.TitleId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.Rating).ThenByDescending(r => r.RatedAt).First());

        // Rewatches, as-of. LastWatchedAt is the only honest date here — FirstSyncedAt is when Trakt
        // was linked, not when the title was watched — and an undated watch reads as not-yet-watched,
        // the same rule the recall probe applies ("absence of a date is not evidence of the wrong
        // date"). Rated rewatches are already in `pool` and keep their rating weight; only the
        // UNRATED ones add a member here.
        var rewatches = await db.WatchedTitles
            .Where(w => w.AccountId == accountId
                && w.Plays >= RewatchPlays
                && w.LastWatchedAt != null
                && w.LastWatchedAt <= asOf)
            .Select(w => new { w.TitleId, w.Plays, w.LastWatchedAt })
            .ToListAsync(ct);

        var rewatchByTitle = rewatches
            .GroupBy(r => r.TitleId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Plays).First());

        var titleIds = pool.Keys.Concat(rewatchByTitle.Keys).Distinct().ToList();
        if (titleIds.Count < MinClusterInputs)
        {
            return [];
        }

        var embeddings = await db.TitleEmbeddings
            .Where(e => titleIds.Contains(e.TitleId))
            .Select(e => new { e.TitleId, e.Embedding })
            .ToListAsync(ct);
        var byTitle = embeddings.ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        var inputs = new List<TasteClusterer.Input>(titleIds.Count);
        foreach (var titleId in titleIds)
        {
            if (!byTitle.TryGetValue(titleId, out var embedding))
            {
                continue;
            }

            var rewatch = rewatchByTitle.GetValueOrDefault(titleId);
            var plays = rewatch?.Plays ?? 0;
            inputs.Add(pool.TryGetValue(titleId, out var rated)
                ? new TasteClusterer.Input(titleId, embedding, rated.Rating, rated.RatedAt, plays)
                : new TasteClusterer.Input(titleId, embedding, null, rewatch!.LastWatchedAt!.Value, plays));
        }

        // The bar is on EMBEDDED inputs, not on rows: thirty titles of which four carry an embedding
        // is a four-title clustering wearing a thirty-title label.
        return inputs.Count < MinClusterInputs
            ? []
            : TasteClusterer.Partition(inputs, accountId, asOf, userMean);
    }
}
