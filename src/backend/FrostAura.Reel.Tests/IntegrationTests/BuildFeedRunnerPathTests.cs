using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
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
/// The feed build as the JOB RUNNER actually runs it, not as the other suites shortcut it.
///
/// <para>Every existing build test constructs the handler on a context built with an UNPINNED
/// <see cref="AccountContext"/> and calls <c>ExecuteAsync</c> directly. Live, the runner pins the
/// job's account on the scope first and then commits the job's own status on the SAME context —
/// and it is that second half that the shortcut skipped. The founder's database carried eight
/// snapshots, forty-five feed items and zero <see cref="FeedBuildStat"/> / <see cref="ExposureEvent"/>
/// rows while every one of those tests was green.</para>
/// </summary>
[Collection("postgres")]
public class BuildFeedRunnerPathTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_build_run_the_way_the_job_runner_runs_it_persists_its_stat_row_and_its_exposures()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);
        var jobId = await EnqueueBuildAsync(accountId);

        await RunThroughJobRunnerPathAsync(jobId);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var stats = await db.FeedBuildStats.Where(s => s.AccountId == accountId).ToListAsync();
        var exposures = await db.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync();
        var items = await db.FeedItems.CountAsync();

        Assert.NotEqual(0, items);
        var stat = Assert.Single(stats);
        Assert.Equal(FeedBuildOutcome.Fresh, stat.Mode);
        Assert.NotEqual(0, stat.ShortlistCount);
        Assert.Equal(items, exposures.Count);
    }

    /// <summary>
    /// The read path's own write site. <c>GET /api/feed</c> backfills cards that never existed as
    /// FeedItem rows, and <c>RecordAsync</c> is the only thing that records them — through the same
    /// <c>Stage</c> the build uses, so a defect in staging takes both surfaces down together.
    /// </summary>
    [Fact]
    public async Task A_refill_recorded_on_a_pinned_request_context_persists_its_exposures()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 4);
        await SeedActiveArtifactAsync(accountId);

        Guid titleId;
        Guid artifactId;
        Guid snapshotId;
        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var title = NewTitle("refill", popularity: 3m);
            seed.Add(title);
            var snapshot = new FeedSnapshot
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                GeneratedAt = DateTime.UtcNow,
                ModelArtifactId = await seed.ModelArtifacts.Where(a => a.AccountId == accountId).Select(a => a.Id).FirstAsync(),
                Status = SnapshotStatus.Active,
            };
            seed.FeedSnapshots.Add(snapshot);
            await seed.SaveChangesAsync();
            titleId = title.Id;
            snapshotId = snapshot.Id;
            artifactId = snapshot.ModelArtifactId;
        }

        await using (var request = fixture.CreateContext(Scoped(accountId)))
        {
            var log = new ExposureLog(request, NullLogger<ExposureLog>.Instance);
            var written = await log.RecordAsync(
                accountId,
                ExposureSurface.FeedRefill,
                snapshotId,
                artifactId,
                DateTime.UtcNow,
                [new ExposureCard(titleId, 0, 7.5m)],
                CancellationToken.None);
            Assert.Equal(1, written);
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal(1, await verify.ExposureEvents.CountAsync(e => e.Surface == ExposureSurface.FeedRefill && e.AccountId == accountId));
    }

    /// <summary>
    /// The exit that fired on EVERY build the running binary claimed on 2026-09-10, and left
    /// nothing behind. An Active artifact existed; the query filter hid it because a second, older
    /// process on the same database had trained it against an older feature schema. The build is
    /// still right to refuse it — but it must say which of the two things happened, at Error, and
    /// it must leave a row saying a build ran and produced nothing.
    /// </summary>
    [Fact]
    public async Task A_build_that_cannot_read_the_active_artifact_records_a_skipped_row_and_names_both_schema_versions()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 4);
        var artifactId = await SeedActiveArtifactAsync(accountId, schemaVersion: FeatureSchema.Version - 1);
        var jobId = await EnqueueBuildAsync(accountId);

        await RunThroughJobRunnerPathAsync(jobId);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var stat = Assert.Single(await db.FeedBuildStats.Where(s => s.AccountId == accountId).ToListAsync());
        Assert.Equal(FeedBuildOutcome.Skipped, stat.Mode);
        // The artifact it could not use is named, so the row alone identifies the offending model.
        Assert.Equal(artifactId, stat.ModelArtifactId);
        Assert.Null(stat.FeedSnapshotId);
        Assert.NotNull(stat.DegradedReason);
        Assert.Contains(
            (FeatureSchema.Version - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            stat.DegradedReason);
        Assert.Contains(
            FeatureSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stat.DegradedReason);

        // And nothing was dealt — refusing an unreadable model must never produce a page.
        Assert.Equal(0, await db.FeedSnapshots.CountAsync(s => s.AccountId == accountId));
    }

    /// <summary>
    /// The other half of the same fork: an account that genuinely has no model still gets a row,
    /// and still gets the advice that is actually actionable.
    /// </summary>
    [Fact]
    public async Task A_build_with_no_artifact_at_all_records_a_skipped_row_that_asks_for_a_train()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var jobId = await EnqueueBuildAsync(accountId);

        await RunThroughJobRunnerPathAsync(jobId);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var stat = Assert.Single(await db.FeedBuildStats.Where(s => s.AccountId == accountId).ToListAsync());
        Assert.Equal(FeedBuildOutcome.Skipped, stat.Mode);
        Assert.Equal(Guid.Empty, stat.ModelArtifactId);
        Assert.Contains("train a model", stat.DegradedReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors <c>JobRunnerService.RunJobAsync</c>: one scope, one context, the account pinned
    /// BEFORE the handler is resolved, the handler run, and the job's own completion committed on
    /// that same context afterwards.
    /// </summary>
    private async Task RunThroughJobRunnerPathAsync(Guid jobId)
    {
        var accountContext = new AccountContext();
        await using var db = fixture.CreateContext(accountContext);

        // The claim, exactly as the runner issues it.
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs" SET "Status" = 'Running', "StartedAt" = {DateTime.UtcNow},
                 "HeartbeatAt" = {DateTime.UtcNow}, "AttemptCount" = "AttemptCount" + 1
             WHERE "Id" = {jobId}
             """);

        var job = await db.SyncJobs.FirstAsync(j => j.Id == jobId);
        accountContext.SetAccount(job.AccountId!.Value);

        var handler = HandlerFor(db);
        await handler.ExecuteAsync(job, CancellationToken.None);

        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        job.ProgressPct = 100;
        job.Error = null;
        await db.SaveChangesAsync();
    }

    private static BuildFeedJobHandler HandlerFor(ReelDbContext db)
    {
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
        return new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(db, featureBuilder, new FastTreeModelEngine(), NewCache(), NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, NewCache()),
            new DislikeSignal(db, NewCache()),
            new UnavailableSearchAgent(),
            new TasteFactsBuilder(db, new DislikeSignal(db, NewCache()), featureBuilder, NewCache()),
            new PersonalisationContext(db, new DislikeSignal(db, NewCache())),
            NewCache(),
            new NoopEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance);
    }

    private static MemoryCache NewCache() => new(new MemoryCacheOptions());

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> EnqueueBuildAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.BuildFeed,
            Priority = 1,
            EnqueuedAt = DateTime.UtcNow,
            TriggerReason = "reconcile",
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"runnerpath-{Guid.NewGuid():N}",
            Tier = AccountTier.Founder,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

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
            db.Add(NewTitle($"candidate {i}", popularity: 2m + (i * 0.001m), genre));
        }

        await db.SaveChangesAsync();
    }

    private static Title NewTitle(string name, decimal popularity, string genre = "drama") => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en",
        PosterPath = "/p.jpg",
        Genres = [genre],
        Year = 2015,
        RuntimeMinutes = 110,
        TraktRating = 7.2m,
        TmdbVoteAverage = 7.4m,
        TmdbPopularity = popularity,
        ReleasedAt = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId, int? schemaVersion = null)
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
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            // The schema version is a parameter because the defect under test is what happens when
            // it does NOT match the running build — which is not reachable by any amount of seeding
            // through the normal train path, since that always stamps the current version.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = schemaVersion ?? FeatureSchema.Version, names }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
        db.ModelArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private sealed class OfflineTmdbClient : ITmdbClient
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

    private sealed class UnavailableSearchAgent : ISearchAgent
    {
        public bool IsAvailable => false;

        public string ModelId => "stub";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, CancellationToken ct = default) =>
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
