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
        var watched = await db.WatchedTitles.Where(w => w.AccountId == id)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(w => w.UpdatedAt), Plays = g.Sum(w => w.Plays) })
            .FirstOrDefaultAsync(ct);
        var rated = await db.UserRatings.Where(r => r.AccountId == id)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.SyncedAt), Sum = g.Sum(r => (int)r.Rating) })
            .FirstOrDefaultAsync(ct);
        var people = await db.UserPersonRatings.Where(r => r.AccountId == id)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.UpdatedAt), Sum = g.Sum(r => (int)r.Rating) })
            .FirstOrDefaultAsync(ct);
        var reactions = await db.UserTitleReactions.Where(r => r.AccountId == id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Created = g.Max(r => r.CreatedAt),
                Revoked = g.Count(r => r.RevokedAt != null),
                LatestRevoke = g.Max(r => r.RevokedAt),
            })
            .FirstOrDefaultAsync(ct);
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
            watched is null ? "w0" : $"w{watched.Count.ToString(CultureInfo.InvariantCulture)}:{watched.Plays.ToString(CultureInfo.InvariantCulture)}:{Stamp(watched.Latest)}",
            rated is null ? "r0" : $"r{rated.Count.ToString(CultureInfo.InvariantCulture)}:{rated.Sum.ToString(CultureInfo.InvariantCulture)}:{Stamp(rated.Latest)}",
            people is null ? "p0" : $"p{people.Count.ToString(CultureInfo.InvariantCulture)}:{people.Sum.ToString(CultureInfo.InvariantCulture)}:{Stamp(people.Latest)}",
            reactions is null
                ? "x0"
                : $"x{reactions.Count.ToString(CultureInfo.InvariantCulture)}:{Stamp(reactions.Created)}:{reactions.Revoked.ToString(CultureInfo.InvariantCulture)}:{Stamp(reactions.LatestRevoke)}",
            "c" + string.Join(',', conditions.Order(StringComparer.Ordinal)),
            $"s{account.Region}:{account.Tier}:{account.Settings.MaturityCeiling}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 8));
    }

    private static string Stamp(DateTime? at) =>
        at is { } value ? value.Ticks.ToString(CultureInfo.InvariantCulture) : "-";
}
