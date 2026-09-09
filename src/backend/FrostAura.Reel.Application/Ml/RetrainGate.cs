using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using FrostAura.Reel.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The single reader of <see cref="RetrainPolicy"/> against the database. Two callers share it on
/// purpose: the nightly reconcile, which acts on the verdict, and the delta sync, which merely
/// REPORTS it in the post-sync message. They used to be unrelated code paths, so the pill could
/// promise a retrain the reconcile would not perform — and the promise is the part the user
/// remembers.
/// </summary>
public sealed class RetrainGate(
    IReelDbContext db,
    RatingCalibrationService calibration,
    IConfiguration? configuration = null)
{
    /// <summary>The account's active artifact, with the two numbers the gate needs from it.</summary>
    public readonly record struct ArtifactState(Guid Id, DateTime TrainedAt);

    /// <summary>Configured <c>RETRAIN_MIN_NEW_RATINGS</c>, or the policy default.</summary>
    public int Threshold =>
        FeatureFlags.ReadInt(configuration, FeatureFlags.RetrainMinNewRatings, RetrainPolicy.DefaultMinNewRatings);

    /// <summary>
    /// Why this account is owed a retrain right now, or null. Reads the ≥N-ratings rule off
    /// <c>FirstRatedAt</c> (immutable, set on insert — Trakt's own <c>RatedAt</c> is rewritten on
    /// every ratings walk, so a back-dated import used to look like nothing new), the shelf-life
    /// rule off the artifact, and the calibration breach off the account's own closed loop.
    /// </summary>
    public async Task<RetrainTrigger?> EvaluateAsync(Guid accountId, DateTime utcNow, CancellationToken ct)
    {
        if (await ActiveArtifactAsync(accountId, ct) is not { } artifact)
        {
            // No model yet — the onboarding Train chain owns the first one; there is nothing to
            // retrain and nothing to promise.
            return null;
        }

        var newRatings = await NewRatingsSinceAsync(accountId, artifact.TrainedAt, ct);

        // The closed-loop read is the expensive half, so it is skipped whenever the cheap rules have
        // already decided. Nothing below can overturn a trigger that has already fired.
        if (newRatings >= Threshold)
        {
            return RetrainTrigger.NewRatings;
        }

        if ((utcNow - artifact.TrainedAt).TotalDays > RetrainPolicy.StaleArtifactDays)
        {
            return RetrainTrigger.StaleArtifact;
        }

        if (RetrainPolicy.WithinTrainFloor(artifact.TrainedAt, utcNow))
        {
            return null;
        }

        var (pairCount, bias, mae) = await CalibrationDiagnosticsAsync(accountId, artifact.Id, ct);
        return RetrainPolicy.CalibrationBreached(pairCount, bias, mae) ? RetrainTrigger.CalibrationBreach : null;
    }

    /// <summary>
    /// What the post-sync message may promise: whether a retrain is coming, and when. A Train
    /// already queued or running is "now" (null time) rather than "tonight" — it is not waiting for
    /// the 03:00 pass. Otherwise a due account retrains on the next nightly run.
    /// </summary>
    public async Task<(bool Queued, DateTime? At)> OutlookAsync(Guid accountId, DateTime utcNow, CancellationToken ct)
    {
        var inFlight = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && (j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (inFlight)
        {
            return (true, null);
        }

        return await EvaluateAsync(accountId, utcNow, ct) is null
            ? (false, null)
            : (true, RetrainPolicy.NextNightlyRunUtc(utcNow));
    }

    /// <summary>The account's Active artifact, newest version first (see RatingCalibrationService).</summary>
    public async Task<ArtifactState?> ActiveArtifactAsync(Guid accountId, CancellationToken ct) =>
        await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => new ArtifactState(a.Id, a.TrainedAt))
            .Cast<ArtifactState?>()
            .FirstOrDefaultAsync(ct);

    /// <summary>Labels the artifact could not have seen — counted on FirstRatedAt, never RatedAt.</summary>
    public async Task<int> NewRatingsSinceAsync(Guid accountId, DateTime since, CancellationToken ct) =>
        await db.UserRatings.CountAsync(r => r.AccountId == accountId && r.FirstRatedAt > since, ct);

    /// <summary>
    /// Bias and MAE over the pairs this artifact itself produced — "fresh since the last train" in
    /// the only sense the data supports. A pair scored by a superseded model says nothing about
    /// whether the CURRENT one is mis-levelled, and including them would keep an account in breach
    /// long after the retrain that fixed it.
    /// </summary>
    public async Task<(int Count, double Bias, double MeanAbsoluteError)> CalibrationDiagnosticsAsync(
        Guid accountId, Guid artifactId, CancellationToken ct)
    {
        var pairs = (await calibration.LoadPairsAsync(accountId, ct))
            .Where(p => p.ArtifactId == artifactId)
            .Select(p => new PredictionPair(p.Predicted, p.Actual))
            .ToList();
        var accuracy = RetroLoopStatistics.Summarise(pairs);
        return (accuracy.Count, accuracy.Bias, accuracy.MeanAbsoluteError);
    }
}
