using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The account's median rating over its movie- and show-level ratings — the line a row has to
/// clear before its title may say "high for you". Season and episode ratings are left out because
/// a row is a list of titles, and a binge of one show's episodes would otherwise move the bar.
///
/// <para>Read as ten counts (ratings are whole numbers 1–10), never as the rows themselves, so the
/// Home path pays one grouped, indexed read however long the history is.</para>
/// </summary>
public static class AccountRatingMedian
{
    /// <summary>The median, or null when the account has rated no movie or show.</summary>
    public static async Task<decimal?> ForAsync(IReelDbContext db, Guid accountId, CancellationToken ct)
    {
        var counts = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .GroupBy(r => r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Of(counts.Select(c => (c.Rating, c.Count)));
    }

    /// <summary>
    /// The median of a rating histogram: the middle value, or the mean of the two middle values
    /// for an even count. Null for an empty histogram.
    /// </summary>
    public static decimal? Of(IEnumerable<(short Rating, int Count)> histogram)
    {
        var ordered = histogram.Where(h => h.Count > 0).OrderBy(h => h.Rating).ToList();
        var total = ordered.Sum(h => h.Count);
        if (total == 0)
        {
            return null;
        }

        // 0-based positions of the two middle ratings (the same position when the count is odd).
        var lowIndex = (total - 1) / 2;
        var highIndex = total / 2;
        decimal? low = null;
        decimal? high = null;
        var seen = 0;
        foreach (var (rating, count) in ordered)
        {
            var last = seen + count - 1;
            if (low is null && lowIndex <= last)
            {
                low = rating;
            }

            if (highIndex <= last)
            {
                high = rating;
                break;
            }

            seen += count;
        }

        return (low!.Value + high!.Value) / 2m;
    }
}
