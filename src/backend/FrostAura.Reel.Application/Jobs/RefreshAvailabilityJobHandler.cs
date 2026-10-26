using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Proactively caches watch-provider availability for the most-prominent feed titles in the
/// account's region, so the feed's streaming strips populate without a per-render TMDB call.
/// Bounded + 24h-TTL + global-shared (TitleAvailability is catalog-wide). Chained from BuildFeed.
/// </summary>
public sealed class RefreshAvailabilityJobHandler(
    IReelDbContext db, ITmdbClient tmdb, ILogger<RefreshAvailabilityJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.RefreshAvailability;

    private const int MaxTitles = 24;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("RefreshAvailability requires an account.");
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var region = account.Region;
        if (string.IsNullOrWhiteSpace(region))
        {
            return;
        }

        var snapshot = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null)
        {
            return;
        }

        var titles = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id)
            .OrderBy(i => i.Row).ThenBy(i => i.Rank)
            .Join(db.Titles, i => i.TitleId, t => t.Id, (i, t) => t)
            .Where(t => t.TmdbId != null)
            .Take(MaxTitles)
            .ToListAsync(ct);

        var cutoff = DateTime.UtcNow.Subtract(Ttl);
        var refreshed = 0;
        foreach (var title in titles)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var fresh = await db.TitleAvailabilities
                .AnyAsync(a => a.TitleId == title.Id && a.Region == region && a.FetchedAt > cutoff, ct);
            if (fresh)
            {
                continue;
            }

            var providers = await tmdb.GetWatchProvidersAsync(title.TmdbId!.Value, title.MediaType == MediaType.Movie, region, ct);

            // Through the one snapshot writer, so a refetch carries each offer's FirstSeenAt rather
            // than making every offer look like it arrived today.
            await AvailabilitySnapshot.ReplaceAsync(db, title.Id, region, providers, DateTime.UtcNow, ct);
            refreshed++;
        }

        logger.LogInformation("RefreshAvailability: {Count} title(s) refreshed for {Account} ({Region}).", refreshed, accountId, region);
    }
}
