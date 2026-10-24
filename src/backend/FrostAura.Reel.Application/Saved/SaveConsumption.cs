using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Saved;

/// <summary>
/// The single definition of a CONSUMED save: a live <see cref="ReactionKind.SaveForLater"/> whose
/// title the account has since fully watched, or carries any rating for.
///
/// <para><b>Why it exists.</b> Saved used to list every save ever made that had not been undone. On
/// the founder's database 116 of 150 live saves were titles he had already watched and rated — the
/// shelf meant to answer "what do I put on tonight" led with films he had scored 8 and 9 (G-1). The
/// watch path and the reconciler closed the managed-list row and left the reaction itself live, and
/// the queue read had no footprint filter at all.</para>
///
/// <para><b>Three places apply it, deliberately the same rule.</b> The queue read filters on it, so
/// a consumed save never renders even before anything has revoked it; the reconciler and the in-app
/// rating path revoke it at write time, in the caller's own transaction; and the one-off repair
/// revokes the historical backlog. A consumed save is <b>revoked, never deleted</b> — the reaction
/// history is the record of what the user asked for, and the conversion funnel reads it.</para>
///
/// <para><b>"Any rating" is intentional and matches eligibility.</b> The locked zero-footprint rule
/// treats any rating row on a title — movie, show, season or episode — as footprint, so a rated
/// title is already ineligible on every suggestion surface. Saved holds the user's own picks rather
/// than suggestions, but a title the user has rated is one they have seen, and "seen it" is exactly
/// what removes a title from the live queue.</para>
/// </summary>
public static class SaveConsumption
{
    /// <summary>Every live (unrevoked) save the account holds, consumed or not.</summary>
    public static IQueryable<UserTitleReaction> LiveSaves(IReelDbContext db, Guid accountId) =>
        db.UserTitleReactions.Where(r =>
            r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null);

    /// <summary>Live saves whose title the account has not yet fully watched or rated — the queue.</summary>
    public static IQueryable<UserTitleReaction> Unconsumed(
        this IQueryable<UserTitleReaction> saves, IReelDbContext db) =>
        saves.Where(r =>
            !db.WatchedTitles.Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched)
            && !db.UserRatings.Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId));

    /// <summary>Live saves whose title the account has since fully watched or rated.</summary>
    public static IQueryable<UserTitleReaction> Consumed(
        this IQueryable<UserTitleReaction> saves, IReelDbContext db) =>
        saves.Where(r =>
            db.WatchedTitles.Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched)
            || db.UserRatings.Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId));

    /// <summary>
    /// Revokes every consumed live save the account holds, on the TRACKED context. Does not save:
    /// the caller commits it in the same transaction as the watch or rating that consumed it, so a
    /// sync that lands a watch and the revocation it implies can never be half-applied. Returns the
    /// title ids revoked.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RevokeConsumedAsync(
        IReelDbContext db, Guid accountId, DateTime now, CancellationToken ct)
    {
        var consumed = await LiveSaves(db, accountId).Consumed(db).ToListAsync(ct);
        foreach (var save in consumed)
        {
            save.RevokedAt = now;
        }

        return consumed.Select(s => s.TitleId).ToList();
    }

    /// <summary>
    /// Revokes one title's live save, if the account holds one, on the tracked context. For the
    /// in-app rating path, which already knows the title has just been consumed. Does not save.
    /// </summary>
    public static async Task<bool> RevokeAsync(
        IReelDbContext db, Guid accountId, Guid titleId, DateTime now, CancellationToken ct)
    {
        var saves = await LiveSaves(db, accountId).Where(r => r.TitleId == titleId).ToListAsync(ct);
        foreach (var save in saves)
        {
            save.RevokedAt = now;
        }

        return saves.Count > 0;
    }
}
