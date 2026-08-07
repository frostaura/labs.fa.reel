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

    /// <summary>
    /// Guards against a division by a near-zero ‖x‖² for an all-but-empty vector, which would turn
    /// the normalised step below back into an unbounded one.
    /// </summary>
    private const double NormEpsilon = 1.0;

    /// <summary>
    /// The online update rule this class implements, persisted on every head it writes.
    /// <list type="bullet">
    /// <item><b>0</b> — raw SGD: <c>w += lr·sw·err·x</c>. UNSAFE, and the reason this constant exists.
    /// The features are raw, not standardised: <c>runtimeMinutes</c> is ~105, the affinity scalars are
    /// ~7, <c>tmdbPopularityLog</c> and <c>traktVotesLog</c> are ~2–10. A single event with a typical
    /// error (a not-interested on a title the base scored 7 gives err ≈ −5) moves w₁ by
    /// 0.03·0.8·(−5)·105 ≈ −12.6, which lands on the ±2 clamp immediately. Once a handful of scalars
    /// are pinned there, <c>dot(w, x)</c> is in the hundreds and <see cref="Residual"/> returns the
    /// ±<see cref="ResidualClamp"/> rail for EVERY title — the head stops being a per-title correction
    /// and becomes a constant offset on the whole distribution.</item>
    /// <item><b>1</b> — normalised (NLMS) step: <c>w += lr·sw·err·x / (ε + ‖x‖²)</c>. The change this
    /// makes to the head's own prediction for the observed vector is <c>lr·sw·err</c> regardless of
    /// feature scale, so no feature's units can drive its weight to the clamp and the output rail is
    /// reached only by a genuinely persistent, consistent error — which is what it was always for.</item>
    /// </list>
    /// </summary>
    public const int CurrentAlgorithmVersion = 1;

    /// <summary>The loaded residual head: dense weights (feature-aligned) + intercept.</summary>
    public readonly record struct ResidualState(float[] Weights, double Bias);

    /// <summary>
    /// Loads the persisted head IFF it still corrects the given Active artifact AND was produced by
    /// the current update rule; otherwise returns a fresh zero state. Two auto-reset keys, same
    /// mechanism and no write on either: <see cref="OnlineModelState.BaseArtifactId"/> retires a head
    /// whose base was retrained away, and <see cref="OnlineModelState.AlgorithmVersion"/> retires one
    /// whose weights mean something the current <see cref="Residual"/> would misread — including every
    /// head saturated by the pre-v1 unnormalised rule (see <see cref="CurrentAlgorithmVersion"/>).
    /// </summary>
    public async Task<ResidualState> LoadAsync(Guid accountId, Guid activeArtifactId, CancellationToken ct)
    {
        var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
        return IsStale(state, activeArtifactId)
            ? new ResidualState(new float[FeatureSchema.VectorLength], 0d)
            : new ResidualState(DeserializeWeights(state!.Weights), state.Bias);
    }

    /// <summary>Both auto-reset keys in one place, so load and observe can never disagree about them.</summary>
    private static bool IsStale(OnlineModelState? state, Guid activeArtifactId) =>
        state is null
        || state.BaseArtifactId != activeArtifactId
        || state.AlgorithmVersion != CurrentAlgorithmVersion;

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
    /// One NORMALISED (NLMS) gradient step: <c>w += lr·sw·err·x / (ε + ‖x‖²)</c>, with L2 decay and
    /// the per-weight / bias clamps. Dividing by ‖x‖² makes the step's effect on this vector's own
    /// prediction <c>lr·sw·err</c> whatever the features' units are — which is what stops
    /// <c>runtimeMinutes</c> (~105) from slamming its weight onto the ±<see cref="WeightClamp"/> rail
    /// on the very first event and turning the whole head into a constant ±<see cref="ResidualClamp"/>
    /// offset on every title. See <see cref="CurrentAlgorithmVersion"/> for the full history.
    ///
    /// Public and static so the rule itself is directly testable: the property that matters (a single
    /// event must not saturate the head) is arithmetic, and asserting it through a database and a
    /// trained artifact would prove less while costing more.
    /// </summary>
    /// <param name="error">target − (base score + current residual).</param>
    public static (float[] Weights, double Bias) Step(
        float[] weights, double bias, float[] features, double error, double sampleWeight)
    {
        var normSquared = NormEpsilon;
        foreach (var x in features)
        {
            normSquared += (double)x * x;
        }

        var step = LearningRate * sampleWeight * error / normSquared;
        var updated = new float[weights.Length];
        for (var i = 0; i < weights.Length; i++)
        {
            var w = weights[i] * (1 - (LearningRate * L2Lambda));
            if (i < features.Length)
            {
                w += step * features[i];
            }

            updated[i] = (float)Math.Clamp(w, -WeightClamp, WeightClamp);
        }

        return (updated, Math.Clamp(bias + (LearningRate * sampleWeight * error), -BiasClamp, BiasClamp));
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
                .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            // Newest-first rather than an unordered FirstOrDefault: a filtered unique
            // index makes two Active artifacts unrepresentable today, but an unordered
            // read would silently start flipping predictions between models the moment
            // that index changed. Deterministic by construction costs nothing here.
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
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

            // Load the head, resetting to zero when it belongs to a superseded base or an older
            // update rule (auto-reset).
            var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
            var reset = IsStale(state, artifact.Id);
            var weights = reset ? new float[FeatureSchema.VectorLength] : DeserializeWeights(state!.Weights);
            var bias = reset ? 0d : state!.Bias;

            // prediction = frozen base + current residual; err drives the step.
            var baseScore = engine.ScoreCached(artifact.Id, artifact.ArtifactBytes, [vector])[0];
            var prediction = baseScore + Residual(weights, bias, vector);
            var err = target - prediction;

            (weights, bias) = Step(weights, bias, vector, err, sampleWeight);

            if (state is null)
            {
                state = new OnlineModelState { Id = Guid.NewGuid(), AccountId = accountId };
                db.OnlineModelStates.Add(state);
            }

            state.BaseArtifactId = artifact.Id;
            state.AlgorithmVersion = CurrentAlgorithmVersion;
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
