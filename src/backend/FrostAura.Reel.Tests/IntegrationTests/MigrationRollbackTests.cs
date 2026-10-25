using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Rolling M4 back on a real database (program §5.0 rule 8, ireview_system defect 10). The exposure
/// log is append-only, so a rollback never rewrites it: once a breakdown has been recorded as
/// unknown the rollback refuses before it changes anything and names the dump as the way back;
/// with nothing recorded since the upgrade it reverts the schema in place. Runs on a database of
/// its own (<c>&lt;test db&gt;_rollback</c>), because migrating down would pull the schema out from
/// under every other test in the shared one.
/// </summary>
[Collection("migration-rollback")]
public sealed class MigrationRollbackTests(MigrationRollbackDatabaseFixture fixture)
{
    private const string M3 = "20261025060003_M3EnrichmentClaimsAndReattemptCap";
    private const string M4 = "20261025060004_M4ExposureBreakdownAndFeedProvenance";

    [Fact]
    public async Task Rolling_back_M4_over_an_unknown_breakdown_refuses_and_leaves_the_log_and_schema_untouched()
    {
        if (!await fixture.ResetToLatestAsync())
        {
            return;
        }

        var unknown = await InsertExposureAsync(baseScore: null, calibrationDelta: null);
        var known = await InsertExposureAsync(baseScore: 3.25m, calibrationDelta: 0.5m);

        await using var db = fixture.CreateContext();
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(M3));
        Assert.Contains("cannot be rolled back in place", refusal.MessageText, StringComparison.Ordinal);
        Assert.Contains("pre-migration dump", refusal.Hint, StringComparison.Ordinal);

        // Nothing changed: the migration ran in one transaction and refused first.
        Assert.Equal(M4, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.True(await ColumnExistsAsync("FeedItems", "WhySource"));
        Assert.True(await IsNullableAsync("ExposureEvents", "BaseScore"));
        Assert.Equal((null, null), await BreakdownAsync(unknown));
        Assert.Equal((3.25m, 0.5m), await BreakdownAsync(known));
    }

    [Fact]
    public async Task Rolling_back_M4_with_no_unknown_breakdown_reverts_only_the_schema()
    {
        if (!await fixture.ResetToLatestAsync())
        {
            return;
        }

        var known = await InsertExposureAsync(baseScore: 4.5m, calibrationDelta: -0.25m);

        await using (var db = fixture.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(M3);
            Assert.Equal(M3, (await db.Database.GetAppliedMigrationsAsync()).Last());
        }

        Assert.False(await ColumnExistsAsync("FeedItems", "WhySource"));
        Assert.False(await ColumnExistsAsync("FeedItems", "EvidenceJson"));
        foreach (var column in new[] { "BaseScore", "CalibrationDelta", "ResidualApplied", "DemotionApplied" })
        {
            Assert.False(await IsNullableAsync("ExposureEvents", column), $"{column} is still nullable");
            Assert.Null(await DefaultOfAsync("ExposureEvents", column));
        }

        Assert.Equal((4.5m, -0.25m), await BreakdownAsync(known));
    }

    private async Task<Guid> InsertExposureAsync(decimal? baseScore, decimal? calibrationDelta)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // Every NOT NULL column without a default, read from the catalogue, so the row stays valid
        // as the log grows columns: a type-appropriate zero value, then the breakdown under test.
        var required = new List<(string Name, string Type)>();
        await using (var columns = new NpgsqlCommand(
            """
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_name = 'ExposureEvents' AND is_nullable = 'NO' AND column_default IS NULL
              AND is_generated = 'NEVER'
            """, connection))
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                required.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var values = required.ToDictionary(c => c.Name, c => ZeroOf(c.Type), StringComparer.Ordinal);
        values["Id"] = id;
        values["BaseScore"] = (object?)baseScore ?? DBNull.Value;
        values["CalibrationDelta"] = (object?)calibrationDelta ?? DBNull.Value;
        values["ResidualApplied"] = 0m;
        values["DemotionApplied"] = 0m;

        var names = values.Keys.ToList();
        await using var insert = new NpgsqlCommand(
            $"""INSERT INTO "ExposureEvents" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})""",
            connection);
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"p{i}", values[names[i]]);
        }

        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private static object ZeroOf(string dataType) => dataType switch
    {
        "uuid" => Guid.NewGuid(),
        "integer" => 0,
        "smallint" => (short)0,
        "bigint" => 0L,
        "numeric" => 0m,
        "real" => 0f,
        "double precision" => 0d,
        "boolean" => false,
        "timestamp with time zone" => DateTime.UtcNow,
        "timestamp without time zone" => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
        _ => "x",
    };

    private async Task<(decimal? BaseScore, decimal? CalibrationDelta)> BreakdownAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT "BaseScore", "CalibrationDelta" FROM "ExposureEvents" WHERE "Id" = @id""", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.IsDBNull(0) ? null : reader.GetDecimal(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1));
    }

    private Task<bool> ColumnExistsAsync(string table, string column) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = @t AND column_name = @c)", table, column);

    private async Task<bool> IsNullableAsync(string table, string column) =>
        await ScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = @t AND column_name = @c", table, column) == "YES";

    private Task<string?> DefaultOfAsync(string table, string column) =>
        ScalarAsync<string?>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = @t AND column_name = @c", table, column);

    private async Task<T> ScalarAsync<T>(string sql, string table, string column)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("c", column);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? default! : (T)value;
    }
}

/// <summary>A database of the rollback tests' own, rebuilt to the latest migration before each test.</summary>
public sealed class MigrationRollbackDatabaseFixture : IAsyncLifetime
{
    private readonly PostgresFixture _gate = new();

    public string ConnectionString { get; private set; } = string.Empty;

    private bool _enabled;

    public Task InitializeAsync()
    {
        switch (PostgresFixtureGate.Decide(_gate.ConnectionString, Environment.GetEnvironmentVariable(PostgresFixtureGate.RequireVariable)))
        {
            case IntegrationGateDecision.Fail:
                throw new InvalidOperationException(PostgresFixtureGate.MissingConnectionStringMessage);
            case IntegrationGateDecision.SkipSilently:
                return Task.CompletedTask;
        }

        if (PostgresFixtureGate.LiveTargetRefusal(_gate.ConnectionString) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var builder = new NpgsqlConnectionStringBuilder(_gate.ConnectionString);
        builder.Database += "_rollback";

        // Every test drops and recreates the database, which kills any pooled connection to it.
        builder.Pooling = false;
        ConnectionString = builder.ConnectionString;
        _enabled = true;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_enabled)
        {
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    /// <summary>Drops and re-migrates the database; false when integration tests are skipped.</summary>
    public async Task<bool> ResetToLatestAsync()
    {
        if (!_enabled)
        {
            return false;
        }

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return true;
    }

    public ReelDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
                .Options,
            new AccountContext());
}

[CollectionDefinition("migration-rollback")]
public class MigrationRollbackCollection : ICollectionFixture<MigrationRollbackDatabaseFixture>;
