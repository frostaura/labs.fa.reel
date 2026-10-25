using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Saved;

/// <summary>
/// The single definition of a CONSUMED save: a live <see cref="ReactionKind.SaveForLater"/> whose
/// title the account has fully watched, or rated, <b>at or after the moment it was saved</b>.
///
/// <para><b>Why it exists.</b> Saved used to list every save ever made that had not been undone. On
/// the founder's database 116 of 150 live saves were titles he had already watched and rated — the
/// shelf meant to answer "what do I put on tonight" led with films he had scored 8 and 9 (G-1). The
/// watch path and the reconciler closed the managed-list row and left the reaction itself live, and
/// the queue read had no footprint filter at all.</para>
///
/// <para><b>Why "after the save", not "ever".</b> A save is the user saying "I want to watch this".
/// Saving a film rated years ago is a deliberate rewatch, and a rule that ignored time made that
/// save a ghost: the endpoint said "saved", queued ListAdd and AddToWatchlist to Trakt, and the
/// shelf never showed it — until the next reconcile revoked it and queued the ListRemove (review
/// D1). Every one of the founder's 116 consumed saves was watched and rated AFTER it was saved, so
/// the time-aware rule loses none of them. The watch time is <c>LastWatchedAt</c>, falling back to
/// the row's <c>UpdatedAt</c> when Trakt reported no time; the rating time is <c>RatedAt</c>, which a
/// re-rate moves forward — a re-rate after a rewatch save is a consumption too.</para>
///
/// <para><b>Four places apply it, deliberately the same rule.</b> The queue read filters on it, so
/// a consumed save never renders even before anything has revoked it; the reconciler and the in-app
/// rating path revoke it at write time, in the caller's own transaction; and the boot repair
/// revokes the historical backlog. A consumed save is <b>revoked, never deleted</b> — the reaction
/// history is the record of what the user asked for, and the conversion funnel reads it.</para>
///
/// <para><b>"Any rating" is intentional and matches eligibility.</b> The locked zero-footprint rule
/// treats any rating row on a title — movie, show, season or episode — as footprint, so a rated
/// title is already ineligible on every suggestion surface. Saved holds the user's own picks rather
/// than suggestions, but a rating given after the save means the user has now seen it.</para>
///
/// <para><b>Trakt mirrors it.</b> A live revocation queues <see cref="OutboxKind.RemoveFromWatchlist"/>,
/// exactly as un-saving does: the save put the title on the watchlist, so consuming it takes it off.
/// Trakt drops a watched movie from the watchlist on its own; a rated-but-unwatched title it does
/// not, which is the case this write exists for (review D8).</para>
/// </summary>
public static class SaveConsumption
{
    /// <summary>Every live (unrevoked) save the account holds, consumed or not.</summary>
    public static IQueryable<UserTitleReaction> LiveSaves(IReelDbContext db, Guid accountId) =>
        db.UserTitleReactions.Where(r =>
            r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null);

    /// <summary>Live saves the account has not fully watched or rated since saving — the queue.</summary>
    public static IQueryable<UserTitleReaction> Unconsumed(
        this IQueryable<UserTitleReaction> saves, IReelDbContext db) =>
        saves.Where(r =>
            !db.WatchedTitles.Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched
                && (w.LastWatchedAt ?? w.UpdatedAt) >= r.CreatedAt)
            && !db.UserRatings.Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId
                && u.RatedAt >= r.CreatedAt));

    /// <summary>Live saves whose title the account has fully watched or rated since saving it.</summary>
    public static IQueryable<UserTitleReaction> Consumed(
        this IQueryable<UserTitleReaction> saves, IReelDbContext db) =>
        saves.Where(r =>
            db.WatchedTitles.Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched
                && (w.LastWatchedAt ?? w.UpdatedAt) >= r.CreatedAt)
            || db.UserRatings.Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId
                && u.RatedAt >= r.CreatedAt));

    /// <summary>
    /// Revokes every consumed live save the account holds, on the TRACKED context, and queues the
    /// Trakt watchlist removal for each. Does not save: the caller commits it in the same
    /// transaction as the watch or rating that consumed it, so a sync that lands a watch and the
    /// revocation it implies can never be half-applied. Returns the title ids revoked.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RevokeConsumedAsync(
        IReelDbContext db, Guid accountId, DateTime now, CancellationToken ct)
    {
        var consumed = await LiveSaves(db, accountId)
            .Consumed(db)
            .Join(db.Titles, r => r.TitleId, t => t.Id, (r, t) => new { Save = r, Title = t })
            .ToListAsync(ct);
        foreach (var x in consumed)
        {
            x.Save.RevokedAt = now;
        }

        foreach (var title in consumed.Select(x => x.Title).DistinctBy(t => t.Id))
        {
            EnqueueWatchlistRemoval(db, accountId, title, now);
        }

        return consumed.Select(x => x.Title.Id).Distinct().ToList();
    }

    /// <summary>
    /// Revokes one title's live save, if the account holds one, on the tracked context, and queues
    /// its Trakt watchlist removal. For the in-app rating path, which already knows the title has
    /// just been consumed. Does not save. Returns whether a save was revoked.
    /// </summary>
    public static async Task<bool> RevokeAsync(
        IReelDbContext db, Guid accountId, Title title, DateTime now, CancellationToken ct)
    {
        var saves = await LiveSaves(db, accountId).Where(r => r.TitleId == title.Id).ToListAsync(ct);
        foreach (var save in saves)
        {
            save.RevokedAt = now;
        }

        if (saves.Count > 0)
        {
            EnqueueWatchlistRemoval(db, accountId, title, now);
        }

        return saves.Count > 0;
    }

    private static void EnqueueWatchlistRemoval(IReelDbContext db, Guid accountId, Title title, DateTime now) =>
        db.TraktOutbox.Add(new TraktOutboxEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = OutboxKind.RemoveFromWatchlist,
            PayloadJson = JsonSerializer.Serialize(new OutboxDispatcher.OutboxPayload(
                title.Id, title.MediaType, title.TmdbId, title.TraktId, null, null)),
            EnqueuedAt = now,
            NextAttemptAt = now,
        });
}
