using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The two serving write paths WS-5 task 2 changed, on real Postgres: exposure rows are inserted
/// one-by-one under <c>ON CONFLICT DO NOTHING</c> (a collision costs one row, never the batch), and
/// <see cref="TitleScoreWriter"/> upserts on the unique key (a concurrent second writer refreshes
/// the row instead of throwing 23505). Plus the breakdown rule: all four or none, never zero for
/// "unknown".
/// </summary>
[Collection("postgres")]
public class ExposureWriteIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 24, 18, 5, 12, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)] // the collision is the first card
    [InlineData(1)] // ... in the middle
    [InlineData(2)] // ... the last
    public async Task One_colliding_card_is_skipped_and_the_rest_of_the_batch_still_lands(int collidingIndex)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(3);

        // Another rail already logged one of these titles this minute — the F-7 shape: two genre
        // rails, one film, one minute, one GenreRail surface.
        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);
            Assert.Equal(1, await log.RecordAsync(
                accountId, ExposureSurface.GenreRail, null, artifactId, Now,
                [new ExposureCard(titles[collidingIndex], 0, 7m, WhySource: ExposureWhySource.Templater, RowKey: "genre:action")],
                CancellationToken.None));
        }

        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);
            var written = await log.RecordAsync(
                accountId, ExposureSurface.GenreRail, null, artifactId, Now.AddSeconds(20),
                [.. titles.Select((t, i) => new ExposureCard(t, i, 7m, WhySource: ExposureWhySource.Templater, RowKey: "genre:fantasy"))],
                CancellationToken.None);

            Assert.Equal(2, written);
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(titles.ToHashSet(), rows.Select(r => r.TitleId).ToHashSet());
        // The colliding title keeps the row the FIRST rail wrote — append-only, nothing overwritten.
        Assert.Equal("genre:action", rows.Single(r => r.TitleId == titles[collidingIndex]).RowKey);
    }

    [Fact]
    public async Task A_race_lost_on_one_row_costs_that_row_and_not_the_batch()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(3);

        // Request B reads "nothing logged yet" for title 0 ...
        await using var requestB = fixture.CreateContext(Scoped(accountId));
        var logB = new ExposureLog(requestB, NullLogger<ExposureLog>.Instance);
        await logB.Stage(
            accountId, ExposureSurface.GenreRail, null, artifactId, Now,
            [new ExposureCard(titles[0], 0, 7m, WhySource: ExposureWhySource.Templater)], CancellationToken.None);

        // ... then request A, a concurrent rail serving the same film, commits it first ...
        await using (var requestA = fixture.CreateContext(Scoped(accountId)))
        {
            var logA = new ExposureLog(requestA, NullLogger<ExposureLog>.Instance);
            Assert.Equal(1, await logA.RecordAsync(
                accountId, ExposureSurface.GenreRail, null, artifactId, Now,
                [new ExposureCard(titles[0], 0, 7m, WhySource: ExposureWhySource.Templater)], CancellationToken.None));
        }

        // ... and B records its whole page. Under the old tracked save this threw 23505 on title 0
        // and detached all three, so titles 1 and 2 were never recorded at all.
        var written = await logB.RecordAsync(
            accountId, ExposureSurface.GenreRail, null, artifactId, Now,
            [.. titles.Select((t, i) => new ExposureCard(t, i, 7m, WhySource: ExposureWhySource.Templater))],
            CancellationToken.None);

        Assert.Equal(2, written);
        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal(3, await verify.ExposureEvents.CountAsync(e => e.AccountId == accountId));
    }

    [Fact]
    public async Task A_record_saves_nothing_else_the_caller_had_tracked()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(1);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var title = await db.Titles.SingleAsync(t => t.Id == titles[0]);
        var original = title.Name;
        title.Name = "a mutation the caller never meant to persist";

        var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);
        Assert.Equal(1, await log.RecordAsync(
            accountId, ExposureSurface.FeedRefill, null, artifactId, Now,
            [new ExposureCard(titles[0], 0, 7m, WhySource: ExposureWhySource.Templater)],
            CancellationToken.None));

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.Equal(original, await verify.Titles.Where(t => t.Id == titles[0]).Select(t => t.Name).SingleAsync());
    }

    [Fact]
    public async Task A_breakdown_is_written_whole_or_left_null_and_never_defaults_to_zero()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(3);
        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);
            Assert.Equal(3, await log.RecordAsync(
                accountId, ExposureSurface.FeedPage, null, artifactId, Now,
                [
                    // A surface that scored the card: all four, written as given (3 dp).
                    new ExposureCard(
                        titles[0], 0, 7.42m, BaseScore: 6.9124m, CalibrationDelta: 0.61m, ResidualApplied: -0.05m,
                        DemotionApplied: 0.0876m, WhySource: ExposureWhySource.Templater),

                    // A surface that could not recover the decomposition: nothing claimed.
                    new ExposureCard(titles[1], 1, 7.1m, WhySource: ExposureWhySource.Templater),

                    // A writer that half-guessed: half a decomposition is a wrong one, so none.
                    new ExposureCard(titles[2], 2, 6.8m, BaseScore: 6.2m, WhySource: ExposureWhySource.Templater),
                ],
                CancellationToken.None));
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents.Where(e => e.AccountId == accountId).ToDictionaryAsync(e => e.TitleId);

        var full = rows[titles[0]];
        Assert.Equal(6.912m, full.BaseScore);
        Assert.Equal(0.61m, full.CalibrationDelta);
        Assert.Equal(-0.05m, full.ResidualApplied);
        Assert.Equal(0.088m, full.DemotionApplied);

        foreach (var unknown in new[] { rows[titles[1]], rows[titles[2]] })
        {
            Assert.Null(unknown.BaseScore);
            Assert.Null(unknown.CalibrationDelta);
            Assert.Null(unknown.ResidualApplied);
            Assert.Null(unknown.DemotionApplied);
        }

        // The acceptance query: no row claims a zero base score under a non-zero prediction.
        Assert.Equal(0, await verify.ExposureEvents.CountAsync(e =>
            e.AccountId == accountId && e.BaseScore == 0m && e.PredictedRating != 0m));
    }

    [Fact]
    public async Task Concurrent_score_writers_for_the_same_titles_never_throw_and_the_last_write_wins()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(6);

        // Nine rails at once, all scoring overlapping titles against one artifact — the F-7 burst.
        var writers = Enumerable.Range(0, 9).Select(async i =>
        {
            await using var db = fixture.CreateContext(Scoped(accountId));
            await TitleScoreWriter.UpsertAsync(
                db, accountId, artifactId,
                titles.Select(t => new TitleScoreWriter.Score(t, 6m + (i * 0.1m), "[]")),
                Now.AddSeconds(i),
                CancellationToken.None);
        });
        await Task.WhenAll(writers);

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var stored = await verify.TitleScores.Where(s => s.AccountId == accountId).ToListAsync();
        Assert.Equal(titles.Count, stored.Count); // one row per (account, title, artifact), never two
        Assert.All(stored, s => Assert.InRange(s.PredictedRating, 6m, 6.8m));
    }

    [Fact]
    public async Task A_number_only_refresh_keeps_the_contributions_a_build_stored()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles, artifactId) = await SeedAsync(2);
        const string buildContributions = """[{"feature": "directorAffinity", "value": 0.42}]""";
        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            await TitleScoreWriter.UpsertAsync(
                db, accountId, artifactId,
                [new TitleScoreWriter.Score(titles[0], 7.1m, buildContributions)],
                Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            // The on-demand scorer: an updated number for the stored title, a new row for the other.
            Assert.Equal(2, await TitleScoreWriter.UpsertAsync(
                db, accountId, artifactId,
                [new TitleScoreWriter.Score(titles[0], 7.6m, "[]"), new TitleScoreWriter.Score(titles[1], 6.4m, "[]")],
                Now.AddMinutes(1), CancellationToken.None, keepStoredContributions: true));
        }

        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            var stored = await db.TitleScores.Where(s => s.AccountId == accountId).ToDictionaryAsync(s => s.TitleId);
            Assert.Equal(7.6m, stored[titles[0]].PredictedRating);
            Assert.Contains("directorAffinity", stored[titles[0]].ContributionsJson, StringComparison.Ordinal);
            Assert.Equal("[]", stored[titles[1]].ContributionsJson);
            Assert.Equal(Now.AddMinutes(1), stored[titles[0]].ScoredAt, TimeSpan.FromSeconds(1));
        }

        // And a full write (the build) does replace them.
        await using (var db = fixture.CreateContext(Scoped(accountId)))
        {
            await TitleScoreWriter.UpsertAsync(
                db, accountId, artifactId,
                [new TitleScoreWriter.Score(titles[0], 7.7m, "[]")],
                Now.AddMinutes(2), CancellationToken.None);
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal("[]", await verify.TitleScores
            .Where(s => s.AccountId == accountId && s.TitleId == titles[0])
            .Select(s => s.ContributionsJson)
            .SingleAsync());
    }

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<(Guid AccountId, IReadOnlyList<Guid> Titles, Guid ArtifactId)> SeedAsync(int titleCount)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"exposure-write-{Guid.NewGuid():N}",
            CreatedAt = Now,
            LastSeenAt = Now,
            PipelineStageChangedAt = Now,
        };
        db.Accounts.Add(account);

        var titles = Enumerable.Range(0, titleCount).Select(i => new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"exposure-write-{i}-{Guid.NewGuid():N}",
            Name = $"exposure write {i}",
            Year = 2020,
            CreatedAt = Now,
        }).ToList();
        db.Titles.AddRange(titles);

        // TitleScores carries a foreign key to its artifact, so a real (minimal) artifact row.
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Version = 1,
            ArtifactBytes = [1],
            FeatureSchemaJson = """{"version": 5}""",
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Superseded,
            TrainedAt = Now,
        };
        db.ModelArtifacts.Add(artifact);

        await db.SaveChangesAsync();
        return (account.Id, titles.Select(t => t.Id).ToList(), artifact.Id);
    }
}
