using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Saved;

/// <summary>
/// Where one saved card stands against the managed Trakt list ("Reel — Up Next").
///
/// <para><b>Why a third state.</b> The card used to render "syncing…" whenever the title was not on
/// the list — which read "not on the list" as "queued to sync". On the founder's shelf that put the
/// label on 116 cards with no outbox row pending at all; nothing was ever going to sync them (G-3).
/// "Syncing" is now said only when a <see cref="OutboxKind.ListAdd"/> write-back is genuinely still
/// waiting to reach Trakt, so the number of labels equals the number of pending rows by
/// construction.</para>
/// </summary>
public static class SavedListSync
{
    /// <summary>A ListAdd write-back for this title is still waiting (Pending, or Failed and retrying).</summary>
    public const string Pending = "pending";

    /// <summary>The title is on the managed list and nothing is waiting.</summary>
    public const string OnList = "onList";

    /// <summary>
    /// Not on the managed list and nothing is waiting — the user took it off on Trakt, or the
    /// write-back gave up (dead-lettered). Honest either way; never "syncing".
    /// </summary>
    public const string OffList = "offList";

    /// <summary>
    /// The state of each title, from the account's live managed-list rows and its still-pending
    /// ListAdd outbox rows. A pending write wins over the list row, because the local list row is
    /// written in the same transaction as the save and so says "should be on the list", not "is".
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ForAsync(
        IReelDbContext db, Guid accountId, IReadOnlyCollection<Guid> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var onList = (await db.ManagedListItems
                .Where(m => m.AccountId == accountId && m.RemovedAt == null && titleIds.Contains(m.TitleId))
                .Select(m => m.TitleId)
                .ToListAsync(ct))
            .ToHashSet();

        // Outbox payloads are jsonb fragments keyed by title inside the document, so the title is
        // read back in C#. The set is small by construction: a row leaves Pending/Failed within
        // seconds of a healthy dispatch, and dead letters are excluded.
        var pendingPayloads = await db.TraktOutbox
            .Where(o => o.AccountId == accountId
                && o.Kind == OutboxKind.ListAdd
                && (o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed))
            .Select(o => o.PayloadJson)
            .ToListAsync(ct);
        var pending = pendingPayloads
            .Select(TitleOf)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        return titleIds.Distinct().ToDictionary(
            id => id,
            id => pending.Contains(id) ? Pending : onList.Contains(id) ? OnList : OffList);
    }

    private static Guid? TitleOf(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<OutboxDispatcher.OutboxPayload>(payloadJson)?.TitleId;
        }
        catch (JsonException)
        {
            // A payload this build cannot read is not a pending write it can describe.
            return null;
        }
    }
}
