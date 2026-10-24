using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
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

        var tmdb = new RecordingTmdbClient();
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
        var romanceIds = GenreStandIns.Patterns["romance"].Select(p => GenreStandIns.TmdbKeywordIds[p.Text]).Order().ToList();
        Assert.Equal(romanceIds, show.ExcludeKeywordIds!.Order().ToList());
        Assert.Contains(9840, show.ExcludeKeywordIds!);
        // The ids come from the table: the only keyword TMDB was asked about is the request's concept.
        Assert.Equal(["ninja"], tmdb.KeywordSearches);

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
        var tmdb = new RecordingTmdbClient { PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m) };
        var enough = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 20));
        Assert.Equal(1, enough.Pages);
        Assert.Equal(20, enough.ProvisionalItems.Count);   // new titles: usable once hydrated, never "eligible" yet
        Assert.Empty(enough.EligibleItems);

        // Both media at once: they page concurrently against TMDB and take turns on the one context.
        tmdb = new RecordingTmdbClient
        {
            PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m),
            DiscoverDelay = _ => TimeSpan.FromMilliseconds(5),
        };
        var bothMedia = await RunAsync(tmdb, Request(accountId, media: MediaScope.Any, want: 30));
        Assert.Equal(4, bothMedia.Pages);
        Assert.Equal(80, bothMedia.ProvisionalItems.Count);

        // A short page is the last page there is.
        tmdb = new RecordingTmdbClient { PageFactory = (movies, page) => Page(movies, page, page == 1 ? 20 : 7, voteAverage: 7.5m) };
        var shortPage = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 100));
        Assert.Equal(2, shortPage.Pages);
        Assert.Equal(27, shortPage.Items.Count);

        // Nothing eligible (all English-less): three pages and no more.
        tmdb = new RecordingTmdbClient { PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m, language: "ja") };
        var capped = await RunAsync(tmdb, Request(accountId, media: MediaScope.Movies, want: 5));
        Assert.Equal(TmdbDiscoveryArm.MaxPages, capped.Pages);
        Assert.Equal(TmdbDiscoveryArm.MaxPages, tmdb.Discovers.Count);
        Assert.Empty(capped.EligibleItems);
        Assert.Empty(capped.ProvisionalItems);
        Assert.Equal(0d, capped.EligibleShare);
        Assert.Equal(0d, capped.UsableShare);
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
        var tmdb = new RecordingTmdbClient
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
        Assert.Equal(TmdbDiscoveryVerdict.Provisional, byId[newGood].Verdict);   // a list row cannot be fully judged
        Assert.False(byId[newGood].Eligible);
        Assert.Equal(TmdbDiscoveryVerdict.Ineligible, byId[newLowRated].Verdict);  // under the crowd floor
        Assert.Equal(TmdbDiscoveryVerdict.Ineligible, byId[newComedy].Verdict);    // excluded genre, read off TMDB's genre ids

        // The gate's share counts eligible CATALOGUE titles only (1 of 5); provisional ones are a bound.
        Assert.Equal(0.2, result.EligibleShare, precision: 3);
        Assert.Equal(0.4, result.UsableShare, precision: 3);
    }

    [Fact]
    public async Task A_title_shaped_message_is_searched_by_title_and_a_description_is_not()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var tmdb = new RecordingTmdbClient();
        var titled = await RunAsync(tmdb, Request(accountId, message: "Inception"));
        Assert.Equal("Inception", titled.TitleSearched);
        Assert.Equal(2, tmdb.Searches.Count);              // movie and TV
        Assert.All(tmdb.Searches, s => Assert.Equal("Inception", s));

        tmdb = new RecordingTmdbClient();
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
        var tmdb = new RecordingTmdbClient
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

    [Fact]
    public async Task The_turns_own_rejections_judge_every_result_as_strictly_as_a_standing_exclusion()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // "Sci-fi shows, no fantasy, no zombies": TV's 10765 is BOTH, so it is not sent as
        // without_genres (the request asked for sci-fi) — and the judging must still enforce it.
        long Id() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var sciFiOnlyId = Id();
        var sciFiFantasyId = Id();
        var zombieId = Id();
        var sciFiOnly = await SeedTitleAsync(sciFiOnlyId, t => { t.MediaType = MediaType.Show; t.Genres = ["science-fiction"]; });
        await SeedTitleAsync(sciFiFantasyId, t => { t.MediaType = MediaType.Show; t.Genres = ["science-fiction", "fantasy"]; });
        await SeedTitleAsync(zombieId, t => { t.MediaType = MediaType.Show; t.Genres = ["science-fiction"]; t.Keywords = ["zombie apocalypse"]; });
        var newFusedId = Id();
        var newDramaId = Id();

        TmdbListItem Show(long id, int[] genres) =>
            new(id, false, $"arm-fixture {id}", null, null, null, 10m, 7.5m, 500, new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc), genres, "en");

        var tmdb = new RecordingTmdbClient
        {
            PageFactory = (movies, page) => !movies && page == 1
                ? [Show(sciFiOnlyId, [10765]), Show(sciFiFantasyId, [10765]), Show(zombieId, [10765]), Show(newFusedId, [10765]), Show(newDramaId, [18])]
                : [],
        };

        var result = await RunAsync(tmdb, Request(
            accountId, genres: ["science-fiction"], excludeGenres: ["fantasy"], excludeConcepts: ["zombie"], media: MediaScope.Shows));
        var byId = result.Items.ToDictionary(i => i.Item.Id);

        var call = Assert.Single(tmdb.Discovers);
        Assert.DoesNotContain(10765, call.ExcludeGenreIds!);                       // never empties the sci-fi request at TMDB
        Assert.Equal(TmdbDiscoveryVerdict.Eligible, byId[sciFiOnlyId].Verdict);    // stored as sci-fi alone: survives
        Assert.Equal(sciFiOnly, byId[sciFiOnlyId].CatalogueTitleId);
        Assert.Equal(TmdbDiscoveryVerdict.Ineligible, byId[sciFiFantasyId].Verdict); // tagged fantasy: the rejection binds
        Assert.Equal(TmdbDiscoveryVerdict.Ineligible, byId[zombieId].Verdict);       // keyword label carries "zombie"
        Assert.Equal(TmdbDiscoveryVerdict.Ineligible, byId[newFusedId].Verdict);     // a new 10765 show reads as fantasy too
        Assert.Equal(TmdbDiscoveryVerdict.Provisional, byId[newDramaId].Verdict);

        // The same request with no rejections: the fantasy and zombie shows are eligible again, so it
        // was the turn's rejections, not the fixture, that removed them.
        tmdb = new RecordingTmdbClient { PageFactory = tmdb.PageFactory };
        var unrejected = await RunAsync(tmdb, Request(accountId, genres: ["science-fiction"], media: MediaScope.Shows));
        var again = unrejected.Items.ToDictionary(i => i.Item.Id);
        Assert.True(again[sciFiFantasyId].Eligible);
        Assert.True(again[zombieId].Eligible);
        Assert.True(again[newFusedId].Provisional);
    }

    [Fact]
    public async Task A_rejection_tv_cannot_tag_reaches_tv_as_stand_in_keywords()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdb = new RecordingTmdbClient();
        tmdb.Keywords["romance"] = 9840;

        await RunAsync(tmdb, Request(accountId, excludeGenres: ["romance"]));

        var movie = Assert.Single(tmdb.Discovers, d => d.Movies);
        var show = Assert.Single(tmdb.Discovers, d => !d.Movies);
        Assert.Contains(10749, movie.ExcludeGenreIds!);        // movies name romance by genre id
        Assert.DoesNotContain(9840, movie.ExcludeKeywordIds!);
        Assert.Contains(9840, show.ExcludeKeywordIds!);        // TV has no romance id: the stand-in carries it
    }

    [Fact]
    public async Task A_keyword_lookup_that_fails_never_drops_the_topic_nor_escapes_nor_is_remembered()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        // A socket failure surfaces as HttpRequestException: the arm absorbs it, and with its only
        // concept unresolved it skips discover rather than return popular, off-topic titles.
        var tmdb = new RecordingTmdbClient { PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m) };
        tmdb.Keywords["ninja"] = 1001;
        tmdb.ThrowingKeywords.Add("ninja");
        var thrown = await RunAsync(tmdb, Request(accountId, concepts: ["ninja"]), cache: cache);
        Assert.True(thrown.ConceptsUnresolved);
        Assert.Empty(tmdb.Discovers);
        Assert.Empty(thrown.Items);

        // TMDB did not answer (a 5xx / 429): the same, and it is NOT cached — once TMDB recovers, the
        // very next call resolves "ninja" and discovers on it.
        tmdb = new RecordingTmdbClient { PageFactory = tmdb.PageFactory };
        tmdb.Keywords["ninja"] = 1001;
        tmdb.UnansweredKeywords.Add("ninja");
        var unanswered = await RunAsync(tmdb, Request(accountId, concepts: ["ninja"]), cache: cache);
        Assert.True(unanswered.ConceptsUnresolved);
        Assert.Empty(tmdb.Discovers);

        tmdb.UnansweredKeywords.Clear();
        var recovered = await RunAsync(tmdb, Request(accountId, concepts: ["ninja"]), cache: cache);
        Assert.False(recovered.ConceptsUnresolved);
        Assert.All(tmdb.Discovers, d => Assert.Equal([1001], d.KeywordIds));
        Assert.NotEmpty(tmdb.Discovers);

        // One concept of two failing keeps the other: the OR-joined topic narrows, it is not dropped.
        tmdb = new RecordingTmdbClient { PageFactory = tmdb.PageFactory };
        tmdb.Keywords["samurai"] = 2002;
        tmdb.Keywords["shinobi"] = 2003;
        tmdb.UnansweredKeywords.Add("shinobi");
        var partial = await RunAsync(tmdb, Request(accountId, concepts: ["samurai", "shinobi"]), cache: cache);
        Assert.False(partial.ConceptsUnresolved);
        Assert.All(tmdb.Discovers, d => Assert.Equal([2002], d.KeywordIds));

        // TMDB's real answer "no such keyword" IS remembered (for KeywordMissCacheWindow): the second
        // call asks TMDB nothing about it, and still skips the off-topic discover.
        tmdb = new RecordingTmdbClient { PageFactory = tmdb.PageFactory };
        await RunAsync(tmdb, Request(accountId, concepts: ["no-such-keyword"]), cache: cache);
        await RunAsync(tmdb, Request(accountId, concepts: ["no-such-keyword"]), cache: cache);
        Assert.Single(tmdb.KeywordSearches, k => k == "no-such-keyword");
        Assert.Empty(tmdb.Discovers);
    }

    [Fact]
    public async Task A_deadline_during_keyword_resolution_leaves_nothing_running_on_the_context()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "romance");
        // The concept resolves at once, the stand-ins come from the table, but the turn's own
        // rejected concept hangs: no page may already be running (and judging on the shared
        // context) when the deadline fires.
        var tmdb = new RecordingTmdbClient
        {
            PageFactory = (movies, page) => Page(movies, page, 20, voteAverage: 7.5m),
            KeywordDelay = phrase => phrase == "ninja" ? TimeSpan.Zero : TimeSpan.FromSeconds(30),
        };
        tmdb.Keywords["ninja"] = 1001;

        await using var db = fixture.CreateContext(new AccountContext());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var arm = new TmdbDiscoveryArm(tmdb, db, new EligibilityQueryBuilder(db), cache);
        var result = await arm.DiscoverAsync(
            Request(accountId, concepts: ["ninja"], excludeConcepts: ["slow phrase"]), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Empty(tmdb.Discovers);   // no page was started, so none can outlive the call

        // The caller's context is free at once: a second operation on it does not collide.
        for (var i = 0; i < 20; i++)
        {
            Assert.True(await db.ContentFilters.CountAsync(f => f.AccountId == accountId) >= 1);
        }
    }

    [Fact]
    public async Task A_message_that_exactly_names_a_catalogue_title_is_searched_whatever_its_words()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var titles = new[] { $"Scary Movie {suffix}", $"Monsters, Inc. {suffix}", $"How to Train Your Dragon {suffix}" };
        foreach (var name in titles)
        {
            await SeedTitleAsync(Random.Shared.NextInt64(1_000_000_000, long.MaxValue), t => t.Name = name);
        }

        foreach (var name in titles)
        {
            var tmdb = new RecordingTmdbClient();
            var result = await RunAsync(tmdb, Request(accountId, message: name.ToUpperInvariant()));
            Assert.Equal(name.ToUpperInvariant(), result.TitleSearched);
            Assert.NotEmpty(tmdb.Searches);
        }

        // The same shapes naming nothing in the catalogue stay descriptions.
        var unknown = new RecordingTmdbClient();
        Assert.Null((await RunAsync(unknown, Request(accountId, message: $"scary movie {suffix}x"))).TitleSearched);
        Assert.Empty(unknown.Searches);
    }

    private static TmdbDiscoveryRequest Request(
        Guid accountId,
        IReadOnlyList<string>? concepts = null,
        MediaScope media = MediaScope.Any,
        YearRange? years = null,
        string? message = null,
        int want = 40,
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? excludeGenres = null,
        IReadOnlyList<string>? excludeConcepts = null) =>
        new(accountId, genres ?? [], concepts ?? [], excludeGenres ?? [], excludeConcepts ?? [], media, years, null, message, null, want);

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

    private async Task<TmdbDiscoveryResult> RunAsync(
        RecordingTmdbClient tmdb, TmdbDiscoveryRequest request, TimeSpan? timeout = null, IMemoryCache? cache = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        using var own = new MemoryCache(new MemoryCacheOptions());
        var arm = new TmdbDiscoveryArm(tmdb, db, new EligibilityQueryBuilder(db), cache ?? own);
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

    private async Task<Guid> SeedTitleAsync(long tmdbId, Action<Title>? shape = null)
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
        shape?.Invoke(title);
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
}
