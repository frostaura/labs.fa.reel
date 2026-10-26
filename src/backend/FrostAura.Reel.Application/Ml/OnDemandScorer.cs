using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Scores an arbitrary small set of titles for an account at request time — the on-demand path
/// the batch BuildFeed job never provided (the "MlScorerCache" of the original design notes).
/// Loads the account's Active artifact, scores through the engine's per-model cache (so the model
/// is deserialized once per artifact, not once per request), builds taste state once, vectorises,
/// and upserts <see cref="TitleScore"/>. Serving uses <c>asOf = now</c>; leakage-clean because
/// TitleScore is write-only and never read by training/eval. The final score is
/// <c>Calibrate(base + online residual) − the <see cref="DislikeSignal"/> demotion</c> — the same
/// order as every other scoring surface, so declined reasons steer the title page, search badges and
/// Ask Reel's blend without a retrain, and the number they steer is on the account's real scale.
/// </summary>
public sealed class OnDemandScorer(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    OnlineResidualModel residualModel,
    DislikeSignal dislikes,
    RatingCalibrationService calibration)
{
    /// <summary>
    /// Predicts the user's rating for each title and persists it. Returns titleId → predicted
    /// (0–10). Empty when there's no active model or no vectorisable titles — callers must treat a
    /// missing entry as "unscored" (the surfaces already tolerate a null predicted rating).
    /// Pass a prebuilt <paramref name="taste"/> to avoid recomputing it when the caller already has one.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> ScoreAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> titleIds,
        DateTime asOf,
        CancellationToken ct,
        FeatureVectorBuilder.TasteState? taste = null)
    {
        var ids = titleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var artifact = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            // Newest-first rather than an unordered FirstOrDefault: a filtered unique
            // index makes two Active artifacts unrepresentable today, but an unordered
            // read would silently start flipping predictions between models the moment
            // that index changed. Deterministic by construction costs nothing here.
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
        if (artifact is null)
        {
            return new Dictionary<Guid, decimal>();
        }

        taste ??= await featureBuilder.BuildTasteStateAsync(accountId, asOf, ct);
        var vectors = await featureBuilder.BuildAsync(taste, ids, asOf, ct);
        var orderedIds = ids.Where(vectors.ContainsKey).ToList();
        if (orderedIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        // Scored WITH contributions, as the build and the paginator score: a title reached from Ask or
        // typeahead opens a page whose "why X for you" waterfall reads these rows, and a number-only
        // score left it explaining a 6.2 with a −0.6 demotion and a +0.0 prior (1,206 of 29,377
        // TitleScores rows had none, 2026-10-26). Same top-5 the build persists.
        var scored = engine.ScoreWithContributionsCached(
            artifact.Id,
            artifact.ArtifactBytes,
            orderedIds.Select(id => vectors[id]).ToArray(),
            FeatureSchema.AllNames());

        // Fold in the online residual head (loaded once): base + learned correction, then clamp.
        // Zero head ⇒ no change, so this is a strict no-op until the account has logged events.
        var residual = await residualModel.LoadAsync(accountId, artifact, ct);

        // The monotone predicted→actual map, fit on this account's own closed loop and applied LAST
        // (before the demotion) wherever a number reaches a human. Identity until the account has
        // pairs, so a new account is unaffected.
        var calibrator = await calibration.GetAsync(accountId, ct);

        // The reason-weighted dislike demotion, from the ~10-min-cached profile (reaction
        // endpoints evict it on create/revoke, so a fresh dislike bites immediately).
        var profile = await dislikes.GetProfileAsync(accountId, ct);
        var demotions = await dislikes.DemotionsForAsync(profile, orderedIds, ct);

        var result = new Dictionary<Guid, decimal>(orderedIds.Count);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var titleId = orderedIds[i];
            var adjusted = scored[i].Score + OnlineResidualModel.Residual(residual, vectors[titleId]);
            var predicted = Math.Round((decimal)calibrator.Calibrate(adjusted), 2);
            predicted = Math.Round(
                DislikeSignal.ApplyDemotion(predicted, demotions.GetValueOrDefault(titleId)), 2);
            result[titleId] = predicted;
        }

        // An upsert, not load-then-insert: a title opened while the paginator is scoring it raced
        // into a 23505. The number and the contributions that explain it are written together, so a
        // stored row never pairs a fresh number with an older score's reasons.
        await TitleScoreWriter.UpsertAsync(
            db,
            accountId,
            artifact.Id,
            orderedIds.Select((titleId, i) => new TitleScoreWriter.Score(
                titleId, result[titleId], TitleScoreWriter.SerializeContributions(scored[i].TopContributions))),
            asOf,
            ct);

        // Kept from the tracked write this replaced: callers have relied on this method saving
        // their own pending changes alongside the scores. A no-op when nothing is tracked.
        await db.SaveChangesAsync(ct);
        return result;
    }
}
