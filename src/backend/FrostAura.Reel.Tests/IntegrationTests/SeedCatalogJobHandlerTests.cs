using System.Globalization;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The catalogue bulk seed on real Postgres, against a scripted TMDB that serves a known number of
/// pages per partition.
///
/// <para>
/// Every assertion here is about a property that a passing job would otherwise hide. A seed that
/// re-fetches a page it already paid for still finishes. A seed that loses its cursor on a crash
/// still finishes — from the top. A seed that ignores its page budget still finishes, having held a
/// worker slot for a week and been reclaimed as stale halfway. The job's own success is worth
/// nothing as evidence, which is why none of these tests assert on it.
/// </para>
/// </summary>
[Collection("postgres")]
public class SeedCatalogJobHandlerTests(PostgresFixture fixture)
{
    /// <summary>
    /// The plan is ~74 partitions wide, so a test that walked it all would spend hundreds of round
    /// trips proving nothing extra. Every test here bounds the run with a small page budget and
    /// asserts on the first partitions instead.
    /// </summary>
    private const int SmallPageBudget = 5;

    [Fact]
    public async Task Walks_a_partition_page_by_page_then_advances_to_the_next()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 2, itemsPerPage: 3);
        var job = await RunAsync(tmdb, pageBudget: SmallPageBudget);

        // 5 pages of budget over 2-page partitions: movie/2026 p1, p2, show/2026 p1, p2, movie/2025 p1.
        Assert.Equal(
            [
                (CatalogSeedPlan.MovieMedia, CurrentYear, 1),
                (CatalogSeedPlan.MovieMedia, CurrentYear, 2),
                (CatalogSeedPlan.ShowMedia, CurrentYear, 1),
                (CatalogSeedPlan.ShowMedia, CurrentYear, 2),
                (CatalogSeedPlan.MovieMedia, CurrentYear - 1, 1),
            ],
            tmdb.Fetched);

        var cursor = CatalogSeedCursor.Read(job.CursorJson)!;
        Assert.Equal(CatalogSeedPlan.MovieMedia, cursor.Media);
        Assert.Equal(CurrentYear - 1, cursor.Year);
        Assert.Equal(2, cursor.Page);
        Assert.Equal(5, cursor.PagesFetched);
        Assert.Equal(15, cursor.TitlesAdded);
        Assert.False(cursor.Done);
    }

    /// <summary>
    /// The vote floor and the year window reach TMDB as SENT parameters, not as a post-filter. TMDB
    /// ignores a parameter it cannot parse and returns a valid unfiltered page, so a floor that
    /// never left the process is indistinguishable from one that worked — until the corpus is wrong.
    /// </summary>
    [Fact]
    public async Task Each_query_carries_its_own_medium_vote_floor_and_a_single_year()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 1, itemsPerPage: 1);
        await RunAsync(tmdb, pageBudget: 4);

        Assert.Equal(
            [
                (true, CurrentYear, CatalogSeedPlan.MovieMinVoteCount),
                (false, CurrentYear, CatalogSeedPlan.ShowMinVoteCount),
                (true, CurrentYear - 1, CatalogSeedPlan.MovieMinVoteCount),
                (false, CurrentYear - 1, CatalogSeedPlan.ShowMinVoteCount),
            ],
            tmdb.Queries);
    }

    /// <summary>
    /// A crash mid-partition costs one page and nothing else. The cursor is written after EVERY
    /// page precisely so this is true — and the assertion is that the resumed walk never re-fetches
    /// a page, because "it finished eventually" is also true of a walk that starts over.
    /// </summary>
    [Fact]
    public async Task Resumes_mid_partition_after_a_crash_without_refetching_a_page()
    {
        if (!fixture.Available)
        {
            return;
        }

        var first = new ScriptedSeedTmdb(pagesPerPartition: 6, itemsPerPage: 2);
        var crashed = await RunAsync(first, pageBudget: 3);

        var cursor = CatalogSeedCursor.Read(crashed.CursorJson)!;
        Assert.Equal(4, cursor.Page); // mid-partition: pages 1-3 done, 4 next
        Assert.Equal(CatalogSeedPlan.MovieMedia, cursor.Media);

        // The runner reclaims a stale Running job and re-executes it from its own cursor — same job
        // row, same CursorJson, a fresh handler. That is the exact shape simulated here.
        var resumed = new ScriptedSeedTmdb(pagesPerPartition: 6, itemsPerPage: 2, seed: first.Seed);
        await RunAsync(resumed, pageBudget: 3, existing: crashed);

        Assert.Equal(
            [
                (CatalogSeedPlan.MovieMedia, CurrentYear, 4),
                (CatalogSeedPlan.MovieMedia, CurrentYear, 5),
                (CatalogSeedPlan.MovieMedia, CurrentYear, 6),
            ],
            resumed.Fetched);

        var after = CatalogSeedCursor.Read(crashed.CursorJson)!;
        // Cumulative across the crash, not reset by it: 6 pages × 2 items, all distinct.
        Assert.Equal(6, after.PagesFetched);
        Assert.Equal(12, after.TitlesAdded);
        Assert.Equal(CatalogSeedPlan.ShowMedia, after.Media); // partition finished, moved on
        Assert.Equal(1, after.Page);
    }

    /// <summary>
    /// A partition ends at TMDB's reported <c>total_pages</c>. Nothing else may end it — least of
    /// all a fixed page depth, which would silently amputate the tail of every busy year.
    /// </summary>
    [Fact]
    public async Task A_partition_ends_at_the_total_pages_TMDB_reports()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 3, itemsPerPage: 1);
        await RunAsync(tmdb, pageBudget: 4);

        Assert.Equal(3, tmdb.Fetched.Count(f => f.Media == CatalogSeedPlan.MovieMedia && f.Year == CurrentYear));
        Assert.DoesNotContain((CatalogSeedPlan.MovieMedia, CurrentYear, 4), tmdb.Fetched);
    }

    /// <summary>
    /// TMDB reports <c>total_pages</c> for the whole result set even where it will only SERVE 500 of
    /// them. Without the clamp the walk pages into a wall for as long as its budget lasts, and the
    /// partition after it never starts.
    /// </summary>
    [Fact]
    public async Task A_partition_claiming_more_than_500_pages_still_stops_at_the_wall()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 9_999, itemsPerPage: 1);
        await RunAsync(tmdb, pageBudget: 3, startAtPage: CatalogSeedPlan.MaxDiscoverPage - 1);

        Assert.Equal(
            [
                (CatalogSeedPlan.MovieMedia, CurrentYear, CatalogSeedPlan.MaxDiscoverPage - 1),
                (CatalogSeedPlan.MovieMedia, CurrentYear, CatalogSeedPlan.MaxDiscoverPage),
                // The wall ends the partition even though the fake claims 9,999 pages.
                (CatalogSeedPlan.ShowMedia, CurrentYear, 1),
            ],
            tmdb.Fetched);
    }

    /// <summary>
    /// The page budget is what turns a week-long walk into a chain of short jobs. A run that
    /// overspends it holds a worker slot past the runner's 12-minute stale-heartbeat window and gets
    /// reclaimed mid-flight.
    /// </summary>
    [Fact]
    public async Task The_page_budget_bounds_a_run_and_queues_a_successor_carrying_the_cursor()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 50, itemsPerPage: 1);
        var job = await RunAsync(tmdb, pageBudget: 4);

        Assert.Equal(4, tmdb.Fetched.Count);

        await using var db = fixture.CreateContext(new AccountContext());
        var successor = await db.SyncJobs
            .Where(j => j.Kind == JobKind.SeedCatalog && j.Id != job.Id && j.Status == JobStatus.Pending)
            .SingleAsync();
        Assert.Equal(CatalogSeedScheduler.Priority, successor.Priority);
        Assert.Null(successor.AccountId);

        var carried = CatalogSeedCursor.Read(successor.CursorJson)!;
        Assert.Equal(5, carried.Page);
        Assert.Equal(4, carried.PagesFetched);
        Assert.Equal(4, carried.TitlesAdded);
    }

    /// <summary>
    /// <c>SEED_MAX_TITLES</c> is a guard, not a pacing knob: tripping it means TMDB returned far
    /// more than the arithmetic predicted, so the walk STOPS and queues nothing. A successor here
    /// would turn a guard into a speed bump.
    /// </summary>
    [Fact]
    public async Task The_title_ceiling_stops_the_walk_and_queues_no_successor()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 50, itemsPerPage: 4);
        var job = await RunAsync(tmdb, pageBudget: 50, maxTitles: 6);

        // Two pages take the cumulative count to 8, past the ceiling of 6; the third never happens.
        Assert.Equal(2, tmdb.Fetched.Count);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.False(await db.SyncJobs.AnyAsync(j => j.Kind == JobKind.SeedCatalog && j.Id != job.Id));
    }

    /// <summary>
    /// The rows are what the whole exercise is for: real catalogue titles with a TMDB id, a name, a
    /// vote count and a language, created without hydration and therefore without metadata. The
    /// language matters more than it looks — an unhydrated candidate with no language is dropped by
    /// the English-only eligibility default before it is ever hydrated.
    /// </summary>
    [Fact]
    public async Task Seeded_rows_are_unhydrated_summary_titles_carrying_their_discovery_language()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 1, itemsPerPage: 3);
        await RunAsync(tmdb, pageBudget: 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var ids = tmdb.IssuedTmdbIds;
        var rows = await db.Titles.Where(t => t.TmdbId != null && ids.Contains(t.TmdbId.Value)).ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, t => Assert.Null(t.LastMetadataRefreshAt));
        Assert.All(rows, t => Assert.Equal(MediaType.Movie, t.MediaType));
        Assert.All(rows, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        Assert.All(rows, t => Assert.True(t.TmdbVoteCount >= CatalogSeedPlan.MovieMinVoteCount));
        Assert.All(rows, t => Assert.Equal("en", t.Language));
        Assert.All(rows, t => Assert.Equal(CurrentYear, t.Year));
    }

    /// <summary>
    /// Re-walking a partition must cost pages and add no rows. It is the property that makes a
    /// restart from a lost cursor merely wasteful instead of corrupting.
    /// </summary>
    [Fact]
    public async Task Re_walking_the_same_partition_upserts_rather_than_duplicating()
    {
        if (!fixture.Available)
        {
            return;
        }

        var first = new ScriptedSeedTmdb(pagesPerPartition: 1, itemsPerPage: 4);
        await RunAsync(first, pageBudget: 1);

        var again = new ScriptedSeedTmdb(pagesPerPartition: 1, itemsPerPage: 4, seed: first.Seed);
        var job = await RunAsync(again, pageBudget: 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var ids = first.IssuedTmdbIds;
        Assert.Equal(4, await db.Titles.CountAsync(t => t.TmdbId != null && ids.Contains(t.TmdbId.Value)));
        Assert.Equal(0, CatalogSeedCursor.Read(job.CursorJson)!.TitlesAdded);
    }

    /// <summary>A run that added rows hands them to the follow-through, or nothing ever completes them.</summary>
    [Fact]
    public async Task A_run_that_added_rows_queues_the_global_hydration_that_completes_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = new ScriptedSeedTmdb(pagesPerPartition: 1, itemsPerPage: 2);
        await RunAsync(tmdb, pageBudget: 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var hydrate = await db.SyncJobs
            .Where(j => j.Kind == JobKind.HydrateCatalog && j.AccountId == null && j.Status == JobStatus.Pending)
            .ToListAsync();
        Assert.Single(hydrate);
        Assert.Equal("catalogue-follow-through", hydrate[0].TriggerReason);
    }

    /// <summary>
    /// The seed writes rows every tenant shares, so an account-scoped seed would be one account
    /// deciding what the catalogue contains for everyone. Refused loudly rather than run quietly.
    /// </summary>
    [Fact]
    public async Task An_account_scoped_seed_job_is_refused()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var handler = NewHandler(db, new ScriptedSeedTmdb(1, 1), Config(1, null));
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Kind = JobKind.SeedCatalog,
            Status = JobStatus.Running,
            EnqueuedAt = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(job, CancellationToken.None));
    }

    /// <summary>
    /// Two POSTs, one walk. The partial unique index keys on <c>(AccountId, Kind)</c> and Postgres
    /// treats the NULL account of a global job as distinct from every other NULL, so the database
    /// will happily accept a duplicate — the scheduler's own check is the only guard there is.
    /// </summary>
    [Fact]
    public async Task Enqueue_is_idempotent_while_a_seed_is_in_flight()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        await ClearSeedJobsAsync(db);

        var (firstQueued, first) = await CatalogSeedScheduler.EnqueueAsync(db, DateTime.UtcNow, CancellationToken.None);
        var (secondQueued, second) = await CatalogSeedScheduler.EnqueueAsync(db, DateTime.UtcNow, CancellationToken.None);

        Assert.True(firstQueued);
        Assert.False(secondQueued);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.SyncJobs.CountAsync(j => j.Kind == JobKind.SeedCatalog));
    }

    /// <summary>
    /// Enqueue after a run has ENDED is a resume, not a restart. Without this a job that failed its
    /// third attempt would send the next POST back to 1990 and re-spend every page already paid for.
    /// </summary>
    [Fact]
    public async Task Enqueue_after_a_finished_run_resumes_from_the_last_cursor()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        await ClearSeedJobsAsync(db);

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = null,
            Kind = JobKind.SeedCatalog,
            Status = JobStatus.Failed,
            Priority = CatalogSeedScheduler.Priority,
            EnqueuedAt = DateTime.UtcNow.AddHours(-1),
            CursorJson = new CatalogSeedCursor(CatalogSeedPlan.ShowMedia, 2011, 7, 4_242, 900, false).Write(),
        });
        await db.SaveChangesAsync();

        var (queued, job) = await CatalogSeedScheduler.EnqueueAsync(db, DateTime.UtcNow, CancellationToken.None);
        var cursor = CatalogSeedCursor.Read(job.CursorJson)!;

        Assert.True(queued);
        Assert.Equal(CatalogSeedPlan.ShowMedia, cursor.Media);
        Assert.Equal(2011, cursor.Year);
        Assert.Equal(7, cursor.Page);
        Assert.Equal(4_242, cursor.TitlesAdded);
        Assert.Equal("seed-catalog-resume", job.TriggerReason);
    }

    private static int CurrentYear => DateTime.UtcNow.Year;

    /// <summary>
    /// Runs one seed pass exactly as JobRunnerService would, then mirrors the runner's own status
    /// transition so the next enqueue is not blocked by a job left Running.
    /// </summary>
    private async Task<SyncJob> RunAsync(
        ScriptedSeedTmdb tmdb,
        int pageBudget,
        int? maxTitles = null,
        SyncJob? existing = null,
        int? startAtPage = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await ClearSeedJobsAsync(db);
        await ClearGlobalHydrationJobsAsync(db);

        SyncJob job;
        if (existing is null)
        {
            job = new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = null,
                Kind = JobKind.SeedCatalog,
                Status = JobStatus.Running,
                Priority = CatalogSeedScheduler.Priority,
                EnqueuedAt = DateTime.UtcNow,
                CursorJson = startAtPage is { } page
                    ? (CatalogSeedCursor.Start(DateTime.UtcNow) with { Page = page }).Write()
                    : null,
            };
            db.SyncJobs.Add(job);
        }
        else
        {
            job = new SyncJob
            {
                Id = existing.Id,
                AccountId = null,
                Kind = JobKind.SeedCatalog,
                Status = JobStatus.Running,
                Priority = CatalogSeedScheduler.Priority,
                EnqueuedAt = existing.EnqueuedAt,
                CursorJson = existing.CursorJson,
            };
            db.SyncJobs.Add(job);
        }

        await db.SaveChangesAsync();

        await NewHandler(db, tmdb, Config(pageBudget, maxTitles)).ExecuteAsync(job, CancellationToken.None);

        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (existing is not null)
        {
            existing.CursorJson = job.CursorJson;
        }

        return job;
    }

    private static SeedCatalogJobHandler NewHandler(
        Infrastructure.Persistence.ReelDbContext db, ITmdbClient tmdb, IConfiguration configuration) =>
        new(
            db,
            tmdb,
            new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance),
            configuration,
            NullLogger<SeedCatalogJobHandler>.Instance);

    private static IConfiguration Config(int pageBudget, int? maxTitles) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogSeedPlan.PagesPerRunKey] = pageBudget.ToString(CultureInfo.InvariantCulture),
                [CatalogSeedPlan.MaxTitlesKey] = maxTitles?.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

    /// <summary>
    /// The catalogue is deliberately global and shared with every other class in this collection, so
    /// seed jobs left behind by a sibling test would be read as this test's own in-flight run.
    /// </summary>
    private static async Task ClearSeedJobsAsync(Infrastructure.Persistence.ReelDbContext db)
    {
        db.SyncJobs.RemoveRange(await db.SyncJobs.Where(j => j.Kind == JobKind.SeedCatalog).ToListAsync());
        await db.SaveChangesAsync();
    }

    private static async Task ClearGlobalHydrationJobsAsync(Infrastructure.Persistence.ReelDbContext db)
    {
        db.SyncJobs.RemoveRange(await db.SyncJobs
            .Where(j => j.Kind == JobKind.HydrateCatalog && j.AccountId == null)
            .ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A TMDB discover that serves a fixed number of pages per partition, with distinct ids per
    /// (medium, year, page, slot) so an upsert can be told apart from a duplicate. It records every
    /// query it was asked, which is the only way to assert that the floors and the year window were
    /// SENT rather than merely computed.
    /// </summary>
    private sealed class ScriptedSeedTmdb(int pagesPerPartition, int itemsPerPage, long? seed = null) : ITmdbClient
    {
        // A per-instance id base keeps this class's rows out of every other class's assertions in a
        // shared database; passing a previous instance's Seed re-issues the identical corpus.
        public long Seed { get; } = seed ?? (900_000_000L + (Random.Shared.NextInt64(0, 80_000) * 1_000_000L));

        public List<(string Media, int Year, int Page)> Fetched { get; } = [];

        public List<(bool Movies, int Year, int MinVoteCount)> Queries { get; } = [];

        public List<long> IssuedTmdbIds { get; } = [];

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(
            bool movies, int year, int minVoteCount, int page, CancellationToken ct = default)
        {
            Fetched.Add((movies ? CatalogSeedPlan.MovieMedia : CatalogSeedPlan.ShowMedia, year, page));
            Queries.Add((movies, year, minVoteCount));

            if (page > pagesPerPartition)
            {
                return Task.FromResult(new TmdbDiscoverPage([], page, pagesPerPartition, 0));
            }

            var items = new List<TmdbListItem>();
            for (var i = 0; i < itemsPerPage; i++)
            {
                var id = Seed
                    + (movies ? 0 : 500_000)
                    + ((year - CatalogSeedPlan.FirstYear) * 10_000L)
                    + (page * 100L)
                    + i;
                IssuedTmdbIds.Add(id);
                items.Add(new TmdbListItem(
                    id,
                    movies,
                    string.Create(CultureInfo.InvariantCulture, $"Seeded {id}"),
                    "/p.jpg",
                    "/b.jpg",
                    "An overview.",
                    12.5m,
                    7.1m,
                    minVoteCount + i,
                    new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    [],
                    "en"));
            }

            return Task.FromResult(new TmdbDiscoverPage(
                items, page, pagesPerPartition, pagesPerPartition * itemsPerPage));
        }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
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
}
