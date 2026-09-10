using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// Regression for the defect that stranded ~28% of the catalogue unhydrated. HydrateBatchAsync had
/// no per-title try/catch and its caller saved once at the END of a 25-title batch, so any failure
/// that was not a 404 — a 429, a 5xx, a malformed payload — threw out of the loop before
/// SaveChangesAsync. Every healthy title in that batch lost its already-completed work, the retry
/// landed on the identical batch and hit the identical bad title, and after three attempts the job
/// was marked Failed with up to 24 good titles left at LastMetadataRefreshAt == null — which also
/// excludes them from EnrichCatalog, so no embedding and no LLM attributes either.
///
/// These tests pin the property that failure violated: one bad title costs exactly one title.
/// </summary>
public class TitleHydratorResilienceTests
{
    [Fact]
    public async Task One_failing_title_does_not_cost_its_batch_mates_their_hydration()
    {
        var titles = Batch(5);
        var tmdb = new StubTmdb(failFor: titles[2].TmdbId!.Value, new HttpRequestException("429 Too Many Requests"));

        var result = await Hydrator(tmdb).HydrateBatchResilientAsync(titles, CancellationToken.None);

        Assert.Equal(4, result.Hydrated);
        Assert.Equal([titles[2]], result.Failed);

        // The four healthy titles carry their TMDB payload AND their refresh stamp, so the caller's
        // SaveChangesAsync persists real progress and they are eligible for enrichment.
        foreach (var survivor in titles.Where(t => t != titles[2]))
        {
            Assert.NotNull(survivor.LastMetadataRefreshAt);
            Assert.Equal("/poster.jpg", survivor.PosterPath);
            Assert.Equal(101, survivor.RuntimeMinutes);
        }

        // The failed one is left un-stamped: it stays pending so the cursor owner can retry it.
        Assert.Null(titles[2].LastMetadataRefreshAt);
        Assert.Null(titles[2].PosterPath);
    }

    [Fact]
    public async Task A_batch_in_which_every_title_fails_still_returns_rather_than_throwing()
    {
        var titles = Batch(3);
        var tmdb = new StubTmdb(failFor: null, new InvalidOperationException("TMDB is down"), failEverything: true);

        var result = await Hydrator(tmdb).HydrateBatchResilientAsync(titles, CancellationToken.None);

        Assert.Equal(0, result.Hydrated);
        Assert.Equal(3, result.Failed.Count);
        Assert.All(titles, t => Assert.Null(t.LastMetadataRefreshAt));
    }

    [Fact]
    public async Task A_timeout_is_isolated_too_even_though_it_arrives_as_a_cancellation()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException with the caller's token
        // un-cancelled. A naive "catch everything except OperationCanceledException" would let it
        // through and poison the batch exactly as before.
        var titles = Batch(3);
        var tmdb = new StubTmdb(failFor: titles[1].TmdbId!.Value, new TaskCanceledException("The request timed out."));

        var result = await Hydrator(tmdb).HydrateBatchResilientAsync(titles, CancellationToken.None);

