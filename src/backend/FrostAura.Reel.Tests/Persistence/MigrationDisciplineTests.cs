using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FrostAura.Reel.Tests.Persistence;

/// <summary>
/// Schema discipline (program §5.0 rule 8): from the trust-v1 baseline on, every migration is
/// additive — expand only, no drops, no renames, no narrowing — and every persisted enum is a
/// string. That is what lets any image built during the program be rolled back by tag after a
/// later migration: an older image never finds a column it reads gone, renamed or tightened.
///
/// <para>Offline: the migrations are instantiated from the assembly and their <c>Up</c> operations
/// inspected, so this runs in the unit job with no database. A migration that genuinely has to
/// drop something (say, an index replaced by a better one in the same migration) goes into
/// <see cref="Exemptions"/> with its reason, which makes the exception a reviewed decision of the
/// schema steward instead of a silent one.</para>
/// </summary>
public partial class MigrationDisciplineTests
{
    /// <summary>
    /// The last migration on <c>merge-pr1@99c0761</c>, where the program branched. Everything up to
    /// and including it predates the rule and is grandfathered; everything after it is held to it.
    /// </summary>
    internal const string Baseline = "20260911022643_AskReelExposureTurnKeyAndConflict";

    /// <summary>Post-baseline migrations allowed a non-additive operation, each with its reason.</summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal);

