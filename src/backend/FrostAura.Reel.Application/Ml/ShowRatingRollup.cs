using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The show-level signal in a show's season and episode ratings (WS-5 task 12, N-8 rollup part).
/// A show the account rated only season by season or episode by episode said a great deal about
/// itself and was invisible to every taste pool, because only movie- and show-level rows were read:
/// on the founder's copy, 17 shows carry 1,708 such ratings and no show rating.
///
/// <para><b>The rule.</b> A show-level rating always wins and suppresses the rollup. Otherwise the
/// season ratings are the summary the user already wrote, so their mean is the signal; with no season
/// rating, the episode ratings are shrunk toward the account's mean with <see cref="EpisodeShrinkage"/>
/// pseudo-ratings, because three episodes rated 10 are weaker evidence about a show than a 10 given
/// to the show. Rounded to the 1–10 scale every other rating is on, and dated at the latest
/// constituent rating, so an as-of read (the eval harness, training) sees a rollup only once every
/// rating it is made of existed.</para>
///
/// <para><b>Where it is read.</b> The taste pools — genre, decade, person and contrarian — and the
/// media share. Never a training label (labels stay movie- and show-level rows on the immutable
/// <c>FirstRatedAt</c> axis), and never a "because you loved" anchor: an anchor names a title the
/// reader rated as a whole.</para>
/// </summary>
public static class ShowRatingRollup
{
    /// <summary>How many pseudo-ratings at the account's mean an episode-only rollup is shrunk with.</summary>
    public const int EpisodeShrinkage = 3;

    /// <summary>One rolled-up show: its derived rating and when its newest constituent was given.</summary>
    public sealed record Rolled(Guid TitleId, short Rating, DateTime RatedAt);

    /// <summary>One season or episode rating, as read.</summary>
    public sealed record Part(Guid TitleId, RatingSubjectType Subject, short Rating, DateTime RatedAt);

    /// <summary>
    /// The rollups for every show in <paramref name="parts"/> not in <paramref name="ratedAsWhole"/>,
    /// with <paramref name="userMean"/> as the episode shrinkage target.
    /// </summary>
    public static IReadOnlyList<Rolled> Roll(IEnumerable<Part> parts, IReadOnlySet<Guid> ratedAsWhole, decimal userMean) =>
        [.. parts
            .Where(p => !ratedAsWhole.Contains(p.TitleId))
            .GroupBy(p => p.TitleId)
            .Select(g =>
            {
                var seasons = g.Where(p => p.Subject == RatingSubjectType.Season).ToList();
                var value = seasons.Count > 0
                    ? seasons.Average(p => (decimal)p.Rating)
                    : (g.Sum(p => (decimal)p.Rating) + (EpisodeShrinkage * userMean)) / (g.Count() + EpisodeShrinkage);
                var rating = (short)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1m, 10m);
                return new Rolled(g.Key, rating, g.Max(p => p.RatedAt));
            })
            .OrderBy(r => r.TitleId)];

    /// <summary>The account's season and episode ratings given at or before <paramref name="asOf"/>.</summary>
    public static async Task<List<Part>> PartsAsync(IReelDbContext db, Guid accountId, DateTime asOf, CancellationToken ct) =>
        await db.UserRatings
            .Where(r => r.AccountId == accountId && r.RatedAt <= asOf
                && (r.SubjectType == RatingSubjectType.Season || r.SubjectType == RatingSubjectType.Episode))
            .Select(r => new Part(r.TitleId, r.SubjectType, r.Rating, r.RatedAt))
            .ToListAsync(ct);
}
