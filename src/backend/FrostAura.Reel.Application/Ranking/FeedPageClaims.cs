using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// One Home page's "already on screen" set, shared by every rail request that page makes, so no
/// title is served on two rails of the same page.
///
/// <para><b>Why the server holds it.</b> Home fires every rail's first <c>POST /feed/more</c>
/// within ~165 ms of the feed landing. A page-wide exclude list sent by the client cannot help
/// those requests — none of them has seen another's answer yet — and the result was 23% of the
/// titles on a 1440 px Home appearing in two or more sections (NCIS in five). The client therefore
/// sends one opaque token per page view; each request CLAIMS its cards against the token's set
/// under a lock, and a card another rail already claimed is skipped for the next-best one. The set
/// is seeded with the Active snapshot's titles, so a genre rail also never repeats tonight's picks
/// or a because-you-loved rail above it.</para>
///
/// <para>In-process and short-lived on purpose: a page view is minutes long, and the worst a lost
/// set costs (a restart mid-page) is the pre-existing behaviour for the rest of that page.</para>
/// </summary>
public static class FeedPageClaims
{
    /// <summary>How long an idle page's set is kept. Sliding: every claim keeps it alive.</summary>
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    /// <summary>Longest token accepted — a client nonce, not a payload.</summary>
    public const int MaxTokenLength = 64;

    /// <summary>
    /// Claims, in order, as many of <paramref name="orderedTitleIds"/> as are free on this page, up
    /// to <paramref name="limit"/>, and returns them in the same order. With no token every title is
    /// free (the behaviour before page claims existed).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClaimAsync(
        IReelDbContext db, IMemoryCache cache, Guid accountId, string? pageToken,
        IReadOnlyList<Guid> orderedTitleIds, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pageToken) || pageToken.Length > MaxTokenLength)
        {
            return [.. orderedTitleIds.Take(limit)];
        }

        var key = $"feedpage:{accountId}:{pageToken}";
        if (!cache.TryGetValue<HashSet<Guid>>(key, out var claimed) || claimed is null)
        {
            // Seeded with tonight's snapshot, read once per page view. Two first requests racing
            // here may both seed; the loser's set is simply discarded by GetOrCreate below.
            var seed = await db.FeedItems
                .Where(i => i.AccountId == accountId
                    && db.FeedSnapshots.Any(s => s.Id == i.FeedSnapshotId && s.Status == SnapshotStatus.Active))
                .Select(i => i.TitleId)
                .ToListAsync(ct);
            claimed = cache.GetOrCreate(key, entry =>
            {
                entry.SlidingExpiration = IdleTtl;
                return seed.ToHashSet();
            })!;
        }

        var taken = new List<Guid>(Math.Min(limit, orderedTitleIds.Count));
        lock (claimed)
        {
            foreach (var titleId in orderedTitleIds)
            {
                if (taken.Count >= limit)
                {
                    break;
                }

                if (claimed.Add(titleId))
                {
                    taken.Add(titleId);
                }
            }
        }

        return taken;
    }
}
