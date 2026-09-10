using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What the build actually asks TMDB for, on real Postgres.
///
/// <para>
/// The thing under test is not "does discovery return items" — it always did. It is whether the
/// items it asks for are ones this account can be SHOWN. Measured on the founder's account
/// (2026-09-09): ~421 of 5,319 catalogue titles are eligible under his own rules, and the build's
/// discover sent no language, no year floor, no crowd-rating floor and no genre exclusions — so
/// ~92% of every page was rejected after the fact, on every build, for ever. Meanwhile the eight
/// genres he has banned included two of his highest-COUNT ones, so up to half the discover budget
/// was spent fetching titles the eligibility layer is guaranteed to reject.
/// </para>
///
/// <para>
/// These tests therefore assert on the CALLS as much as the pool: which filters reach TMDB, which
/// pages, which media, which seeds, and how many calls it all costs.
/// </para>
/// </summary>
[Collection("postgres")]
public class CandidateGeneratorPoolTests(PostgresFixture fixture)
{
    private static readonly DateTime BuildAt = new(2026, 9, 9, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_banned_genre_never_costs_a_discover_arm_however_much_the_user_rated_it()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        // Comedy is rated the most AND banned — exactly the shape that made this expensive.
        await RateGenreAsync(accountId, "comedy", 9, count: 12);
        await RateGenreAsync(accountId, "thriller", 8, count: 4);
        await RateGenreAsync(accountId, "drama", 8, count: 3);
        await ExcludeGenreAsync(accountId, "comedy");

        var tmdb = await RefreshAsync(accountId);

        var comedy = TmdbGenres.SlugToIds["comedy"];
        Assert.DoesNotContain(tmdb.DiscoveredGenreIds, id => id == comedy.Movie);
        Assert.DoesNotContain(tmdb.DiscoveredGenreIds, id => id == comedy.Tv);
        // The budget it used to burn goes to a genre the user can actually be shown.
        var thriller = TmdbGenres.SlugToIds["thriller"];
        Assert.Contains(tmdb.DiscoveredGenreIds, id => id == thriller.Movie);
        // And the ban is now also stated to TMDB, so it shapes every other arm's page too — a
        // negation applied only after the fetch can reorder a pool but never change what is in it.
        Assert.All(
            tmdb.Discovers.Where(d => d.IsMovie),
            d => Assert.Contains(comedy.Movie!.Value, d.ExcludeGenreIds ?? []));
    }

