using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WHEN enrichment runs — the half of the 2026-09-09 supply finding that scheduling, not the
/// handler, is responsible for.
///
/// <para>
/// Until this landed, <see cref="JobKind.EnrichCatalog"/> had exactly one enqueue site in the whole
/// backend: the onboarding chain. Enrichment therefore ran once per account, at OAuth link, and
/// never again — so every candidate discovered afterwards stayed permanently unembedded and
/// unattributed (~1,360 and ~1,400 of 5,319 titles when measured), invisible to the ANN retrieval
/// arm with 13 of 92 feature slots pinned at zero. Three schedulers now close that loop, and these
/// tests pin all three plus the coalescing that stops them piling up on each other.
/// </para>
/// </summary>
[Collection("postgres")]
public class EnrichSchedulingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_build_that_upserted_new_candidates_schedules_enrichment()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();

        // Three titles TMDB has and the catalogue does not: RefreshPoolAsync upserts them, and every
        // one arrives with no embedding and no attributes.
        await RunBuildAsync(accountId, new SeedingTmdbClient(3));

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.True(await EnrichPendingAsync(db, accountId));
    }

    [Fact]
    public async Task A_build_that_upserted_nothing_leaves_enrichment_unscheduled()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();

        // Same build, offline discovery: nothing new entered the catalogue, so there is no new gap
        // and nothing to pay an LLM for. The nightly pass is what covers a standing backlog.
        await RunBuildAsync(accountId, new SeedingTmdbClient(0));

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.False(await EnrichPendingAsync(db, accountId));
    }

    /// <summary>
    /// The in-flight guard, which is what lets three schedulers fire freely. A burst of builds must
    /// leave ONE enrich job queued, not one per build — enrichment is idempotent but not free.
    /// </summary>
    [Fact]
    public async Task A_second_enqueue_while_one_is_in_flight_is_coalesced()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;

        await using var db = fixture.CreateContext(Scoped(accountId));
        Assert.True(await EnrichmentScheduler.EnqueueAsync(db, accountId, now, CancellationToken.None));
        await db.SaveChangesAsync();

        Assert.False(await EnrichmentScheduler.EnqueueAsync(db, accountId, now, CancellationToken.None));

        // Running counts as in flight too — a job mid-sweep already covers the new work.
        var queued = await db.SyncJobs.SingleAsync(j => j.AccountId == accountId && j.Kind == JobKind.EnrichCatalog);
        queued.Status = JobStatus.Running;
        await db.SaveChangesAsync();
        Assert.False(await EnrichmentScheduler.EnqueueAsync(db, accountId, now, CancellationToken.None));

        // Settled: the next one is allowed, because the next discovery genuinely needs a new run.
        queued.Status = JobStatus.Succeeded;
        queued.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.True(await EnrichmentScheduler.EnqueueAsync(db, accountId, now, CancellationToken.None));
    }

    /// <summary>
    /// The GLOBAL job the weekly catalogue-growth sweep queues. Postgres treats NULL index keys as
    /// distinct, so the partial unique index on <c>(AccountId, Kind)</c> — the guard every other job
    /// kind leans on — cannot dedup these at all. The scheduler's explicit check is the only thing
    /// standing between a weekly sweep and an unbounded pile of global enrich jobs, so it is pinned
    /// here rather than assumed.
    /// </summary>
    [Fact]
    public async Task A_global_enqueue_is_deduped_by_the_explicit_check_the_index_cannot_do()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var before = await db.SyncJobs.CountAsync(j => j.AccountId == null && j.Kind == JobKind.EnrichCatalog);

        var now = DateTime.UtcNow;
        Assert.True(await EnrichmentScheduler.EnqueueAsync(db, null, now, CancellationToken.None));
        await db.SaveChangesAsync();
        Assert.False(await EnrichmentScheduler.EnqueueAsync(db, null, now, CancellationToken.None));

        var after = await db.SyncJobs.CountAsync(j => j.AccountId == null && j.Kind == JobKind.EnrichCatalog);
        Assert.Equal(before + 1, after);

        // A pending job for an ACCOUNT is not a global job and must not suppress one: the two sweep
        // in different priority orders and neither substitutes for the other.
        var accountId = await SeedAccountAsync();
        Assert.True(await EnrichmentScheduler.EnqueueAsync(db, accountId, now, CancellationToken.None));
    }

    /// <summary>
    /// ExtractorVersion invalidation as the nightly pass runs it: rows below the running prompt
    /// generation go back to Pending with a fresh budget, no more than the cap per pass, and a row
    /// already reset is not re-written — so the sweep converges instead of churning every night.
    /// </summary>
    [Fact]
    public async Task Stale_extractor_version_rows_are_requeued_up_to_the_cap_and_then_left_alone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var titleIds = await SeedAttributeRowsAsync(3, version: 1, status: AttributeExtractionStatus.Done, attempts: 2);

        await using var db = fixture.CreateContext(new AccountContext());

        // Counted globally, not over the seeded three: the sweep is catalogue-wide by design and this
        // collection shares one database, so an assertion scoped to this class's own rows would be an
        // assertion about which rows happened to sort first. What IS exact is that at least three
        // rows are stale (we just wrote them), so a cap of two must bind.
        var staleBefore = await StaleCountAsync(db);
        Assert.True(staleBefore >= 3);

        Assert.Equal(2, await EnrichmentScheduler.RequeueStaleExtractorVersionsAsync(db, 2, cap: 2, CancellationToken.None));
        await db.SaveChangesAsync();
        Assert.Equal(staleBefore - 2, await StaleCountAsync(db));

        // Drain the rest in one pass and check what a reset row actually looks like: back to Pending
        // with the attempt budget returned, because the prompt that exhausted it asked a different
        // question. Without the reset a row that failed twice under the old prompt would be settled
        // for ever under the new one.
        await EnrichmentScheduler.RequeueStaleExtractorVersionsAsync(db, 2, cap: 10_000, CancellationToken.None);
        await db.SaveChangesAsync();

        var mine = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, a => Assert.Equal(AttributeExtractionStatus.Pending, a.Status));
        Assert.All(mine, a => Assert.Equal(0, a.AttemptCount));
        Assert.All(mine, a => Assert.Equal(1, a.ExtractorVersion)); // the EXTRACTOR restamps, not the sweep

        // Converged: a row already reset no longer matches, so the nightly pass stops re-writing it
        // rather than churning the same rows every night for ever.
        Assert.Equal(0, await EnrichmentScheduler.RequeueStaleExtractorVersionsAsync(db, 2, cap: 10_000, CancellationToken.None));

        // And a row already at the running version is never touched, whatever its status.
        Assert.Equal(0, await EnrichmentScheduler.RequeueStaleExtractorVersionsAsync(db, 1, cap: 10_000, CancellationToken.None));
    }

    /// <summary>Rows the version sweep would act on, catalogue-wide — its predicate, restated.</summary>
    private static Task<int> StaleCountAsync(ReelDbContext db) =>
        db.TitleAttributes.CountAsync(a => a.ExtractorVersion < 2
            && (a.Status != AttributeExtractionStatus.Pending || a.AttemptCount != 0));

    /// <summary>
    /// The numbers behind the <c>enrichment</c> block on GET /api/metrics/pipeline. Asserted as
    /// DELTAS against whatever the shared catalogue already holds — the counts are global by design
    /// (catalog rows are tenant-shared), so an absolute assertion would be a claim about every other
    /// test class rather than about this one.
    /// </summary>
    [Fact]
    public async Task The_backlog_report_counts_the_gap_the_scheduler_exists_to_close()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var before = await EnrichmentScheduler.BacklogAsync(db, 2, CancellationToken.None);

        // Two hydrated titles with nothing: both gaps widen by two.
        var bare = await SeedHydratedTitlesAsync(2);

        // One more that is Done but at the superseded prompt version: present, settled, and still
        // wrong — it must show up as stale, and NOT as a missing attribute.
        await SeedAttributeRowsAsync(1, version: 1, status: AttributeExtractionStatus.Done, attempts: 1);

        var after = await EnrichmentScheduler.BacklogAsync(db, 2, CancellationToken.None);

        Assert.Equal(before.MissingEmbedding + 3, after.MissingEmbedding);
        Assert.Equal(before.MissingAttributes + 2, after.MissingAttributes);
        Assert.Equal(before.StaleExtractorVersion + 1, after.StaleExtractorVersion);
        Assert.Equal(2, bare.Count);
    }

    private static async Task<bool> EnrichPendingAsync(ReelDbContext db, Guid accountId) =>
        await db.SyncJobs.AnyAsync(j =>
            j.AccountId == accountId && j.Kind == JobKind.EnrichCatalog && j.Status == JobStatus.Pending);

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"enrich-sched-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>An account a BuildFeed job will actually run for: it needs an Active artifact.</summary>
    private async Task<Guid> SeedBuildableAccountAsync()
    {
        var accountId = await SeedAccountAsync();
        var rng = new Random(23);
        var names = FeatureSchema.AllNames();
        var rows = new float[120][];
        var labels = new float[120];
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
            """{"Trees":10,"Leaves":4,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

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
        return accountId;
    }

    private async Task<List<Guid>> SeedHydratedTitlesAsync(int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = $"sched-{Guid.NewGuid():N}",
                Name = $"Scheduling Title {Guid.NewGuid():N}",
                Overview = "Seeded for the enrichment backlog count.",
                Genres = ["drama"],
                LastMetadataRefreshAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<List<Guid>> SeedAttributeRowsAsync(
        int count,
        int version = 1,
        AttributeExtractionStatus status = AttributeExtractionStatus.Done,
        int attempts = 1)
    {
        var ids = await SeedHydratedTitlesAsync(count);
        if (count == 0)
        {
            return ids;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var id in ids)
        {
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = id,
                ExtractorModel = "sched/test-model",
                ExtractorVersion = version,
                Status = status,
                AttemptCount = attempts,
                ExtractedAt = status == AttributeExtractionStatus.Done ? DateTime.UtcNow : null,
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task RunBuildAsync(Guid accountId, ITmdbClient tmdb)
    {
        await using var db = fixture.CreateContext(Scoped(accountId));
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var handler = new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, featureBuilder, new FastTreeModelEngine(),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            new UnavailableSearchAgent(),
            new MemoryCache(new MemoryCacheOptions()),
            new NoopEventHub(),
            NullLogger<BuildFeedJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
            },
            CancellationToken.None);
    }

    /// <summary>
    /// A TMDB port whose trending arm yields <paramref name="newTitles"/> titles the catalogue has
    /// never seen — the shape of a build that actually widened supply. Zero makes it the offline
    /// client, which is the control case.
    /// </summary>
    private sealed class SeedingTmdbClient(int newTitles) : ITmdbClient
    {
        private readonly List<TmdbListItem> _items = Enumerable.Range(0, newTitles)
            .Select(_ => new TmdbListItem(
                Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
                IsMovie: true,
                Name: $"Discovered {Guid.NewGuid():N}",
                PosterPath: "/p.jpg",
                BackdropPath: null,
                Overview: "Freshly discovered, and therefore unembedded and unattributed.",
                Popularity: 2m,
                VoteAverage: 7m,
                VoteCount: 100,
                ReleasedAt: new DateTime(2018, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                GenreIds: [18],
                OriginalLanguage: "en"))
            .ToList();

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        // The one arm that yields: RefreshPoolAsync always calls trending, whatever the taste state.
        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>(movies ? _items : []);

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

    private sealed class UnavailableSearchAgent : ISearchAgent
    {
        public bool IsAvailable => false;

        public string ModelId => "stub";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(IReadOnlyList<RerankInput> titles, string query, string tasteSummary, string? anchorName = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
