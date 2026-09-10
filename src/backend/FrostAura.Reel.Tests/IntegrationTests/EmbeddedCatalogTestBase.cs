using FrostAura.Reel.Application.Tenancy;
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
/// <para>Note what this does NOT do, and must not be mistaken for: it does not make the product's
/// filtered vector search correct. That flaw is real, lives in Postgres rather than in the
/// fixtures, and is fixed in <c>HnswFilteredSearchInterceptor</c> —
/// <see cref="FilteredAnnRecallTests"/> is the test that holds it. This class only keeps one test's
/// catalogue out of another test's index.</para>
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
    /// Removes every embedding row that appeared during this test. Override to add your own
    /// teardown and call <c>await base.DisposeAsync()</c>.
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
        if (added.Count == 0)
        {
            return;
        }

        db.TitleEmbeddings.RemoveRange(added);
        await db.SaveChangesAsync();
    }
}