    private static ReelDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only", npgsql => npgsql.UseVector())
                .Options,
            new AccountContext());

    private static IReadOnlyList<(string Id, Migration Migration)> AllMigrations()
    {
        using var db = CreateContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var provider = db.Database.ProviderName!;
        return assembly.Migrations
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => (m.Key, assembly.CreateMigration(m.Value, provider)))
            .ToList();
    }

    [Fact]
    public void The_baseline_migration_exists()
    {
        // If this fails the baseline was renamed or squashed, and the rule below is silently
        // checking the wrong range — fix the constant, do not delete the test.
        Assert.Contains(AllMigrations(), m => m.Id == Baseline);
    }

    [Fact]
    public void Every_migration_after_the_baseline_is_additive()
    {
        var violations = AllMigrations()
            .Where(m => string.CompareOrdinal(m.Id, Baseline) > 0 && !Exemptions.ContainsKey(m.Id))
            .SelectMany(m => Violations(m.Migration.UpOperations).Select(v => $"{m.Id}: {v}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Non-additive migration operations (program §5.0 rule 8 — expand only):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_exemption_names_a_real_post_baseline_migration_and_a_reason()
    {
        var ids = AllMigrations().Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Exemptions, e =>
        {
            Assert.Contains(e.Key, ids);
            Assert.True(string.CompareOrdinal(e.Key, Baseline) > 0, $"{e.Key} is grandfathered already");
            Assert.False(string.IsNullOrWhiteSpace(e.Value), $"{e.Key} has no reason");
        });
    }

    /// <summary>
    /// Proof the detector reads real migration classes, not just the synthetic operations below:
    /// the pre-rule history did drop and alter things, and the checker must see them.
    /// </summary>
    [Fact]
    public void The_detector_finds_the_non_additive_operations_in_the_grandfathered_history()
    {
        var historical = AllMigrations()
            .Where(m => string.CompareOrdinal(m.Id, Baseline) <= 0)
            .SelectMany(m => Violations(m.Migration.UpOperations))
            .ToList();

        Assert.NotEmpty(historical);
    }

    [Fact]
    public void The_model_has_no_change_that_a_migration_does_not_carry()
    {
        // An entity edited without its migration means the integrator's re-scaffold at merge would
        // produce operations nobody reviewed — the usual way a surprise drop gets in.
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges(),
            "The model differs from the migrations snapshot; add a migration for the change.");
    }

    [Fact]
    public void Every_persisted_enum_is_stored_as_a_string()
    {
        using var db = CreateContext();
        var numeric = db.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Entity: t, Property: p)))
            .Where(x => (Nullable.GetUnderlyingType(x.Property.ClrType) ?? x.Property.ClrType).IsEnum)
            .Where(x => x.Property.GetProviderClrType() != typeof(string)
                && x.Property.GetTypeMapping().Converter?.ProviderClrType != typeof(string))
            .Select(x => $"{x.Entity.ClrType.Name}.{x.Property.Name}")
            .ToList();

        Assert.Empty(numeric);
    }

    // ── The detector, exercised on synthetic operations ─────────────────────────────────────

    public static TheoryData<string, MigrationOperation> NonAdditive => new()
    {
        { "drop column", new DropColumnOperation { Table = "SyncJobs", Name = "Error" } },
        { "drop table", new DropTableOperation { Name = "SyncJobs" } },
        { "rename column", new RenameColumnOperation { Table = "SyncJobs", Name = "Error", NewName = "Failure" } },
        { "rename table", new RenameTableOperation { Name = "SyncJobs", NewName = "Jobs" } },
        { "rename index", new RenameIndexOperation { Table = "SyncJobs", Name = "IX_A", NewName = "IX_B" } },
        { "drop index", new DropIndexOperation { Table = "SyncJobs", Name = "IX_A" } },
        { "drop foreign key", new DropForeignKeyOperation { Table = "SyncJobs", Name = "FK_A" } },
        { "delete seed data", new DeleteDataOperation { Table = "SyncJobs", KeyColumns = ["Id"], KeyValues = new object[,] { { 1 } } } },
        {
            "nullable to required",
            Alter(new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "text" },
                new AddColumnOperation { ClrType = typeof(string), IsNullable = false, ColumnType = "text" })
        },
        {
            "type change",
            Alter(new AddColumnOperation { ClrType = typeof(long), ColumnType = "bigint" },
                new AddColumnOperation { ClrType = typeof(int), ColumnType = "integer" })
        },
        {
            "narrowed max length",
            Alter(new AddColumnOperation { ClrType = typeof(string), MaxLength = 64, ColumnType = "character varying(64)" },
                new AddColumnOperation { ClrType = typeof(string), MaxLength = 32, ColumnType = "character varying(32)" })
        },
        {
            "unbounded text given a limit",
            Alter(new AddColumnOperation { ClrType = typeof(string), ColumnType = "text" },
                new AddColumnOperation { ClrType = typeof(string), MaxLength = 32, ColumnType = "character varying(32)" })
        },
        { "raw drop", new SqlOperation { Sql = "ALTER TABLE \"SyncJobs\" DROP COLUMN \"Error\";" } },
        { "raw rename", new SqlOperation { Sql = "alter table \"SyncJobs\" rename column \"Error\" to \"Failure\";" } },
        { "raw type change", new SqlOperation { Sql = "ALTER TABLE \"SyncJobs\" ALTER COLUMN \"Priority\" TYPE integer;" } },
        { "raw set not null", new SqlOperation { Sql = "ALTER TABLE \"SyncJobs\" ALTER COLUMN \"Error\" SET NOT NULL;" } },
        { "raw truncate", new SqlOperation { Sql = "TRUNCATE \"SyncJobs\";" } },
        { "raw delete", new SqlOperation { Sql = "DELETE FROM \"SyncJobs\" WHERE \"Status\" = 'Failed';" } },
    };

    [Theory]
    [MemberData(nameof(NonAdditive))]
    public void A_non_additive_operation_is_a_violation(string because, MigrationOperation operation) =>
        Assert.True(Violations([operation]).Count == 1, $"{because} was not detected");

    public static TheoryData<string, MigrationOperation> Additive => new()
    {
        { "add nullable column", new AddColumnOperation { Table = "SyncJobs", Name = "Reason", ClrType = typeof(string), IsNullable = true } },
        { "create table", new CreateTableOperation { Name = "ClientErrors" } },
        { "create index", new CreateIndexOperation { Table = "SyncJobs", Name = "IX_C", Columns = ["Kind"] } },
        { "add foreign key", new AddForeignKeyOperation { Table = "SyncJobs", Name = "FK_C", Columns = ["AccountId"] } },
        {
            "required to nullable",
            Alter(new AddColumnOperation { ClrType = typeof(string), IsNullable = false, ColumnType = "text" },
                new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "text" })
        },
        {
            "widened max length",
            Alter(new AddColumnOperation { ClrType = typeof(string), MaxLength = 32, ColumnType = "character varying(32)" },
                new AddColumnOperation { ClrType = typeof(string), MaxLength = 64, ColumnType = "character varying(64)" })
        },
        {
            "limit lifted to text",
            Alter(new AddColumnOperation { ClrType = typeof(string), MaxLength = 32, ColumnType = "character varying(32)" },
                new AddColumnOperation { ClrType = typeof(string), ColumnType = "text" })
        },
        {
            "default added",
            Alter(new AddColumnOperation { ClrType = typeof(int), ColumnType = "integer" },
                new AddColumnOperation { ClrType = typeof(int), ColumnType = "integer", DefaultValue = 0 })
        },
        { "raw create index", new SqlOperation { Sql = "CREATE INDEX IF NOT EXISTS \"IX_D\" ON \"Titles\" USING gin (\"Search\");" } },
        { "raw backfill", new SqlOperation { Sql = "UPDATE \"Accounts\" SET \"Timezone\" = 'UTC' WHERE \"Timezone\" IS NULL;" } },
        { "add extension", new AlterDatabaseOperation { ["Npgsql:PostgresExtension:pg_trgm"] = ",," } },
    };

    [Theory]
    [MemberData(nameof(Additive))]
    public void An_additive_operation_is_allowed(string because, MigrationOperation operation) =>
        Assert.True(Violations([operation]).Count == 0, $"{because} was wrongly flagged: {string.Join("; ", Violations([operation]))}");

    [Fact]
    public void Removing_an_extension_is_a_violation()
    {
        var operation = new AlterDatabaseOperation();
        operation.OldDatabase["Npgsql:PostgresExtension:vector"] = ",,";
        Assert.Single(Violations([operation]));
    }

    // ── The detector ────────────────────────────────────────────────────────────────────────

    /// <summary>Every non-additive operation in <paramref name="operations"/>, described.</summary>
    internal static IReadOnlyList<string> Violations(IEnumerable<MigrationOperation> operations)
    {
        var found = new List<string>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case AlterColumnOperation alter:
                    found.AddRange(AlterViolations(alter).Select(v => $"alter {alter.Table}.{alter.Name}: {v}"));
                    break;

                case AlterDatabaseOperation database:
                    found.AddRange(database.OldDatabase.GetAnnotations()
                        .Where(a => a.Name.StartsWith("Npgsql:PostgresExtension:", StringComparison.Ordinal))
                        .Where(a => database.FindAnnotation(a.Name) is null)
                        .Select(a => $"removes extension {a.Name["Npgsql:PostgresExtension:".Length..]}"));
                    break;

                case SqlOperation sql when DestructiveSql().IsMatch(sql.Sql):
                    found.Add($"raw SQL: {DestructiveSql().Match(sql.Sql).Value.Trim()}");
                    break;

                case DeleteDataOperation delete:
                    found.Add($"deletes data from {delete.Table}");
                    break;

                case var other when other.GetType().Name.StartsWith("Drop", StringComparison.Ordinal)
                    || other.GetType().Name.StartsWith("Rename", StringComparison.Ordinal):
                    found.Add($"{other.GetType().Name.Replace("Operation", string.Empty)} {Describe(other)}");
                    break;
            }
        }

        return found;
    }

    private static IEnumerable<string> AlterViolations(AlterColumnOperation alter)
    {
        var old = alter.OldColumn;
        if (old.IsNullable && !alter.IsNullable)
        {
            yield return "nullable to required";
        }

        if (old.ClrType != alter.ClrType)
        {
            yield return $"CLR type {old.ClrType.Name} -> {alter.ClrType.Name}";
        }
        else if (!string.Equals(old.ColumnType, alter.ColumnType, StringComparison.OrdinalIgnoreCase)
            && !IsWidening(old, alter))
        {
            yield return $"column type {old.ColumnType ?? "(default)"} -> {alter.ColumnType ?? "(default)"}";
        }

        if (old.IsFixedLength != alter.IsFixedLength)
        {
            yield return "fixed-length changed";
        }

        if (!string.Equals(old.Collation, alter.Collation, StringComparison.Ordinal))
        {
            yield return "collation changed";
        }

        if (!string.Equals(old.ComputedColumnSql, alter.ComputedColumnSql, StringComparison.Ordinal))
        {
            yield return "computed expression changed";
        }
    }

    /// <summary>A bounded string column getting a larger bound, or losing it.</summary>
    private static bool IsWidening(ColumnOperation old, ColumnOperation next)
    {
        if (old.ClrType != typeof(string))
        {
            return false;
        }

        var oldLength = old.MaxLength ?? VarcharLength(old.ColumnType);
        var newLength = next.MaxLength ?? VarcharLength(next.ColumnType);
        var newUnbounded = newLength is null && (next.ColumnType is null || next.ColumnType.Equals("text", StringComparison.OrdinalIgnoreCase));
        return oldLength is not null && (newUnbounded || newLength > oldLength);
    }

    private static int? VarcharLength(string? columnType) =>
        columnType is not null && VarcharType().Match(columnType) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string Describe(MigrationOperation operation) => operation switch
    {
        DropColumnOperation c => $"{c.Table}.{c.Name}",
        DropTableOperation t => t.Name,
        RenameColumnOperation r => $"{r.Table}.{r.Name} -> {r.NewName}",
        RenameTableOperation r => $"{r.Name} -> {r.NewName}",
        RenameIndexOperation r => $"{r.Table}.{r.Name} -> {r.NewName}",
        DropIndexOperation i => $"{i.Table}.{i.Name}",
        DropForeignKeyOperation f => $"{f.Table}.{f.Name}",
        DropPrimaryKeyOperation p => $"{p.Table}.{p.Name}",
        DropUniqueConstraintOperation u => $"{u.Table}.{u.Name}",
        DropCheckConstraintOperation k => $"{k.Table}.{k.Name}",
        _ => string.Empty,
    };

    [GeneratedRegex(
        @"\b(DROP\s+(TABLE|COLUMN|INDEX|SCHEMA|TYPE|VIEW|CONSTRAINT|EXTENSION|TRIGGER|FUNCTION|SEQUENCE)\b[^;]*|RENAME\b[^;]*|TRUNCATE\b[^;]*|DELETE\s+FROM\b[^;]*|ALTER\s+COLUMN\b[^;]*\b(TYPE|SET\s+NOT\s+NULL)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveSql();

    [GeneratedRegex(@"^(?:character varying|varchar)\((\d+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex VarcharType();

    private static AlterColumnOperation Alter(AddColumnOperation from, AddColumnOperation to)
    {
        var alter = new AlterColumnOperation
        {
            Table = "SyncJobs",
            Name = "Col",
            ClrType = to.ClrType,
            ColumnType = to.ColumnType,
            IsNullable = to.IsNullable,
            MaxLength = to.MaxLength,
            DefaultValue = to.DefaultValue,
        };
        alter.OldColumn.ClrType = from.ClrType;
        alter.OldColumn.ColumnType = from.ColumnType;
        alter.OldColumn.IsNullable = from.IsNullable;
        alter.OldColumn.MaxLength = from.MaxLength;
        alter.OldColumn.DefaultValue = from.DefaultValue;
        return alter;
    }
}
