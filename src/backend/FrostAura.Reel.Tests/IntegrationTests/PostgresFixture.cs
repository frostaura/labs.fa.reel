using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Real-Postgres fixture (pgvector image required). Reads REEL_TEST_CONNECTION_STRING; when
/// absent the integration tests no-op so the plain unit run stays green without a database —
/// UNLESS REEL_REQUIRE_INTEGRATION=1, in which case the fixture throws and the whole collection
/// fails rather than reporting green having executed nothing. CI sets that flag; see
/// <see cref="PostgresFixtureGate"/> for the decision and why it exists.
/// Migrates once per collection on a uniquely-named schema-fresh database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string? _require = Environment.GetEnvironmentVariable(PostgresFixtureGate.RequireVariable);

    public string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable(PostgresFixtureGate.ConnectionStringVariable);

    /// <summary>
    /// True once the database has actually been migrated. False means every test in the
    /// collection early-returns — which can only happen when the gate decided to skip silently,
    /// never when the environment demanded a database.
    /// </summary>
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

        // Never connect to the live Postgres or the live database — not even to skip. This is checked
        // before the gate's silence applies, because the very next statement drops the database.
        if (PostgresFixtureGate.LiveTargetRefusal(ConnectionString) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        try
        {
            await using var db = CreateContext(new AccountContext());
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            // A connection string that points nowhere is the same lie as an absent one, so it
            // gets the same treatment: loud where a database was demanded, silent otherwise.
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
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
