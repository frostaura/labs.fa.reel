using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The conditions editor's new filters, exercised on real Postgres — the only place they can be
/// trusted, because each one is a SQL predicate that casts a text column to a number. A translation
/// or cast fault here is invisible to an in-memory test and fatal in production.
///
/// Every assertion is scoped to the ids this test seeded, so a shared catalogue left behind by other
/// integration tests can neither satisfy nor break it. Skips when REEL_TEST_CONNECTION_STRING is
/// unset (no DB in the run).
/// </summary>
[Collection("postgres")]
public class ContentFilterEligibilityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_footprint_is_excluded_by_hashed_lists_never_a_correlated_anti_join()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Many exclusion rules multiply into a planner estimate of ONE surviving title; with a
        // correlated NOT EXISTS Postgres then nested-loops the watch list per survivor (the founder's
        // draft count took 1.4 s instead of 59 ms). NOT IN runs as a hashed subplan regardless. Both
        // overloads, served and draft, must keep that shape.
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(new AccountContext());
        var builder = new EligibilityQueryBuilder(db);
        var rules = FilterRuleSet.Create(["romance", "history", "music", "anime", "musical"], [], [], ["en"], 6.0m, 2000, null);
        foreach (var sql in new[] { builder.EligibleTitles(accountId).ToQueryString(), builder.EligibleTitles(accountId, rules).ToQueryString() })
        {
            foreach (var table in new[] { "WatchedTitles", "UserRatings", "UserTitleReactions" })
            {
                Assert.Matches($"t\\.\"Id\" NOT IN \\(\\s*SELECT [a-z0-9]+\\.\"TitleId\"\\s*FROM \"{table}\"", sql);
            }

            Assert.DoesNotContain("\"TitleId\" = t.\"Id\"", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Crowd_floor_keeps_well_rated_titles_falls_back_to_trakt_and_drops_the_unrated()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var highTmdb = await SeedTitleAsync(t => { t.TmdbVoteAverage = 8.1m; t.TmdbVoteCount = 900; });
        var lowTmdb = await SeedTitleAsync(t => { t.TmdbVoteAverage = 5.2m; t.TmdbVoteCount = 900; });
        // No TMDB rating at all ⇒ the Trakt fallback decides, exactly as the badge does.
        var highTraktOnly = await SeedTitleAsync(t => { t.TraktRating = 9.0m; t.TraktVotes = 40; });
        var lowTraktOnly = await SeedTitleAsync(t => { t.TraktRating = 4.0m; t.TraktVotes = 40; });
        // A TMDB zero is "never rated", not a rating of zero — it must fall through to Trakt too.
        var zeroTmdbHighTrakt = await SeedTitleAsync(t =>
        {
            t.TmdbVoteAverage = 0m;
            t.TraktRating = 8.8m;
            t.TraktVotes = 15;
        });
        var unrated = await SeedTitleAsync(_ => { });

        var mine = new[] { highTmdb, lowTmdb, highTraktOnly, lowTraktOnly, zeroTmdbHighTrakt, unrated };

        // No floor configured ⇒ every one of them is eligible, including the unrated title.
        Assert.Equal(mine.ToHashSet(), (await EligibleAsync(accountId, mine)).ToHashSet());

        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, ContentFilterRules.FormatRating(7.0m));

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Contains(highTmdb, survivors);
        Assert.Contains(highTraktOnly, survivors);
        Assert.Contains(zeroTmdbHighTrakt, survivors);
        Assert.DoesNotContain(lowTmdb, survivors);
        Assert.DoesNotContain(lowTraktOnly, survivors);
        // Documented call, mirroring PassesMaturity's treatment of an unrated certification: a title
        // the catalogue has no crowd rating for FAILS a crowd floor rather than sliding under it.
        Assert.DoesNotContain(unrated, survivors);
    }

    [Fact]
    public async Task Year_window_is_inclusive_falls_back_to_air_dates_and_keeps_undated_titles()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var tooOld = await SeedTitleAsync(t => t.Year = 1979);
        var lowerEdge = await SeedTitleAsync(t => t.Year = 1990);
        var inside = await SeedTitleAsync(t => t.Year = 1999);
        var upperEdge = await SeedTitleAsync(t => t.Year = 2010);
        var tooNew = await SeedTitleAsync(t => t.Year = 2024);
        // Year unset ⇒ the release / first-aired date carries the comparison.
        var datedOnly = await SeedTitleAsync(t => t.ReleasedAt = new DateTime(1995, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var airedOnly = await SeedTitleAsync(t => t.FirstAiredAt = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var undated = await SeedTitleAsync(_ => { });

        var mine = new[] { tooOld, lowerEdge, inside, upperEdge, tooNew, datedOnly, airedOnly, undated };

        await SeedFilterAsync(accountId, FilterKind.MinYear, ContentFilterRules.FormatYear(1990));
        await SeedFilterAsync(accountId, FilterKind.MaxYear, ContentFilterRules.FormatYear(2010));

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Contains(lowerEdge, survivors);   // bounds are inclusive on both ends
        Assert.Contains(upperEdge, survivors);
        Assert.Contains(inside, survivors);
        Assert.Contains(datedOnly, survivors);
        Assert.DoesNotContain(tooOld, survivors);
        Assert.DoesNotContain(tooNew, survivors);
        Assert.DoesNotContain(airedOnly, survivors);
        // Documented call: absence of a date is not evidence of the wrong date, matching how the
        // Ask Reel year window already treats undated titles.
        Assert.Contains(undated, survivors);
    }

    [Fact]
    public async Task An_open_ended_year_bound_only_constrains_its_own_end()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var old = await SeedTitleAsync(t => t.Year = 1975);
        var recent = await SeedTitleAsync(t => t.Year = 2020);
        var mine = new[] { old, recent };

        await SeedFilterAsync(accountId, FilterKind.MinYear, ContentFilterRules.FormatYear(2000));

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Contains(recent, survivors);
        Assert.DoesNotContain(old, survivors);
    }

    [Fact]
    public async Task Include_genres_narrow_the_pool_to_titles_carrying_a_listed_genre()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var horror = await SeedTitleAsync(t => t.Genres = ["horror"]);
        var horrorPlus = await SeedTitleAsync(t => t.Genres = ["comedy", "horror"]);
        var comedy = await SeedTitleAsync(t => t.Genres = ["comedy"]);
        var genreless = await SeedTitleAsync(t => t.Genres = []);
        var mine = new[] { horror, horrorPlus, comedy, genreless };

        // With no include rows the allow-list is inert — all four are eligible.
        Assert.Equal(mine.ToHashSet(), (await EligibleAsync(accountId, mine)).ToHashSet());

        await SeedFilterAsync(accountId, FilterKind.IncludeGenre, "horror");

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Contains(horror, survivors);
        Assert.Contains(horrorPlus, survivors);   // one listed genre is enough
        Assert.DoesNotContain(comedy, survivors);
        Assert.DoesNotContain(genreless, survivors);
    }

    [Fact]
    public async Task A_genre_rail_never_waives_an_excluded_genre()
    {
        if (!fixture.Available)
        {
            return;
        }

        // F-2, reproduced: the founder excludes animation and comedy, and the Adventure rail served
        // Frozen II, Puss in Boots and both Ice Age films. A rail's own genre outranks an allow-list
        // that does not list it; it never outranks an exclusion (FD-6).
        var accountId = await SeedAccountAsync();
        var adventure = await SeedTitleAsync(t => t.Genres = ["adventure"]);
        var adventureFamily = await SeedTitleAsync(t => t.Genres = ["adventure", "family"]);
        var animated = await SeedTitleAsync(t => t.Genres = ["adventure", "animation", "family"]);
        var comedy = await SeedTitleAsync(t => t.Genres = ["comedy", "adventure"]);
        var both = await SeedTitleAsync(t => t.Genres = ["animation", "comedy", "adventure"]);
        var mine = new[] { adventure, adventureFamily, animated, comedy, both };

        // Before any rule: the rail carries all five, so the fixture is not trivially empty.
        Assert.Equal(mine.ToHashSet(), (await RailAsync(accountId, mine, "adventure")).ToHashSet());

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "animation");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");

        var rail = (await RailAsync(accountId, mine, "adventure")).ToHashSet();
        Assert.Equal(new HashSet<Guid> { adventure, adventureFamily }, rail);

        // The same holds through the builder directly, which is what every other rail-shaped caller
        // reaches: the flag only ever loosens the allow-list.
        Assert.Equal(rail, (await EligibleAsync(accountId, mine, ignoreIncludeGenreFilters: true)).ToHashSet());
    }

    [Fact]
    public async Task A_genre_rail_waives_only_the_include_allow_list_and_every_other_condition_binds()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // A comedy the allow-list (horror only) would keep off Tonight's picks.
        var comedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["comedy"];
            t.Year = 2001;
            t.TmdbVoteAverage = 8.4m;
            t.TmdbVoteCount = 500;
        });
        // Same genre, but it trips the crowd floor — the rail must NOT waive that one.
        var weakComedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["comedy"];
            t.Year = 2001;
            t.TmdbVoteAverage = 4.1m;
            t.TmdbVoteCount = 500;
        });
        // Same genre, outside the year window — also still excluded.
        var oldComedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["comedy"];
            t.Year = 1965;
            t.TmdbVoteAverage = 8.9m;
            t.TmdbVoteCount = 500;
        });
        // A comedy that is also a documentary — and documentaries are excluded. Still excluded on the rail.
        var docComedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["comedy", "documentary"];
            t.Year = 2001;
            t.TmdbVoteAverage = 8.9m;
            t.TmdbVoteCount = 500;
        });
        var mine = new[] { comedy, weakComedy, oldComedy, docComedy };

        await SeedFilterAsync(accountId, FilterKind.IncludeGenre, "horror");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "documentary");
        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, ContentFilterRules.FormatRating(7.0m));
        await SeedFilterAsync(accountId, FilterKind.MinYear, ContentFilterRules.FormatYear(1990));

        // Tonight's picks: nothing here is horror, so the allow-list keeps all four out.
        Assert.Empty(await EligibleAsync(accountId, mine));

        // The comedy rail: the allow-list steps aside for the rail's own genre; the exclusion, the
        // crowd floor and the year window do not.
        var rail = await RailAsync(accountId, mine, "comedy");
        Assert.Equal([comedy], rail);
    }

    [Fact]
    public async Task Tv_titles_are_excluded_by_the_keywords_that_stand_in_for_a_genre_their_taxonomy_lacks()
    {
        if (!fixture.Available)
        {
            return;
        }

        // H-7: TMDB's TV taxonomy has no romance, horror or history genre, so those shows carry the
        // concept as a keyword — and "no romance / horror / history" used to remove 3, 14 and 2 shows.
        var accountId = await SeedAccountAsync();
        var romanceShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["teenage romance"]; });
        var romanticShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["romantic comedy"]; });
        var horrorShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["supernatural horror"]; });
        var slasherShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["slasher"]; });
        var periodShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["british history"]; });
        var loveTriangleShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["love triangle"]; });
        var historicalShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["historical fiction"]; });
        // A Trakt-synced show that does carry the genre slug is excluded by the genre itself.
        var taggedShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama", "romance"]; });

        // Near misses that must stay: a word that merely CONTAINS the stem, a label that names
        // history without being historical, and a FILM whose keyword is romance but whose TMDB genres
        // (a taxonomy that does have romance) say thriller.
        var bromanceShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["bromance"]; });
        var datingHistoryShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["dating history"]; });
        var plainShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["detective"]; });
        // Labels that read as on-genre but fail the film calibration (GenreStandIns.Patterns): 19% of
        // "victorian england" films and 18% of "alternate history" ones are History, and a "shark"
        // show is a nature show as often as a horror one.
        var periodDramaShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["victorian england", "alternate history"]; });
        var nineteenthCenturyShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["19th century"]; });
        var sharkShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["shark", "scuba diving"]; });
        var thrillerFilm = await SeedTitleAsync(t => { t.Genres = ["thriller"]; t.Keywords = ["romance", "slasher"]; });
        var romanceFilm = await SeedTitleAsync(t => t.Genres = ["romance"]);

        var mine = new[]
        {
            romanceShow, romanticShow, horrorShow, slasherShow, periodShow, historicalShow, taggedShow,
            bromanceShow, datingHistoryShow, plainShow, thrillerFilm, romanceFilm, loveTriangleShow,
            periodDramaShow, sharkShow, nineteenthCenturyShow,
        };

        // No rule configured: every one is eligible, so the arms are inert without a row.
        Assert.Equal(mine.ToHashSet(), (await EligibleAsync(accountId, mine)).ToHashSet());

        // One slug at a time first: each arm answers only for its own slug.
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "horror");
        var horrorOnly = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.DoesNotContain(horrorShow, horrorOnly);
        Assert.DoesNotContain(slasherShow, horrorOnly);
        Assert.Contains(romanceShow, horrorOnly);
        Assert.Contains(periodShow, horrorOnly);
        Assert.Contains(thrillerFilm, horrorOnly);

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "romance");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "history");

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Equal(
            new HashSet<Guid> { bromanceShow, datingHistoryShow, plainShow, thrillerFilm, periodDramaShow, sharkShow },
            survivors);
    }

    [Fact]
    public async Task Genres_neither_taxonomy_has_and_trakt_alias_genres_are_excluded_on_both_media()
    {
        if (!fixture.Available)
        {
            return;
        }

        // anime and musical have no TMDB id on either medium — two of the founder's twelve conditions
        // matched 0 and 21 films. "children" is Trakt's own slug for what TMDB TV calls Kids, which
        // ingest folds into family, and "suspense" is Trakt's slug for thriller.
        var accountId = await SeedAccountAsync();
        var animeFilm = await SeedTitleAsync(t => { t.Genres = ["action"]; t.Keywords = ["anime"]; });
        var animeShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["anime", "mecha"]; });
        var liveActionAdaptation = await SeedTitleAsync(t => { t.Genres = ["action"]; t.Keywords = ["based on anime"]; });
        var jukeboxFilm = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Keywords = ["jukebox musical"]; });
        var instrumentsFilm = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Keywords = ["musical instruments"]; });
        var childrenShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["children"]; });
        var suspenseShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["suspense"]; });
        var mine = new[] { animeFilm, animeShow, liveActionAdaptation, jukeboxFilm, instrumentsFilm, childrenShow, suspenseShow };

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "anime");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "musical");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "family");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "thriller");   // Trakt's "suspense" is thriller

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.Equal(new HashSet<Guid> { liveActionAdaptation, instrumentsFilm }, survivors);
    }

    /// <summary>Eligible ids among <paramref name="mine"/> — the discovery path (Tonight's picks).</summary>
    private async Task<IReadOnlyList<Guid>> EligibleAsync(
        Guid accountId, IReadOnlyCollection<Guid> mine, bool ignoreIncludeGenreFilters = false)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new EligibilityQueryBuilder(db)
            .EligibleTitles(accountId, ignoreIncludeGenreFilters)
            .Where(t => mine.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Eligible ids among <paramref name="mine"/> through the genre-rail path — the retriever call
    /// FeedPaginator makes for <c>FeedRowContext.Genre</c>. The lane is pinned to the seeded ids so a
    /// shared catalogue cannot crowd them out of the popularity window.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RailAsync(Guid accountId, IReadOnlyCollection<Guid> mine, string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var retriever = new CandidateRetriever(db, new EligibilityQueryBuilder(db));
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var hits = await retriever.RetrieveAsync(
            accountId,
            taste,
            lane: q => q.Where(t => mine.Contains(t.Id) && t.Genres.Contains(genre)),
            popCount: 50,
            annCount: 0,
            maturityCeiling: null,
            CancellationToken.None,
            ignoreGenreFilters: true);
        return hits.Select(t => t.Id).ToList();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"filters-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(Action<Title> shape)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"filter-fixture {Guid.NewGuid():N}",
            // Eligibility defaults to English-only when no language filter is configured.
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            // Deliberately at the very bottom of the popularity order: these fixtures must never
            // displace another integration test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape(title);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
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
}