    [Fact]
    public async Task With_no_exclusions_the_top_genre_is_still_discovered()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The mirror of the test above: the exclusion is what removes the arm, not the new query.
        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "comedy", 9, count: 12);

        var tmdb = await RefreshAsync(accountId);

        var comedy = TmdbGenres.SlugToIds["comedy"];
        Assert.Contains(tmdb.DiscoveredGenreIds, id => id == comedy.Movie);
    }

    [Fact]
    public async Task Every_content_rule_the_account_has_set_reaches_the_discover_query()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The founder's own stack, minus the genres (covered above).
        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);
        await FilterAsync(accountId, FilterKind.Language, "en");
        await FilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");
        await FilterAsync(accountId, FilterKind.MinYear, "2000");
        await FilterAsync(accountId, FilterKind.MaxYear, "2024");

        var tmdb = await RefreshAsync(accountId);

        Assert.NotEmpty(tmdb.Discovers);
        Assert.All(tmdb.Discovers, d =>
        {
            Assert.Equal("en", d.Options?.OriginalLanguage);
            Assert.Equal(6.0m, d.Options?.MinVoteAverage);
            // The feed's own quality floor, not the concept endpoint's recall-first default of 10.
            Assert.Equal(AccountDiscoveryRules.FeedVoteCountFloor, d.Options?.MinVoteCount);
            // At least the MinYear floor. The fresh-releases arm reaches later still (90 days), and
            // taking the LATER of the two is the point: a year floor must never widen an arm.
            Assert.True(d.ReleasedAfter >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc), d.ReleasedBefore);
            // Background supply must not drain the lane live search shares.
            Assert.Equal(RatePriority.Reconcile, d.Options?.Priority);
        });
    }

    [Fact]
    public async Task An_account_that_allows_every_language_is_not_constrained_to_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);
        await FilterAsync(accountId, FilterKind.Language, "*");

        var tmdb = await RefreshAsync(accountId);

        // "*" and a multi-language allow-list both project to "unconstrained": TMDB takes a single
        // code, so sending one of several would silently discard the user's other languages at the
        // fetch, where no downstream layer could recover them.
        Assert.All(tmdb.Discovers, d => Assert.Null(d.Options?.OriginalLanguage));
    }

    [Fact]
    public async Task With_no_language_rows_at_all_the_english_default_still_reaches_tmdb()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);

        var tmdb = await RefreshAsync(accountId);

        // The eligibility layer defaults an un-configured account to English-only (founder ask
        // 2026-06-30). Discovery has to make the same default, or every build fetches a page it is
        // about to throw away.
        Assert.All(tmdb.Discovers, d => Assert.Equal("en", d.Options?.OriginalLanguage));
    }

    [Fact]
    public async Task The_seeds_are_three_ordered_pools_and_the_saved_list_is_one_of_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var loved = new List<(Guid Id, long TmdbId, short Rating)>();
        short[] ratings = [8, 10, 9, 8, 10, 9, 10, 8];
        foreach (var rating in ratings)
        {
            var (id, tmdbId) = await SeedTitleAsync();
            await RateTitleAsync(accountId, id, rating);
            loved.Add((id, tmdbId, rating));
        }

        // A save is the strongest declared intent the user produces and it seeded nothing at all
        // before this: it is neither loved (unrated) nor eligible (SaveForLater excludes it).
        var (savedId, savedTmdbId) = await SeedTitleAsync();
        await SaveForLaterAsync(accountId, savedId, predicted: 9.1m);

        var tmdb = await RefreshAsync(accountId);

        var seeded = tmdb.RecommendationSeeds.Concat(tmdb.SimilarSeeds).ToList();
        Assert.Contains(savedTmdbId, seeded);
        Assert.All(
            seeded.Where(s => s != savedTmdbId),
            seed => Assert.True(loved.Single(l => l.TmdbId == seed).Rating >= 8));
    }

    [Fact]
    public async Task Both_collaborative_endpoints_are_used_and_the_pairing_rotates_by_day()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Consecutive TMDB ids, so the parity that selects the endpoint is fixed by the fixture
        // rather than by Random.Shared — otherwise a run where all eight seeds happened to share a
        // parity would fail for a reason that has nothing to do with the behaviour under test.
        var accountId = await SeedAccountAsync();
        var seedBase = ConsecutiveTmdbIdBase();
        for (var i = 0; i < 8; i++)
        {
            var (id, _) = await SeedTitleAsync(MediaType.Movie, tmdbId: seedBase + i);
            await RateTitleAsync(accountId, id, 9);
        }

        var monday = await RefreshAsync(accountId, now: BuildAt);
        var tuesday = await RefreshAsync(accountId, now: BuildAt.AddDays(1));

        // `similar` is content-derived and `recommendations` is behavioural — two independent axes
        // for one call per seed. The `similar` endpoint existed on the client and was never called.
        Assert.NotEmpty(monday.SimilarSeeds);
        Assert.NotEmpty(monday.RecommendationSeeds);
        Assert.NotEmpty(tuesday.SimilarSeeds);
        Assert.NotEmpty(tuesday.RecommendationSeeds);

        // Two things move across a day and both are deliberate. The all-time pool rotates, so the
        // day draws a different four out of a top ten a heavy rater's ratings can no longer reorder;
        // and the endpoint pairing walks, so a seed carried over from yesterday is asked the OTHER
        // question today instead of returning the identical list.
        var carried = monday.SimilarSeeds.Intersect(tuesday.RecommendationSeeds)
            .Concat(monday.RecommendationSeeds.Intersect(tuesday.SimilarSeeds))
            .ToList();
        Assert.NotEmpty(carried);
    }

    [Fact]
    public async Task A_top_genre_tmdb_has_no_id_for_falls_back_to_keyword_discovery()
    {
        if (!fixture.Available)
        {
            return;
        }

        // TMDB has no "anime" genre in either id space — and no "musical" or "sports" either. That
        // used to cost the build a whole discover arm and produce nothing but a log line.
        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "anime", 10, count: 20);

        var tmdb = await RefreshAsync(accountId);

        Assert.Contains("anime", tmdb.KeywordSearches);
        Assert.Contains(tmdb.Discovers, d => d.KeywordIds.Contains(RecordingTmdbClient.AnimeKeywordId));
    }

    [Fact]
    public async Task The_director_the_user_keeps_rating_highly_finally_steers_a_query()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);
        var personId = await SeedPersonAsync(tmdbId: 525);
        await AffinityAsync(accountId, personId, CreditRole.Director, affinity: 8.6m, ratedTitles: 9);

        var tmdb = await RefreshAsync(accountId);

        // Nothing in discovery was person-aware, so "because you love this director" had no route
        // into the pool at all — a title could only ever arrive via its genre or its neighbours.
        Assert.Contains(tmdb.Discovers, d => d.Options?.PeopleIds?.Contains(525L) == true);
    }

    [Fact]
    public async Task Discovery_asks_for_the_media_mix_the_user_actually_rates()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Genres TMDB carries in BOTH id spaces, so every arm is a free choice — "thriller" and
        // "romance" exist only for movies, and an arm the taxonomy forces is not evidence of a plan.
        var accountId = await SeedAccountAsync();
        foreach (var genre in new[] { "drama", "crime", "mystery", "western" })
        {
            for (var i = 0; i < 5; i++)
            {
                var (movieId, _) = await SeedTitleAsync(MediaType.Movie, genre);
                await RateTitleAsync(accountId, movieId, 8);
            }

            var (showId, _) = await SeedTitleAsync(MediaType.Show, genre);
            await RateTitleAsync(accountId, showId, 8);
        }

        // Enough consumption to buy a second page, so the plan gets ~8 free slots to express itself.
        var trainedAt = await SeedActiveArtifactAsync(accountId, BuildAt.AddDays(-1));
        await ConsumeAsync(accountId, trainedAt.AddHours(1), count: 60);

        var tmdb = await RefreshAsync(accountId);

        // The live feed on the founder's account came out entirely television under a fixed 1:1
        // intake. A ~5:1 rater should not be fetching half his supply from the medium he rates least
        // — and the minority medium must still never be starved to zero.
        var movieArms = tmdb.Discovers.Count(d => d.IsMovie);
        var tvArms = tmdb.Discovers.Count(d => !d.IsMovie);
        Assert.True(movieArms > tvArms, $"expected a movie-leaning plan, got {movieArms}m/{tvArms}tv");
        Assert.True(tvArms > 0, $"the minority medium must never be starved to zero, got {movieArms}m/{tvArms}tv");
    }

    [Fact]
    public async Task Consumption_moves_the_read_head_off_page_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);

        var shallow = await RefreshAsync(accountId);
        Assert.All(shallow.Discovers, d => Assert.Equal(1, d.Page));

        // Everything above was rated BEFORE the artifact was trained, so it is not consumption.
        var trainedAt = await SeedActiveArtifactAsync(accountId, BuildAt.AddDays(-1));
        await ConsumeAsync(accountId, trainedAt.AddHours(1), count: 40);

        var deep = await RefreshAsync(accountId);

        // Re-fetching page 1 of a popularity-ordered list a user is demonstrably working through is
        // the mechanism behind "the picks get weaker every round".
        Assert.Contains(deep.Discovers, d => d.Page >= 3);
        Assert.All(deep.Discovers, d => Assert.True(d.Page >= 1));
    }

    [Fact]
    public async Task A_heavy_consumer_still_cannot_blow_the_per_build_call_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        foreach (var genre in new[] { "thriller", "drama", "crime", "mystery", "action", "horror" })
        {
            await RateGenreAsync(accountId, genre, 9, count: 6);
        }

        var trainedAt = await SeedActiveArtifactAsync(accountId, BuildAt.AddDays(-1));
        await ConsumeAsync(accountId, trainedAt.AddHours(1), count: 400);

        var tmdb = await RefreshAsync(accountId);

        // Depth is unbounded by the paging formula; the budget is what actually stops a runaway
        // build, and it is a hard ceiling rather than an average.
        Assert.InRange(tmdb.TotalCalls, 1, CandidateGenerator.MaxTmdbCallsPerBuild);
    }

    [Fact]
    public async Task The_refresh_reports_how_many_new_titles_this_account_can_actually_be_shown()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "thriller", 9, count: 6);
        await FilterAsync(accountId, FilterKind.Language, "en");
        await FilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");
        await FilterAsync(accountId, FilterKind.MinYear, "2000");

        var eligibility = new EligibilityQueryBuilder(fixture.CreateContext(new AccountContext()));
        var before = await CountEligibleAsync(accountId);

        // Six titles TMDB is about to return: three the account can be shown, three it cannot —
        // one French, one pre-2000, one under the crowd floor. "Discovered" and even "newly
        // upserted" count all six, which is exactly how a build reports healthy discovery while the
        // servable pool stands still.
        var itemBase = ConsecutiveTmdbIdBase();
        var tmdb = new RecordingTmdbClient
        {
            TrendingMovies =
            [
                Item(itemBase + 1, "eligible one", "en", 7.4m, 2018),
                Item(itemBase + 2, "eligible two", "en", 6.2m, 2021),
                Item(itemBase + 3, "eligible three", "en", 8.1m, 2005),
                Item(itemBase + 4, "wrong language", "fr", 8.5m, 2019),
                Item(itemBase + 5, "too old", "en", 8.5m, 1994),
                Item(itemBase + 6, "under the crowd floor", "en", 4.1m, 2019),
            ],
        };

        var result = await RefreshAsync(accountId, tmdb);

        Assert.Equal(6, result.NewlyUpserted);
        Assert.Equal(3, result.NewlyEligible);
        Assert.Equal(before + 3, await CountEligibleAsync(accountId));
        Assert.InRange(result.TmdbCalls, 1, CandidateGenerator.MaxTmdbCallsPerBuild);
        GC.KeepAlive(eligibility);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<RecordingTmdbClient> RefreshAsync(
        Guid accountId, FeatureVectorBuilder.TasteState? taste = null, DateTime? now = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        taste ??= await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var tmdb = new RecordingTmdbClient();
        var generator = new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance);

        await generator.RefreshPoolAsync(accountId, taste, "ZA", now ?? BuildAt, CancellationToken.None);

        return tmdb;
    }

    private async Task<CandidateGenerator.PoolRefreshResult> RefreshAsync(Guid accountId, RecordingTmdbClient tmdb)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var generator = new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance);
        return await generator.RefreshPoolAsync(accountId, taste, "ZA", BuildAt, CancellationToken.None);
    }

    private async Task<int> CountEligibleAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return new EligibilityQueryBuilder(db).EligibleTitles(accountId).Count();
    }

    /// <summary>
    /// An unused, EVEN base for a run of consecutive TMDB ids. Consecutive so a test can control the
    /// id parity that picks a seed's endpoint; unused because the whole collection shares one
    /// database, so a hand-picked constant like 9001 is one other fixture away from being an
    /// already-existing title and turning an upsert assertion into a silent zero.
    /// </summary>
    private static long ConsecutiveTmdbIdBase() => Random.Shared.NextInt64(10_000_000, 900_000_000) * 10;

    private static TmdbListItem Item(long id, string name, string language, decimal voteAverage, int year) =>
        new(id, true, name, "/p.jpg", "/b.jpg", "overview", 12.5m, voteAverage, 900,
            new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc), [53], language);

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"pool-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private Task<(Guid Id, long TmdbId)> SeedTitleAsync(params string[] genres) =>
        SeedTitleAsync(MediaType.Movie, genres);

    private Task<(Guid Id, long TmdbId)> SeedTitleAsync(MediaType mediaType, params string[] genres) =>
        SeedTitleAsync(mediaType, tmdbId: null, genres);

    private async Task<(Guid Id, long TmdbId)> SeedTitleAsync(MediaType mediaType, long? tmdbId, params string[] genres)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            TmdbId = tmdbId ?? Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"title {Guid.NewGuid():N}",
            Genres = genres,
            Year = 2015,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return (title.Id, title.TmdbId!.Value);
    }

    private async Task<Guid> SeedPersonAsync(long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = tmdbId, Name = $"person {tmdbId}" };
        db.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task AffinityAsync(Guid accountId, Guid personId, CreditRole role, decimal affinity, int ratedTitles)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.Add(new PersonAffinity
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PersonId = personId,
            Role = role,
            Affinity = affinity,
            RatedTitleCount = ratedTitles,
            ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task RateGenreAsync(Guid accountId, string genre, short rating, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (id, _) = await SeedTitleAsync(genre);
            await RateTitleAsync(accountId, id, rating);
        }
    }

    private async Task RateTitleAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ratedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(400, 800));
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

    /// <summary>Reactions and ratings created AFTER the artifact was trained — i.e. real consumption.</summary>
    private async Task ConsumeAsync(Guid accountId, DateTime at, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (id, _) = await SeedTitleAsync("thriller");
            await using var db = fixture.CreateContext(new AccountContext());
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = id,
                Kind = ReactionKind.NotInterested,
                CreatedAt = at,
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<DateTime> SeedActiveArtifactAsync(Guid accountId, DateTime trainedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            // The Active query filter compares a computed column projected out of this document
            // against the running FeatureSchema.Version. An artifact without it reads back as "no
            // model yet", and the consumption counter would silently be zero.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = Array.Empty<string>() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = trainedAt,
        });
        await db.SaveChangesAsync();
        return trainedAt;
    }

    private async Task SaveForLaterAsync(Guid accountId, Guid titleId, decimal predicted)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = ReactionKind.SaveForLater,
            CreatedAt = DateTime.UtcNow,
        });
        db.ManagedListItems.Add(new ManagedListItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            AddedAt = DateTime.UtcNow,
        });
        db.TitleScores.Add(new TitleScore
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ModelArtifactId = Guid.NewGuid(),
            PredictedRating = predicted,
            ScoredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private Task ExcludeGenreAsync(Guid accountId, string genre) =>
        FilterAsync(accountId, FilterKind.ExcludeGenre, genre);

    private async Task FilterAsync(Guid accountId, FilterKind kind, string value)
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

    /// <summary>
    /// Records every call and, by default, answers nothing: the assertions are about what the build
    /// ASKED FOR, which is where the waste was. Seed <see cref="TrendingMovies"/> to also assert on
    /// what the pool did with the answer.
    /// </summary>
    private sealed class RecordingTmdbClient : ITmdbClient
    {
        /// <summary>The id this fake resolves every keyword name to.</summary>
        public const int AnimeKeywordId = 210024;

        public List<int> DiscoveredGenreIds { get; } = [];

        public List<DiscoverCall> Discovers { get; } = [];

        public List<long> RecommendationSeeds { get; } = [];

        public List<long> SimilarSeeds { get; } = [];

        public List<string> KeywordSearches { get; } = [];

        public IReadOnlyList<TmdbListItem> TrendingMovies { get; init; } = [];

        public int TotalCalls { get; private set; }

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default)
        {
            TotalCalls++;
            if (genreId is { } id)
            {
                DiscoveredGenreIds.Add(id);
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default)
        {
            TotalCalls++;
            RecommendationSeeds.Add(tmdbId);
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default)
        {
            TotalCalls++;
            SimilarSeeds.Add(tmdbId);
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(movies ? TrendingMovies : []);
        }

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default)
        {
            TotalCalls++;
            KeywordSearches.Add(query);
            return Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(AnimeKeywordId, query)]);
        }

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default)
        {
            TotalCalls++;
            Discovers.Add(new DiscoverCall(movies, genreIds, keywordIds, page, excludeGenreIds, releasedAfter, releasedBefore, options));
            DiscoveredGenreIds.AddRange(genreIds);
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }

    private sealed record DiscoverCall(
        bool IsMovie,
        IReadOnlyList<int> GenreIds,
        IReadOnlyList<int> KeywordIds,
        int Page,
        IReadOnlyList<int>? ExcludeGenreIds,
        DateTime? ReleasedAfter,
        DateTime? ReleasedBefore,
        TmdbDiscoverOptions? Options);
}
