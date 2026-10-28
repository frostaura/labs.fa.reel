using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Typeahead;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
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

    /// <summary>
    /// HREV1-RET-11: "bluey" listed five Bluey items to an account excluding animation while Bluey itself
    /// (TMDB genres Animation, Comedy, Family, Kids) was hidden — TMDB tags the spin-offs sparsely (Kids
    /// only, Family only, nothing at all), so their rows passed. A row TMDB left untagged, or one that
    /// extends a genre-hidden row's name with a subset of its genres, is now hidden and counted; a row
    /// with a genre its namesake lacks (Bluey (2015), a drama) is a different title and is judged on
    /// its own. Without an ExcludeGenre rule nothing is inferred. A TV row whose overview carries a
    /// romance stand-in is hidden under "no romance" (HREV1-RET-09).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_global_leg_hides_sparsely_tagged_spin_offs_of_a_title_an_excluded_genre_hides(bool excludeGenres)
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        if (excludeGenres)
        {
            await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "animation");
            await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "romance");
        }

        var tmdb = new SearchOnlyTmdbClient
        {
            ShowResults =
            [
                Row(82728, false, token, [16, 35, 10751, 10762], popularity: 90m),
                Row(500001, false, $"{token} Book Reads", [10762], popularity: 80m),
                Row(500003, false, $"{token}: Turtleboy (Auslan)", [], popularity: 60m),
                Row(500004, false, token, [18], popularity: 50m),
                Row(500005, false, $"{token} Nights", [18], popularity: 40m, overview: "A widow and her teenage crush meet again."),
            ],
            MovieResults =
            [
                Row(500002, true, $"{token}'s Big Play", [10751], popularity: 70m),
                Row(500006, true, $"{token} Heist", [80], popularity: 30m),
            ],
        };

        await using var db = fixture.CreateContext(new AccountContext());
        var global = await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).GlobalAsync(accountId, tmdb, token, default);

        if (excludeGenres)
        {
            // Bluey (2015) carries drama, which Bluey lacks; the heist film carries crime. Hidden: Bluey,
            // its three spin-offs and the romance-overview show.
            Assert.Equal([500004L, 500006L], global.Rows.Select(r => r.TmdbId));
            Assert.Equal(5, global.HiddenByConditions);
        }
        else
        {
            Assert.Equal([82728L, 500001L, 500002L, 500003L, 500004L, 500005L], global.Rows.Select(r => r.TmdbId));
            Assert.Equal(0, global.HiddenByConditions);
        }
    }

    /// <summary>
    /// HREV1-RET-11 (refuted round 2): typing past the namesake ("bluey book", "bluey's big play") left
    /// Bluey itself out of the word-start-filtered rows, so nothing was hidden by genre and the sparse
    /// spin-offs were listed again. The namesake is judged from every row TMDB returned, shown or not;
    /// a row with a genre the namesake lacks is still judged on its own.
    /// </summary>
    [Theory]
    [InlineData(" book", new long[0], 1)]
    [InlineData("'s big play", new long[0], 1)]
    [InlineData(": turtleboy", new long[0], 1)]
    [InlineData(" heist", new[] { 500006L }, 0)]
    public async Task Typing_past_a_genre_hidden_namesake_still_hides_its_sparse_spin_offs(string suffix, long[] shown, int hidden)
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "animation");
        var tmdb = new SearchOnlyTmdbClient
        {
            ShowResults =
            [
                Row(82728, false, token, [16, 35, 10751, 10762], popularity: 90m),
                Row(500001, false, $"{token} Book Reads", [10762], popularity: 80m),
                Row(500003, false, $"{token}: Turtleboy (Auslan)", [], popularity: 60m),
            ],
            MovieResults =
            [
                Row(500002, true, $"{token}'s Big Play", [10751], popularity: 70m),
                Row(500006, true, $"{token} Heist", [80], popularity: 30m),
            ],
        };

        await using var db = fixture.CreateContext(new AccountContext());
        var global = await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).GlobalAsync(accountId, tmdb, token + suffix, default);

        Assert.Equal(shown, global.Rows.Select(r => r.TmdbId));
        Assert.Equal(hidden, global.HiddenByConditions);
    }

    [Fact]
    public void A_row_extends_its_namesake_only_at_a_word_boundary_and_with_a_subset_of_its_genres()
    {
        var rules = FilterRuleSet.FromRows(
        [
            new ContentFilter { AccountId = Guid.Empty, Kind = FilterKind.ExcludeGenre, Value = "animation" },
        ]);
        var bluey = Row(1, false, "Bluey", [16, 35, 10751, 10762], popularity: 1m);
        IReadOnlyList<TmdbListItem> hidden = [bluey];

        Assert.True(TypeaheadSearch.UnprovenUnderGenreRules(rules, Row(2, false, "Bluey: Turtleboy", [10762], 1m), hidden));
        Assert.True(TypeaheadSearch.UnprovenUnderGenreRules(rules, Row(3, true, "Blüey’s Big Play", [10751], 1m), hidden));   // accents folded
        Assert.False(TypeaheadSearch.UnprovenUnderGenreRules(rules, Row(4, false, "Blueyonder", [10762], 1m), hidden));      // not a word boundary
        Assert.False(TypeaheadSearch.UnprovenUnderGenreRules(rules, Row(5, false, "Bluey Nights", [10762, 18], 1m), hidden)); // drama is new
        Assert.False(TypeaheadSearch.UnprovenUnderGenreRules(rules, bluey, hidden));                                       // never its own sibling
        Assert.True(TypeaheadSearch.UnprovenUnderGenreRules(rules, Row(6, true, "Anything", [], 1m), []));                  // untagged fails closed
        Assert.False(TypeaheadSearch.UnprovenUnderGenreRules(FilterRuleSet.FromRows([]), Row(7, true, "Anything", [], 1m), []));
    }

    private static TmdbListItem Row(long id, bool isMovie, string name, int[] genreIds, decimal popularity, string overview = "An overview.") =>
        new(id, isMovie, name, "/p.jpg", null, overview, popularity, 7m, 100,
            new DateTime(2015, 5, 14, 0, 0, 0, DateTimeKind.Utc), genreIds, "en");

    /// <summary>
    /// R1-07, the exclusion matrix's maturity cell on both legs. Under a PG-13 ceiling an R-rated
    /// catalogue title appears in neither leg and is counted once (by the local leg); a TMDB-only row
    /// carries no certification, so under a ceiling that does not admit unrated titles it is held out
    /// and counted by the global leg — exactly as <see cref="EligibilityQueryBuilder.PassesMaturity"/>
    /// treats an unrated catalogue title. Under an adult ceiling the unrated row is shown and only
    /// the NC-17 title is hidden; with no ceiling nothing is.
    /// </summary>
    [Theory]
    [InlineData("PG-13", false, 1, 1)]
    [InlineData("R", true, 1, 0)]
    [InlineData(null, true, 0, 0)]
    public async Task The_maturity_ceiling_hides_and_counts_in_both_legs(string? ceiling, bool unratedRowShown, int localHidden, int globalHidden)
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync(ceiling);
        var allowedTmdbId = Random.Shared.NextInt64(1, long.MaxValue);
        var overTmdbId = Random.Shared.NextInt64(1, long.MaxValue);
        var allowed = await SeedTitleAsync($"{token} Family", votes: 500, tmdbId: allowedTmdbId, certification: "PG");
        var over = await SeedTitleAsync($"{token} Adults Only", votes: 900, tmdbId: overTmdbId, certification: ceiling == "R" ? "NC-17" : "R");

        var local = await LocalAsync(accountId, token);
        Assert.Equal(ceiling is null ? [over, allowed] : [allowed], local.Titles.Select(t => t.TitleId));
        Assert.Equal(localHidden, local.HiddenByConditions);

        var tmdb = new SearchOnlyTmdbClient
        {
            MovieResults =
            [
                Item(overTmdbId, $"{token} Adults Only", "en", popularity: 90m),
                Item(allowedTmdbId, $"{token} Family", "en", popularity: 80m),
                Item(91, $"{token} Unrated Abroad", "en", popularity: 70m),
            ],
        };
        await using var db = fixture.CreateContext(new AccountContext());
        var global = await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).GlobalAsync(accountId, tmdb, token, default);

        var expected = new List<long>();
        if (ceiling is null)
        {
            expected.Add(overTmdbId);
        }

        expected.Add(allowedTmdbId);
        if (unratedRowShown)
        {
            expected.Add(91);
        }

        Assert.Equal(expected, global.Rows.Select(r => r.TmdbId));
        Assert.Equal(globalHidden, global.HiddenByConditions);
    }

    /// <summary>
    /// The local leg's maturity count covers every strong match the conditions let through, not only
    /// the shortlist it ranked: an over-ceiling title past the shortlist is still hidden, so it must
    /// still be counted — the global leg leaves catalogue titles to this count.
    /// </summary>
    [Fact]
    public async Task An_over_ceiling_match_beyond_the_shortlist_is_still_counted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = Token();
        var accountId = await SeedAccountAsync("PG-13");
        for (var i = 0; i < (TypeaheadSearch.TitleLimit * 3) + 2; i++)
        {
            await SeedTitleAsync($"{token} Saga {i}", votes: 10_000 - i, certification: "PG");
        }

        await SeedTitleAsync($"{token} Night", votes: 1, certification: "R");

        var local = await LocalAsync(accountId, token);

        Assert.Equal(TypeaheadSearch.TitleLimit, local.Titles.Count);
        Assert.Equal(1, local.HiddenByConditions);
    }

    private static string Token() => "zq" + Guid.NewGuid().ToString("N")[..8];

    private static TmdbListItem Item(long id, string name, string language, decimal popularity) =>
        new(id, true, name, "/p.jpg", null, "An overview.", popularity, 7m, 100,
            new DateTime(2015, 5, 14, 0, 0, 0, DateTimeKind.Utc), [18], language);

    [Fact]
    public async Task A_title_the_account_has_a_footprint_on_carries_it_and_is_never_scored()
    {
        if (!fixture.Available)
        {
            return;
        }

        // HREV5-RET-05: "the crown" listed The Chosen — rated 7, partly watched — with a "for you"
        // prediction and no mark at all, because only a FULL watch counted. Every footprint kind is
        // carried on the row, the user's own rating is shown, and none of them is offered for scoring.
        var token = Token();
        var accountId = await SeedAccountAsync();
        var ratedPartly = await SeedTitleAsync($"{token} Chosen", votes: 900);
        var ratedOnly = await SeedTitleAsync($"{token} Rated", votes: 800);
        var watching = await SeedTitleAsync($"{token} Watching", votes: 700);
        var seen = await SeedTitleAsync($"{token} Seen", votes: 600);
        var episodeRated = await SeedTitleAsync($"{token} Episode", votes: 500);
        var saved = await SeedTitleAsync($"{token} Saved", votes: 400);
        var untouched = await SeedTitleAsync($"{token} Untouched", votes: 300);
        var revokedSave = await SeedTitleAsync($"{token} Revoked", votes: 200);
        var someoneElses = await SeedTitleAsync($"{token} Elsewhere", votes: 100);
        var other = await SeedAccountAsync();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var now = DateTime.UtcNow;
            db.AddRange(
                Rating(accountId, ratedPartly, RatingSubjectType.Show, 7),
                Watch(accountId, ratedPartly, full: false),
                Rating(accountId, ratedOnly, RatingSubjectType.Movie, 4),
                Watch(accountId, watching, full: false),
                Watch(accountId, seen, full: true),
                Rating(accountId, episodeRated, RatingSubjectType.Episode, 9),
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = saved, Kind = ReactionKind.SaveForLater, CreatedAt = now },
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = revokedSave, Kind = ReactionKind.SaveForLater, CreatedAt = now, RevokedAt = now },
                Rating(other, someoneElses, RatingSubjectType.Movie, 10),
                Watch(other, someoneElses, full: true));
            await db.SaveChangesAsync();
        }

        var rows = (await LocalAsync(accountId, token)).Titles.ToDictionary(t => t.TitleId);

        Assert.Equal((short)7, rows[ratedPartly].UserRating);
        Assert.True(rows[ratedPartly].InProgress);
        Assert.False(rows[ratedPartly].IsFullyWatched);
        Assert.Equal((short)4, rows[ratedOnly].UserRating);
        Assert.False(rows[ratedOnly].InProgress);
        Assert.True(rows[watching].InProgress);
        Assert.Null(rows[watching].UserRating);
        Assert.True(rows[seen].IsFullyWatched);
        Assert.False(rows[seen].InProgress);
        Assert.Null(rows[episodeRated].UserRating);
        foreach (var id in new[] { ratedPartly, ratedOnly, watching, seen, episodeRated, saved })
        {
            Assert.True(rows[id].Footprint, $"{id} carries a footprint");
        }

        foreach (var id in new[] { untouched, revokedSave, someoneElses })
        {
            Assert.False(rows[id].Footprint, $"{id} carries none");
            Assert.Null(rows[id].UserRating);
            Assert.False(rows[id].InProgress);
            Assert.False(rows[id].IsFullyWatched);
        }

        await using var read = fixture.CreateContext(new AccountContext());
        var asked = new[] { ratedPartly, untouched, ratedOnly, watching, seen, episodeRated, saved, revokedSave, someoneElses };
        var scorable = await new TypeaheadSearch(read, new EligibilityQueryBuilder(read)).ScorableAsync(accountId, asked, default);
        Assert.Equal([untouched, revokedSave, someoneElses], scorable);
    }

    private static UserRating Rating(Guid accountId, Guid titleId, RatingSubjectType subject, short rating) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        SubjectType = subject,
        SeasonNumber = subject is RatingSubjectType.Season or RatingSubjectType.Episode ? 1 : 0,
        EpisodeNumber = subject == RatingSubjectType.Episode ? 1 : 0,
        Rating = rating,
        RatedAt = DateTime.UtcNow,
        FirstRatedAt = DateTime.UtcNow,
        SyncedAt = DateTime.UtcNow,
    };

    private static WatchedTitle Watch(Guid accountId, Guid titleId, bool full) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        Plays = 1,
        LastWatchedAt = DateTime.UtcNow,
        IsFullyWatched = full,
        FirstSyncedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<TypeaheadLocal> LocalAsync(Guid accountId, string term)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new TypeaheadSearch(db, new EligibilityQueryBuilder(db)).LocalAsync(accountId, term, default);
    }

    private async Task<Guid> SeedAccountAsync(string? maturityCeiling = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Settings = new AccountSettings { MaturityCeiling = maturityCeiling },
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

    private async Task<Guid> SeedTitleAsync(string name, int votes, string[]? genres = null, long? tmdbId = null, string? certification = null)
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
            Certification = certification,
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
