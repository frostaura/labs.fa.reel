using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The OFFLINE DRY RUN for the online residual head: replays an account's rating history, in the
/// order it happened, through the current (v2) update rule and reports where the head ends up —
/// without writing a single row.
///
/// It exists because <c>RESIDUAL_OBSERVE_TRAKT_RATINGS</c> is the switch that would finally feed the
/// ~98% of ratings that arrive from Trakt rather than in-app, and the last time the head was taught
/// in bulk it converged to a constant −1.68 offset that nobody noticed for months. The plan requires
/// this replay to be run and read BEFORE that flag is turned on: if the final bias is near zero and
/// the weight norm is well inside the clamps, the head is learning shape rather than level and the
/// flag is safe; if the bias runs to the rail again, it is not, and the answer is to leave it off.
///
/// The error numbers are PREQUENTIAL — each event is scored before it is learned from, so the
/// "replayed" MAE is an honest test-then-train figure and not the head grading its own homework.
/// </summary>
public sealed class ResidualReplay(IReelDbContext db, FeatureVectorBuilder featureBuilder, IModelEngine engine)
{
    /// <summary>How many of the largest-magnitude weights the report names.</summary>
    private const int TopFeatureCount = 12;

    /// <summary>One feature's learned weight, for reading what the head actually keyed on.</summary>
    public record FeatureWeight(string Feature, double Weight);

    /// <summary>
    /// The dry run's verdict. <paramref name="FinalBias"/> and <paramref name="WeightsAtClamp"/> are
    /// the two numbers that decide whether the flag is safe to turn on.
    /// </summary>
    public record ReplayReport(
        int Events,
        int ArtifactVersion,
        bool Standardized,
        double FinalBias,
        double WeightL2Norm,
        double WeightMaxAbs,
        int WeightsAtClamp,
        double MeanAbsResidual,
        double MaxAbsResidual,
        double BaseMae,
        double BaseBias,
        double ReplayedMae,
        double ReplayedBias,
        IReadOnlyList<FeatureWeight> TopFeatures);

    /// <summary>
    /// Replays every title-level rating the account has, oldest first. Returns null when the account
    /// has no Active artifact or nothing vectorisable — there is no head to exercise in either case.
    /// </summary>
    /// <param name="sampleWeight">The weight the live path would use — 1.0 for an explicit rating.</param>
    public async Task<ReplayReport?> RunAsync(Guid accountId, double sampleWeight, CancellationToken ct)
    {
        var artifact = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
        if (artifact is null)
        {
            return null;
        }

        var history = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .OrderBy(r => r.RatedAt)
            .Select(r => new { r.TitleId, r.Rating })
            .ToListAsync(ct);
        if (history.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        var titleIds = history.Select(h => h.TitleId).Distinct().ToList();
        var vectors = await featureBuilder.BuildAsync(taste, titleIds, now, ct);
        var scorable = titleIds.Where(vectors.ContainsKey).ToList();
        if (scorable.Count == 0)
        {
            return null;
        }

        var scores = engine.ScoreCached(artifact.Id, artifact.ArtifactBytes, scorable.Select(id => vectors[id]).ToArray());
        var baseByTitle = scorable
            .Select((id, index) => (id, score: (double)scores[index]))
            .ToDictionary(x => x.id, x => x.score);

        var stats = FeatureStandardizer.FromSchemaJson(artifact.FeatureSchemaJson);
        var weights = new float[FeatureSchema.VectorLength];
        var bias = 0d;

        var events = 0;
        double baseAbs = 0, baseSigned = 0, replayedAbs = 0, replayedSigned = 0;
        double residualAbs = 0, residualMax = 0;

        foreach (var entry in history)
        {
            if (!vectors.TryGetValue(entry.TitleId, out var raw)
                || !baseByTitle.TryGetValue(entry.TitleId, out var baseScore))
            {
                continue;
            }

            var standardized = stats.Standardize(raw);
            var residual = OnlineResidualModel.Residual(weights, bias, standardized);
            var prediction = baseScore + residual;

            // Prequential: measure, THEN learn.
            var baseError = baseScore - entry.Rating;
            var replayedError = prediction - entry.Rating;
            baseAbs += Math.Abs(baseError);
            baseSigned += baseError;
            replayedAbs += Math.Abs(replayedError);
            replayedSigned += replayedError;
            residualAbs += Math.Abs(residual);
            residualMax = Math.Max(residualMax, Math.Abs(residual));
            events++;

            (weights, bias) = OnlineResidualModel.Step(weights, bias, standardized, entry.Rating - prediction, sampleWeight);
        }

        if (events == 0)
        {
            return null;
        }

        var names = FeatureSchema.AllNames();
        var top = weights
            .Select((w, i) => new FeatureWeight(i < names.Length ? names[i] : $"slot:{i}", w))
            .Where(f => f.Weight != 0d)
            .OrderByDescending(f => Math.Abs(f.Weight))
            .Take(TopFeatureCount)
            .ToList();

        return new ReplayReport(
            Events: events,
            ArtifactVersion: artifact.Version,
            Standardized: !stats.IsIdentity,
            FinalBias: bias,
            WeightL2Norm: Math.Sqrt(weights.Sum(w => (double)w * w)),
            WeightMaxAbs: weights.Length == 0 ? 0d : weights.Max(w => Math.Abs((double)w)),
            WeightsAtClamp: weights.Count(w => Math.Abs(w) >= OnlineResidualModel.WeightClamp - 1e-6),
            MeanAbsResidual: residualAbs / events,
            MaxAbsResidual: residualMax,
            BaseMae: baseAbs / events,
            BaseBias: baseSigned / events,
            ReplayedMae: replayedAbs / events,
            ReplayedBias: replayedSigned / events,
            TopFeatures: top);
    }
}
