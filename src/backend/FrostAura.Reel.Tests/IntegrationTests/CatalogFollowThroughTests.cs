using System.Globalization;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The two stages behind the seed: global hydration and global enrichment, their priority order,
/// and the daily ceilings that pace them.
///
/// <para>
/// The ceilings are the half of this feature that decides whether it is safe to run at all. A seed
/// that lands 35,000 rows in front of an unbounded enrichment sweep presents one night's bill for
/// the whole catalogue; a ceiling that is checked once at the top of a run and never again is a
/// ceiling a single run walks straight through. Both are asserted here against real ledger rows
/// rather than a mock, because a gate that reads a different table to the one the spend lands in is
/// exactly the failure a mock would hide.
/// </para>
///
/// <para>
/// <b>Every assertion is written to survive a shared catalogue.</b> This collection's database is
/// global and every other class in it seeds titles, so absolute counts are meaningless. Two devices
/// make these tests exact anyway: the seeded titles carry an absurd vote count so the supply
/// ordering puts them first whatever else exists, and every ceiling is set RELATIVE to what the
/// budget reports it has already spent.
/// </para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> even though it never writes a
/// <c>TitleEmbedding</c> itself: the enrichment half of the follow-through embeds what it hydrates,
/// so this class leaves vectors in the shared HNSW index exactly as a hand-seeding Fixture does.
/// </remarks>
[Collection("postgres")]
public class CatalogFollowThroughTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>Higher than any real TMDB vote count, so these rows head the supply ordering.</summary>
    private const int TopOfQueueVotes = 9_000_000;

    /// <summary>
    /// Supply order, not id order. The whole value of a capped run is WHICH fraction of a 35,000-row
    /// backlog it takes: id order hydrates a uniformly random sample and leaves the titles most
    /// likely to be rated waiting behind the ones nobody will ever see.
    /// </summary>
    [Fact]
    public async Task Global_hydration_takes_the_highest_vote_count_first()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var low = await SeedUnhydratedAsync(TopOfQueueVotes + 1, 2001);
        var high = await SeedUnhydratedAsync(TopOfQueueVotes + 3, 2002);
        var middle = await SeedUnhydratedAsync(TopOfQueueVotes + 2, 2003);

        var tmdb = await RunGlobalHydrationAsync(extraAllowance: 3);

        Assert.Equal([high.Tmdb, middle.Tmdb, low.Tmdb], tmdb.Hydrated.Take(3));
    }

    /// <summary>Recency breaks a vote-count tie — the second half of "what discovery would surface first".</summary>
    [Fact]
    public async Task Recency_breaks_a_vote_count_tie()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var older = await SeedUnhydratedAsync(TopOfQueueVotes + 7, 1998);
        var newer = await SeedUnhydratedAsync(TopOfQueueVotes + 7, 2024);

        var tmdb = await RunGlobalHydrationAsync(extraAllowance: 2);

        Assert.Equal([newer.Tmdb, older.Tmdb], tmdb.Hydrated.Take(2));
    }

    /// <summary>
    /// The ceiling binds mid-run, not merely at the top of it. Set two slots, offer three titles,
    /// and exactly two may be bought.
    /// </summary>
    [Fact]
    public async Task Global_hydration_stops_at_the_daily_title_ceiling()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        await SeedUnhydratedAsync(TopOfQueueVotes + 13, 2020);
        await SeedUnhydratedAsync(TopOfQueueVotes + 12, 2020);
        await SeedUnhydratedAsync(TopOfQueueVotes + 11, 2020);

        var tmdb = await RunGlobalHydrationAsync(extraAllowance: 2);

        Assert.Equal(2, tmdb.Hydrated.Count);
    }

    /// <summary>
    /// A spent allowance means no call at all — not a run that fetches one title and then notices.
    /// This is also what stops the follow-through ticker turning into a spin: a job with no
    /// allowance is a worker slot claimed to do nothing.
    /// </summary>
    [Fact]
    public async Task A_spent_allowance_buys_nothing_at_all()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        await SeedUnhydratedAsync(TopOfQueueVotes + 21, 2019);

        var tmdb = await RunGlobalHydrationAsync(extraAllowance: 0);

        Assert.Empty(tmdb.Hydrated);
    }

    /// <summary>Hydrating anything queues the enrichment that turns metadata into features.</summary>
    [Fact]
    public async Task Global_hydration_chains_a_global_enrichment()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        await SeedUnhydratedAsync(TopOfQueueVotes + 31, 2018);
        await RunGlobalHydrationAsync(extraAllowance: 1);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.True(await db.SyncJobs.AnyAsync(j =>
            j.Kind == JobKind.EnrichCatalog && j.AccountId == null && j.Status == JobStatus.Pending));
    }

    /// <summary>
    /// One global hydrate job at a time. The partial unique index keys on <c>(AccountId, Kind)</c>
    /// and Postgres treats each NULL account as distinct, so it does not dedup global rows — the
    /// scheduler's explicit check is the only guard, and a ticker running every fifteen minutes
    /// would otherwise pile up a job per tick.
    /// </summary>
    [Fact]
    public async Task The_follow_through_queues_one_hydration_and_dedups_the_next()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        await using var db = Fixture.CreateContext(new AccountContext());
        await ClearGlobalJobsAsync(db);
        await SeedUnhydratedAsync(TopOfQueueVotes + 41, 2017);

        var first = await CatalogFollowThroughScheduler.RunAsync(
            db, Budget(db, 10_000), extractorVersion: 1, DateTime.UtcNow, CancellationToken.None);
        var second = await CatalogFollowThroughScheduler.RunAsync(
            db, Budget(db, 10_000), extractorVersion: 1, DateTime.UtcNow, CancellationToken.None);

        Assert.True(first.QueuedHydration);
        Assert.False(second.QueuedHydration);
        Assert.Equal(1, await db.SyncJobs.CountAsync(j => j.Kind == JobKind.HydrateCatalog && j.AccountId == null));
    }

    /// <summary>
    /// No allowance, no job. Queueing one anyway is the spin the ticker exists to avoid: it would
    /// claim a slot, hydrate nothing, complete, and be queued again fifteen minutes later.
    /// </summary>
    [Fact]
    public async Task The_follow_through_queues_no_hydration_once_the_day_is_spent()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        await using var db = Fixture.CreateContext(new AccountContext());
        await ClearGlobalJobsAsync(db);
        await SeedUnhydratedAsync(TopOfQueueVotes + 51, 2016);

        var used = (await Budget(db, 10_000).AllowanceAsync(DateTime.UtcNow, CancellationToken.None)).Used;
        var result = await CatalogFollowThroughScheduler.RunAsync(
            db, Budget(db, (int)used), extractorVersion: 1, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.QueuedHydration);
        Assert.False(await db.SyncJobs.AnyAsync(j => j.Kind == JobKind.HydrateCatalog && j.AccountId == null));
    }

    /// <summary>
    /// The money gate, driven by the ledger the spend actually lands in. With the ceiling set to
    /// exactly what has been spent, extraction buys nothing — and the embedding phase, which has its
    /// own separate ceiling, is untouched by it.
    /// </summary>
    [Fact]
    public async Task The_extract_token_ceiling_stops_extraction_without_touching_embedding()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var title = await SeedHydratedAsync(TopOfQueueVotes + 61, 2015);
        await SpendAsync(ApiUsagePurpose.ExtractAttributes, 40_000);

        var extractor = new CountingExtractor("ceiling/extract");
        await RunEnrichAsync(extractor, extractTokens: await SpentTodayAsync(ApiUsagePurpose.ExtractAttributes));

        Assert.DoesNotContain(title.Id, extractor.TitlesSeen);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.False(await db.TitleAttributes.AnyAsync(a => a.TitleId == title.Id));
        // Embedding has its own ceiling and its own ledger line; extraction running out must not
        // stall a stage that costs two orders of magnitude less.
        Assert.True(await db.TitleEmbeddings.AnyAsync(e => e.TitleId == title.Id));
    }

    /// <summary>Headroom means the same run does the work — the gate is a ceiling, not an off switch.</summary>
    [Fact]
    public async Task With_headroom_the_same_run_extracts()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var title = await SeedHydratedAsync(TopOfQueueVotes + 71, 2014);

        var extractor = new CountingExtractor("ceiling/headroom");
        await RunEnrichAsync(
            extractor, extractTokens: await SpentTodayAsync(ApiUsagePurpose.ExtractAttributes) + 1_000_000);

        Assert.Contains(title.Id, extractor.TitlesSeen);
    }

    /// <summary>The embed ceiling is enforced on the same terms and against its own ledger line.</summary>
    [Fact]
    public async Task The_embed_token_ceiling_stops_embedding()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var title = await SeedHydratedAsync(TopOfQueueVotes + 81, 2013);
        await SpendAsync(ApiUsagePurpose.Embed, 25_000);

        await RunEnrichAsync(
            new CountingExtractor("ceiling/embed"),
            embedTokens: await SpentTodayAsync(ApiUsagePurpose.Embed));

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.False(await db.TitleEmbeddings.AnyAsync(e => e.TitleId == title.Id));
    }

    /// <summary>
    /// A global enrich run works the backlog in supply order too — the same reasoning as hydration,
    /// and the reason a capped run over a seeded catalogue is worth scheduling at all.
    /// </summary>
    [Fact]
    public async Task A_global_enrich_run_takes_the_highest_vote_count_first()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await DemoteEarlierRunsAsync();

        var low = await SeedHydratedAsync(TopOfQueueVotes + 91, 2012);
        var high = await SeedHydratedAsync(TopOfQueueVotes + 93, 2012);
        var middle = await SeedHydratedAsync(TopOfQueueVotes + 92, 2012);

        var extractor = new CountingExtractor("order/global");
        await RunEnrichAsync(extractor, cap: 3);

        Assert.Equal([high.Id, middle.Id, low.Id], extractor.TitlesSeen.Take(3));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sinks every row an EARLIER test in this class seeded to the bottom of the supply ordering.
    ///
    /// <para>
    /// Necessary, not hygiene. The catalogue is shared and this class deliberately seeds rows that
    /// outrank everything in it, so without this a capped run asserts on the previous test's titles
    /// — and the enrichment eligibility rules make that worse rather than better, because they are
    /// scoped to the extractor MODEL, so a title another test already attributed under a different
    /// model id is eligible all over again. Demoting rather than deleting keeps the foreign keys
    /// from <c>TitleAttributes</c> and <c>TitleEmbeddings</c> intact.
    /// </para>
    /// </summary>
    private async Task DemoteEarlierRunsAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "Titles" SET "TmdbVoteCount" = 0 WHERE "TmdbVoteCount" >= {TopOfQueueVotes}
             """);
    }

    private async Task<(Guid Id, long Tmdb)> SeedUnhydratedAsync(int votes, int year)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = NewTitle(votes, year);
        db.Add(title);
        await db.SaveChangesAsync();
        return (title.Id, title.TmdbId!.Value);
    }

    private async Task<(Guid Id, long Tmdb)> SeedHydratedAsync(int votes, int year)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = NewTitle(votes, year);
        title.LastMetadataRefreshAt = DateTime.UtcNow;
        db.Add(title);
        await db.SaveChangesAsync();
        return (title.Id, title.TmdbId!.Value);
    }

    private static Title NewTitle(int votes, int year) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(700_000_000, 800_000_000),
        TraktSlug = string.Empty,
        Name = string.Create(CultureInfo.InvariantCulture, $"Follow-through {Guid.NewGuid():N}"),
        Overview = "A seeded catalogue row awaiting completion.",
        Genres = ["drama"],
        Language = "en",
        TmdbVoteCount = votes,
        Year = year,
        ReleasedAt = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Runs a global hydration with a ceiling of "whatever has already been spent today, plus
    /// <paramref name="extraAllowance"/>". Relative rather than absolute because every other class in
    /// this collection hydrates titles into the same database on the same day.
    /// </summary>
    private async Task<HydratingTmdb> RunGlobalHydrationAsync(int extraAllowance)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var used = (await Budget(db, 10_000).AllowanceAsync(DateTime.UtcNow, CancellationToken.None)).Used;
        var tmdb = new HydratingTmdb();
        var configuration = Configure(new Dictionary<string, string?>
        {
            [CatalogHydrationBudget.DailyTitlesKey] =
                (used + extraAllowance).ToString(CultureInfo.InvariantCulture),
        });

        var handler = new HydrateCatalogJobHandler(
            db,
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new NoopHub(),
            new CatalogHydrationBudget(db, configuration),
            configuration,
            NullLogger<HydrateCatalogJobHandler>.Instance);

        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = null,
            Kind = JobKind.HydrateCatalog,
            Status = JobStatus.Running,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();

        await handler.ExecuteAsync(job, CancellationToken.None);

        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return tmdb;
    }

    private async Task RunEnrichAsync(
        CountingExtractor extractor, long? extractTokens = null, long? embedTokens = null, int cap = 1)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var configuration = Configure(new Dictionary<string, string?>
        {
            [EnrichCatalogJobHandler.MaxTitlesPerRunKey] = cap.ToString(CultureInfo.InvariantCulture),
            [CatalogEnrichmentBudget.ExtractTokensKey] = extractTokens?.ToString(CultureInfo.InvariantCulture),
            [CatalogEnrichmentBudget.EmbedTokensKey] = embedTokens?.ToString(CultureInfo.InvariantCulture),
        });

        var handler = new EnrichCatalogJobHandler(
            db,
            new UnitEmbeddingProvider(),
            extractor,
            new NoopHub(),
            configuration,
            new CatalogEnrichmentBudget(db, configuration),
            NullLogger<EnrichCatalogJobHandler>.Instance);

        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = null,
            Kind = JobKind.EnrichCatalog,
            Status = JobStatus.Running,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();

        await handler.ExecuteAsync(job, CancellationToken.None);

        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Writes a real ledger row — the gate must read the table the spend lands in.</summary>
    private async Task SpendAsync(string purpose, long tokens)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ExternalApiUsages.Add(new ExternalApiUsage
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = DateTime.UtcNow.Date,
            AccountId = null,
            Model = $"ledger/{Guid.NewGuid():N}",
            Purpose = purpose,
            CallCount = 1,
            PromptTokens = tokens,
            CompletionTokens = 0,
            TokensUsed = tokens,
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SpentTodayAsync(string purpose)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var today = DateTime.UtcNow.Date;
        return await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter && u.Purpose == purpose && u.Day == today)
            .SumAsync(u => u.TokensUsed ?? 0);
    }

    private static CatalogHydrationBudget Budget(ReelDbContext db, int dailyTitles) =>
        new(db, Configure(new Dictionary<string, string?>
        {
            [CatalogHydrationBudget.DailyTitlesKey] = dailyTitles.ToString(CultureInfo.InvariantCulture),
        }));

    private static IConfiguration Configure(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task ClearGlobalJobsAsync(ReelDbContext db)
    {
        db.SyncJobs.RemoveRange(await db.SyncJobs
            .Where(j => j.AccountId == null
                && (j.Kind == JobKind.HydrateCatalog || j.Kind == JobKind.EnrichCatalog))
            .ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>Records the order titles were hydrated in — the only way to assert on priority.</summary>
    private sealed class HydratingTmdb : ITmdbClient
    {
        public List<long> Hydrated { get; } = [];

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
        {
            Hydrated.Add(tmdbId);
            // The vote count is echoed high so a hydrated row does not fall behind this class's
            // still-pending rows in the supply ordering half way through a run.
            return Task.FromResult<TmdbTitleDetails?>(new TmdbTitleDetails(
                tmdbId, "/p.jpg", "/b.jpg", 11m, 7m, TopOfQueueVotes, 101,
                "An overview.", null, null, [], [], null, ["keyword"], "en"));
        }

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => GetMovieAsync(tmdbId, ct);

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

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null,
            TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }

    private sealed class CountingExtractor(string modelId, int version = 1) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => version;

        public List<Guid> TitlesSeen { get; } = [];

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            TitlesSeen.AddRange(inputs.Select(i => i.TitleId));
            IReadOnlyList<ExtractedTitleAttributes?> result = inputs
                .Select(_ => (ExtractedTitleAttributes?)new ExtractedTitleAttributes(
                    0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, "neutral", "contemporary", ["t"], "{}"))
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class UnitEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var vectors = texts.Select(t =>
            {
                var v = new float[1536];
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] = (float)Math.Sin(t.Length + i);
                }

                var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] /= norm;
                }

                return v;
            }).ToArray();
            return Task.FromResult(vectors);
        }
    }

    private sealed class NoopHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data)
        {
        }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
