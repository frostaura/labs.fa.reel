using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV8-RET-01: page 2 of "Deep cuts — under the radar" is held to the same popularity bound as the
/// cards the build dealt under that title. The paging path used to take a fresh 70th percentile of
/// its own window — a window drawn from the same quality-first head as tonight's picks — so scrolling
/// the rail served titles four times as popular as any deep cut the build dealt (Three Billboards at
/// 36.6 after a page-1 maximum of 8.3), and most of page 2 matched tonight's picks card for card. The
/// bound is <see cref="FeedRowRules.DeepCutBoundFromDealt"/> over the active snapshot's deep cuts —
/// the refill's rule (HREV6-RET-01) — and a page with nothing inside it is exhausted, not padded.
/// Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class FeedPaginatorDeepCutsTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    [InlineData(null)]
    public async Task Deep_cuts_page_two_never_serves_a_title_more_popular_than_the_dealt_deep_cuts(string? media)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = await SeedActiveArtifactAsync(accountId);

        // The build dealt one deep cut of each medium; the most popular of them (0.6) is the bound,
        // inclusive — a title at exactly 0.6 is no more popular than a card the build put there.
        var dealtMovie = await SeedTitleAsync(MediaType.Movie, 0.4m);
        var dealtShow = await SeedTitleAsync(MediaType.Show, 0.6m);
        await SeedSnapshotAsync(accountId, artifactId, FeedRowKind.DeepCuts, [dealtMovie, dealtShow]);

        // Under-the-radar supply in both media, the bound itself included.
        foreach (var m in new[] { MediaType.Movie, MediaType.Show })
        {
            for (var i = 0; i < 6; i++)
            {
                await SeedTitleAsync(m, 0.1m + (i * 0.08m));
            }

            await SeedTitleAsync(m, 0.6m);
        }

        // And the titles the rail must never page in: just over the bound, and plainly popular — the
        // shape of the head tonight's picks are drawn from.
        foreach (var m in new[] { MediaType.Movie, MediaType.Show })
        {
            await SeedTitleAsync(m, 0.61m);
            for (var i = 0; i < 10; i++)
            {
                await SeedTitleAsync(m, 30m + i);
            }
        }

        var page = await MoreAsync(accountId, media, exclude: [dealtMovie, dealtShow]);

        Assert.NotEmpty(page);
        Assert.All(page, i => Assert.True(
            FeedRowRules.PopularityOf(i.Title) <= 0.6m,
            $"{i.Title.Name} (popularity {i.Title.TmdbPopularity}) is more popular than any deep cut the build dealt"));
        Assert.All(page, i => Assert.True(
            ForYouBar.Clears(i.Predicted, FeedRowRules.DeepCutPredictedFloor),
            $"{i.Title.Name} predicted {i.Predicted} is under the deep-cuts floor"));
        if (media is not null)
        {
            var wanted = media == "movie" ? MediaType.Movie : MediaType.Show;
            Assert.All(page, i => Assert.Equal(wanted, i.Title.MediaType));
        }
    }

    /// <summary>
    /// No deep cuts dealt — no active snapshot, or one whose deep-cuts row is empty — means nothing
    /// was measured as under the radar, so the rail is exhausted rather than padded from the window.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deep_cuts_page_is_exhausted_when_the_build_dealt_no_deep_cuts(bool snapshotWithOtherRows)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = await SeedActiveArtifactAsync(accountId);
        if (snapshotWithOtherRows)
        {
            var hero = await SeedTitleAsync(MediaType.Movie, 0.2m);
            await SeedSnapshotAsync(accountId, artifactId, FeedRowKind.Hero, [hero]);
        }

        for (var i = 0; i < 6; i++)
        {
            await SeedTitleAsync(MediaType.Movie, 0.1m + (i * 0.05m));
            await SeedTitleAsync(MediaType.Show, 30m + i);
        }

        var page = await MoreAsync(accountId, media: null, exclude: []);

        Assert.Empty(page);
    }

    private async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(Guid accountId, string? media, List<Guid> exclude)
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
        var paginator = new FeedPaginator(
            db, featureBuilder, retriever, engine, residual,
            new RatingCalibrationService(db, cache), new DislikeSignal(db, cache),
            candidateGenerator, tmdb, new UnavailableSearchAgent(),
            new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new FrostAura.Reel.Application.CrossReference.RerankBudget(
                db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            cache, NullLogger<FeedPaginator>.Instance);

        // The body useInfiniteRow sends when the rail scrolls: the row, page 1's ids, a page of 12.
        var req = new MoreFeedRequest(
            FeedRowContext.DeepCuts, AnchorTitleId: null, Genre: null, ExcludeTitleIds: exclude, Limit: 12, MediaType: media);
        return await paginator.MoreAsync(account, req, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"deepcuts-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SeedSnapshotAsync(Guid accountId, Guid artifactId, FeedRowKind row, IReadOnlyList<Guid> dealt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = artifactId,
            GeneratedAt = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        for (var rank = 0; rank < dealt.Count; rank++)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                FeedSnapshotId = snapshot.Id,
                Row = row,
                Rank = rank,
                TitleId = dealt[rank],
                PredictedRating = 7.5m,
                WhyThisSentence = string.Empty,
                WhySource = "Templater",
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTitleAsync(MediaType media, decimal popularity)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"deep-cuts {media} {popularity} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            Keywords = [],
            TmdbPopularity = popularity,
            ReleasedAt = media == MediaType.Movie ? DateTime.UtcNow.AddYears(-3) : null,
            FirstAiredAt = media == MediaType.Show ? DateTime.UtcNow.AddYears(-3) : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// A tiny FastTree on the real feature schema whose every prediction sits high (7.5–8), so the
    /// deep-cuts predicted floor admits every title and popularity is the only thing the page is cut on.
    /// </summary>
    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(29);
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
            labels[i] = 7.5f + (0.5f * v[0]);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
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
        };
        db.ModelArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    /// <summary>A TMDB port that yields nothing: the run stays offline and no deepen grows the window.</summary>
    private sealed class EmptyTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

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
