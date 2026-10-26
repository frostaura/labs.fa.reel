using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Typeahead;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The search box's lookup on real Postgres (WS-3 task 10): tiers beat popularity, the input is
/// literal, people the account has an affinity with lead, and the conditions hide titles in BOTH legs
/// and count what they hid (FD-5's default). Every term carries a token unique to the test, so the
/// shared catalogue cannot add a match.
/// </summary>
[Collection("postgres")]
public class TypeaheadSearchTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Tiers_beat_popularity_and_excluded_titles_are_counted_not_shown()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        var exact = await SeedTitleAsync($"The {token}", votes: 500);
        var prefix = await SeedTitleAsync($"{token} Returns", votes: 9_000);
        var wordStart = await SeedTitleAsync($"Revenge of {token}", votes: 20_000);
        var fuzzy = await SeedTitleAsync($"Super{token}man", votes: 50_000);
        var excluded = await SeedTitleAsync($"{token} Forever", votes: 90_000, genres: ["comedy"]);

        var local = await LocalAsync(accountId, token);

        Assert.Equal([exact, prefix, wordStart, fuzzy], local.Titles.Select(t => t.TitleId));
        Assert.Equal([MatchTier.Exact, MatchTier.Prefix, MatchTier.WordStart, MatchTier.Fuzzy], local.Titles.Select(t => t.Tier));
        Assert.DoesNotContain(excluded, local.Titles.Select(t => t.TitleId));
        Assert.Equal(3, local.StrongMatches);
        Assert.Equal(1, local.HiddenByConditions);
    }

    [Fact]
    public async Task Ilike_metacharacters_match_literally()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        var literal = await SeedTitleAsync($"{token}% Off", votes: 10);
        var unescapedWouldMatch = await SeedTitleAsync($"{token}Prime", votes: 10_000);

        var local = await LocalAsync(accountId, $"{token}%");

        // Unescaped, "{token}%%" is a prefix pattern that makes {token}Prime a strong prefix match.
        Assert.Equal([literal], local.Titles.Select(t => t.TitleId));
        Assert.DoesNotContain(unescapedWouldMatch, local.Titles.Select(t => t.TitleId));
        Assert.Equal(1, local.StrongMatches);
    }

    [Fact]
    public async Task People_the_account_has_an_affinity_with_lead_their_tier()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        await SeedPersonAsync($"Alpha {token}", affinityFor: null);
        var liked = await SeedPersonAsync($"Beta {token}", affinityFor: accountId);

        var local = await LocalAsync(accountId, token);

        // A surname match is the same tier as a first-name match for a person ("nolan" means
        // Christopher Nolan); the account's affinity decides inside the tier.
        Assert.Equal(liked, local.People[0].PersonId);
        Assert.All(local.People, p => Assert.Equal(MatchTier.Prefix, p.Tier));
    }

    [Fact]
    public async Task The_global_leg_judges_tmdb_rows_by_the_conditions_and_counts_what_it_hid()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        var excludedTmdbId = Random.Shared.NextInt64(1, long.MaxValue);
        await SeedTitleAsync($"{token} Catalogue Comedy", votes: 100, genres: ["comedy"], tmdbId: excludedTmdbId);

        var tmdb = new SearchOnlyTmdbClient
        {
            MovieResults =
            [
                Item(excludedTmdbId, $"{token} Catalogue Comedy", "en", popularity: 90m),
                Item(Random.Shared.NextInt64(1, long.MaxValue), $"{token} en français", "fr", popularity: 80m),
                Item(77, $"{token} Kept", "en", popularity: 10m),
                // TMDB's loose search: the name does not carry the term, so neither shown nor counted.
                Item(78, "Whitstable Pearl", "fr", popularity: 50m),
            ],
        };

        await using var db = fixture.CreateContext(new AccountContext());
        var global = await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).GlobalAsync(accountId, tmdb, token, default);

        // Hidden: the catalogue comedy (not counted here — the local leg counts catalogue titles) and
        // the French title beyond the catalogue (counted).
        Assert.Equal([77L], global.Rows.Select(r => r.TmdbId));
        Assert.Equal(1, global.HiddenByConditions);

        var down = await new TypeaheadSearch(db, new EligibilityQueryBuilder(db))
            .GlobalAsync(accountId, new SearchOnlyTmdbClient { Throw = true }, token, default);
        Assert.Empty(down.Rows);
    }

    private static string Token() => "zq" + Guid.NewGuid().ToString("N")[..8];

    private static TmdbListItem Item(long id, string name, string language, decimal popularity) =>
        new(id, true, name, "/p.jpg", null, "An overview.", popularity, 7m, 100,
            new DateTime(2015, 5, 14, 0, 0, 0, DateTimeKind.Utc), [18], language);

    private async Task<TypeaheadLocal> LocalAsync(Guid accountId, string term)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).LocalAsync(accountId, term, default);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"typeahead-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SeedFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = kind, Value = value, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTitleAsync(string name, int votes, string[]? genres = null, long? tmdbId = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId ?? Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = "en",
            Genres = genres ?? ["drama"],
            Year = 2010,
            TmdbVoteAverage = 7m,
            TmdbVoteCount = votes,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task<Guid> SeedPersonAsync(string name, Guid? affinityFor)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1, long.MaxValue), Name = name, KnownForDepartment = "Directing" };
        db.Add(person);
        if (affinityFor is { } accountId)
        {
            db.Add(new PersonAffinity
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                PersonId = person.Id,
                Role = CreditRole.Director,
                Affinity = 0.8m,
                RatedTitleCount = 4,
                ComputedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return person.Id;
    }
}
