using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// A fingerprint of everything an account's feed build reads that the account or its model can
/// change (WS-5 task 13, F+1): the Active artifact, the online head, the
/// whole interaction footprint (watches, title and person ratings, reactions including revokes),
/// the conditions, the account settings the page applies, and the day the stable-tonight contract
/// is keyed on. A Preserve build whose fingerprint matches the one that produced the Active snapshot
/// cannot deal a different page — tonight's seats are kept by contract and nothing vacated one — so
/// the handler records a no-op without refreshing the pool, scoring or assembling.
///
/// <para><b>Not the calibration pin's timestamp:</b> the first build of an artifact pins its map, so
/// the stamp would differ between that build and the next for nothing; the map is fixed per artifact
/// and re-pinned only on a <c>CALIBRATOR_MODE</c> change, which restarts the process and with it the
/// remembered fingerprint.</para>
///
/// <para><b>What it deliberately leaves out:</b> catalogue drift (a Trakt rating back-filled, a title
/// hydrated). It moves a kept card's number by hundredths, and the stable-tonight contract already
/// defers everything but the user's own actions to the next day's Fresh build; reading the whole
/// catalogue's change clock on every five-minute poll would cost what the short-circuit saves.</para>
/// </summary>
public static class FeedBuildInputs
{
    /// <summary>The fingerprint, sixteen hex digits of SHA-256 over an invariant canonical string.</summary>
    public static async Task<string> HashAsync(
        IReelDbContext db, Account account, ModelArtifact artifact, DateOnly day, CancellationToken ct)
    {
        var id = account.Id;
        // One round trip for the four footprints, rooted on the account's own row: each aggregate is
        // a correlated subquery, so an account with no rows in a table reads a count of zero rather
        // than no group at all. HREV5-PLAT-02: the earlier GroupBy(_ => 1)…FirstOrDefaultAsync shape
        // made EF log a FirstWithoutOrderByAndFilter warning per table on every build, the only
        // recurring warnings in the log; a filtered root has nothing to order.
        var f = await db.Accounts.Where(a => a.Id == id)
            .Select(a => new
            {
                Watched = db.WatchedTitles.Count(w => w.AccountId == id),
                WatchedLatest = db.WatchedTitles.Where(w => w.AccountId == id).Max(w => (DateTime?)w.UpdatedAt),
                Plays = db.WatchedTitles.Where(w => w.AccountId == id).Sum(w => w.Plays),
                Rated = db.UserRatings.Count(r => r.AccountId == id),
                RatedLatest = db.UserRatings.Where(r => r.AccountId == id).Max(r => (DateTime?)r.SyncedAt),
                RatedSum = db.UserRatings.Where(r => r.AccountId == id).Sum(r => (int)r.Rating),
                People = db.UserPersonRatings.Count(r => r.AccountId == id),
                PeopleLatest = db.UserPersonRatings.Where(r => r.AccountId == id).Max(r => (DateTime?)r.UpdatedAt),
                PeopleSum = db.UserPersonRatings.Where(r => r.AccountId == id).Sum(r => (int)r.Rating),
                Reactions = db.UserTitleReactions.Count(r => r.AccountId == id),
                ReactionsCreated = db.UserTitleReactions.Where(r => r.AccountId == id).Max(r => (DateTime?)r.CreatedAt),
                Revoked = db.UserTitleReactions.Count(r => r.AccountId == id && r.RevokedAt != null),
                LatestRevoke = db.UserTitleReactions.Where(r => r.AccountId == id).Max(r => r.RevokedAt),
            })
            .SingleAsync(ct);
        var conditions = await db.ContentFilters.Where(f => f.AccountId == id)
            .Select(f => f.Kind.ToString() + "=" + f.Value)
            .ToListAsync(ct);
        var head = await db.OnlineModelStates.Where(s => s.AccountId == id)
            .Select(s => new { s.BaseArtifactId, s.UpdateCount, s.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        var canonical = string.Join(
            '|',
            "v1",
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            artifact.Id.ToString("N"),
            head is null ? "nohead" : $"{head.BaseArtifactId:N}:{head.UpdateCount.ToString(CultureInfo.InvariantCulture)}:{Stamp(head.UpdatedAt)}",
            // An empty table keeps its old bare token ("w0" …), so the fingerprint of an unchanged
            // footprint is the one the previous shape produced and a deploy forces no rebuild.
            f.Watched == 0 ? "w0" : $"w{f.Watched.ToString(CultureInfo.InvariantCulture)}:{f.Plays.ToString(CultureInfo.InvariantCulture)}:{Stamp(f.WatchedLatest)}",
            f.Rated == 0 ? "r0" : $"r{f.Rated.ToString(CultureInfo.InvariantCulture)}:{f.RatedSum.ToString(CultureInfo.InvariantCulture)}:{Stamp(f.RatedLatest)}",
            f.People == 0 ? "p0" : $"p{f.People.ToString(CultureInfo.InvariantCulture)}:{f.PeopleSum.ToString(CultureInfo.InvariantCulture)}:{Stamp(f.PeopleLatest)}",
            f.Reactions == 0
                ? "x0"
                : $"x{f.Reactions.ToString(CultureInfo.InvariantCulture)}:{Stamp(f.ReactionsCreated)}:{f.Revoked.ToString(CultureInfo.InvariantCulture)}:{Stamp(f.LatestRevoke)}",
            "c" + string.Join(',', conditions.Order(StringComparer.Ordinal)),
            $"s{account.Region}:{account.Tier}:{account.Settings.MaturityCeiling}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 8));
    }

    private static string Stamp(DateTime? at) =>
        at is { } value ? value.Ticks.ToString(CultureInfo.InvariantCulture) : "-";
}
