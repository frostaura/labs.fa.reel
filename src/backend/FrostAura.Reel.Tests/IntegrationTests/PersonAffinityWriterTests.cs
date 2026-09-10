using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The writer behind <c>PersonAffinities</c>, on real Postgres.
///
/// <para>
/// The table had a schema, a unique index, a query filter and a reader —
/// <c>CandidateGenerator.TopPeopleTmdbIdsAsync</c> seeds discovery arm 2 from it — and no writer
/// at all, so it was empty on every account and that arm contributed nothing to any feed build,
/// silently. These tests assert the round trip that was missing: seeded credits and ratings
/// produce rows, and those rows make the person-discover arm return ids.
/// </para>
///
/// <para>
/// Isolation note: <c>Titles</c>, <c>Persons</c> and <c>TitleCredits</c> are GLOBAL and this
/// collection shares one database, so every assertion here is scoped to people this test seeded
/// rather than to a count of all rows — the trap that put two sibling classes in the local-flaky
/// quarantine.
/// </para>
/// </summary>
[Collection("postgres")]
public class PersonAffinityWriterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Credits_on_rated_titles_become_affinity_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var director = await SeedPersonAsync();
        var oneOff = await SeedPersonAsync();

        // Three rated titles carrying the director; one carrying the one-off, who therefore never
        // clears VerifiedPeople.MinRatedTitles.
        foreach (var rating in new short[] { 9, 8, 10 })
        {
            var titleId = await SeedTitleAsync();
            await CreditAsync(titleId, director, CreditRole.Director);
            await RateAsync(accountId, titleId, rating);
        }

        var singleTitle = await SeedTitleAsync();
        await CreditAsync(singleTitle, oneOff, CreditRole.Director);
        await RateAsync(accountId, singleTitle, 7);

        var written = await RefreshAsync(accountId);

        Assert.Equal(1, written.Inserted);
        Assert.Equal(0, written.Updated);

        var rows = await RowsAsync(accountId, [director, oneOff]);
        var row = Assert.Single(rows);
        Assert.Equal(director, row.PersonId);
        Assert.Equal(CreditRole.Director, row.Role);
        Assert.Equal(3, row.RatedTitleCount);
        Assert.NotEqual(default, row.ComputedAt);

        // The stored number is the one the why-sentences quote — same helper, not a second
        // implementation. Shrunken toward the user's own mean, so it sits under the raw 9.0.
        await using var db = fixture.CreateContext(new AccountContext());
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var expected = TasteMath.ShrunkenMean(taste.PersonRatings[director], taste.UserMean);
        Assert.Equal(Math.Round(expected, 2), Math.Round(row.Affinity, 2));
    }

    [Fact]
    public async Task A_person_credited_in_two_roles_gets_a_row_for_each()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var auteur = await SeedPersonAsync();

        foreach (var rating in new short[] { 9, 8 })
        {
            var titleId = await SeedTitleAsync();
            await CreditAsync(titleId, auteur, CreditRole.Director);
            await CreditAsync(titleId, auteur, CreditRole.Writer);
            await RateAsync(accountId, titleId, rating);
        }

        await RefreshAsync(accountId);

        var rows = await RowsAsync(accountId, [auteur]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Role == CreditRole.Director);
        Assert.Contains(rows, r => r.Role == CreditRole.Writer);

        // Both rows carry the SAME average: TasteState.PersonRatings is keyed by person, not by
        // (person, role), and the credit features and why-sentences read that same pool. A per-role
        // average here would be a second, disagreeing number for one person.
        Assert.Equal(rows[0].Affinity, rows[1].Affinity);
    }

    [Fact]
    public async Task A_second_refresh_updates_in_place_and_retires_what_no_longer_qualifies()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var director = await SeedPersonAsync();
        var titles = new List<Guid>();
        foreach (var rating in new short[] { 9, 8 })
        {
            var titleId = await SeedTitleAsync();
            await CreditAsync(titleId, director, CreditRole.Director);
            await RateAsync(accountId, titleId, rating);
            titles.Add(titleId);
        }

        var first = await RefreshAsync(accountId);
        Assert.Equal(1, first.Inserted);
        var firstRow = Assert.Single(await RowsAsync(accountId, [director]));

        // A third, lower rating: same row, new numbers, no duplicate — the unique index on
        // (AccountId, PersonId, Role) would reject an insert anyway, so a writer that only ever
        // inserted would break the second train rather than refresh anything.
        var third = await SeedTitleAsync();
        await CreditAsync(third, director, CreditRole.Director);
        await RateAsync(accountId, third, 4);

        var second = await RefreshAsync(accountId);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Updated);
        var updatedRow = Assert.Single(await RowsAsync(accountId, [director]));
        Assert.Equal(firstRow.Id, updatedRow.Id);
        Assert.Equal(3, updatedRow.RatedTitleCount);
        Assert.True(updatedRow.Affinity < firstRow.Affinity);

        // Withdraw two ratings — the pool drops under the bar, so the row must go rather than keep
        // seeding discovery from evidence that no longer exists.
        await UnrateAsync(accountId, titles);
        var third_ = await RefreshAsync(accountId);
        Assert.Equal(1, third_.Deleted);
        Assert.Empty(await RowsAsync(accountId, [director]));
    }

    [Fact]
    public async Task The_person_discover_arm_returns_ids_once_the_writer_has_run()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The end-to-end point of the whole exercise: before the writer, this arm read an empty
        // table and asked TMDB about nobody, on every build, for every account.
        var accountId = await SeedAccountAsync();
        var directorTmdbId = Random.Shared.NextInt64(1_000_000, 9_000_000);
        var director = await SeedPersonAsync(directorTmdbId);

        // CandidateGenerator's derived-person bar is RatedTitleCount >= 3, above VerifiedPeople's 2.
        foreach (var rating in new short[] { 10, 9, 9, 8 })
        {
            var titleId = await SeedTitleAsync();
            await CreditAsync(titleId, director, CreditRole.Director);
            await RateAsync(accountId, titleId, rating);
        }

        var askedBefore = await DiscoveredPeopleAsync(accountId);
        Assert.DoesNotContain(directorTmdbId, askedBefore);

        await RefreshAsync(accountId);

        var askedAfter = await DiscoveredPeopleAsync(accountId);
        Assert.Contains(directorTmdbId, askedAfter);
    }

    [Fact]
    public async Task One_accounts_ratings_never_produce_another_accounts_affinities()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Credits and titles are global; ratings are not. A writer that derived people from the
        // catalogue rather than from THIS account's ratings would leak taste across tenants.
        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var director = await SeedPersonAsync();

        foreach (var rating in new short[] { 9, 9, 8 })
        {
            var titleId = await SeedTitleAsync();
            await CreditAsync(titleId, director, CreditRole.Director);
            await RateAsync(mine, titleId, rating);
        }

        await RefreshAsync(mine);
        await RefreshAsync(theirs);

        Assert.Single(await RowsAsync(mine, [director]));
        Assert.Empty(await RowsAsync(theirs, [director]));

        // And the tenancy filter hides mine from a request scoped to them, not merely the query.
        var scoped = new AccountContext();
        scoped.SetAccount(theirs);
        await using var db = fixture.CreateContext(scoped);
        Assert.Empty(await db.PersonAffinities.Where(a => a.PersonId == director).ToListAsync());
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<PersonAffinityWriter.Result> RefreshAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var writer = new PersonAffinityWriter(db, NullLogger<PersonAffinityWriter>.Instance);
        var result = await writer.RefreshAsync(accountId, taste, DateTime.UtcNow, default);
        await db.SaveChangesAsync();
        return result;
    }

    private async Task<List<PersonAffinity>> RowsAsync(Guid accountId, IReadOnlyList<Guid> personIds)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.PersonAffinities
            .Where(a => a.AccountId == accountId && personIds.Contains(a.PersonId))
            .OrderBy(a => a.Role)
            .ToListAsync();
    }

    private async Task<List<long>> DiscoveredPeopleAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var tmdb = new SilentTmdbClient();
        var generator = new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance);
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        await generator.RefreshPoolAsync(accountId, taste, "ZA", DateTime.UtcNow, default);
        return tmdb.PeopleAsked;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"affinity-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedPersonAsync(long? tmdbId = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person
        {
            Id = Guid.NewGuid(),
            TmdbId = tmdbId ?? Random.Shared.NextInt64(1_000_000, 9_000_000),
            Name = $"person {Guid.NewGuid():N}",
        };
        db.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"title {Guid.NewGuid():N}",
            Genres = ["thriller"],
            Language = "en",
            Year = 2015,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task CreditAsync(Guid titleId, Guid personId, CreditRole role)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleCredits.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            PersonId = personId,
            Role = role,
        });
        await db.SaveChangesAsync();
    }

    private async Task RateAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ratedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(30, 400));
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = ratedAt,
            FirstRatedAt = ratedAt,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task UnrateAsync(Guid accountId, IReadOnlyList<Guid> titleIds)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.UserRatings
            .Where(r => r.AccountId == accountId && titleIds.Contains(r.TitleId))
            .ToListAsync();
        db.UserRatings.RemoveRange(rows);
        await db.SaveChangesAsync();
    }
    /// <summary>
    /// Answers nothing and records only which PEOPLE the build asked TMDB about — the single
    /// signal these tests are about. Deliberately not shared with CandidateGeneratorPoolTests'
    /// recorder: that one is shaped around genre/page/budget assertions, and widening it for one
    /// caller is how a fake stops being readable.
    /// </summary>
    private sealed class SilentTmdbClient : ITmdbClient
    {
        public List<long> PeopleAsked { get; } = [];

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region,
            DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null,
            IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null,
            int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default)
        {
            PeopleAsked.AddRange(options?.PeopleIds ?? []);
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
