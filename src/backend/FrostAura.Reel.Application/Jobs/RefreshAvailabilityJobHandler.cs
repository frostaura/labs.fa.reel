using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Proactively caches watch-provider availability in the account's region for the two places that
/// say "streaming now" without a title page open: up to <see cref="SavedAvailabilityRefresh.MaxSaves"/>
/// stale live saves (stalest first), then the most-prominent titles of the active feed. Bounded,
/// 24h-TTL and global-shared (TitleAvailability is catalogue-wide). Chained from BuildFeed, and
/// queued by opening Saved (<see cref="SavedAvailabilityRefresh.RequestAsync"/>).
/// </summary>
public sealed class RefreshAvailabilityJobHandler(
    IReelDbContext db, ITmdbClient tmdb, ILogger<RefreshAvailabilityJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.RefreshAvailability;

    private const int MaxFeedTitles = 24;
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

        // Saves first: the feed's titles are re-chained by every build, while a save has no other
        // route to fresh availability, and Saved is where "streaming now" decides the evening.
        var saves = await SavedAvailabilityRefresh.StaleSavesAsync(
            db, accountId, region, DateTime.UtcNow, SavedAvailabilityRefresh.MaxSaves, ct);

        var snapshot = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .FirstOrDefaultAsync(ct);
        List<Title> feedTitles = snapshot is null
            ? []
            : await db.FeedItems
                .Where(i => i.FeedSnapshotId == snapshot.Id)
                .OrderBy(i => i.Row).ThenBy(i => i.Rank)
                .Join(db.Titles, i => i.TitleId, t => t.Id, (i, t) => t)
                .Where(t => t.TmdbId != null)
                .Take(MaxFeedTitles)
                .ToListAsync(ct);

        var savedRefreshed = await RefreshAsync(saves, region, ct);
        var feedRefreshed = await RefreshAsync(feedTitles.Where(t => saves.All(s => s.Id != t.Id)).ToList(), region, ct);

        logger.LogInformation(
            "RefreshAvailability: {Saved} save(s) and {Feed} feed title(s) refreshed for {Account} ({Region}).",
            savedRefreshed, feedRefreshed, accountId, region);
    }

    private async Task<int> RefreshAsync(IReadOnlyList<Title> titles, string region, CancellationToken ct)
    {
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

        return refreshed;
    }
}
