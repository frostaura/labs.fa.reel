using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HydrateCatalog on real Postgres — the end of the chain that stranded 1,096 of 3,946 titles
/// (28%) at LastMetadataRefreshAt == null, which also excluded every one of them from
/// EnrichCatalog (it scopes to hydrated titles) and therefore from embeddings and training.
///
/// Two independent defects produced that number and both are pinned here:
///   * one failing title threw out of a 25-title batch before the single SaveChangesAsync, so the
///     batch's healthy work was discarded and the retry hit the identical bad title; and
///   * the cursor was walked forward exactly once, so anything sorting BELOW it — a title that
///     failed, one a concurrent sync inserted, or on a resumed job the whole library — was
///     unreachable while the job still reported 100% and Succeeded.
/// </summary>
[Collection("postgres")]
public class HydrateCatalogResilienceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task One_poisoned_title_no_longer_costs_its_batch_their_persisted_hydration()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Two full save-batches plus change, so a batch-level abort is clearly visible.
        var (accountId, titleIds) = await SeedLibraryAsync(30);
        var poisoned = await TmdbIdOfAsync(titleIds[7]);

        await RunAsync(accountId, new ScriptedTmdb(alwaysFailFor: [poisoned]));

        await using var db = fixture.CreateContext(new AccountContext());
        var titles = await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync();

        // 29 hydrated and durable. Previously: zero, because the batch never reached SaveChanges.
        Assert.Equal(29, titles.Count(t => t.LastMetadataRefreshAt is not null && t.PosterPath is not null));
        Assert.All(titles.Where(t => t.PosterPath is not null), t => Assert.Equal(101, t.RuntimeMinutes));
    }

    [Fact]
    public async Task A_permanently_failing_title_is_retired_rather_than_retried_for_ever()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(6);
        var poisoned = await TmdbIdOfAsync(titleIds[2]);
        var tmdb = new ScriptedTmdb(alwaysFailFor: [poisoned]);

        await RunAsync(accountId, tmdb);

        await using var db = fixture.CreateContext(new AccountContext());
        var titles = await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync();

        // Everything is resolved in one run: five hydrated, the poison pill retired after its
        // attempt budget so it stops blocking the cursor AND becomes visible to enrichment.
        Assert.All(titles, t => Assert.NotNull(t.LastMetadataRefreshAt));
        Assert.Equal(5, titles.Count(t => t.PosterPath is not null));
        Assert.Null(titles.Single(t => t.Id == titleIds[2]).PosterPath);

        // Exactly the attempt budget — not one call more, not an unbounded retry loop.
        Assert.Equal(3, tmdb.CallsFor(poisoned));
    }

    [Fact]
    public async Task A_resumed_cursor_past_the_end_still_hydrates_the_library_behind_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        // This is the shape the live job actually died in: it resumed from a persisted cursor,
        // walked forward to exhaustion, and reported "hydrated 887/1859 titles" at 100% Succeeded
        // while 972 titles sat below the cursor, untouched, for ever.
        var (accountId, titleIds) = await SeedLibraryAsync(5);
        var cursorPastEverything = JsonSerializer.Serialize(new { LastTitleId = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"), Pass = 1 });

        await RunAsync(accountId, new ScriptedTmdb(alwaysFailFor: []), cursorJson: cursorPastEverything);

        await using var db = fixture.CreateContext(new AccountContext());
        var titles = await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync();
        Assert.All(titles, t => Assert.NotNull(t.LastMetadataRefreshAt));
        Assert.Equal(5, titles.Count(t => t.PosterPath is not null));
    }

    [Fact]
    public async Task A_wholesale_outage_fails_the_job_instead_of_retiring_the_library()
    {
        if (!fixture.Available)
        {
            return;
        }

        // 50+ titles failing together is TMDB being down, not 50 poison pills. Retiring them would
        // permanently mark the library "hydrated" with no metadata; failing is recoverable.
        var (accountId, titleIds) = await SeedLibraryAsync(60);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(accountId, new ScriptedTmdb(alwaysFailFor: [], failEverything: true)));

        await using var db = fixture.CreateContext(new AccountContext());
        var titles = await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync();
        Assert.All(titles, t => Assert.Null(t.LastMetadataRefreshAt));
    }

    private async Task RunAsync(Guid accountId, ScriptedTmdb tmdb, string? cursorJson = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var handler = new HydrateCatalogJobHandler(
            db,
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new NoopHub(),
            NullLogger<HydrateCatalogJobHandler>.Instance);

        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.HydrateCatalog,
            Status = JobStatus.Running,
            EnqueuedAt = DateTime.UtcNow,
            CursorJson = cursorJson,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();

        try
        {
            await handler.ExecuteAsync(job, CancellationToken.None);
        }
        finally
        {
            // The runner owns status transitions; mirror it so the in-flight unique index releases.
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private async Task<long> TmdbIdOfAsync(Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return (await db.Titles.SingleAsync(t => t.Id == titleId)).TmdbId!.Value;
    }

    private async Task<(Guid AccountId, List<Guid> TitleIds)> SeedLibraryAsync(int count)
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

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TmdbId = Random.Shared.NextInt64(1, int.MaxValue),
                TraktSlug = $"hydrate-{i}-{Guid.NewGuid():N}",
                Name = $"Unhydrated Title {i}",
                LastMetadataRefreshAt = null, // the state 1,096 live titles are stuck in
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                Plays = 1,
                LastWatchedAt = DateTime.UtcNow,
                IsFullyWatched = true,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return (account.Id, ids);
    }

    private sealed class NoopHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data)
        {
        }

        public IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(Guid accountId, long? lastEventId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Serves credit-free details, throwing for the nominated TMDB ids and counting calls.</summary>
    private sealed class ScriptedTmdb(long[] alwaysFailFor, bool failEverything = false) : ITmdbClient
    {
        private readonly Dictionary<long, int> _calls = [];

        public int CallsFor(long tmdbId) => _calls.GetValueOrDefault(tmdbId);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
        {
            _calls[tmdbId] = _calls.GetValueOrDefault(tmdbId) + 1;
            if (failEverything || alwaysFailFor.Contains(tmdbId))
            {
                throw new HttpRequestException("500 Internal Server Error");
            }

            return Task.FromResult<TmdbTitleDetails?>(new TmdbTitleDetails(
                tmdbId, "/poster.jpg", "/backdrop.jpg", 11m, 7m, 500, 101,
                "An overview.", null, null, [], [], null, [], "en"));
        }

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => GetMovieAsync(tmdbId, ct);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
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
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
