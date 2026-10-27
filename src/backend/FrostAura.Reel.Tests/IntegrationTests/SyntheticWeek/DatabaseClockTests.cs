using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests.SyntheticWeek;

/// <summary>
/// The synthetic week's clock (R1-36): aging a database moves every stored timestamp back by the same
/// interval and nothing else — on a database of its own, because aging the shared test database would
/// age every other class's rows under it.
/// </summary>
[Collection("postgres")]
public class DatabaseClockTests(PostgresFixture fixture)
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Aging_moves_every_stored_timestamp_back_and_leaves_the_rest()
    {
        if (!fixture.Available)
        {
            return;
        }

        var connectionString = await FreshDatabaseAsync("clock");
        try
        {
            var (accountId, titleId, jobId) = await SeedAsync(connectionString);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var written = await DatabaseClock.AgeAsync(connection, TimeSpan.FromHours(6));
                Assert.Equal(1, written["SyncJobs"]);
                Assert.DoesNotContain("TitleEmbeddings", written.Keys);
                Assert.DoesNotContain("ExposureOutcomes", written.Keys); // a view
            }

            await using var db = Context(connectionString);
            var job = await db.SyncJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(Noon.AddHours(-6), job.EnqueuedAt);
            Assert.Equal(Noon.AddHours(-6).AddSeconds(3), job.StartedAt);
            Assert.Null(job.CompletedAt); // a null stays null
            Assert.Equal("""{"Force": true}""", job.CursorJson); // JSON is opaque to the clock

            var title = await db.Titles.SingleAsync(t => t.Id == titleId);
            Assert.Equal(Noon.AddDays(-30).AddHours(-6), title.ReleasedAt); // a release is a week further back a week later
            Assert.Equal(2026, title.Year); // a year is not a timestamp

            // The skipped table really is skipped.
            Assert.Equal(Noon, (await db.TitleEmbeddings.SingleAsync(e => e.TitleId == titleId)).CreatedAt);

            // Vary one field: the two exposures six hours apart on one snapshot — the second is moved onto
            // the first's minute bucket. One-step, the unique index would refuse it; two-step, both move.
            var exposures = await db.ExposureEvents.Where(e => e.AccountId == accountId).OrderBy(e => e.OccurredAt).ToListAsync();
            Assert.Equal([Noon.AddHours(-12), Noon.AddHours(-6)], exposures.Select(e => e.OccurredAt));
            Assert.Equal(exposures.Select(e => e.OccurredAt), exposures.Select(e => e.MinuteBucket));
        }
        finally
        {
            await using var db = Context(connectionString);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task The_plan_shifts_a_unique_timestamp_index_in_two_steps_and_never_a_generated_column()
    {
        if (!fixture.Available)
        {
            return;
        }

        var connectionString = await FreshDatabaseAsync("clockplan");
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var plan = (await DatabaseClock.PlanAsync(connection)).ToDictionary(p => p.Table);

            Assert.True(plan["ExposureEvents"].TwoStep);
            Assert.DoesNotContain("MinuteBucket", plan["ExposureEvents"].Columns);
            Assert.False(plan["SyncJobs"].TwoStep);
            Assert.Subset(plan["SyncJobs"].Columns.ToHashSet(), new HashSet<string> { "EnqueuedAt", "StartedAt", "HeartbeatAt", "CompletedAt" });
            Assert.DoesNotContain("TitleEmbeddings", plan.Keys);
            Assert.DoesNotContain("__EFMigrationsHistory", plan.Keys);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DatabaseClock.AgeAsync(connection, TimeSpan.Zero));
        }
        finally
        {
            await using var db = Context(connectionString);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private async Task<string> FreshDatabaseAsync(string suffix)
    {
        var shared = new NpgsqlConnectionStringBuilder(fixture.ConnectionString!);
        var own = new NpgsqlConnectionStringBuilder(fixture.ConnectionString!) { Database = $"{shared.Database}_{suffix}" }.ConnectionString;
        if (PostgresFixtureGate.LiveTargetRefusal(own) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        await using var db = Context(own);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return own;
    }

    private static async Task<(Guid AccountId, Guid TitleId, Guid JobId)> SeedAsync(string connectionString)
    {
        await using var db = Context(connectionString);
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"clock-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = Noon,
            LastSeenAt = Noon,
            PipelineStageChangedAt = Noon,
        };
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = 4242,
            TraktSlug = string.Empty,
            Name = "Clockwork",
            Language = "en",
            Genres = ["drama"],
            Year = 2026,
            ReleasedAt = Noon.AddDays(-30),
            CreatedAt = Noon,
        };
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = JobKind.DeltaSync,
            Status = JobStatus.Running,
            EnqueuedAt = Noon,
            StartedAt = Noon.AddSeconds(3),
            CursorJson = """{"Force": true}""",
        };
        var snapshot = Guid.NewGuid();
        db.AddRange(account, title, job);
        db.Add(new TitleEmbedding { TitleId = title.Id, Embedding = new Vector(new float[1536]), EmbeddingModel = "clock", SourceTextHash = "h", CreatedAt = Noon });
        await db.SaveChangesAsync();

        // The later exposure is written first (its own save: EF orders one batch's inserts by key, not by
        // Add), so it is also updated first — a one-step shift moves it onto the earlier one's minute
        // bucket while that row is still there.
        foreach (var at in new[] { Noon, Noon.AddHours(-6) })
        {
            db.Add(new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                OccurredAt = at,
                Surface = ExposureSurface.FeedHero,
                FeedSnapshotId = snapshot,
                ModelArtifactId = Guid.NewGuid(),
                PredictedRating = 7m,
            });
            await db.SaveChangesAsync();
        }

        return (account.Id, title.Id, job.Id);
    }

    private static ReelDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(connectionString, n => n.UseVector()).Options, new AccountContext());
}
