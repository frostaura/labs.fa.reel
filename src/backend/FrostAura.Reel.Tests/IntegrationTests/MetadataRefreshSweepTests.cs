using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
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
/// WS-4 task 9 (E-11, FD-9): hydrated catalogue rows are fetched from TMDB again — recent releases
/// weekly, everything else inside TMDB's six-month cache limit — by the same global job and inside
/// the same daily ceiling as first hydration. Before, every caller selected
/// <c>LastMetadataRefreshAt == null</c> and a row was frozen at its first fetch.
/// </summary>
[Collection("postgres")]
public class MetadataRefreshSweepTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_tiers_select_recent_releases_weekly_and_everything_else_before_the_cache_limit()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var recentDue = await SeedAsync(refreshedAgo: TimeSpan.FromDays(8), releasedAgo: TimeSpan.FromDays(30));
        var recentFresh = await SeedAsync(refreshedAgo: TimeSpan.FromDays(2), releasedAgo: TimeSpan.FromDays(30));
        var upcoming = await SeedAsync(refreshedAgo: TimeSpan.FromDays(8), releasedAgo: TimeSpan.FromDays(-60));
        var olderFresh = await SeedAsync(refreshedAgo: TimeSpan.FromDays(100), releasedAgo: TimeSpan.FromDays(4000));
        var olderOverdue = await SeedAsync(refreshedAgo: TimeSpan.FromDays(151), releasedAgo: TimeSpan.FromDays(4000));
        var neverHydrated = await SeedAsync(refreshedAgo: null, releasedAgo: TimeSpan.FromDays(30));
        var ids = new[] { recentDue, recentFresh, upcoming, olderFresh, olderOverdue, neverHydrated }.Select(t => t.Id).ToList();

        await using var db = fixture.CreateContext(new AccountContext());
        try
        {
            async Task<HashSet<Guid>> In(IQueryable<Title> q) => (await q.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();

            Assert.Equal([olderOverdue.Id], await In(MetadataRefreshPolicy.Overdue(db, now)));
            Assert.Equal(new HashSet<Guid> { recentDue.Id, upcoming.Id }, await In(MetadataRefreshPolicy.RecentDue(db, now)));
            Assert.Equal(new HashSet<Guid> { recentDue.Id, upcoming.Id, olderOverdue.Id }, await In(MetadataRefreshPolicy.Due(db, now)));
        }
        finally
        {
            // Due rows left in the shared catalogue would be refreshed by every other test's global sweep.
            await db.Titles.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_global_sweep_spends_the_day_on_the_oldest_overdue_row_first_and_refreshes_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Last refreshed in 1980: older than anything else in the shared test catalogue, so it heads
        // the overdue tier. A never-hydrated row with the highest vote count waits behind it.
        var overdue = await SeedAsync(refreshedAt: new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc), releasedAgo: TimeSpan.FromDays(9000), votes: 5);
        var backlog = await SeedAsync(refreshedAgo: null, releasedAgo: TimeSpan.FromDays(30), votes: int.MaxValue);
        var started = DateTime.UtcNow;
        try
        {
            await SweepOneAsync(overdue);
        }
        finally
        {
            // The sweep queues a global enrich after a hydration; left pending, it would answer the
            // next test's "is one in flight?" for it.
            await using var cleanup = fixture.CreateContext(new AccountContext());
            await cleanup.Titles.Where(t => t.Id == overdue.Id || t.Id == backlog.Id).ExecuteDeleteAsync();
            await cleanup.SyncJobs
                .Where(j => j.AccountId == null && j.Kind == JobKind.EnrichCatalog && j.Status == JobStatus.Pending && j.EnqueuedAt >= started)
                .ExecuteDeleteAsync();
        }
    }

    private async Task SweepOneAsync(Title overdue)
    {
        var tmdb = DetailsScript.Create(out var script);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // A ceiling with room for exactly one title today, whatever else the shared database did.
            var used = (await new CatalogHydrationBudget(db, null).AllowanceAsync(DateTime.UtcNow, CancellationToken.None)).Used;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [CatalogHydrationBudget.DailyTitlesKey] = (used + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) })
                .Build();
            var handler = new HydrateCatalogJobHandler(
                db,
                new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
                new NoopEventHub(),
                new CatalogHydrationBudget(db, configuration),
                configuration,
                NullLogger<HydrateCatalogJobHandler>.Instance);
            await handler.ExecuteAsync(new SyncJob { Id = Guid.NewGuid(), AccountId = null, Kind = JobKind.HydrateCatalog }, CancellationToken.None);
        }

        Assert.Equal([overdue.TmdbId!.Value], script.Calls);

        await using var check = fixture.CreateContext(new AccountContext());
        var row = await check.Titles.AsNoTracking().SingleAsync(t => t.Id == overdue.Id);
        Assert.True(row.LastMetadataRefreshAt > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(4321, row.TmdbVoteCount);
        Assert.Contains(TvMovieGenre.Slug, row.Genres);
    }

    private async Task<Title> SeedAsync(
        TimeSpan? refreshedAgo = null, TimeSpan? releasedAgo = null, int votes = 10, DateTime? refreshedAt = null)
    {
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
            TraktSlug = $"refresh-{Guid.NewGuid():N}",
            Name = $"Refresh Fixture {Guid.NewGuid():N}",
            Genres = ["drama"],
            TmdbVoteCount = votes,
            ReleasedAt = releasedAgo is { } released ? now - released : null,
            LastMetadataRefreshAt = refreshedAt ?? (refreshedAgo is { } ago ? now - ago : null),
            CreatedAt = now,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Answers movie details with a TV-movie payload and records which titles were fetched; refuses anything else.</summary>
    public class DetailsScript : DispatchProxy
    {
        public ConcurrentQueue<long> Calls { get; } = [];

        public static ITmdbClient Create(out DetailsScript script)
        {
            var proxy = Create<ITmdbClient, DetailsScript>();
            script = (DetailsScript)(object)proxy;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ITmdbClient.GetMovieAsync))
            {
                throw new NotSupportedException($"The sweep called TMDB {targetMethod?.Name}.");
            }

            var id = (long)args![0]!;
            Calls.Enqueue(id);
            return Task.FromResult<TmdbTitleDetails?>(new TmdbTitleDetails(
                id, null, null, 12.5m, 6.4m, 4321, 95, null, null, null, [], [], null, [],
                GenreIds: [18, TvMovieGenre.TmdbMovieGenreId]));
        }
    }
}
