using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Base for every class in the <c>postgres</c> collection that writes <c>TitleEmbeddings</c>. It
/// removes the embedding rows THAT TEST added when the test ends, and nothing else.
///
/// <para><b>Why embeddings specifically, when leftover Titles are tolerated.</b> The collection
/// shares one database, and classes already isolate themselves at the ELIGIBILITY layer — a
/// synthetic genre plus an IncludeGenre allowlist, or a language nothing else uses. That is enough
/// for a leftover <c>Title</c>, which can only reach another account's pool through a predicate.
/// It is not enough for a leftover <c>TitleEmbedding</c>, because the vectors share one HNSW index
/// and an HNSW scan applies the query's predicate to a bounded window of graph candidates — so a
/// sibling class's vectors change WHICH rows an unrelated account's search ever gets to filter.
/// Eligibility isolation cannot reach that: the interference happens inside the index, before the
/// predicate runs.</para>
///
/// <para>Concretely, and this is what made it worth a base class rather than a fourth per-fixture
/// patch: on merged main <c>LiveSearchExpansionTests</c> failed roughly one run in four, because
/// its 20-row eligible set came back as 10 whenever the planner chose the index over a sort.
/// Three separate fixtures had each already fixed themselves — a row deletion, reserved embedding
/// dimensions, a non-English language — and the victim broke again as soon as a fourth embedded
/// fixture landed. Per-fixture care does not compose; the shared index is a shared resource and
/// wants a shared discipline.</para>
///
/// <para><b>Delete-what-I-added, not truncate.</b> The snapshot is taken in
/// <see cref="InitializeAsync"/> and the difference removed in <see cref="DisposeAsync"/>, so a
/// class can never destroy rows a different class is still relying on. xUnit builds one instance
/// per test and serialises the collection, which is what makes the snapshot honest.</para>
///
/// <para><b>Deleting the rows is only half of it, and the half that was missing cost the suite its
/// determinism twice.</b> A <c>DELETE</c> does not remove an element from an HNSW graph: pgvector
/// leaves the entry in the index and the scan walks it, discovers the heap tuple is dead, and
/// discards it — having already spent it out of the <c>hnsw.ef_search</c> candidate budget. Once
/// the suite has churned a few hundred vectors through this cleanup, a filtered ANN pull over the
/// handful of rows that ARE live comes back a fraction full, and <c>hnsw.iterative_scan</c> does
/// not rescue it: iterating resumes a walk through the same dead graph rather than reaching live
/// elements it never linked to. Measured on 2026-09-11 at 914 dead entries against a 20-row live
/// catalogue: an index-ordered pull asking for 40 returned between 2 and 19 of the 20, varying per
/// run with nothing but the planner's choice, and returned all 20 the moment the table was
/// vacuumed. So <see cref="RepairSharedVectorIndexAsync"/> runs on every teardown — including a
/// teardown that removed nothing, because sibling fixtures delete their own embeddings by their own
/// routes before calling up to this one, and the damage is shared whoever caused it.</para>
///
/// <para>Note what this does NOT do, and must not be mistaken for: it does not make the product's
/// filtered vector search correct. That flaw is real, lives in Postgres rather than in the
/// fixtures, and is fixed in <c>HnswFilteredSearchInterceptor</c> —
/// <see cref="FilteredAnnRecallTests"/> is the test that holds it, and holds this one too. This
/// class only keeps one test's catalogue, and one test's deletions, out of another test's index.</para>
///
/// <para>Enforced by <c>Harness.EmbeddedFixtureIsolationTests</c>: a class that seeds
/// <c>TitleEmbeddings</c> without inheriting this fails the build.</para>
/// </summary>
public abstract class EmbeddedCatalogTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    private IReadOnlyList<Guid> _preexisting = [];

    /// <summary>The shared real-Postgres fixture, for subclasses that need it by name.</summary>
    protected PostgresFixture Fixture { get; } = fixture;

    /// <summary>
    /// Snapshots the embedding rows that were already there. Override to add your own setup and
    /// call <c>await base.InitializeAsync()</c> — skipping it silently disables the cleanup.
    /// </summary>
    public virtual async Task InitializeAsync()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        _preexisting = await db.TitleEmbeddings.Select(e => e.TitleId).ToListAsync();
    }

    /// <summary>
    /// Removes every embedding row that appeared during this test, then repairs the vector index
    /// those deletions damaged. Override to add your own teardown and call
    /// <c>await base.DisposeAsync()</c>.
    /// </summary>
    public virtual async Task DisposeAsync()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var preexisting = _preexisting;
        var added = await db.TitleEmbeddings
            .Where(e => !preexisting.Contains(e.TitleId))
            .ToListAsync();
        if (added.Count > 0)
        {
            db.TitleEmbeddings.RemoveRange(added);
            await db.SaveChangesAsync();
        }

        // Unconditional, deliberately: a class that added nothing may still have deleted embeddings
        // through its own teardown, and a dead HNSW entry costs the next class its determinism
        // whoever left it. See the class remark.
        await RepairSharedVectorIndexAsync(db);
    }

    /// <summary>
    /// Reclaims the rows a cleanup deleted so pgvector removes them from the shared HNSW graph
    /// instead of leaving them there to be walked and discarded. <c>VACUUM</c> rather than
    /// <c>REINDEX</c> because pgvector's own bulk-delete is what repairs the graph's links, and it
    /// is what autovacuum would eventually do anyway — the suite simply churns the table faster
    /// than autovacuum's naptime, which is why the damage is a test-suite artefact rather than a
    /// production one. Exposed so a test can name the exact operation rather than restate it.
    /// </summary>
    public static Task RepairSharedVectorIndexAsync(ReelDbContext db) =>
        db.Database.ExecuteSqlRawAsync("VACUUM \"TitleEmbeddings\"");
}
