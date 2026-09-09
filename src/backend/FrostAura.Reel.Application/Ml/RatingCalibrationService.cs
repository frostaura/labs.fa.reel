using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Loads and caches the per-account <see cref="RatingCalibrator"/>. The fit reads the account's own
/// CLOSED LOOP — every case where the product committed to a number before the user gave one — from
/// two sources that are deliberately unioned rather than picked between:
///
/// <list type="bullet">
/// <item><b>Ratings.</b> Every Movie/Show <see cref="UserRating"/> paired with the newest
/// <see cref="Domain.Ml.TitleScore"/> whose <c>ScoredAt</c> is strictly BEFORE its <c>RatedAt</c>.
/// Strictly-before is the leakage guard: a score written after the rating landed has already seen
/// that rating through the feature vector, so pairing with it would flatter the calibrator.</item>
/// <item><b>Watch conversions.</b> Every <see cref="ManagedListItem"/> removed as
/// <see cref="ListRemovalReason.Watched"/> — Reel recommended it, the user saved it, watched it, and
/// rated it — paired with its rating and the newest score before <c>RemovedAt</c>. On the founder's
/// database this is the 73-row set the panel identified as the sharpest evidence the product has:
/// predicted 6.81, actual 5.78.</item>
/// </list>
///
/// De-duplicated by title, ratings winning, because the two sources overlap heavily and a title
/// counted twice would weight itself twice in the isotonic pooling.
///
/// Cached ~10 minutes per account and evicted the moment a rating lands (the in-app rating endpoint
/// and the Trakt ratings ingest both call <see cref="Invalidate"/>), so a rating the user just gave
/// is reflected in the very next score rather than up to ten minutes later.
/// </summary>
public sealed class RatingCalibrationService(IReelDbContext db, IMemoryCache cache)
{
    /// <summary>Short enough that a missed invalidation self-heals; long enough that a feed build fits it once.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>The fitted map for this account, from cache or freshly fitted.</summary>
    public async Task<RatingCalibrator> GetAsync(Guid accountId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKey(accountId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return await FitAsync(accountId, ct);
        }) ?? await FitAsync(accountId, ct);

    /// <summary>Drops the cached map so the next score reflects the rating that just landed.</summary>
    public void Invalidate(Guid accountId) => cache.Remove(CacheKey(accountId));

    /// <summary>Fits from live data, bypassing the cache. Public so diagnostics can report the fit.</summary>
    public async Task<RatingCalibrator> FitAsync(Guid accountId, CancellationToken ct) =>
        RatingCalibrator.Fit(await LoadPairsAsync(accountId, ct));

    /// <summary>
    /// The account's predicted-vs-actual pairs, de-duplicated by title. Public because the founder-
    /// facing calibration diagnostics report over exactly this set — one definition of the closed
    /// loop, not two that can drift apart.
    /// </summary>
    public async Task<IReadOnlyList<CalibrationPair>> LoadPairsAsync(Guid accountId, CancellationToken ct)
    {
        var fromRatings = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new
            {
                r.TitleId,
                Actual = (double)r.Rating,
                Predicted = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == r.TitleId && s.ScoredAt < r.RatedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
            })
            .Where(x => x.Predicted != null)
            .ToListAsync(ct);

        var fromConversions = await db.ManagedListItems
            .Where(m => m.AccountId == accountId
                && m.RemovedAt != null
                && m.RemovalReason == ListRemovalReason.Watched)
            .Select(m => new
            {
                m.TitleId,
                Actual = db.UserRatings
                    .Where(r => r.AccountId == accountId && r.TitleId == m.TitleId
                        && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                    .OrderByDescending(r => r.RatedAt)
                    .Select(r => (short?)r.Rating)
                    .FirstOrDefault(),
                Predicted = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == m.TitleId && s.ScoredAt < m.RemovedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
            })
            .Where(x => x.Actual != null && x.Predicted != null)
            .ToListAsync(ct);

        var byTitle = new Dictionary<Guid, CalibrationPair>(fromRatings.Count + fromConversions.Count);
        foreach (var row in fromRatings)
        {
            byTitle[row.TitleId] = new CalibrationPair(row.TitleId, (double)row.Predicted!.Value, row.Actual);
        }

        foreach (var row in fromConversions)
        {
            // Ratings win the tie: their pairing is anchored on the rating's own timestamp, which is
            // a tighter "before" than the list removal that merely preceded it.
            if (!byTitle.ContainsKey(row.TitleId))
            {
                byTitle[row.TitleId] = new CalibrationPair(row.TitleId, (double)row.Predicted!.Value, row.Actual!.Value);
            }
        }

        return byTitle.Values.ToList();
    }

    private static string CacheKey(Guid accountId) => $"ratingcalibration:{accountId}";
}