        Assert.Equal(2, result.Hydrated);
        Assert.Equal([titles[1]], result.Failed);
    }

    [Fact]
    public async Task A_real_cancellation_still_unwinds()
    {
        var titles = Batch(3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Hydrator(new StubTmdb(null, new InvalidOperationException("unused"))).HydrateBatchResilientAsync(titles, cts.Token));
    }

    [Fact]
    public async Task A_title_TMDB_404s_is_stamped_so_it_is_never_retried_forever()
    {
        var titles = Batch(2);
        var tmdb = new StubTmdb(failFor: null, new InvalidOperationException("unused"), notFoundFor: titles[0].TmdbId!.Value);

        var result = await Hydrator(tmdb).HydrateBatchResilientAsync(titles, CancellationToken.None);

        Assert.Equal(2, result.Hydrated);
        Assert.Empty(result.Failed);
        Assert.All(titles, t => Assert.NotNull(t.LastMetadataRefreshAt));
    }

    [Fact]
    public async Task Titles_without_a_tmdb_id_are_skipped_not_failed()
    {
        var titles = Batch(2);
        titles[0].TmdbId = null;

        var result = await Hydrator(new StubTmdb(null, new InvalidOperationException("unused"))).HydrateBatchResilientAsync(titles, CancellationToken.None);

        Assert.Equal(1, result.Hydrated);
        Assert.Empty(result.Failed);
        Assert.Null(titles[0].LastMetadataRefreshAt);
    }

    internal static TitleHydrator Hydrator(ITmdbClient tmdb) =>
        new(new UnusedDbContext(), tmdb, NullLogger<TitleHydrator>.Instance);

    private static List<Title> Batch(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Title
            {
                Id = Guid.NewGuid(),
                TmdbId = 1000 + i,
                MediaType = MediaType.Movie,
                Name = $"Title {i}",
            })
            .ToList();

    /// <summary>
    /// Returns credit-free details (so hydration never needs the database), 404s or throws for the
    /// nominated TMDB id.
    /// </summary>
    internal sealed class StubTmdb(
        long? failFor, Exception failure, bool failEverything = false, long? notFoundFor = null,
        Func<long, TmdbTitleDetails>? details = null) : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
        {
            if (failEverything || tmdbId == failFor)
            {
                throw failure;
            }

            if (tmdbId == notFoundFor)
            {
                return Task.FromResult<TmdbTitleDetails?>(null); // TMDB 404 — confirmed missing.
            }

            return Task.FromResult<TmdbTitleDetails?>(details?.Invoke(tmdbId) ?? new TmdbTitleDetails(
                tmdbId, "/poster.jpg", "/backdrop.jpg", 12.5m, 7.1m, 900, 101,
                "An overview.", "A tagline.", null, [], [], "yt-key", ["heist"], "en"));
        }

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => GetMovieAsync(tmdbId, ct);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null,
            TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Deliberately inert persistence port: these titles carry no credits, so a correct hydrator
    /// never touches the database at all. Any member access here is a test failure with a name.
    /// </summary>
    internal sealed class UnusedDbContext : IReelDbContext
    {
        public DbSet<Account> Accounts => throw Unexpected();
        public DbSet<TraktConnection> TraktConnections => throw Unexpected();
        public DbSet<RefreshSession> RefreshSessions => throw Unexpected();
        public DbSet<Title> Titles => throw Unexpected();
        public DbSet<TitleEmbedding> TitleEmbeddings => throw Unexpected();
        public DbSet<TitleAttributes> TitleAttributes => throw Unexpected();
        public DbSet<Person> Persons => throw Unexpected();
        public DbSet<TitleCredit> TitleCredits => throw Unexpected();
        public DbSet<StreamingProvider> StreamingProviders => throw Unexpected();
        public DbSet<TitleAvailability> TitleAvailabilities => throw Unexpected();
        public DbSet<WatchedTitle> WatchedTitles => throw Unexpected();
        public DbSet<ShowWatchProgress> ShowWatchProgresses => throw Unexpected();
        public DbSet<UserRating> UserRatings => throw Unexpected();
        public DbSet<UserRatingEvent> UserRatingEvents => throw Unexpected();
        public DbSet<UserPersonRating> UserPersonRatings => throw Unexpected();
        public DbSet<UserTitleReaction> UserTitleReactions => throw Unexpected();
        public DbSet<ContentFilter> ContentFilters => throw Unexpected();
        public DbSet<UserPreferenceTag> UserPreferenceTags => throw Unexpected();
        public DbSet<Domain.Search.SearchConversation> SearchConversations => throw Unexpected();
        public DbSet<Domain.Search.SearchTurn> SearchTurns => throw Unexpected();
        public DbSet<PersonAffinity> PersonAffinities => throw Unexpected();
        public DbSet<TrainingRun> TrainingRuns => throw Unexpected();
        public DbSet<ModelArtifact> ModelArtifacts => throw Unexpected();
        public DbSet<EvaluationResult> EvaluationResults => throw Unexpected();
        public DbSet<TitleScore> TitleScores => throw Unexpected();
        public DbSet<OnlineModelState> OnlineModelStates => throw Unexpected();
        public DbSet<FeedSnapshot> FeedSnapshots => throw Unexpected();
        public DbSet<FeedItem> FeedItems => throw Unexpected();

        public DbSet<FeedBuildStat> FeedBuildStats => throw Unexpected();
        public DbSet<AccountDigest> AccountDigests => throw Unexpected();
        public DbSet<Domain.Lens.AccountTasteNarrative> AccountTasteNarratives => throw Unexpected();
        public DbSet<Domain.Lens.TitleLensCopy> TitleLensCopies => throw Unexpected();
        public DbSet<Domain.CrossReference.TitleCrossReferenceVerdict> TitleCrossReferenceVerdicts => throw Unexpected();
        public DbSet<Domain.CrossReference.CrossReferenceEvaluation> CrossReferenceEvaluations => throw Unexpected();
        public Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database => throw Unexpected();
        public DbSet<ExposureEvent> ExposureEvents => throw Unexpected();
        public DbSet<ExposureOutcome> ExposureOutcomes => throw Unexpected();
        public DbSet<TitleExposureRollup> TitleExposureRollups => throw Unexpected();
        public DbSet<Domain.Notifications.PushSubscription> PushSubscriptions => throw Unexpected();
        public DbSet<Domain.Notifications.AppSecret> AppSecrets => throw Unexpected();
        public DbSet<SyncJob> SyncJobs => throw Unexpected();
        public DbSet<TraktOutboxEntry> TraktOutbox => throw Unexpected();
        public DbSet<ManagedListItem> ManagedListItems => throw Unexpected();
        public DbSet<ExternalApiUsage> ExternalApiUsages => throw Unexpected();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw Unexpected();

        private static NotSupportedException Unexpected() =>
            new("Hydrating credit-free titles must not touch the database.");
    }
}
