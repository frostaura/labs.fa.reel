using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Saved;
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
/// <para>
/// It also revokes the consumed SAVE in that same transaction (see <see cref="SaveConsumption"/>).
/// Closing only the list row is what left 116 watched-and-rated titles on the Saved shelf: the list
/// promise was kept and the shelf's was not. A rated title counts as consumed here exactly as it
/// does on the shelf — the in-app gesture is "seen it + rate", and a Trakt rating is the same fact.
/// </para>
/// </summary>
public sealed class ManagedListReconciler(IReelDbContext db)
{
    /// <summary>
    /// Marks every not-yet-removed managed-list item the account has since fully watched or rated as
    /// Removed (reason <see cref="ListRemovalReason.Watched"/>), enqueues the Trakt ListRemove
    /// write-back, and revokes every consumed live save (revoked, never deleted).
    /// Returns the ids of the titles removed — not a count: this is the product's only genuine
    /// "a recommendation was consumed" event, and callers need to know WHICH titles converted, not
    /// how many. Does not call SaveChanges — the caller commits it in its own transaction alongside
    /// the sync/reconcile that surfaced the watch.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RemoveWatchedAsync(Guid accountId, DateTime now, CancellationToken ct)
    {
        var watchedInList = await (
            from m in db.ManagedListItems
            join t in db.Titles on m.TitleId equals t.Id
            where m.AccountId == accountId && m.RemovedAt == null
                && (db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == m.TitleId && w.IsFullyWatched)
                    || db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == m.TitleId))
            select new { Item = m, Title = t }).ToListAsync(ct);

        // Same transaction, same rule: the shelf and the list leave together or not at all.
        await SaveConsumption.RevokeConsumedAsync(db, accountId, now, ct);

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

        return watchedInList.Select(x => x.Title.Id).ToList();
    }
}
