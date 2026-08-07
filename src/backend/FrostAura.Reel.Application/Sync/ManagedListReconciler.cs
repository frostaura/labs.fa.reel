using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// Managed-list ("Reel — Up Next") hygiene: anything in the live queue that has since been detected
/// fully watched leaves the list — the stated product promise ("a clean live queue"). This is the
/// single implementation of that rule, shared by the reactive DeltaSync completion path (so a watch
/// pulled from Trakt drops out within minutes) and the nightly reconcile backstop (which still runs
/// it as a drift repair). Side-effect-only: it marks the local removal and enqueues the Trakt
/// <see cref="OutboxKind.ListRemove"/> write-back on the tracked context but never calls SaveChanges,
/// so it composes inside whatever transaction the caller already owns.
/// </summary>
public sealed class ManagedListReconciler(IReelDbContext db)
{
    /// <summary>
    /// Marks every not-yet-removed managed-list item the account has since fully watched as Removed
    /// (reason <see cref="ListRemovalReason.Watched"/>) and enqueues the Trakt ListRemove write-back.
    /// Returns the number of items removed. Does not call SaveChanges — the caller commits it in its
    /// own transaction alongside the sync/reconcile that surfaced the watch.
    /// </summary>
    public async Task<int> RemoveWatchedAsync(Guid accountId, DateTime now, CancellationToken ct)
    {
        var watchedInList = await (
            from m in db.ManagedListItems
            join t in db.Titles on m.TitleId equals t.Id
            where m.AccountId == accountId && m.RemovedAt == null
                && db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == m.TitleId && w.IsFullyWatched)
            select new { Item = m, Title = t }).ToListAsync(ct);

        foreach (var x in watchedInList)
        {
            x.Item.RemovedAt = now;
            x.Item.RemovalReason = ListRemovalReason.Watched;
            db.TraktOutbox.Add(new TraktOutboxEntry
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = OutboxKind.ListRemove,
                PayloadJson = JsonSerializer.Serialize(new OutboxDispatcher.OutboxPayload(
                    x.Title.Id, x.Title.MediaType, x.Title.TmdbId, x.Title.TraktId, null, null)),
                EnqueuedAt = now,
                NextAttemptAt = now,
            });
        }

        return watchedInList.Count;
    }
}
