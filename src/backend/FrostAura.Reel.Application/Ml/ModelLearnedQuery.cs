using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// What the most recent retrain actually bought the user.
/// <param name="TrainedAt">When the Active artifact was fitted — also the band's dismissal key.</param>
/// <param name="NewRatingsSinceLast">Labels this artifact learned from that its predecessor never saw.</param>
/// <param name="CalibrationBias">Mean(predicted − actual) on the evaluation holdout. + = over-promising.</param>
/// <param name="CalibrationMae">Mean |predicted − actual| on the holdout — "how wrong am I".</param>
/// <param name="MaeImprovement">Previous MAE − current MAE. Positive = the model runs closer now.</param>
/// <param name="Spearman">Rank correlation on the holdout — whether the ORDER is right.</param>
/// <param name="PicksChanged">Cards on tonight's page the previous snapshot did not show.</param>
/// </summary>
public readonly record struct ModelLearned(
    DateTime TrainedAt,
    int NewRatingsSinceLast,
    decimal? CalibrationBias,
    decimal? CalibrationMae,
    decimal? MaeImprovement,
    decimal? Spearman,
    int PicksChanged);

/// <summary>
/// Assembles the "your model just learned" band from what a retrain already wrote — the latest
/// <see cref="EvaluationResult"/>, the artifact pair around it, and the two newest feed snapshots.
/// Nothing new is persisted and no column is added: every number here is either already in the
/// ledger or is a diff of two rows that already exist.
///
/// <para>Returns null whenever the band would be a claim the data cannot support: no Active
/// artifact, no predecessor to have learned SINCE, or an evaluation that has not run yet. A band
/// that reads "learned from 0 new ratings" is worse than no band.</para>
/// </summary>
public sealed class ModelLearnedQuery(IReelDbContext db)
{
    public async Task<ModelLearned?> GetAsync(Guid accountId, CancellationToken ct)
    {
        var active = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => new { a.Id, a.TrainedAt })
            .FirstOrDefaultAsync(ct);
        if (active is null)
        {
            return null;
        }

        // The artifact this one replaced. Without it there is no "since last" to count against, and
        // counting from zero would report the founder's entire 2,000-rating history as "just
        // learned" on the very first band anyone ever sees.
        var previousTrainedAt = await db.ModelArtifacts
            .IgnoreQueryFilters()
            .Where(a => a.AccountId == accountId && a.Id != active.Id && a.TrainedAt < active.TrainedAt)
            .OrderByDescending(a => a.TrainedAt)
            .Select(a => (DateTime?)a.TrainedAt)
            .FirstOrDefaultAsync(ct);
        if (previousTrainedAt is not { } since)
        {
            return null;
        }

        // FirstRatedAt, not RatedAt — the same immutable axis the retrain gate counts on. Bounded at
        // both ends so this is what THIS artifact learned, not everything that has landed since.
        var newRatings = await db.UserRatings.CountAsync(
            r => r.AccountId == accountId && r.FirstRatedAt > since && r.FirstRatedAt <= active.TrainedAt, ct);
        if (newRatings <= 0)
        {
            return null;
        }

        var evaluations = await db.EvaluationResults
            .Where(e => e.AccountId == accountId)
            .OrderByDescending(e => e.ComputedAt)
            .Select(e => new { e.Mae, e.CalibrationBias, e.SpearmanRho })
            .Take(2)
            .ToListAsync(ct);
        if (evaluations.Count == 0)
        {
            return null;
        }

        var latest = evaluations[0];
        var improvement = evaluations.Count > 1 ? evaluations[1].Mae - latest.Mae : (decimal?)null;

        return new ModelLearned(
            TrainedAt: active.TrainedAt,
            NewRatingsSinceLast: newRatings,
            CalibrationBias: latest.CalibrationBias,
            CalibrationMae: latest.Mae,
            MaeImprovement: improvement,
            Spearman: latest.SpearmanRho,
            PicksChanged: await PicksChangedAsync(accountId, ct));
    }

    /// <summary>
    /// Titles on the Active snapshot that the snapshot before it did not carry. Derived rather than
    /// stored: <c>FeedBuildStat</c> would have needed a column, and this whole change ships without
    /// a migration. Snapshots are pruned to the last seven, so the predecessor is available for as
    /// long as the band is worth showing; when it is gone the honest answer is zero.
    /// </summary>
    private async Task<int> PicksChangedAsync(Guid accountId, CancellationToken ct)
    {
        var snapshots = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.GeneratedAt)
            .Select(s => new { s.Id, s.Status })
            .Take(2)
            .ToListAsync(ct);
        if (snapshots.Count < 2 || snapshots[0].Status != SnapshotStatus.Active)
        {
            return 0;
        }

        var currentIds = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshots[0].Id)
            .Select(i => i.TitleId)
            .Distinct()
            .ToListAsync(ct);
        var previousIds = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshots[1].Id)
            .Select(i => i.TitleId)
            .Distinct()
            .ToListAsync(ct);

        var before = previousIds.ToHashSet();
        return currentIds.Count(id => !before.Contains(id));
    }
}
