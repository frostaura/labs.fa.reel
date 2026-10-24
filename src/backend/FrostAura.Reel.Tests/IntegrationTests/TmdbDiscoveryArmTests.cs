using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The TMDB discovery arm on real Postgres, against a recording TMDB fake: what it SENDS (the
/// account's standing conditions, stand-in keywords on TV only, the vote floor, the interactive
/// lane), how it PAGES (stops on enough eligible titles, on a short page, or at three pages), when
/// it searches by title, and how it JUDGES what comes back (a catalogue row through the eligibility
/// builder — so a seen title is never eligible — and a new title through the same rules in memory).
/// </summary>
[Collection("postgres")]
public class TmdbDiscoveryArmTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public void The_container_serves_the_arm()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var scope = api.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TmdbDiscoveryArm>());
    }

    [Fact]
    public async Task Standing_conditions_travel_with_the_fetch_and_stand_ins_reach_tv_only()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "romance");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "animation");
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");
        await SeedFilterAsync(accountId, FilterKind.MinYear, "2000");

        var tmdb = new RecordingTmdb();
        tmdb.Keywords["ninja"] = 1001;
        tmdb.Keywords["romance"] = 9840;
        tmdb.Keywords["romantic"] = 9841;

        var result = await RunAsync(tmdb, Request(accountId, concepts: ["ninja"], years: new YearRange(1990, 2015)));

        var movie = Assert.Single(tmdb.Discovers, d => d.Movies);
        var show = Assert.Single(tmdb.Discovers, d => !d.Movies);
        foreach (var call in new[] { movie, show })
        {
            Assert.Equal("en", call.Options!.OriginalLanguage);
            Assert.Equal(6.0m, call.Options.MinVoteAverage);
            Assert.Equal(TmdbDiscoveryArm.DefaultMinVoteCount, call.Options.MinVoteCount);
            Assert.Equal(RatePriority.Interactive, call.Options.Priority);
            Assert.Equal([1001], call.KeywordIds);
            // The tighter of the account's MinYear (2000) and the request's window (1990–2015).
            Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), call.After);
            Assert.Equal(new DateTime(2015, 12, 31, 0, 0, 0, DateTimeKind.Utc), call.Before);
        }

        // Movies name romance and animation by genre id; TV has no romance id, so romance reaches TV
        // as without_keywords and nowhere else.
        Assert.Equal([16, 10749], movie.ExcludeGenreIds!.Order().ToList());
        Assert.Equal([16], show.ExcludeGenreIds);
        Assert.Empty(movie.ExcludeKeywordIds!);
        Assert.Equal([9840, 9841], show.ExcludeKeywordIds!.Order().ToList());

        // "ninja" is the request's own concept — a topic, not a title — so no title search ran.
        Assert.Null(result.TitleSearched);
        Assert.Empty(tmdb.Searches);
    }

    [Fact]
    public async Task Paging_stops_on_enough_eligible_titles_on_a_short_page_and_at_three_pages()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // Full pages of eligible new titles: one page holds 20, so want=20 stops after page 1.
        var tmdb = new RecordingTmdb { PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m) };
        var enough = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 20));
        Assert.Equal(1, enough.Pages);
        Assert.Equal(20, enough.EligibleItems.Count);

        // Both media at once: they page concurrently against TMDB and take turns on the one context.
        tmdb = new RecordingTmdb
        {
            PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m),
            DiscoverDelay = _ => TimeSpan.FromMilliseconds(5),
        };
        var bothMedia = await RunAsync(tmdb, Request(accountId, media: MediaScope.Any, want: 30));
        Assert.Equal(4, bothMedia.Pages);
        Assert.Equal(80, bothMedia.EligibleItems.Count);

        // A short page is the last page there is.
        tmdb = new RecordingTmdb { PageFactory = (movies, page) => Page(movies, page, page == 1 ? 20 : 7, voteAverage: 7.5m) };
        var shortPage = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 100));
        Assert.Equal(2, shortPage.Pages);
        Assert.Equal(27, shortPage.Items.Count);

        // Nothing eligible (all English-less): three pages and no more.
        tmdb = new RecordingTmdb { PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m, language: "ja") };
        var capped = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 5));
        Assert.Equal(TmdbDiscoveryArm.MaxPages, capped.Pages);
        Assert.Equal(TmdbDiscoveryArm.MaxPages, tmdb.Discovers.Count);
        Assert.Empty(capped.EligibleItems);
        Assert.Equal(0d, capped.EligibleShare);
    }

    [Fact]
    public async Task A_catalogue_title_is_judged_by_the_builder_and_a_new_title_by_the_same_rules()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");

        var seenTmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var freshTmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var seen = await SeedTitleAsync(seenTmdbId);
        var fresh = await SeedTitleAsync(freshTmdbId);
        await WatchAsync(accountId, seen);

        TmdbListItem Item(long id, decimal vote, int[] genres) =>
            new(id, true, $"arm-fixture {id}", null, null, null, 10m, vote, 500, new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc), genres, "en");

        var newGood = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var newLowRated = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var newComedy = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var tmdb = new RecordingTmdb
        {
            PageFactory = (movies, page) => movies && page == 1
                ?
                [
                    Item(seenTmdbId, 8m, [18]), Item(freshTmdbId, 8m, [18]),
                    Item(newGood, 7m, [18]), Item(newLowRated, 5.5m, [18]), Item(newComedy, 7m, [35, 18]),
                ]
                : [],
        };

        var result = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies));
        var byId = result.Items.ToDictionary(i => i.Item.Id);

        Assert.Equal(seen, byId[seenTmdbId].CatalogueTitleId);
        Assert.False(byId[seenTmdbId].Eligible);          // watched: never eligible, whatever TMDB says
        Assert.Equal(fresh, byId[freshTmdbId].CatalogueTitleId);
        Assert.True(byId[freshTmdbId].Eligible);
        Assert.Null(byId[newGood].CatalogueTitleId);
        Assert.True(byId[newGood].Eligible);
        Assert.False(byId[newLowRated].Eligible);          // under the crowd floor
        Assert.False(byId[newComedy].Eligible);            // excluded genre, read off TMDB's genre ids
        Assert.Equal(0.4, result.EligibleShare, precision: 3);
    }

    [Fact]
    public async Task A_title_shaped_message_is_searched_by_title_and_a_description_is_not()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var tmdb = new RecordingTmdb();
        var titled = await RunAsync(tmdb, Request(accountId, message: "Inception"));
        Assert.Equal("Inception", titled.TitleSearched);
        Assert.Equal(2, tmdb.Searches.Count);              // movie and TV
        Assert.All(tmdb.Searches, s => Assert.Equal("Inception", s));

        tmdb = new RecordingTmdb();
        var described = await RunAsync(
            tmdb, Request(accountId, message: "none of the above are ninja movies. I want ninja movies", concepts: ["ninja"]));
        Assert.Null(described.TitleSearched);
        Assert.Empty(tmdb.Searches);
    }

    [Fact]
    public async Task The_deadline_returns_what_arrived_instead_of_waiting()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdb = new RecordingTmdb
        {
            PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m),
            DiscoverDelay = page => page >= 2 ? TimeSpan.FromSeconds(30) : TimeSpan.Zero,
        };

        var result = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 500), TimeSpan.FromSeconds(2));
        Assert.True(result.TimedOut);
        Assert.Equal(1, result.Pages);
        Assert.Equal(20, result.Items.Count);
        Assert.True(result.ElapsedMs < 10_000);
    }

    private static TmdbDiscoveryRequest Request(
        Guid accountId,
        IReadOnlyList<string>? concepts = null,
        MediaScope media = MediaScope.Any,
        YearRange? years = null,
        string? message = null,
        int want = 40) =>
        new(accountId, [], concepts ?? [], [], [], media, years, null, message, null, want);

    private static List<TmdbListItem> Page(bool movies, int page, int count, decimal voteAverage, string language = "en") =>
        [.. Enumerable.Range(0, count).Select(i => new TmdbListItem(
            (movies ? 2_000_000_000L : 3_000_000_000L) + (page * 100) + i,
            movies,
            $"arm-page {page}-{i}",
            null,
            null,
            null,
            100m - i,
            voteAverage,
            900,
            new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            [18],
            language))];

    private async Task<TmdbDiscoveryResult> RunAsync(RecordingTmdb tmdb, TmdbDiscoveryRequest request, TimeSpan? timeout = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var arm = new TmdbDiscoveryArm(tmdb, db, new EligibilityQueryBuilder(db), cache);
        return await arm.DiscoverAsync(request, timeout ?? TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"tmdb-arm-{Guid.NewGuid():N}",
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
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTitleAsync(long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Empty,
            Name = $"arm-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            TmdbVoteAverage = 8m,
            TmdbVoteCount = 500,
            TmdbPopularity = 0m,
            Year = 2010,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task WatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            LastWatchedAt = DateTime.UtcNow,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A TMDB that records every discover and title search and answers from a page factory.</summary>
    private sealed class RecordingTmdb : ITmdbClient
    {
        public sealed record DiscoverCall(
            bool Movies, int Page, IReadOnlyList<int> KeywordIds, IReadOnlyList<int>? ExcludeGenreIds,
            IReadOnlyList<int>? ExcludeKeywordIds, DateTime? After, DateTime? Before, TmdbDiscoverOptions? Options);

        public Dictionary<string, int> Keywords { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<DiscoverCall> Discovers { get; } = [];

        public List<string> Searches { get; } = [];

        public Func<bool, int, List<TmdbListItem>> PageFactory { get; init; } = (_, _) => [];

        public Func<int, TimeSpan> DiscoverDelay { get; init; } = _ => TimeSpan.Zero;

        public async Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default)
        {
            lock (Discovers)
            {
                Discovers.Add(new DiscoverCall(
                    movies, page, keywordIds, excludeGenreIds, excludeKeywordIds, releasedAfter, releasedBefore, options));
            }

            var delay = DiscoverDelay(page);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            return PageFactory(movies, page);
        }

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>(
                Keywords.TryGetValue(query, out var id) ? [new TmdbKeyword(id, query)] : []);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default)
        {
            lock (Searches)
            {
                Searches.Add(query);
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
