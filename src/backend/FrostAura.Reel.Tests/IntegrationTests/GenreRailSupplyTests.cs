using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A short genre rail says how much of its genre the account's own conditions hold out (WS-5 task 9,
/// D-16) — counted through WS-3's supply reporter, so the N in "your conditions hide N" is the number
/// the eligibility builder actually hides. Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class GenreRailSupplyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_short_rail_counts_what_the_conditions_hide_and_what_was_already_seen()
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"rail-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitleAsync(genre, "en", 2010);                     // eligible
        await SeedTitleAsync(genre, "en", 2012, seenBy: accountId);  // already seen
        await SeedTitleAsync(genre, "en", 1985);                     // held out by the year condition
        await SeedTitleAsync(genre, "en", 1979);                     // held out by the year condition
        await AddFilterAsync(accountId, FilterKind.MinYear, "2000");

        await using var db = Context(accountId);
        var supply = await FeedEndpoints.GenreRailSupplyAsync(
            db, new SupplyReporter(db, new EligibilityQueryBuilder(db)), accountId, genre, mediaType: null, CancellationToken.None);

        Assert.NotNull(supply);
        Assert.Equal(2, supply!.HiddenByConditions);
        Assert.Equal(1, supply.AlreadySeen);
    }

    [Fact]
    public async Task An_include_genre_rule_the_rail_waives_gets_no_number_rather_than_a_wrong_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"rail-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitleAsync(genre, "en", 2010);
        await AddFilterAsync(accountId, FilterKind.IncludeGenre, "drama");

        await using var db = Context(accountId);
        Assert.Null(await FeedEndpoints.GenreRailSupplyAsync(
            db, new SupplyReporter(db, new EligibilityQueryBuilder(db)), accountId, genre, mediaType: null, CancellationToken.None));
    }

    private Infrastructure.Persistence.ReelDbContext Context(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        return fixture.CreateContext(scoped);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rail-supply-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task AddFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = kind, Value = value, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task SeedTitleAsync(string genre, string language, int year, Guid? seenBy = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{genre} {year}",
            Language = language,
            PosterPath = "/p.jpg",
            Genres = [genre],
            Year = year,
            ReleasedAt = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TraktRating = 7.4m,
            TraktVotes = 4_000,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        if (seenBy is { } accountId)
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                LastWatchedAt = DateTime.UtcNow.AddDays(-3),
                Plays = 1,
                IsFullyWatched = true,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }
}
