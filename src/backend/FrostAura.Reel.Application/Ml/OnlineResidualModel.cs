using System.Runtime.InteropServices;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The per-account ONLINE RESIDUAL HEAD. The frozen FastTree base carries the heavy lifting; this
/// tiny linear model learns the base's residual error online — one bounded SGD step per event — and
/// its output is ADDED to the base score wherever a predicted rating is produced. It is zero-init,
/// so a fresh account (no events) contributes exactly 0 — a strict no-op, no regression on the base.
/// The correction is clamped to ±3 so it can nudge but never dominate a 0–10 base score, and each
/// weight/bias is clamped so no single event can blow up the head. The state is pinned to the
/// artifact it corrects (<see cref="OnlineModelState.BaseArtifactId"/>): when a retrain ships a new
/// Active artifact the id stops matching and the residual auto-resets to zero — dormant until the
/// new base accumulates its own events. That reset is why no TrainJobHandler change is needed.
/// </summary>
public sealed class OnlineResidualModel(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    IMemoryCache cache,
    ILogger<OnlineResidualModel> logger)
{
    // SGD with L2: small lr for stable online steps; tiny lambda so weights decay slowly toward 0
    // when a feature stops correlating with the residual. Per-weight/bias/output clamps bound the head.
    private const double LearningRate = 0.03;
    private const double L2Lambda = 1e-4;
    private const double WeightClamp = 2.0;
    private const double BiasClamp = 3.0;
    private const double ResidualClamp = 3.0;

    /// <summary>The loaded residual head: dense weights (feature-aligned) + intercept.</summary>
    public readonly record struct ResidualState(float[] Weights, double Bias);

    /// <summary>
    /// Loads the persisted head IFF it still corrects the given Active artifact; otherwise returns a
    /// fresh zero state. The <see cref="OnlineModelState.BaseArtifactId"/> == <paramref name="activeArtifactId"/>
    /// check IS the auto-reset — a new Active artifact from a retrain makes the old residual dormant
    /// with no write, so serving never applies a residual trained against a superseded base.
    /// </summary>
    public async Task<ResidualState> LoadAsync(Guid accountId, Guid activeArtifactId, CancellationToken ct)
    {
        var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
        return state is null || state.BaseArtifactId != activeArtifactId
            ? new ResidualState(new float[FeatureSchema.VectorLength], 0d)
            : new ResidualState(DeserializeWeights(state.Weights), state.Bias);
    }

    /// <summary>
    /// The residual correction for one feature vector: dot(weights, features) + bias, CLAMPED to
    /// ±<see cref="ResidualClamp"/> so it stays a nudge on the base score, never a takeover. Zero
    /// weights + zero bias (the fresh state) yield exactly 0 — the no-op guarantee.
    /// </summary>
    public static double Residual(float[] weights, double bias, float[] features)
    {
        var n = Math.Min(weights.Length, features.Length);
        var dot = 0d;
        for (var i = 0; i < n; i++)
        {
            dot += weights[i] * features[i];
        }

        return Math.Clamp(dot + bias, -ResidualClamp, ResidualClamp);
    }

    /// <summary>
    /// One online SGD update from a single observed event: learn the residual (target − base −
    /// current residual) and fold it into the head. Side-effect only and fully exception-safe — a
    /// failed observe must never break the user's action, so any error is logged and swallowed.
    /// </summary>
    /// <param name="target">The observed "true" rating the pair should score (1–10 for a rating; a
    /// soft pseudo-label for a not-interested / save / passive watch).</param>
    /// <param name="sampleWeight">How hard to learn from this event (explicit ratings 1.0; softer signals less).</param>
    public async Task ObserveAsync(Guid accountId, Guid titleId, double target, double sampleWeight, CancellationToken ct)
    {
        try
        {
            // No base model yet ⇒ nothing to correct.
            var artifact = await db.ModelArtifacts
                .FirstOrDefaultAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active, ct);
            if (artifact is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var taste = await GetOrBuildTasteAsync(accountId, now, ct);
            var vectors = await featureBuilder.BuildAsync(taste, [titleId], now, ct);
            if (!vectors.TryGetValue(titleId, out var vector))
            {
                return; // title isn't vectorisable (e.g. not in catalog) — skip.
            }

            // Load the head, resetting to zero when it belongs to a superseded base (auto-reset).
            var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
            var reset = state is null || state.BaseArtifactId != artifact.Id;
            var weights = reset ? new float[FeatureSchema.VectorLength] : DeserializeWeights(state!.Weights);
            var bias = reset ? 0d : state!.Bias;

            // prediction = frozen base + current residual; err drives the step.
            var baseScore = engine.ScoreCached(artifact.Id, artifact.ArtifactBytes, [vector])[0];
            var prediction = baseScore + Residual(weights, bias, vector);
            var err = target - prediction;

            for (var i = 0; i < weights.Length && i < vector.Length; i++)
            {
                var updated = (weights[i] * (1 - (LearningRate * L2Lambda))) + (LearningRate * sampleWeight * err * vector[i]);
                weights[i] = (float)Math.Clamp(updated, -WeightClamp, WeightClamp);
            }

            bias = Math.Clamp(bias + (LearningRate * sampleWeight * err), -BiasClamp, BiasClamp);

            if (state is null)
            {
                state = new OnlineModelState { Id = Guid.NewGuid(), AccountId = accountId };
                db.OnlineModelStates.Add(state);
            }

            state.BaseArtifactId = artifact.Id;
            state.Weights = SerializeWeights(weights);
            state.Bias = bias;
            state.UpdateCount = (reset ? 0 : state.UpdateCount) + 1;
            state.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Online residual observe failed for account {AccountId}, title {TitleId} — skipped.", accountId, titleId);
        }
    }

    /// <summary>
    /// Reuse the feed's briefly-cached taste state so a burst of observes (e.g. a delta sync's passive
    /// watches) build it once, not once per event. Same key + TTL as the feed paginator's build.
    /// </summary>
    private async Task<FeatureVectorBuilder.TasteState> GetOrBuildTasteAsync(Guid accountId, DateTime now, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"feedtaste:{accountId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        }) ?? await featureBuilder.BuildTasteStateAsync(accountId, now, ct);

    private static byte[] SerializeWeights(float[] weights) =>
        MemoryMarshal.AsBytes(weights.AsSpan()).ToArray();

    private static float[] DeserializeWeights(byte[] bytes)
    {
        var weights = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, weights, 0, weights.Length * sizeof(float));
        return weights;
    }
}
