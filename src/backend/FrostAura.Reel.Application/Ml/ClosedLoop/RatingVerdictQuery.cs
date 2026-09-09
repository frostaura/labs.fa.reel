using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// What Reel had said about a title before the user rated it — the payload behind the after-rating
/// moment.
/// </summary>
/// <param name="PredictedBefore">The number Reel committed to, at two decimals.</param>
/// <param name="Surface">Where it was shown, when that is known; null when the evidence is a bare score.</param>
/// <param name="ShownAt">When it was shown; null for the same reason.</param>
/// <param name="Delta">predicted − actual. Positive means Reel over-promised.</param>
public readonly record struct RatingVerdict(decimal PredictedBefore, string? Surface, DateTime? ShownAt, decimal Delta);

/// <summary>
/// Resolves the after-rating moment: "Reel said 8.4 · you said 5".
///
/// <para><b>Evidence, in order of strength.</b> First the exposure log — a card carrying that exact
/// number was returned to this account at a known moment, so the moment can name the surface and
/// the date. Failing that, the newest <c>TitleScore</c> written strictly BEFORE the rating: proof
/// the model committed to a number, but not proof the user saw it, so surface and shown-at stay
/// null and the client's copy drops the "you saw this N days ago" clause rather than inventing
/// one.</para>
///
/// <para>Returns null when neither exists. A moment that cannot name what Reel predicted is not a
/// moment, and a toast reading "Reel said — · you said 7" is worse than silence.</para>
///
/// <para><b>Read before the rating is committed.</b> The caller invokes this while the new rating
/// is still only staged, which is what keeps <c>ScoredAt &lt; ratedAt</c> meaningful: a scoring
/// pass triggered BY the rating would otherwise become the thing the rating is scored against.</para>
/// </summary>
public sealed class RatingVerdictQuery(IReelDbContext db)
{
    public async Task<RatingVerdict?> ForAsync(Guid accountId, Guid titleId, short rating, DateTime ratedAt, CancellationToken ct)
    {
        var exposure = await db.ExposureEvents
            .Where(e => e.AccountId == accountId && e.TitleId == titleId && e.OccurredAt < ratedAt)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.PredictedRating, e.Surface, e.OccurredAt })
            .FirstOrDefaultAsync(ct);
        if (exposure is not null)
        {
            return new RatingVerdict(
                exposure.PredictedRating,
                exposure.Surface.ToString(),
                exposure.OccurredAt,
                exposure.PredictedRating - rating);
        }

        // The bridge that makes this work on day one, before the exposure log has any history: the
        // scores are already there, and 116 of them predate a rating on the founder's own account.
        var scored = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.TitleId == titleId && s.ScoredAt < ratedAt)
            .OrderByDescending(s => s.ScoredAt)
            .Select(s => (decimal?)s.PredictedRating)
            .FirstOrDefaultAsync(ct);

        return scored is { } predicted
            ? new RatingVerdict(predicted, null, null, predicted - rating)
            : null;
    }
}
