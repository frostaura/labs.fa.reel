using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// On-demand title hydration on real Postgres: navigating to a title Reel has never seen fetches,
/// upserts and hydrates it exactly once even under concurrent opens (single-flight), a genuine
/// TMDB 404 still resolves to null so the endpoint can 404 honestly, and a freshly-hydrated title
/// scores against the Active artifact so the detail response carries a predictedRating. Skips
/// when REEL_TEST_CONNECTION_STRING is unset, matching the other integration tests.
/// </summary>
[Collection("postgres")]
public class OnDemandTitleHydrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_concurrent_opens_of_an_unknown_title_share_one_hydration()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdbId = Random.Shared.NextInt64(10_000_000, long.MaxValue);
        var tmdb = new CountingTmdbClient(tmdbId, "Jeanne Dielman, 23 quai du Commerce, 1080 Bruxelles");
        var coordinator = new CatalogWorkCoordinator();

        // Two requests, two scoped contexts, one coordinator — exactly the production shape.
        var first = HydrateAsync(tmdb, coordinator, tmdbId);
        var second = HydrateAsync(tmdb, coordinator, tmdbId);
        var titles = await Task.WhenAll(first, second);

        Assert.All(titles, t => Assert.NotNull(t));
        Assert.Equal(titles[0]!.Id, titles[1]!.Id);
        Assert.Equal("Jeanne Dielman, 23 quai du Commerce, 1080 Bruxelles", titles[0]!.Name);

        // One factory execution costs exactly two detail fetches (one to synthesize the candidate
        // row, one inside TitleHydrator). A second execution would have pushed this to 3+.
        Assert.Equal(2, tmdb.MovieFetches);

        // And exactly one row landed, hydrated.
        await using var db = fixture.CreateContext(new AccountContext());
        var row = Assert.Single(await db.Titles.Where(t => t.TmdbId == tmdbId).ToListAsync());
        Assert.NotNull(row.LastMetadataRefreshAt);
        Assert.Equal(1975, row.Year);
        Assert.Contains("drama", row.Genres);
    }

    [Fact]
    public async Task A_tmdb_404_resolves_to_null_and_creates_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdbId = Random.Shared.NextInt64(10_000_000, long.MaxValue);
        var tmdb = new CountingTmdbClient(tmdbId, "irrelevant") { NotFound = true };

        var title = await HydrateAsync(tmdb, new CatalogWorkCoordinator(), tmdbId);

        Assert.Null(title); // → the endpoint 404s honestly

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.False(await db.Titles.AnyAsync(t => t.TmdbId == tmdbId));
    }

    [Fact]
    public async Task An_unparseable_media_type_resolves_to_null_without_touching_tmdb()
    {
        var tmdb = new CountingTmdbClient(1, "irrelevant");
        await using var db = fixture.CreateContext(new AccountContext());

        var title = await TitleEndpoints.HydrateOnDemandAsync(
            db, tmdb,
            new CandidateGenerator(db, tmdb, NullLogger<CandidateGenerator>.Instance),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new CatalogWorkCoordinator(), "bogus", 1, CancellationToken.None);

        Assert.Null(title);
        Assert.Equal(0, tmdb.MovieFetches);
    }

    [Fact]
    public async Task A_hydrated_title_scores_against_the_active_artifact()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedActiveArtifactAsync(accountId);

        var tmdbId = Random.Shared.NextInt64(10_000_000, long.MaxValue);
        var tmdb = new CountingTmdbClient(tmdbId, "News from Home");
        var title = await HydrateAsync(tmdb, new CatalogWorkCoordinator(), tmdbId);
        Assert.NotNull(title);

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var scorer = new OnDemandScorer(
            db, featureBuilder, engine,
            new OnlineResidualModel(db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance));

        // No persisted score exists yet — the endpoint path must score on demand and re-read.
        var score = await TitleEndpoints.ResolveScoreAsync(db, scorer, accountId, title!.Id, CancellationToken.None);

        Assert.NotNull(score);
        Assert.InRange(score!.PredictedRating, 0m, 10m); // the response carries a predictedRating

        // And a second resolve reuses the persisted row rather than re-scoring blind.
        var again = await TitleEndpoints.ResolveScoreAsync(db, scorer, accountId, title.Id, CancellationToken.None);
        Assert.Equal(score.Id, again!.Id);
    }

    /// <summary>Runs the endpoint helper the way one request would: its own scoped context and services.</summary>
    private async Task<Title?> HydrateAsync(ITmdbClient tmdb, CatalogWorkCoordinator coordinator, long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await TitleEndpoints.HydrateOnDemandAsync(
            db, tmdb,
            new CandidateGenerator(db, tmdb, NullLogger<CandidateGenerator>.Instance),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            coordinator, "movie", tmdbId, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"hydrate-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>Small real FastTree artifact marked Active — same fixture recipe as the residual tests.</summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(1);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 200; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(5f + (v[0] - 0.5f), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows.ToArray(), labels.ToArray(), FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);

        await using var db = fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Details-serving fake that counts movie fetches — the single-flight spy.</summary>
    private sealed class CountingTmdbClient(long knownTmdbId, string name) : ITmdbClient
    {
        private int _movieFetches;

        public int MovieFetches => _movieFetches;

        public bool NotFound { get; init; }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _movieFetches);
            return Task.FromResult<TmdbTitleDetails?>(NotFound || tmdbId != knownTmdbId
                ? null
                : new TmdbTitleDetails(
                    tmdbId, "/jd.jpg", "/jd-back.jpg", 12m, 7.8m, 400, 201,
                    "A widowed mother's meticulously ordered days.", null, null, [], [], null,
                    ["slow cinema"], "fr", ["fr"], null,
                    Name: name,
                    ReleasedAt: new DateTime(1975, 5, 14, 0, 0, 0, DateTimeKind.Utc),
                    GenreIds: [18]));
        }

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
