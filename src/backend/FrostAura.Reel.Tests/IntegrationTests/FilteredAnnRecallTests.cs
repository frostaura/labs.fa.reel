using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The product defect this file exists for: <b>a filtered pgvector search silently returns a
/// fraction of the rows it asks for, and every ANN arm in Reel is a filtered search.</b>
///
/// <para>An HNSW index scan walks the proximity graph collecting a bounded set of candidates
/// (<c>hnsw.ef_search</c>, default 40) and hands them up; the query's own <c>WHERE</c> is applied to
/// that window AFTERWARDS, and with <c>hnsw.iterative_scan = off</c> — pgvector's default — the scan
/// then ends. So "the 40 nearest ELIGIBLE titles" is served as "the eligible members of the 40
/// nearest titles overall". Reel's eligibility rule is *never show things I've already seen*, so
/// the filter removes exactly the rows a heavy watcher's catalogue is made of: the more the product
/// is used, the less the semantic arms return. Writing the eligibility predicate before the
/// ordering in LINQ does not help — Postgres pushes <c>ORDER BY … LIMIT</c> into the index either
/// way — which is why the flaw survived two rounds of people believing they had closed it.</para>
///
/// <para><b>How it surfaced.</b> Not as a product report — as a test that failed one run in four.
/// <c>LiveSearchExpansionTests</c> seeds 14 clean thrillers and 6 horror titles, asks for a thriller
/// with horror excluded, and asserts no horror is shown; on merged main it intermittently showed
/// horror. The pool was not being mis-partitioned, it was arriving short: the semantic arm asked for
/// 40 neighbours out of a 20-row eligible set and got 10, so the clean set fell under the
/// MinResults floor and the flagged-outside channel opened exactly as designed. Captured plans from
/// a failing run show an <c>Index Scan using IX_TitleEmbeddings_Embedding</c> emitting 49 tuples of
/// a 116-row table and stopping; the passing runs show a sequential scan and a sort, which is
/// exact. The intermittency was the planner choosing, and a production-sized catalogue removes the
/// choice — it will always be the index.</para>
///
/// <para>The fix is <c>HnswFilteredSearchInterceptor</c>, which sets <c>hnsw.iterative_scan</c> on
/// every connection so the scan resumes the graph walk instead of stopping when the filter empties
/// its window. This test holds both halves: that the default really does collapse, and that what
/// the app actually opens its connections with really does not.</para>
/// </summary>
[Collection("postgres")]
public class FilteredAnnRecallTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>Catalogue size for this fixture.</summary>
    private const int CorpusSize = 600;

    /// <summary>One title in six is unwatched — the shape of a heavy watcher's catalogue.</summary>
    private const int EligibleEveryNth = 6;

    /// <summary>
    /// Neighbours asked for — the same 40 the semantic arm asks for
    /// (<c>LiveSearchExpansionService.SemanticNeighbourCount</c>), which is also pgvector's default
    /// <c>hnsw.ef_search</c>. That equality is not a coincidence to be tuned away: the arm asks for
    /// exactly one candidate window's worth, so any filtering at all leaves it short.
    /// </summary>
    private const int Requested = 40;

    [Fact]
    public async Task A_filtered_ann_pull_returns_the_whole_window_it_asked_for()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedCorpusAsync();

        // Force the index-ordered path. On a real catalogue the planner takes it unprompted — it is
        // the only way to answer ORDER BY distance LIMIT n without reading everything — but at
        // fixture scale reading everything is cheap and it flips between the two, and a test that
        // only sometimes exercises the code under test is not a test. Denying the sort is what
        // settles it: the exact plan needs one and the index-ordered scan does not. Both settings
        // live on the connection, so the pulls below have to run on that same one.
        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
            await db.Database.ExecuteSqlRawAsync("SET enable_sort = off");

            // ── The defect, reproduced ────────────────────────────────────────────────────────
            await db.Database.ExecuteSqlRawAsync("SET hnsw.iterative_scan = off");
            var collapsed = await PullAsync(db, accountId);

            // ── The app's own setting ─────────────────────────────────────────────────────────
            await db.Database.ExecuteSqlRawAsync(HnswFilteredSearchInterceptor.Statement);
            var whole = await PullAsync(db, accountId);

            Assert.Equal(Requested, whole.Count);

            Assert.True(
                collapsed.Count < Requested,
                $"pgvector's default HNSW scan returned {collapsed.Count} of {Requested} eligible "
                + "neighbours when this test was written, which is the defect "
                + "HnswFilteredSearchInterceptor exists to close. It has now returned the full "
                + "window with hnsw.iterative_scan = off. Either the extension changed its default "
                + "behaviour — in which case confirm that before deleting the interceptor, because "
                + "the deployed image is what decides — or the planner declined the index despite "
                + "enable_seqscan and enable_sort being off, in which case raise SeenCount rather "
                + "than relaxing this.");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// The interceptor is attached in <c>ReelDbContext.OnConfiguring</c> rather than at DI
    /// registration precisely so a hand-built context gets it too. This asserts that it did, on a
    /// connection nobody configured — otherwise the fix would hold in the API and quietly not hold
    /// in the integration suite that is supposed to be guarding it.
    /// </summary>
    [Fact]
    public async Task Every_connection_opens_with_iterative_scan_enabled()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var setting = await db.Database
            .SqlQueryRaw<string>("SELECT current_setting('hnsw.iterative_scan') AS \"Value\"")
            .SingleAsync();

        Assert.Equal("strict_order", setting);
    }

    /// <summary>
    /// The SECOND way a filtered ANN pull arrives short, and the one <c>hnsw.iterative_scan</c>
    /// cannot close: an index still full of rows the suite already deleted.
    ///
    /// <para>A <c>DELETE</c> does not remove an element from an HNSW graph. pgvector leaves the
    /// entry in place until the table is vacuumed; the scan walks it, reads the heap, finds the
    /// tuple dead and discards it — having already spent it out of the candidate budget. Iterating
    /// does not rescue that, because iterating resumes a walk through the same dead graph rather
    /// than reaching live elements it was never linked to. Every embedded fixture in this
    /// collection deletes its catalogue on the way out, so by the time a small fixture runs the
    /// index can hold most of a thousand dead entries around its handful of live ones.</para>
    ///
    /// <para><b>Measured in situ on 2026-09-11</b>, at the moment <c>LiveSearchExpansionTests</c>
    /// failed, with 914 dead entries and a 20-row live catalogue. Asking that index for 40
    /// neighbours of a 20-row eligible set gave
    /// <c>Index Scan using "IX_TitleEmbeddings_Embedding" … (actual rows=0)</c> on one run and
    /// <c>rows=10</c> on another — and <c>rows=20</c> on the same connection, same query, same
    /// forced plan, immediately after <c>VACUUM "TitleEmbeddings"</c>. That is the whole of the
    /// order flake: it had been read as leftover LIVE vectors and is not, because the victim's
    /// eligible pool was exactly its own 20 rows and the index was still wrong about them.</para>
    ///
    /// <para><b>What this test pins, and what it deliberately does not.</b> The collapse depends on
    /// the shape of the graph the suite's churn happens to build, and a synthetic churn — several
    /// hundred vectors inserted and deleted in rounds, on curves or scattered — does NOT reproduce
    /// it reliably; four attempts came back whole. Pinning a symptom that only sometimes appears
    /// would be a test that passes for the wrong reason. So this pins the INVARIANT instead: the
    /// teardown leaves the shared vector index with nothing dead in it, which is the condition
    /// under which the pull above was measured to be whole and without which it was measured not to
    /// be. Remove <see cref="EmbeddedCatalogTestBase.RepairSharedVectorIndexAsync"/> and this goes
    /// red here rather than somewhere else on one run in four.</para>
    /// </summary>
    [Fact]
    public async Task The_teardown_leaves_nothing_dead_in_the_shared_vector_index()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // A sibling class's lifetime, compressed: seed a catalogue and hand it to the base's own
        // teardown. Driving the real lifecycle rather than a hand-written delete is the point —
        // a restatement of the cleanup could drift from the cleanup.
        var churn = new ChurnProbe(Fixture);
        await churn.InitializeAsync();
        await SeedEmbeddedTitlesAsync(ChurnSize, ChurnSeedBase);
        await churn.DisposeAsync();

        await using var db = Fixture.CreateContext(new AccountContext());
        var dead = await DeadEntriesAsync(db);

        Assert.True(
            dead == 0,
            $"{ChurnSize} embedding rows went through EmbeddedCatalogTestBase's teardown and left "
            + $"{dead} dead entries in the shared HNSW index. A deleted row stays in the graph until "
            + "the table is vacuumed, is spent out of the ef_search budget before the query's own "
            + "predicate ever runs, and hnsw.iterative_scan does not reach past it — so the next "
            + "small fixture's semantic arm comes back a fraction full, intermittently, for reasons "
            + "nothing in its own file mentions. RepairSharedVectorIndexAsync is what keeps this at "
            + "zero.");

        // And the pull the victim makes is whole on the index that teardown left behind. This
        // passes with or without the repair on a synthetic churn — see the remark — so it is
        // corroboration, not the assertion carrying the test.
        var accountId = await SeedVictimAsync();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
            await db.Database.ExecuteSqlRawAsync("SET enable_sort = off");
            var pulled = await PullAsync(db, accountId, new Vector(Scattered(VictimSeedBase)));
            Assert.Equal(VictimEligible, pulled.Count);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Dead row versions Postgres is still carrying for the embeddings table — the ones
    /// whose index entries a vector scan has to walk and throw away.</summary>
    private static async Task<long> DeadEntriesAsync(ReelDbContext db) =>
        await db.Database
            .SqlQueryRaw<long>(
                "SELECT COALESCE(n_dead_tup, 0) AS \"Value\" FROM pg_stat_user_tables WHERE relname = 'TitleEmbeddings'")
            .SingleOrDefaultAsync();

    /// <summary>
    /// A stand-in for any sibling fixture: it exists only to run the base's real
    /// <c>InitializeAsync</c>/<c>DisposeAsync</c> around a catalogue, so the test churns the index
    /// the way the suite actually churns it rather than through a hand-written delete that could
    /// drift from it.
    /// </summary>
    private sealed class ChurnProbe(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture);

    /// <summary>How many vectors the churn puts through the index and takes out again — the order
    /// of magnitude one full integration run actually churns.</summary>
    private const int ChurnSize = 900;

    /// <summary>
    /// The victim's whole eligible catalogue — deliberately SMALLER than the window it asks for,
    /// which is the shape every small embedded fixture in this collection has and the shape that
    /// makes the damage visible: a pull that cannot be filled has to exhaust the graph, and a graph
    /// of dead entries is exhausted long before the live rows are found.
    /// </summary>
    private const int VictimEligible = Requested / 2;

    /// <summary>Seeds for the churn's vectors, and for the victim's — disjoint, so neither set sits
    /// on the other's doorstep.</summary>
    private const int ChurnSeedBase = 10_000;

    /// <inheritdoc cref="ChurnSeedBase"/>
    private const int VictimSeedBase = 17;

    /// <summary>
    /// A normalized 1536-vector that is near-orthogonal to every other seed's — the shape an
    /// EMBEDDED CATALOGUE has, as opposed to the smooth curve <see cref="Unit"/> draws for the
    /// recall test above. It matters here: a live pocket strung along one curve is trivially
    /// reachable however damaged the graph is, and scattered live rows among scattered dead ones is
    /// what the suite actually leaves behind.
    /// </summary>
    private static float[] Scattered(int seed)
    {
        var v = new float[1536];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)Math.Sin(seed + i);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }

    /// <summary>Embedded titles on scattered directions, owned by nobody.</summary>
    private async Task<List<Guid>> SeedEmbeddedTitlesAsync(int count, int seedBase)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"Ann Recall {i}",
                Language = "en",
                PosterPath = "/p.jpg",
                Genres = ["drama"],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(Scattered(seedBase + i)),
                EmbeddingModel = "test",
                SourceTextHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// An account whose eligible pool is exactly <see cref="VictimEligible"/> freshly-embedded
    /// titles. Everything already in the shared catalogue is marked watched first, the way the
    /// fixtures that broke do it, so the pull below is asking about these rows and nothing else.
    /// </summary>
    private async Task<Guid> SeedVictimAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"annchurn-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        foreach (var titleId in await db.Titles.Select(t => t.Id).ToListAsync())
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = titleId,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = DateTime.UtcNow,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        await SeedEmbeddedTitlesAsync(VictimEligible, VictimSeedBase);
        return account.Id;
    }

    /// <summary>
    /// The query <c>LiveSearchExpansionService.SemanticNeighbourIdsAsync</c> emits, written out
    /// here because it is private and because the SHAPE is the thing under test: the account's
    /// eligibility predicate, an <c>ORDER BY</c> on cosine distance, and a <c>Take</c>. Anything
    /// with a secondary sort key — <c>CandidateRetriever.AnnPullAsync</c> adds
    /// <c>ThenBy(TitleId)</c> — makes a plain index-ordered scan insufficient and the planner falls
    /// back to sorting, which is exact and therefore not where the defect shows; the semantic arm
    /// has no such key and is what broke. Both are covered by the fix regardless of which plan they
    /// get, which is the point of setting it per connection rather than per query.
    /// </summary>
    private static Task<List<Guid>> PullAsync(ReelDbContext db, Guid accountId) =>
        PullAsync(db, accountId, new Vector(Unit(AnchorPhase)));

    /// <inheritdoc cref="PullAsync(ReelDbContext, Guid)"/>
    private static Task<List<Guid>> PullAsync(ReelDbContext db, Guid accountId, Vector anchor)
    {
        var eligibleIds = new EligibilityQueryBuilder(db).EligibleTitles(accountId).Select(t => t.Id);
        return db.TitleEmbeddings
            .Where(e => eligibleIds.Contains(e.TitleId))
            .OrderBy(e => e.Embedding.CosineDistance(anchor))
            .Take(Requested)
            .Join(db.Titles, e => e.TitleId, t => t.Id, (e, t) => t.Id)
            .ToListAsync();
    }

    /// <summary>
    /// A catalogue shaped the way a heavy watcher's is: <see cref="CorpusSize"/> embedded titles
    /// spread along a smooth phase curve, five in six of them ALREADY SEEN. The eligible ones are
    /// interleaved rather than parked in a distant cluster, which is both what a real catalogue
    /// looks like and what keeps the assertion about the SCAN rather than about HNSW's randomised
    /// graph: an eligible pocket the graph cannot reach at all would fail with or without the fix.
    /// Around the anchor, the forty nearest titles hold roughly seven eligible ones — so the
    /// default scan comes back a fifth full and an iterative one keeps walking until it is not.
    ///
    /// <para>Deliberately NOT narrowed by a synthetic genre and an IncludeGenre allowlist, the way
    /// sibling fixtures isolate themselves. That allowlist is selective enough to make the planner
    /// drive the join from <c>Titles</c> and sort, which is the exact plan that does NOT exhibit the
    /// defect — the fixture would then prove nothing while looking rigorous. A broad eligibility
    /// predicate over a mostly-watched catalogue is both the shape production has and the shape that
    /// makes the index-ordered scan the obvious plan. Isolation comes from
    /// <see cref="EmbeddedCatalogTestBase"/> instead: with every embedded fixture cleaning up after
    /// itself, the only vectors in the index when this runs are these.</para>
    /// </summary>
    private async Task<Guid> SeedCorpusAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"annrecall-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        for (var i = 0; i < CorpusSize; i++)
        {
            var seen = i % EligibleEveryNth != 0;
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"Ann Recall {i}",
                Language = "en",
                PosterPath = "/p.jpg",
                Genres = ["drama"],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(Unit(i * PhaseStep)),
                EmbeddingModel = "test",
                SourceTextHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });

            if (seen)
            {
                db.WatchedTitles.Add(new WatchedTitle
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    TitleId = title.Id,
                    Plays = 1,
                    IsFullyWatched = true,
                    LastWatchedAt = DateTime.UtcNow,
                    FirstSyncedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>Radians between neighbouring titles on the curve.</summary>
    private const float PhaseStep = 0.01f;

    /// <summary>Where the query vector sits — the middle of the corpus, so the window is not
    /// truncated by running off the end of the curve.</summary>
    private const float AnchorPhase = CorpusSize / 2 * PhaseStep;

    /// <summary>A normalized 1536-vector on a smooth phase curve — neighbouring phases are near,
    /// distant phases are not, so distance order is meaningful and predictable.</summary>
    private static float[] Unit(float phase)
    {
        var v = new float[1536];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)Math.Sin(phase + i);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }
}
