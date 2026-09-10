using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Multi-centroid retrieval on real Postgres + pgvector.
///
/// <para><b>The core claim, and the control that makes it a claim.</b> A single mean vector over an
/// account's whole loved set points at the majority pocket and away from everything else — measured
/// on the founder's live account, only 93 of his 652 loved titles (14%) rank inside that centroid's
/// own top-500 among 4,963 embedded titles. The first test here reproduces that in miniature and
/// then runs the SAME fixture at the SAME budget through the pre-B1 single-centroid path, where the
/// minority pocket's candidate is not retrieved. Without that second half the test would pass on the
/// old code and prove nothing.</para>
///
/// <para>Fixture isolation follows <c>CandidateRetrieverTests</c>: a synthetic genre stamped on
/// every title this instance seeds, with a matching IncludeGenre allowlist row, because the
/// catalogue is GLOBAL and shared by every class in the postgres collection.</para>
/// </summary>
[Collection("postgres")]
public class TasteClusterRetrievalTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// Removes this instance's titles and interactions when the test ends; the base class removes
    /// the embeddings, for every fixture rather than only this one.
    ///
    /// <para>The embedding half is why <see cref="EmbeddedCatalogTestBase"/> exists — read it
    /// before assuming a leftover row here is harmless. The rest is local: this fixture plants
    /// ~150 titles per test with ratings and watch rows behind them, and a sibling class that
    /// counts an account's library has no reason to be paying for that.</para>
    /// </summary>
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles.Where(t => t.Language == _scope).Select(t => t.Id).ToListAsync();
        if (titleIds.Count == 0)
        {
            return;
        }

        await db.UserRatings.Where(r => titleIds.Contains(r.TitleId)).ExecuteDeleteAsync();
        await db.WatchedTitles.Where(w => titleIds.Contains(w.TitleId)).ExecuteDeleteAsync();
        await db.TitleEmbeddings.Where(e => titleIds.Contains(e.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    /// <summary>
    /// A synthetic genre AND a synthetic LANGUAGE stamped on every title this instance seeds, with
    /// matching IncludeGenre + Language allowlist rows on its account.
    ///
    /// <para>The genre half is the isolation <c>CandidateRetrieverTests</c> already uses: the
    /// catalogue is GLOBAL, so a sibling class's popular title displaces this file's popularity head.
    /// The language half is this file's own addition and it runs the OTHER way — it stops these rows
    /// contaminating everyone else. Eligibility defaults to English-only when an account configures
    /// no language rows, so a non-English language makes these titles invisible to every other
    /// account in the collection. That matters here and not in the sibling file because this fixture
    /// plants ~150 EMBEDDED titles per test, and a semantic-search test elsewhere ranks the whole
    /// catalogue by cosine: without this, this file quietly decides what
    /// <c>LiveSearchExpansionTests</c> finds. Verified — it did.</para>
    /// </summary>
    private readonly string _scope = $"clusterscope{Guid.NewGuid():N}";

    /// <summary>
    /// Sized so the k rule yields 3: <c>min(clamp(round(sqrt(64/10)), 3, 12), 64/20) = 3</c>. Below
    /// 60 inputs <c>n/20</c> collapses k to 1 or 2 and the fixture would be testing the single
    /// centroid under a different name.
    /// </summary>
    private const int MajorityLoves = 60;

    /// <summary>
    /// Four, not three. The plan's sketch says three, and three would survive
    /// <see cref="TasteClusterer.MinClusterMembers"/> exactly — <c>&lt; 3</c> merges, so 3 stands.
    /// Four is chosen anyway because it leaves a member of headroom: the merge rules also fold a
    /// cluster whose centroid duplicates another, and a pocket sitting exactly on the survival line
    /// makes a fixture that fails for an uninteresting reason the day a jitter constant changes.
    /// </summary>
    private const int MinorityLoves = 4;

    /// <summary>
    /// Unseen filler near the MAJORITY direction — enough that the single-centroid pull exhausts its
    /// whole over-fetched budget on them before it ever reaches the minority pocket. This is what
    /// makes the control meaningful rather than accidental.
    /// </summary>
    private const int Filler = 90;

    private const int AnnBudget = 60;

    /// <summary>
    /// A low-popularity, eligible title sitting in the account's MINORITY taste pocket is retrieved
    /// when the ANN budget is split across taste clusters, and is NOT retrieved by the single
    /// all-time centroid at the same budget.
    /// </summary>
    [Fact]
    public async Task A_minority_pocket_candidate_is_retrieved_with_clusters_and_missed_without_them()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedPocketsAsync(accountId);

        // The title under test: unseen, nearly invisible by popularity, and sitting on the minority
        // pocket's axis. Nothing but a centroid pointing THERE can reach it.
        var minorityCandidate = await SeedTitleAsync("Minority Pocket Deep Cut", popularity: 1m, NearUnit(7, 0.02f));

        var withClusters = await RetrieveAsync(accountId, popCount: 1, annCount: AnnBudget, useClusters: true);
        var withoutClusters = await RetrieveAsync(accountId, popCount: 1, annCount: AnnBudget, useClusters: false);

        Assert.Contains(minorityCandidate, withClusters.Hits.Select(h => h.Title.Id));

        // THE CONTROL. If this ever starts passing, the test has stopped being about clustering.
        Assert.DoesNotContain(minorityCandidate, withoutClusters.Hits.Select(h => h.Title.Id));

        // And the arms say which mechanism did it: a cluster pull, attributed to a specific pocket.
        var hit = withClusters.Hits.Single(h => h.Title.Id == minorityCandidate);
        Assert.Equal(RetrievalArm.AnnCluster, hit.Arm);
        Assert.NotNull(hit.ClusterIndex);

        // The budget plan is reported, sums to the WHOLE ANN budget, and honours the per-cluster
        // floor. It is the whole budget rather than the post-drift remainder because every rating in
        // this fixture is 400 days old, so RecentCentroid is null and the drift arm does not fire —
        // deliberately, so a drift pull competing for the same slots cannot make a failure ambiguous.
        Assert.NotEmpty(withClusters.ClusterPulls);
        Assert.DoesNotContain(withClusters.Hits, h => h.Arm == RetrievalArm.AnnRecent);
        Assert.Equal(AnnBudget, withClusters.ClusterPulls.Sum(p => p.Target));
        Assert.All(withClusters.ClusterPulls, p => Assert.True(p.Target >= CandidateRetriever.MinPerClusterPull));

        // Pre-B1 path is byte-identical in shape: one loved arm, no cluster plan.
        Assert.Empty(withoutClusters.ClusterPulls);
        Assert.Contains(withoutClusters.Hits, h => h.Arm == RetrievalArm.AnnLoved);
        Assert.DoesNotContain(withClusters.Hits, h => h.Arm == RetrievalArm.AnnLoved);
    }

    /// <summary>
    /// Two identical builds retrieve identical pools. The stable-tonight contract depends on it: the
    /// no-op suppression compares an assembled feed against the Active snapshot, and a partition that
    /// moved between two rebuilds would make every rebuild look like a change.
    /// </summary>
    [Fact]
    public async Task Two_identical_retrievals_return_the_identical_pool_in_the_identical_order()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedPocketsAsync(accountId);

        // ONE cache across both retrievals, because that is what production has: the provider's key
        // is day-granular, so every rebuild inside a UTC day reads the partition the first one
        // computed. Without that shared partition the recency weights would be recomputed against a
        // slightly later clock each time and the budget split would drift continuously — which is
        // precisely the reason the cache key is a DAY rather than an hour.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var first = await RetrieveAsync(accountId, popCount: 5, annCount: AnnBudget, useClusters: true, cache);
        var second = await RetrieveAsync(accountId, popCount: 5, annCount: AnnBudget, useClusters: true, cache);

        Assert.Equal(
            first.Hits.Select(h => (h.Title.Id, h.Arm, h.ClusterIndex)).ToList(),
            second.Hits.Select(h => (h.Title.Id, h.Arm, h.ClusterIndex)).ToList());
        Assert.Equal(first.ClusterPulls, second.ClusterPulls);
    }

    /// <summary>
    /// A cold account retrieves the IDENTICAL pool with and without the cluster provider. Below
    /// <see cref="TasteClusterProvider.MinClusterInputs"/> there is no partition, so B1 must be
    /// literally invisible — a fresh account is never worse off, and never merely differently off.
    /// </summary>
    [Fact]
    public async Task A_cold_account_retrieves_the_identical_pool_with_and_without_clusters()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(); // no ratings at all
        await SeedTitleAsync("Cold A", popularity: 50m, UnitVector(0));
        await SeedTitleAsync("Cold B", popularity: 40m, UnitVector(1));
        await SeedTitleAsync("Cold C", popularity: 30m, UnitVector(2));

        var withClusters = await RetrieveAsync(accountId, popCount: 5, annCount: AnnBudget, useClusters: true);
        var withoutClusters = await RetrieveAsync(accountId, popCount: 5, annCount: AnnBudget, useClusters: false);

        Assert.Equal(
            withoutClusters.Hits.Select(h => (h.Title.Id, h.Arm)).ToList(),
            withClusters.Hits.Select(h => (h.Title.Id, h.Arm)).ToList());
        Assert.Empty(withClusters.ClusterPulls);
    }

    /// <summary>
    /// An account whose loved titles carry no embeddings at all gets the popularity slice and nothing
    /// else — no partition, no ANN arm, no error. The bar is on EMBEDDED inputs precisely so a
    /// thirty-title history of which four are embedded cannot present itself as a thirty-title
    /// clustering.
    /// </summary>
    [Fact]
    public async Task An_account_whose_history_has_no_embeddings_falls_back_to_popularity_only()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        for (var i = 0; i < MajorityLoves; i++)
        {
            var lovedId = await SeedTitleAsync($"Unembedded Love {i}", popularity: 2m, embedding: null);
            await SeedRatingAsync(accountId, lovedId, 9);
        }

        var popularId = await SeedTitleAsync("Popular Unseen", popularity: 999m, UnitVector(3));

        var result = await RetrieveAsync(accountId, popCount: 5, annCount: AnnBudget, useClusters: true);

        Assert.Empty(result.ClusterPulls);
        Assert.All(result.Hits, h => Assert.Equal(RetrievalArm.Popularity, h.Arm));
        Assert.Contains(popularId, result.Hits.Select(h => h.Title.Id));
    }

    /// <summary>
    /// The stored partition is account-scoped by the global query filter, like every other
    /// <see cref="IAccountScoped"/> entity. A model test already fails the build if the filter is
    /// missing; this proves the filter actually bites on real rows, which is the part a model test
    /// cannot see.
    /// </summary>
    [Fact]
    public async Task A_persisted_partition_is_invisible_to_another_account()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        await SeedPocketsAsync(mine);

        var now = DateTime.UtcNow;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var provider = ProviderFor(db);
            var partition = await provider.GetAsync(mine, now, CancellationToken.None);
            Assert.NotEmpty(partition);
            await provider.PersistAsync(mine, now, partition, CancellationToken.None);
        }

        await using (var pinnedToThem = Fixture.CreateContext(Scoped(theirs)))
        {
            Assert.Empty(await pinnedToThem.TasteClusters.ToListAsync());
        }

        await using (var pinnedToMe = Fixture.CreateContext(Scoped(mine)))
        {
            var rows = await pinnedToMe.TasteClusters.OrderBy(c => c.Ordinal).ToListAsync();
            Assert.NotEmpty(rows);
            // A REAL pgvector column, not JSON floats — a 1536-wide vector round-trips as one.
            Assert.All(rows, r => Assert.Equal(1536, r.Centroid.ToArray().Length));
            Assert.All(rows, r => Assert.Null(r.Label));
            Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(r => r.Ordinal));
        }
    }

    /// <summary>
    /// A second persist for the same day REPLACES the day's partition rather than appending to it —
    /// otherwise the several rebuilds the stable-tonight contract performs would leave a day holding
    /// two interleaved partitions and the unique index would reject the second one outright.
    /// </summary>
    [Fact]
    public async Task Persisting_twice_on_one_day_replaces_that_days_partition()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedPocketsAsync(accountId);
        // Anchored to midday so the +1h call below stays inside the same UTC day — a 23:xx run
        // otherwise writes a second partition and this assertion reddens for one hour every day.
        var now = DateTime.UtcNow.Date.AddHours(12);

        int Count() => Fixture.CreateContext(Scoped(accountId)).TasteClusters.Count();

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var provider = ProviderFor(db);
            var partition = await provider.GetAsync(accountId, now, CancellationToken.None);
            await provider.PersistAsync(accountId, now, partition, CancellationToken.None);
        }

        var after = Count();

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var provider = ProviderFor(db);
            var partition = await provider.GetAsync(accountId, now.AddHours(1), CancellationToken.None);
            await provider.PersistAsync(accountId, now.AddHours(1), partition, CancellationToken.None);
        }

        Assert.Equal(after, Count());
    }

    /// <summary>
    /// Retention: partitions older than <see cref="TasteClusterProvider.RetentionDays"/> are pruned
    /// and today's is not.
    /// </summary>
    [Fact]
    public async Task The_prune_keeps_a_fortnight_and_drops_what_is_older()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var age in new[] { 0, 13, 15, 60 })
            {
                db.TasteClusters.Add(new TasteCluster
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ComputedForDate = DateOnly.FromDateTime(now.AddDays(-age)),
                    Ordinal = 0,
                    Centroid = new Vector(UnitVector(0)),
                    Weight = 1m,
                    MemberCount = 5,
                    ExemplarTitleIdsJson = "[]",
                    ComputedAt = now.AddDays(-age),
                });
            }

            await db.SaveChangesAsync();
        }

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(2, await ProviderFor(db).PruneAsync(accountId, now, CancellationToken.None));
        }

        await using (var db = Fixture.CreateContext(Scoped(accountId)))
        {
            Assert.Equal(2, await db.TasteClusters.CountAsync());
        }
    }

    /// <summary>
    /// With the partition forced to a single cluster, the probe's <c>annCluster</c> reach equals what
    /// today's <c>annLoved</c> arm reaches on the identical fixture.
    ///
    /// <para>It holds for a reason worth stating: cosine ranking is invariant to the SCALE of the
    /// query vector, so a unit-normalised centroid over exactly the loved set is a positive multiple
    /// of the un-normalised loved centroid and orders the catalogue identically. The fixture forces
    /// k = 1 by sitting between <c>MinClusterInputs</c> (30) and the <c>n/20</c> rule's next step
    /// (40), and holds no rewatches and nothing rated 7, so both centroids are built from the same
    /// titles. If clustering ever changes the ANN arm on a one-cluster account, something other than
    /// the partition has moved.</para>
    /// </summary>
    [Fact]
    public async Task With_k_forced_to_one_the_cluster_arm_reaches_exactly_what_the_loved_arm_reaches()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // 32 loves: >= MinClusterInputs (30), < 40 so n/20 == 1.
        for (var i = 0; i < 32; i++)
        {
            var lovedId = await SeedTitleAsync($"Single Pocket Love {i}", popularity: 2m, NearUnit(0, 0.01f * (i % 6)));
            await SeedRatingAsync(accountId, lovedId, 9);
        }

        for (var i = 0; i < 30; i++)
        {
            await SeedTitleAsync($"Unseen {i}", popularity: 1m + i, NearUnit(i % 2 == 0 ? 0 : 5, 0.02f));
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var provider = ProviderFor(db);
        Assert.Single(await provider.GetAsync(accountId, DateTime.UtcNow, CancellationToken.None));

        var eligibility = new EligibilityQueryBuilder(db);
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);

        var clustered = await new CandidateRetriever(db, eligibility, provider).RetrieveWithArmsAsync(
            accountId, taste, Lane(), popCount: 0, annCount: AnnBudget, maturityCeiling: null, CancellationToken.None);
        var legacy = await new CandidateRetriever(db, eligibility).RetrieveWithArmsAsync(
            accountId, taste, Lane(), popCount: 0, annCount: AnnBudget, maturityCeiling: null, CancellationToken.None);

        Assert.Equal(
            legacy.Hits.Where(h => h.Arm == RetrievalArm.AnnLoved).Select(h => h.Title.Id).ToList(),
            clustered.Hits.Where(h => h.Arm == RetrievalArm.AnnCluster).Select(h => h.Title.Id).ToList());
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A majority pocket on dim 0 and a minority pocket on dim 7, plus enough unseen filler along the
    /// majority axis that the single centroid's over-fetched budget never reaches dim 7.
    /// </summary>
    private async Task SeedPocketsAsync(Guid accountId)
    {
        for (var i = 0; i < MajorityLoves; i++)
        {
            var id = await SeedTitleAsync($"Majority Love {i}", popularity: 3m, NearUnit(0, 0.01f * (i % 6)));
            await SeedRatingAsync(accountId, id, 9);
        }

        for (var i = 0; i < MinorityLoves; i++)
        {
            var id = await SeedTitleAsync($"Minority Love {i}", popularity: 3m, NearUnit(7, 0.01f * (i % 3)));
            await SeedRatingAsync(accountId, id, 9);
        }

        for (var i = 0; i < Filler; i++)
        {
            await SeedTitleAsync($"Majority Filler {i}", popularity: 2m, NearUnit(0, 0.005f * (i % 9)));
        }

        await SeedTitleAsync("Popular But Off-Taste", popularity: 999m, UnitVector(3));
    }

    private static Func<IQueryable<Title>, IQueryable<Title>> Lane() =>
        q => q.Where(t => t.TmdbId != null && t.PosterPath != null);

    private async Task<RetrievalResult> RetrieveAsync(
        Guid accountId, int popCount, int annCount, bool useClusters, MemoryCache? cache = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var eligibility = new EligibilityQueryBuilder(db);
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var retriever = useClusters
            ? new CandidateRetriever(db, eligibility, ProviderFor(db, cache))
            : new CandidateRetriever(db, eligibility);

        return await retriever.RetrieveWithArmsAsync(
            accountId, taste, Lane(), popCount, annCount, maturityCeiling: null, CancellationToken.None);
    }

    private static TasteClusterProvider ProviderFor(
        Infrastructure.Persistence.ReelDbContext db, MemoryCache? cache = null) =>
        new(db, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<TasteClusterProvider>.Instance);

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"cluster-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.Language,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, decimal popularity, float[]? embedding)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            // The scope, not "en" — see _scope. English-only is the default for every account that
            // configures no language rows, so this is what keeps these rows out of everyone else's
            // eligible catalogue.
            Language = _scope,
            PosterPath = "/p.jpg",
            Genres = [_scope],
            TmdbPopularity = popularity,
            CreatedAt = DateTime.UtcNow.AddDays(-500),
        };
        db.Add(title);
        if (embedding is not null)
        {
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(embedding),
                EmbeddingModel = "test",
                SourceTextHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// Rated 400+ days ago on purpose, so <c>RecentCentroid</c> stays null and the drift arm never
    /// fires — the tests here are about the CLUSTER split, and a drift pull competing for the same
    /// budget would make a failure ambiguous.
    /// </summary>
    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-400),
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static float[] UnitVector(int axis)
    {
        var v = new float[1536];
        v[axis] = 1f;
        return v;
    }

    private static float[] NearUnit(int axis, float nudge)
    {
        var v = new float[1536];
        v[axis] = 1f;
        v[axis + 1] = nudge;
        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }
}
