using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A real-Postgres fixture on its OWN database, for the tests whose assertions depend on how much
/// is in the catalogue rather than only on what they seeded.
///
/// <para><b>Why this exists — and it is not a flake.</b> <c>TitleEmbeddings.Embedding</c> carries an
/// HNSW index. Ask Reel's semantic arm is a POST-FILTERED kNN — <c>WHERE TitleId IN (eligible)
/// ORDER BY embedding &lt;=&gt; q LIMIT n</c> — and an HNSW scan walks the graph in similarity order,
/// applying the filter afterwards, stopping once <c>hnsw.ef_search</c> candidates have been
/// visited. When the eligible set is a small fraction of the index, most of those candidates are
/// filtered away and the query returns FEWER ROWS THAN IT SHOULD, silently and without an error.</para>
///
/// <para>Tests that narrow the eligible pool to their own seeds (marking everything else watched, so
/// their counts are exactly what they seeded) therefore get quietly worse as the shared database
/// fills up with sibling classes' titles — which is precisely the "fails about half the time
/// locally" behaviour that got <c>LiveSearchExpansionTests</c> quarantined, and it moved with the
/// suite's size rather than with the environment. Two changes close it, both here rather than in
/// the assertions: a database this class alone writes to, and <c>hnsw.ef_search</c> raised on every
/// connection so a filtered scan cannot run out of candidates before it finds the eligible rows.</para>
///
/// <para><b>The same effect is live in production and is NOT fixed here.</b> A heavy watcher's
/// eligible pool is a small fraction of the catalogue by definition, so their semantic arm is
/// exactly the query this works around. That belongs to the filtered-search work in progress on the
/// service; this fixture only stops the test suite from lying about it.</para>
/// </summary>
public abstract class IsolatedPostgresFixture(string databaseSuffix) : IAsyncLifetime
{
    private readonly string? _require = Environment.GetEnvironmentVariable(PostgresFixtureGate.RequireVariable);

    /// <summary>
    /// This fixture's own throwaway database: the shared connection string with the database name
    /// suffixed, and <c>hnsw.ef_search</c> raised. The fixture DROPS whatever this names, so the
    /// suffix is what keeps it away from every other collection's database — and away from
    /// <c>reel</c>, which the shared string must never name in the first place.
    /// </summary>
    public string? ConnectionString { get; } = Build(
        Environment.GetEnvironmentVariable(PostgresFixtureGate.ConnectionStringVariable), databaseSuffix);

    /// <inheritdoc cref="PostgresFixture.Available"/>
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        switch (PostgresFixtureGate.Decide(ConnectionString, _require))
        {
            case IntegrationGateDecision.Fail:
                throw new InvalidOperationException(PostgresFixtureGate.MissingConnectionStringMessage);
            case IntegrationGateDecision.SkipSilently:
                return;
        }

        try
        {
            await using var db = CreateContext(new AccountContext());
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            if (PostgresFixtureGate.IsRequired(_require))
            {
                throw new InvalidOperationException(PostgresFixtureGate.UnreachableMessage(ex), ex);
            }

            return;
        }

        Available = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ReelDbContext CreateContext(IAccountContext accountContext) =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql(ConnectionString!, npgsql => npgsql.UseVector())
                .Options,
            accountContext);

    private static string? Build(string? shared, string suffix)
    {
        if (string.IsNullOrWhiteSpace(shared))
        {
            return shared;
        }

        var builder = new NpgsqlConnectionStringBuilder(shared)
        {
            // A custom-GUC startup option: pgvector accepts it as a placeholder before the extension
            // loads and applies it once loaded. Verified against the running image rather than
            // assumed — an unrecognised parameter here would refuse the connection outright.
            Options = "-c hnsw.ef_search=1000",
        };
        builder.Database = $"{builder.Database}_{suffix}";
        return builder.ConnectionString;
    }
}

/// <summary>The isolated database for <see cref="LiveSearchExpansionTests"/>.</summary>
public sealed class AskReelSearchPostgresFixture() : IsolatedPostgresFixture("askreel");

/// <summary>
/// The collection for Ask Reel tests whose counts depend on the size of the catalogue, not merely on
/// what they seeded. Separate from <c>postgres</c> so their database is theirs alone.
/// </summary>
[CollectionDefinition("askreel-postgres")]
public class AskReelPostgresCollection : ICollectionFixture<AskReelSearchPostgresFixture>;
