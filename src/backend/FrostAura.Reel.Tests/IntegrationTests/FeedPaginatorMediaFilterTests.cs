using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Media-aware pagination on real Postgres: a Hero "load more" request carrying MediaType="movie"
/// must page ONLY movies through the whole scoring path, even when the eligible catalog is a mix of
/// movies and shows — the server-side fix that lets a "Movies" filter fill Tonight's Picks with
/// movies instead of the client filtering a mostly-shows page down to nothing. Skips when
/// REEL_TEST_CONNECTION_STRING is unset (no DB in the run).
/// </summary>
[Collection("postgres")]
public class FeedPaginatorMediaFilterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Hero_more_request_with_movie_media_returns_only_movies()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // A healthy eligible pool of BOTH media. More than the deepen threshold (40) of movies so the
        // assertion never leans on the TMDB deepen — the media filter is the only thing keeping shows
        // out. Shows are MORE popular than the movies here, so without the server-side gate the
        // popularity head would be show-dominated (the exact bug: hero picks skew to shows, Movies
        // view dead-ends). Popularity is kept low (< the shared-catalog popularity head other
        // integration tests assert against) so this test doesn't perturb their top-N ordering.
        for (var i = 0; i < 45; i++)
        {
            await SeedTitleAsync(MediaType.Movie, popularity: 20m - (i * 0.1m));
        }

        for (var i = 0; i < 20; i++)
        {
            await SeedTitleAsync(MediaType.Show, popularity: 35m - (i * 0.1m));
        }

        await SeedActiveArtifactAsync(accountId);

        var movieItems = await MoreAsync(accountId, media: "movie");
        Assert.NotEmpty(movieItems);
        Assert.All(movieItems, i => Assert.Equal(MediaType.Movie, i.Title.MediaType));

        // And the symmetric case: a "show" request pages only shows.
        var showItems = await MoreAsync(accountId, media: "show");
        Assert.NotEmpty(showItems);
        Assert.All(showItems, i => Assert.Equal(MediaType.Show, i.Title.MediaType));
    }

    /// <summary>
    /// HREV1-RET-08: a genre rail serves the genre as its supply counts it. TMDB's TV taxonomy has no
    /// Thriller, so TV thrillers are tagged by keyword ("psychopath", "cat and mouse") or carry
    /// Trakt's alias genre ("suspense"); the rail filtered on the tag alone and came back empty on
    /// Shows while <see cref="RailSupply.ForGenreRailAsync"/> counted 44 clearing the bar. The rail
    /// must reach the stand-in and alias shows, and must stay exactly as strict as the supply
    /// predicate everywhere else: a FILM with only a stand-in keyword is not a thriller (TMDB tags
    /// films with the genre itself), and a show with neither is not one either.
    /// </summary>
    [Fact]
    public async Task Thriller_rail_on_shows_serves_stand_in_and_alias_shows_the_supply_counts()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var standInShows = new List<Guid>
        {
            await SeedTitleAsync(MediaType.Show, 21m, genres: ["drama"], keywords: ["psychopath"]),
            await SeedTitleAsync(MediaType.Show, 20.5m, genres: ["crime"], keywords: ["cat and mouse", "detective"]),
            await SeedTitleAsync(MediaType.Show, 20m, genres: ["drama"], keywords: ["psychological thriller"]),
        };
        var aliasShow = await SeedTitleAsync(MediaType.Show, 19.5m, genres: ["suspense"], keywords: []);
        var plainShow = await SeedTitleAsync(MediaType.Show, 22m, genres: ["drama"], keywords: ["family"]);
        var standInFilm = await SeedTitleAsync(MediaType.Movie, 22m, genres: ["drama"], keywords: ["psychopath"]);
        var taggedFilm = await SeedTitleAsync(MediaType.Movie, 21m, genres: ["thriller"], keywords: []);
        await SeedActiveArtifactAsync(accountId);

        var shows = await MoreAsync(accountId, media: "show", context: FeedRowContext.Genre, genre: "thriller", limit: 30);
        var showIds = shows.Select(i => i.Title.Id).ToHashSet();
        Assert.All(standInShows, id => Assert.Contains(id, showIds));
        Assert.Contains(aliasShow, showIds);
        Assert.DoesNotContain(plainShow, showIds);
        Assert.All(shows, i => Assert.Equal(MediaType.Show, i.Title.MediaType));

        // Every card is one the supply's own predicate counts — the rail and "N clear your bar"
        // read the same genre.
        var supplyPredicate = FilterBreaches.ExcludedGenre("thriller").Compile();
        Assert.All(shows, i => Assert.True(supplyPredicate(i.Title), $"{i.Title.Name} is not a thriller by the supply's predicate"));

        // Both media: the tagged film is a thriller; the film with only a TV stand-in keyword is not.
        var any = await MoreAsync(accountId, media: null, context: FeedRowContext.Genre, genre: "thriller", limit: 30);
        var anyIds = any.Select(i => i.Title.Id).ToHashSet();
        Assert.Contains(taggedFilm, anyIds);
        Assert.DoesNotContain(standInFilm, anyIds);
        Assert.DoesNotContain(plainShow, anyIds);
        Assert.All(any, i => Assert.True(supplyPredicate(i.Title), $"{i.Title.Name} is not a thriller by the supply's predicate"));
    }

    private async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(Guid accountId, string media)
        => await MoreAsync(accountId, media, FeedRowContext.Hero, genre: null, limit: 12);

    private async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(
        Guid accountId, string? media, FeedRowContext context, string? genre, int limit)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var retriever = new CandidateRetriever(db, eligibility);
        var engine = new FastTreeModelEngine();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var residual = new OnlineResidualModel(db, featureBuilder, engine, cache, NullLogger<OnlineResidualModel>.Instance);
        var tmdb = new EmptyTmdbClient();
        var candidateGenerator = new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance);
        var agent = new UnavailableSearchAgent();
        var paginator = new FeedPaginator(
            db, featureBuilder, retriever, engine, residual,
            new RatingCalibrationService(db, cache), new DislikeSignal(db, cache),
            candidateGenerator, tmdb, agent,
            new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new FrostAura.Reel.Application.CrossReference.RerankBudget(
                db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            cache, NullLogger<FeedPaginator>.Instance);

        var req = new MoreFeedRequest(context, AnchorTitleId: null, Genre: genre, ExcludeTitleIds: [], Limit: limit, MediaType: media);
        return await paginator.MoreAsync(account, req, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"paginator-{Guid.NewGuid():N}",
            // Paid so the paginator's row surface is unlocked (the endpoint gates free tier, but MoreAsync
            // itself needs a real, active artifact; the tier gate lives in the endpoint above it).
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(
        MediaType media, decimal popularity, string[]? genres = null, string[]? keywords = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{media} {Guid.NewGuid():N}",
            Language = "en", // eligibility defaults to English-only when no language filter is configured
            PosterPath = "/p.jpg",
            Genres = genres ?? ["drama"],
            Keywords = keywords ?? [],
            TmdbPopularity = popularity,
            ReleasedAt = media == MediaType.Movie ? DateTime.UtcNow.AddYears(-1) : null,
            FirstAiredAt = media == MediaType.Show ? DateTime.UtcNow.AddYears(-1) : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>Trains a tiny FastTree on the real feature schema and stores it as the account's Active
    /// artifact — the paginator returns nothing without one, so this is the minimum to exercise MoreAsync.</summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(17);
        var names = FeatureSchema.AllNames();
        var rows = new float[240][];
        var labels = new float[240];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (5f * v[0]), 1f, 10f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            // Stamped with the running schema version — an artifact recorded against any other
            // version is deliberately unreadable as Active (ReelDbContext), so "[]" would make
            // this fixture invisible to the very code under test.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A TMDB port that yields nothing — the media filter, not a deepen, must carry the test,
    /// and this keeps the run offline (no network to TMDB).</summary>
    private sealed class EmptyTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
