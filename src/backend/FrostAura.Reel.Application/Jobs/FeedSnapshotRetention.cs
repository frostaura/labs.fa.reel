using FrostAura.Reel.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Feed history retention: one account keeps its newest <see cref="KeepSnapshots"/> snapshots
/// and everything older is hard-deleted, items first.
///
/// Extracted from BuildFeedJobHandler so the policy is testable on its own — behaviour is
/// unchanged. Two properties are load-bearing and are what the retention test pins:
/// the prune is ACCOUNT-SCOPED (a rebuild for one account must never evict another's history),
/// and FeedItems go with their snapshot rather than being orphaned.
///
/// Note for anyone counting rows: the build calls this while the new snapshot is still only
/// tracked, not saved, so the query cannot see it — a build therefore ends with the 7 kept rows
/// plus the new one. That is the existing behaviour and it is deliberate here; changing the
/// depth is a product decision, not a refactor.
/// </summary>
public static class FeedSnapshotRetention
{
    /// <summary>How many of an account's most recent feed snapshots survive a build.</summary>
    public const int KeepSnapshots = 7;

    /// <summary>
    /// Deletes everything older than the newest <see cref="KeepSnapshots"/> snapshots for
    /// <paramref name="accountId"/>, items before snapshots. Returns how many snapshots went.
    /// </summary>
    public static async Task<int> PruneAsync(IReelDbContext db, Guid accountId, CancellationToken ct)
    {
        var staleIds = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.GeneratedAt)
            .Skip(KeepSnapshots)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (staleIds.Count == 0)
        {
            return 0;
        }

        await db.FeedItems.Where(i => staleIds.Contains(i.FeedSnapshotId)).ExecuteDeleteAsync(ct);
        await db.FeedSnapshots.Where(s => staleIds.Contains(s.Id)).ExecuteDeleteAsync(ct);

        return staleIds.Count;
    }
}
