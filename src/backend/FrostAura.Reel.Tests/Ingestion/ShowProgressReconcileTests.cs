using System.Runtime.CompilerServices;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// Regression for the additive-ingest gap: /sync/watched/shows is a full snapshot, so a show that
/// vanishes from it had its history removed on Trakt and must lose its in-progress row — otherwise
/// it is stuck in Continue Watching forever. ShowWatchProgress is purely ingest-derived (never
/// written by a local action), so reconciling against the snapshot is safe.
/// </summary>
[Collection("postgres")]
public class ShowProgressReconcileTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Show_removed_from_trakt_history_loses_its_continue_watching_progress()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"recon-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        var kept = new Title { Id = Guid.NewGuid(), MediaType = MediaType.Show, TraktId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Kept Show", AiredEpisodes = 10, CreatedAt = DateTime.UtcNow };
        var removed = new Title { Id = Guid.NewGuid(), MediaType = MediaType.Show, TraktId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Removed Show", AiredEpisodes = 10, CreatedAt = DateTime.UtcNow };

        var ctx = new AccountContext();
        ctx.SetAccount(account.Id);

        // Prior state: both shows in progress, as an earlier sync would have left them.
        await using (var db = fixture.CreateContext(ctx))
        {
            db.Add(account);
            db.AddRange(kept, removed);
            db.ShowWatchProgresses.AddRange(InProgress(account.Id, kept.Id), InProgress(account.Id, removed.Id));
            await db.SaveChangesAsync();
        }

        // Trakt now reports ONLY the kept show — the user cleared the removed show's history.
        var trakt = new StubTraktClient
        {
            WatchedShows = [WatchedShow(kept.TraktId!.Value, "Kept Show", airedEpisodes: 10, watchedEpisodes: 3)],
        };

        await using (var db = fixture.CreateContext(ctx))
        {
            var ingestor = new TraktLibraryIngestor(
                db, trakt, new NoopEventHub(),
                new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())),
                NullLogger<TraktLibraryIngestor>.Instance);
            await ingestor.IngestShowsAsync(account.Id, "token", RatePriority.DeltaPoll, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var verify = fixture.CreateContext(ctx))
        {
            Assert.True(
                await verify.ShowWatchProgresses.AnyAsync(p => p.TitleId == kept.Id),
                "a show still in Trakt history keeps its Continue Watching progress");
            Assert.False(
                await verify.ShowWatchProgresses.AnyAsync(p => p.TitleId == removed.Id),
                "a show removed from Trakt history must leave Continue Watching");
        }
    }

    private static ShowWatchProgress InProgress(Guid accountId, Guid titleId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        WatchedEpisodeCount = 3,
        TotalAiredEpisodes = 10,
        CompletionPct = 0.3m,
        LastWatchedAt = DateTime.UtcNow.AddDays(-2),
        ResumeLikelihood = 0.5m,
        UpdatedAt = DateTime.UtcNow.AddDays(-2),
    };

    private static TraktWatchedShow WatchedShow(long traktId, string name, int airedEpisodes, int watchedEpisodes)
    {
        var show = new TraktShow(
            name, 2024, new TraktIds(traktId, null, null, null), null, null, null,
            null, null, "returning series", null, 0, null, null, null, null, null, null, airedEpisodes);
        var episodes = Enumerable.Range(1, watchedEpisodes)
            .Select(n => new TraktWatchedEpisode(n, 1, DateTime.UtcNow.AddDays(-2)))
            .ToArray();
        return new TraktWatchedShow(watchedEpisodes, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-2), null,
            show, [new TraktWatchedSeason(1, episodes)]);
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

}
