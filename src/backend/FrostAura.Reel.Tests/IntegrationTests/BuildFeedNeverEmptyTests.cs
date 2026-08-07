using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The two founder-reported feed-build defects of 2026-08-07, pinned end-to-end on real Postgres.
///
/// <b>Empty feed.</b> The founder combined exclude-documentary, crowd ≥ 6.0, for-you ≥ 5.5,
/// year ≥ 2000 and English and got <c>hero: 0, rows: 0</c> from a job that reported Succeeded —
/// while ~250 eligible, scorable candidates sat in the pool. The proximate cause was the online
/// residual head saturating its clamp (covered arithmetically in <c>OnlineResidualSaturationTests</c>),
/// which pushed every prediction ~3 points down until nothing could clear the account's
/// MinPredictedRating. This test pins the structural half: whatever the model's output scale does,
/// a build with a healthy candidate pool must never produce zero cards, and when the floor has to be
/// relaxed to achieve that the snapshot must SAY so instead of rendering a blank page.
///
/// <b>Frozen anchors.</b> "Because you loved The Great Wall / The Bone Collector" never changed,
/// because the anchors were the strict top of a rating-first order that a large rating history
/// freezes permanently. Two consecutive builds over identical ratings must now anchor differently.
///
/// Skips when REEL_TEST_CONNECTION_STRING is unset (no DB in the run).
/// </summary>
[Collection("postgres")]
public class BuildFeedNeverEmptyTests(PostgresFixture fixture)
{
    /// <summary>Above anything the seeded model can predict — so the strict floor admits nothing.</summary>
    private const decimal UnreachableFloor = 9.9m;

    [Fact]
    public async Task A_healthy_pool_never_builds_an_empty_feed_and_says_why_the_floor_was_relaxed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);

        // A hard CONTENT rule alongside the soft floor, so the test can prove the relaxation is
        // surgical: documentaries are excluded outright and a mass of them is the most attractive
        // thing in the pool, so a build that relaxed indiscriminately would surface them.
        var excludedGenre = $"doc-{Guid.NewGuid():N}";
        await SeedEligibleTitlesAsync(40, genre: excludedGenre);
        await AddContentFilterAsync(accountId, FilterKind.ExcludeGenre, excludedGenre);

        await SeedActiveArtifactAsync(accountId);
        await AddContentFilterAsync(accountId, FilterKind.MinPredictedRating, ContentFilterRules.FormatRating(UnreachableFloor));

        await RunBuildAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();

        // The candidate pool was healthy by construction — so zero cards is a pipeline failure, not
        // an honest verdict, exactly as the relaxation ladder in LiveSearchExpansionService holds.
        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.Row == FeedRowKind.Hero);

        // And the user is told, rather than left staring at picks that quietly ignore their setting.
        Assert.NotNull(snapshot.DegradedReason);
        Assert.Contains("9.9", snapshot.DegradedReason);

        // Relaxing the SOFT floor must never relax a HARD content rule with it.
        var shownIds = items.Select(i => i.TitleId).ToList();
        var shownGenres = await db.Titles.Where(t => shownIds.Contains(t.Id)).Select(t => t.Genres).ToListAsync();
        Assert.All(shownGenres, g => Assert.DoesNotContain(excludedGenre, g));
    }

    [Fact]
    public async Task A_build_that_clears_the_floor_is_not_marked_degraded()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The control case: the relaxation must be reserved for the broken path. A feed that honours
        // the user's floor on its own must carry no reason at all, or the UI would cry wolf forever.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        Assert.Null(snapshot.DegradedReason);
        Assert.NotEmpty(await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync());
    }

    [Fact]
    public async Task Two_consecutive_builds_with_the_same_ratings_anchor_on_different_titles()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);
        var first = await AnchorsOfActiveSnapshotAsync(accountId);

        // Nothing about the account changes between the two builds — same ratings, same catalogue.
        // Under the old rating-first selection this returned an identical anchor set for ever.
        await RunBuildAsync(accountId);
        var second = await AnchorsOfActiveSnapshotAsync(accountId);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Empty(first.Intersect(second));
    }

    private async Task<HashSet<Guid>> AnchorsOfActiveSnapshotAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        return (await db.FeedItems
                .Where(i => i.FeedSnapshotId == snapshot.Id && i.AnchorTitleId != null)
                .Select(i => i.AnchorTitleId!.Value)
                .Distinct()
                .ToListAsync())
            .ToHashSet();
    }

    private static async Task<FeedSnapshot> ActiveSnapshotAsync(ReelDbContext db, Guid accountId) =>
        await db.FeedSnapshots.FirstAsync(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active);

    private async Task RunBuildAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
        var handler = new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, featureBuilder, new FastTreeModelEngine(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                NullLogger<OnlineResidualModel>.Instance),
            new NoopEventHub(),
            NullLogger<BuildFeedJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.BuildFeed, EnqueuedAt = DateTime.UtcNow },
            CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"buildfeed-{Guid.NewGuid():N}",
            Tier = AccountTier.Founder,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// Loved titles spread across years of RatedAt. The spread is the point: a rating-first order
    /// treats them as interchangeable and freezes on the highest, whereas the recency-weighted
    /// anchor pool has something to rotate through.
    /// </summary>
    private async Task SeedLovedTitlesAsync(Guid accountId, int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"loved {i}", popularity: 1m + (i * 0.01m));
            db.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(8 + (i % 3)),
                RatedAt = DateTime.UtcNow.AddDays(-30 * i),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedEligibleTitlesAsync(int count, string genre = "drama")
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            // Popularity kept low so this seeding never perturbs the popularity head the other
            // integration tests in this shared-catalogue collection assert against.
            db.Add(NewTitle($"candidate {i}", popularity: 2m + (i * 0.001m), genre));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A renderable, eligible title. <c>LastMetadataRefreshAt</c> is set so the build's hydration
    /// phase is a no-op — this suite is about assembly, not about the TMDB round trip.
    /// </summary>
    private static Title NewTitle(string name, decimal popularity, string genre = "drama") => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en", // eligibility defaults to English-only with no language filter configured
        PosterPath = "/p.jpg",
        Genres = [genre], // a shared genre ⇒ genre-Jaccard similarity clears the row threshold
        Year = 2015,
        RuntimeMinutes = 110,
        TraktRating = 7.2m,
        TmdbVoteAverage = 7.4m,
        TmdbPopularity = popularity,
        ReleasedAt = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task AddContentFilterAsync(Guid accountId, FilterKind kind, string value)
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

    /// <summary>A tiny FastTree over the real schema, labelled well below the unreachable floor.</summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(23);
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
            labels[i] = Math.Clamp(3f + (4f * v[0]), 1f, 8f);
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
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>TMDB port that yields nothing, so the build runs offline and the assertions rest on
    /// the seeded catalogue rather than on whatever discover happens to return today.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
