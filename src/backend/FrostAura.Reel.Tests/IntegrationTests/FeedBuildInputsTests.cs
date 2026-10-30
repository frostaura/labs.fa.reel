using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The feed build's input fingerprint (<see cref="FeedBuildInputs"/>), read on every five-minute
/// poll. HREV5-PLAT-02: each build logged four EF "First/FirstOrDefault without OrderBy" warnings,
/// one per constant-<c>GroupBy</c> aggregate, and they were the only recurring warnings in an
/// otherwise clean log — a phantom defect an operator met on every build. These pin that the read
/// is warning-free over an empty and a populated footprint, and that the fingerprint still moves
/// on every input it claims to cover.
/// </summary>
[Collection("postgres")]
public class FeedBuildInputsTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 28);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hashing_the_inputs_logs_no_query_warning(bool populated)
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync();
        if (populated)
        {
            await SeedFootprintAsync(account.Id);
        }

        var warnings = new List<string>();
        var options = new DbContextOptionsBuilder<ReelDbContext>()
            .UseNpgsql(fixture.ConnectionString!, npgsql => npgsql.UseVector())
            .LogTo(
                (_, level) => level >= LogLevel.Warning,
                data =>
                {
                    if (data.EventId.Name?.StartsWith(DbLoggerCategory.Query.Name, StringComparison.Ordinal) == true)
                    {
                        warnings.Add($"{data.EventId.Id} {data.EventId.Name}: {data}");
                    }
                })
            .Options;
        await using var db = new ReelDbContext(options, Scoped(account.Id));

        await FeedBuildInputs.HashAsync(db, account, Artifact(account.Id), Day, CancellationToken.None);

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task The_fingerprint_moves_on_every_input_it_covers_and_holds_when_nothing_moved()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync();
        var artifact = Artifact(account.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task<string> HashAsync()
        {
            await using var db = fixture.CreateContext(Scoped(account.Id));
            return await FeedBuildInputs.HashAsync(db, account, artifact, Day, CancellationToken.None);
        }

        async Task Moved(string what)
        {
            var hash = await HashAsync();
            Assert.True(seen.Add(hash), $"the fingerprint did not move on: {what}");
            Assert.Equal(hash, await HashAsync());
        }

        await Moved("nothing at all (the empty footprint)");

        var titleId = await SeedTitleAsync();
        var personId = await SeedPersonAsync();
        var now = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        await WriteAsync(account.Id, db => db.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TitleId = titleId,
            Plays = 1,
            FirstSyncedAt = now,
            UpdatedAt = now,
        }));
        await Moved("a first watch");

        await WriteAsync(account.Id, async db =>
            (await db.WatchedTitles.SingleAsync(w => w.AccountId == account.Id)).Plays = 2);
        await Moved("a rewatch that only bumps the play count");

        var ratingId = Guid.NewGuid();
        await WriteAsync(account.Id, db => db.Add(new UserRating
        {
            Id = ratingId,
            AccountId = account.Id,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 7,
            RatedAt = now,
            FirstRatedAt = now,
            SyncedAt = now,
        }));
        await Moved("a first title rating");

        await WriteAsync(account.Id, async db => (await db.UserRatings.SingleAsync(r => r.Id == ratingId)).Rating = 9);
        await Moved("a re-rate that keeps the count and the sync stamp");

        await WriteAsync(account.Id, db => db.Add(new UserPersonRating
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            PersonId = personId,
            Rating = 8,
            RatedAt = now,
            UpdatedAt = now,
        }));
        await Moved("a first person rating");

        var reactionId = Guid.NewGuid();
        await WriteAsync(account.Id, db => db.Add(new UserTitleReaction
        {
            Id = reactionId,
            AccountId = account.Id,
            TitleId = titleId,
            Kind = ReactionKind.SaveForLater,
            CreatedAt = now,
        }));
        await Moved("a first reaction, not revoked");

        await WriteAsync(account.Id, async db =>
            (await db.UserTitleReactions.SingleAsync(r => r.Id == reactionId)).RevokedAt = now.AddMinutes(5));
        await Moved("that reaction revoked");
    }

    private static ModelArtifact Artifact(Guid accountId) => new() { Id = Guid.NewGuid(), AccountId = accountId };

    private static AccountContext Scoped(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        return scoped;
    }

    private async Task<Account> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"inputs-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title { Id = Guid.NewGuid(), MediaType = MediaType.Movie, TraktSlug = $"inputs-{Guid.NewGuid():N}", Name = "Inputs" };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task<Guid> SeedPersonAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(10_000_000_000, 90_000_000_000), Name = "Inputs Person" };
        db.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    /// <summary>A watch, two ratings of each kind and a revoked reaction beside a live one.</summary>
    private async Task SeedFootprintAsync(Guid accountId)
    {
        var now = DateTime.UtcNow;
        var titles = new[] { await SeedTitleAsync(), await SeedTitleAsync() };
        var people = new[] { await SeedPersonAsync(), await SeedPersonAsync() };
        await WriteAsync(accountId, db =>
        {
            foreach (var titleId in titles)
            {
                db.Add(new WatchedTitle { Id = Guid.NewGuid(), AccountId = accountId, TitleId = titleId, Plays = 2, FirstSyncedAt = now, UpdatedAt = now });
                db.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = titleId,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 8,
                    RatedAt = now,
                    FirstRatedAt = now,
                    SyncedAt = now,
                });
            }

            foreach (var personId in people)
            {
                db.Add(new UserPersonRating { Id = Guid.NewGuid(), AccountId = accountId, PersonId = personId, Rating = 6, RatedAt = now, UpdatedAt = now });
            }

            db.Add(new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = titles[0], Kind = ReactionKind.NotInterested, CreatedAt = now, RevokedAt = now });
            db.Add(new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = titles[1], Kind = ReactionKind.SaveForLater, CreatedAt = now });
        });
    }

    private Task WriteAsync(Guid accountId, Action<ReelDbContext> write) =>
        WriteAsync(accountId, db =>
        {
            write(db);
            return Task.CompletedTask;
        });

    private async Task WriteAsync(Guid accountId, Func<ReelDbContext, Task> write)
    {
        await using var db = fixture.CreateContext(Scoped(accountId));
        await write(db);
        await db.SaveChangesAsync();
    }
}
