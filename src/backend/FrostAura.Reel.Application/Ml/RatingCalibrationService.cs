using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

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
/// Each pair carries the ModelArtifactId of the score it came from, because a prediction only means
/// something relative to the model that produced it and this account's history spans several. What
/// <see cref="RatingCalibrator.Fit"/> then does with that — filter to the Active artifact, or de-mean
/// every artifact onto the Active one's level — is documented there. Resolving WHICH artifact is
/// Active is this class's job, since it is the one with the database.
///
/// Cached ~10 minutes per account and evicted the moment a rating lands (the in-app rating endpoint
/// and the Trakt ratings ingest both call <see cref="Invalidate"/>), so a rating the user just gave
/// is reflected in the very next score rather than up to ten minutes later.
/// </summary>
public sealed class RatingCalibrationService(IReelDbContext db, IMemoryCache cache, IConfiguration? configuration = null)
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

    /// <summary>The configured <c>CALIBRATOR_MODE</c>. <see cref="CalibratorMode.Auto"/> unless pinned.</summary>
    public CalibratorMode Mode => FeatureFlags.Read(configuration, FeatureFlags.CalibratorMode, CalibratorMode.Auto);

    /// <summary>Fits from live data, bypassing the cache. Public so diagnostics can report the fit.</summary>
    public async Task<RatingCalibrator> FitAsync(Guid accountId, CancellationToken ct) =>
        RatingCalibrator.Fit(
            await LoadPairsAsync(accountId, ct),
            await ActiveArtifactIdAsync(accountId, ct),
            Mode);

    /// <summary>
    /// The account's Active artifact, or null when it has never trained one. Exactly one row is
    /// Active per account by construction; the version ordering is belt and braces for the window in
    /// which a retrain has published the new one and not yet superseded the old.
    /// </summary>
    public async Task<Guid?> ActiveArtifactIdAsync(Guid accountId, CancellationToken ct) =>
        await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

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
                    .ThenByDescending(s => s.Id)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
                // The SAME row as Predicted: identical filter, identical ordering, and the Id
                // tie-break above makes "identical" true even when two scores share a timestamp.
                ArtifactId = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == r.TitleId && s.ScoredAt < r.RatedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (Guid?)s.ModelArtifactId)
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
                    .ThenByDescending(s => s.Id)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
                ArtifactId = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == m.TitleId && s.ScoredAt < m.RemovedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (Guid?)s.ModelArtifactId)
                    .FirstOrDefault(),
            })
            .Where(x => x.Actual != null && x.Predicted != null)
            .ToListAsync(ct);

        var byTitle = new Dictionary<Guid, CalibrationPair>(fromRatings.Count + fromConversions.Count);
        foreach (var row in fromRatings)
        {
            byTitle[row.TitleId] = new CalibrationPair(row.TitleId, (double)row.Predicted!.Value, row.Actual, row.ArtifactId);
        }

        foreach (var row in fromConversions)
        {
            // Ratings win the tie: their pairing is anchored on the rating's own timestamp, which is
            // a tighter "before" than the list removal that merely preceded it.
            if (!byTitle.ContainsKey(row.TitleId))
            {
                byTitle[row.TitleId] = new CalibrationPair(row.TitleId, (double)row.Predicted!.Value, row.Actual!.Value, row.ArtifactId);
            }
        }

        return byTitle.Values.ToList();
    }

    private static string CacheKey(Guid accountId) => $"ratingcalibration:{accountId}";
}
