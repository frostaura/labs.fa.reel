using System.Runtime.InteropServices;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The per-account ONLINE RESIDUAL HEAD. The frozen FastTree base carries the heavy lifting; this
/// tiny linear model learns the base's residual error online — one bounded SGD step per event — and
/// its output is ADDED to the base score before the <see cref="RatingCalibrator"/> maps the result
/// onto the account's real rating scale. It is zero-init, so a fresh account (no events) contributes
/// exactly 0 — a strict no-op, no regression on the base. The correction is clamped to ±3 so it can
/// nudge but never dominate a 0–10 base score, and each weight/bias is clamped so no single event
/// can blow up the head. The state is pinned to the artifact it corrects
/// (<see cref="OnlineModelState.BaseArtifactId"/>): when a retrain ships a new Active artifact the id
/// stops matching and the residual auto-resets to zero — dormant until the new base accumulates its
/// own events. That reset is why no TrainJobHandler change is needed.
///
/// <b>The head is no longer the product's calibrator.</b> Until 2026-09-09 its free, unnormalised
/// bias term was the only thing correcting the base model's level, and it had wandered to −1.68 on
/// the founder's live account: every predicted rating in the product was silently
/// <c>base − 1.68</c>, learned from soft pseudo-labels and resettable by any retrain. Level now
/// belongs to <see cref="RatingCalibrator"/>, which is fit on real outcomes and can be explained;
/// what stays here is the DISCRIMINATIVE part — this title, for this account, is better or worse
/// than the base thinks. <c>RESIDUAL_HEAD_ENABLED=false</c> turns the head off entirely (loads as
/// zero, observes are dropped) so the calibrator can be measured against a clean baseline.
/// </summary>
public sealed class OnlineResidualModel(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    IModelEngine engine,
    IMemoryCache cache,
    ILogger<OnlineResidualModel> logger,
    IConfiguration? configuration = null,
    IServiceScopeFactory? scopeFactory = null)
{
    // SGD with L2: small lr for stable online steps; tiny lambda so weights decay slowly toward 0
    // when a feature stops correlating with the residual. Per-weight/bias/output clamps bound the head.
    // Internal rather than private so the saturation tests can assert the step bound against the
    // constants the production rule actually uses, instead of re-declaring them and drifting.
    internal const double LearningRate = 0.03;
    internal const double L2Lambda = 1e-4;
    internal const double WeightClamp = 2.0;
    internal const double BiasClamp = 3.0;
    private const double ResidualClamp = 3.0;

    /// <summary>
    /// Guards against a division by a near-zero ‖x‖² for an all-but-empty vector, which would turn
    /// the normalised step below back into an unbounded one.
    /// </summary>
    private const double NormEpsilon = 1.0;

    /// <summary>
    /// The bias is a CONSTANT-1 FEATURE, not a free parameter — this is its squared magnitude, and it
    /// enters ‖x‖² exactly like every other slot. See <see cref="CurrentAlgorithmVersion"/> rule 2.
    /// </summary>
    private const double BiasFeature = 1.0;

    /// <summary>
    /// The online update rule this class implements, persisted on every head it writes.
    /// <list type="bullet">
    /// <item><b>0</b> — raw SGD: <c>w += lr·sw·err·x</c>. UNSAFE. The features are raw, not
    /// standardised: <c>runtimeMinutes</c> is ~105, the affinity scalars ~7. A single event with a
    /// typical error moved w₁ straight onto the ±<see cref="WeightClamp"/> rail, and with a handful
    /// of scalars pinned <see cref="Residual"/> returned the ±<see cref="ResidualClamp"/> rail for
    /// EVERY title — a constant offset on the whole distribution, not a per-title correction.</item>
    /// <item><b>1</b> — normalised (NLMS) step for the WEIGHTS: <c>w += lr·sw·err·x / (ε + ‖x‖²)</c>.
    /// This fixed the weights and left the bias on the raw rule, <c>b += lr·sw·err</c>. That is the
    /// defect the 2026-09-09 panel found in production: a free, unnormalised bias next to normalised
    /// weights is a level-only learner with a ~100× larger step than everything beside it, so the
    /// head converged to a pure offset (−1.68 after 224 updates) and the offset became, by accident,
    /// the only calibration in the product.</item>
    /// <item><b>2</b> — ONE RULE FOR ALL SLOTS. The bias is treated as a constant-1 feature: it joins
    /// ‖x‖², takes the same normalised step and the same L2 decay as every weight, and can no longer
    /// outrun them. The feature vector is standardised by the artifact's persisted per-feature μ/σ
    /// (<see cref="FeatureStandardizer"/>) before any of it, so no slot's units set the learning rate
    /// for the others. Every v1 head resets on first read — including the live −1.68 one, whose
    /// level is now the <see cref="RatingCalibrator"/>'s job.</item>
    /// </list>
    /// </summary>
    public const int CurrentAlgorithmVersion = 2;

    /// <summary>
    /// The loaded residual head: dense weights (feature-aligned), intercept, and the standardiser
    /// the weights were learned under. <see cref="Stats"/> travels WITH the weights because they are
    /// only interpretable together — a head learned on standardised features and applied to raw ones
    /// is silent nonsense.
    /// </summary>
    public readonly record struct ResidualState(float[] Weights, double Bias, FeatureStandardizer Stats);

    /// <summary>False when <c>RESIDUAL_HEAD_ENABLED=false</c> — the head loads as zero and drops observes.</summary>
    private bool Enabled { get; } = FeatureFlags.IsEnabled(configuration, FeatureFlags.ResidualHeadEnabled, fallback: true);

    /// <summary>
    /// Loads the persisted head IFF it still corrects the given Active artifact AND was produced by
    /// the current update rule; otherwise returns a fresh zero state. Two auto-reset keys, same
    /// mechanism and no write on either: <see cref="OnlineModelState.BaseArtifactId"/> retires a head
    /// whose base was retrained away, and <see cref="OnlineModelState.AlgorithmVersion"/> retires one
    /// whose weights mean something the current <see cref="Residual"/> would misread — including
    /// every head learned under the free-bias v1 rule (see <see cref="CurrentAlgorithmVersion"/>).
    /// </summary>
    public async Task<ResidualState> LoadAsync(Guid accountId, ModelArtifact artifact, CancellationToken ct)
    {
        var stats = StatsFor(artifact);
        if (!Enabled)
        {
            return Zero(stats);
        }

        var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
        return IsStale(state, artifact.Id)
            ? Zero(stats)
            : new ResidualState(DeserializeWeights(state!.Weights), state.Bias, stats);
    }

    private static ResidualState Zero(FeatureStandardizer stats) =>
        new(new float[FeatureSchema.VectorLength], 0d, stats);

    /// <summary>
    /// The artifact's persisted per-feature μ/σ, parsed once and cached by artifact id — the schema
    /// document is ~200 numbers and a paged feed request would otherwise re-parse it per page.
    /// </summary>
    private FeatureStandardizer StatsFor(ModelArtifact artifact) =>
        cache.GetOrCreate($"featurestats:{artifact.Id}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            return FeatureStandardizer.FromSchemaJson(artifact.FeatureSchemaJson);
        }) ?? FeatureStandardizer.Identity;

    /// <summary>Both auto-reset keys in one place, so load and observe can never disagree about them.</summary>
    private static bool IsStale(OnlineModelState? state, Guid activeArtifactId) =>
        state is null
        || state.BaseArtifactId != activeArtifactId
        || state.AlgorithmVersion != CurrentAlgorithmVersion;

    /// <summary>
    /// The residual correction for one RAW feature vector — standardises it with the head's own stats
    /// first, so every call site can keep passing the vector the feature builder produced. This is the
    /// overload serving code should use.
    /// </summary>
    public static double Residual(in ResidualState state, float[] rawFeatures) =>
        Residual(state.Weights, state.Bias, state.Stats.Standardize(rawFeatures));

    /// <summary>
    /// The correction itself: dot(weights, features) + bias, CLAMPED to ±<see cref="ResidualClamp"/>
    /// so it stays a nudge on the base score, never a takeover. Zero weights + zero bias (the fresh
    /// state) yield exactly 0 — the no-op guarantee. <paramref name="features"/> must already be
    /// standardised; <see cref="Residual(in ResidualState, float[])"/> is the overload that does that.
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
    /// One NORMALISED (NLMS) gradient step over ALL slots, bias included:
    /// <c>step = lr·sw·err / (ε + ‖x‖² + 1)</c>, then <c>wᵢ += step·xᵢ</c> and <c>b += step·1</c>,
    /// each with the same L2 decay and its own clamp. The <c>+1</c> is the bias's constant-1 feature
    /// entering the norm like any other slot — which is the whole of the v2 fix.
    ///
    /// The property this buys, and the one the tests assert: a single step moves THIS vector's own
    /// prediction by <c>step·(‖x‖² + 1) &lt; lr·sw·err</c>, whatever the features' units are. No slot
    /// can outrun the others, so the head reaches its output rail only on a genuinely persistent,
    /// consistent error — which is what it was always for.
    ///
    /// Public and static so the rule itself is directly testable: the property that matters is
    /// arithmetic, and asserting it through a database and a trained artifact would prove less while
    /// costing more.
    /// </summary>
    /// <param name="features">The STANDARDISED feature vector (see <see cref="FeatureStandardizer"/>).</param>
    /// <param name="error">target − (base score + current residual).</param>
    public static (float[] Weights, double Bias) Step(
        float[] weights, double bias, float[] features, double error, double sampleWeight)
    {
        var normSquared = NormEpsilon + BiasFeature;
        foreach (var x in features)
        {
            normSquared += (double)x * x;
        }

        var step = LearningRate * sampleWeight * error / normSquared;
        var decay = 1 - (LearningRate * L2Lambda);
        var updated = new float[weights.Length];
        for (var i = 0; i < weights.Length; i++)
        {
            var w = weights[i] * decay;
            if (i < features.Length)
            {
                w += step * features[i];
            }

            updated[i] = (float)Math.Clamp(w, -WeightClamp, WeightClamp);
        }

        var updatedBias = Math.Clamp((bias * decay) + (step * BiasFeature), -BiasClamp, BiasClamp);
        return (updated, updatedBias);
    }

    /// <summary>
    /// One online SGD update from a single observed event. Delegates to
    /// <see cref="ObserveBatchAsync"/> so there is exactly one implementation of the observe path.
    ///
    /// <b>Deliberately still synchronous.</b> The obvious next move is to enqueue onto a bounded
    /// channel drained by a hosted service, and the plumbing for that is small — but the in-app
    /// rating endpoint documents an ordering contract ("teach the head FIRST, so the rebuild enqueued
    /// next already scores with the updated correction") that going asynchronous would quietly break,
    /// and that endpoint is owned by another branch this cycle. The correctness half of the problem —
    /// this method committing the CALLER's open transaction through their DbContext — is fixed here
    /// instead, by doing the work in its own scope. Revisit the queue when the endpoint is free to
    /// change with it.
    /// </summary>
    /// <param name="target">The observed "true" rating the pair should score (1–10 for a rating; a
    /// soft pseudo-label for a not-interested / save / passive watch).</param>
    /// <param name="sampleWeight">How hard to learn from this event (explicit ratings 1.0; softer signals less).</param>
    public Task ObserveAsync(Guid accountId, Guid titleId, double target, double sampleWeight, CancellationToken ct) =>
        ObserveBatchAsync(accountId, [new ObservedEvent(titleId, target, sampleWeight)], ct);

    /// <summary>
    /// Learns from a BATCH of events in one pass: one taste build, one feature build, one score call,
    /// N in-memory steps, one save. A delta sync's passive watches used to cost that whole chain per
    /// title; they now cost it once.
    ///
    /// Side-effect only and fully exception-safe — a failed observe must never break the user's
    /// action, so any error is logged and swallowed. The work runs in its OWN service scope (and its
    /// own DbContext) when one is available: the previous implementation called SaveChanges on the
    /// caller's scoped context, which committed whatever else that caller had pending — a reaction
    /// endpoint's outbox rows and library writes went to disk early, mid-request, as a side effect of
    /// teaching the model.
    /// </summary>
    public async Task ObserveBatchAsync(Guid accountId, IReadOnlyList<ObservedEvent> events, CancellationToken ct)
    {
        if (!Enabled || events.Count == 0)
        {
            return;
        }

        try
        {
            if (scopeFactory is null)
            {
                // No DI scope available (direct construction in tests): fall back to the injected
                // context. The transaction concern does not arise there — the caller IS the test.
                await ObserveCoreAsync(db, featureBuilder, accountId, events, ct);
                return;
            }

            // One retry, on a FRESH scope. The head is read-modify-write, so two concurrent observes
            // for one account would otherwise leave the loser's step silently overwritten — the event
            // lost, with nothing to see. ObserveCoreAsync writes through a guarded UPDATE and raises
            // a concurrency exception when it loses; recomputing the step from the winner's state is
            // the correct resolution, because the alternative is dropping a real signal.
            for (var attempt = 0; ; attempt++)
            {
                using var scope = scopeFactory.CreateScope();
                var provider = scope.ServiceProvider;
                provider.GetRequiredService<IAccountContext>().SetAccount(accountId);
                try
                {
                    await ObserveCoreAsync(
                        provider.GetRequiredService<IReelDbContext>(),
                        provider.GetRequiredService<FeatureVectorBuilder>(),
                        accountId,
                        events,
                        ct);
                    return;
                }
                // DbUpdateConcurrencyException is the guarded UPDATE losing; a plain
                // DbUpdateException here is the unique index on AccountId rejecting a raced FIRST
                // write. Both mean "someone else wrote the head between our read and our write", and
                // both take the same single retry.
                catch (DbUpdateException ex) when (attempt == 0)
                {
                    logger.LogInformation(
                        ex, "Online residual head for {AccountId} lost a concurrent update; retrying once from the winning state.",
                        accountId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Online residual observe failed for account {AccountId} ({Count} event(s)) — skipped.", accountId, events.Count);
        }
    }

    /// <summary>One observed event: which title, what it should have scored, and how hard to learn from it.</summary>
    public readonly record struct ObservedEvent(Guid TitleId, double Target, double Weight);

    private async Task ObserveCoreAsync(
        IReelDbContext context,
        FeatureVectorBuilder vectorBuilder,
        Guid accountId,
        IReadOnlyList<ObservedEvent> events,
        CancellationToken ct)
    {
        // No base model yet ⇒ nothing to correct.
        var artifact = await context.ModelArtifacts
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
        var taste = await GetOrBuildTasteAsync(vectorBuilder, accountId, now, ct);
        var titleIds = events.Select(e => e.TitleId).Distinct().ToList();
        var vectors = await vectorBuilder.BuildAsync(taste, titleIds, now, ct);
        var scorable = titleIds.Where(vectors.ContainsKey).ToList();
        if (scorable.Count == 0)
        {
            return; // nothing vectorisable (e.g. not in catalog) — skip.
        }

        // One score call for the whole batch, then N in-memory steps.
        var baseScores = engine.ScoreCached(artifact.Id, artifact.ArtifactBytes, scorable.Select(id => vectors[id]).ToArray());
        var baseByTitle = scorable
            .Select((id, index) => (id, score: baseScores[index]))
            .ToDictionary(x => x.id, x => (double)x.score);

        // Load the head, resetting to zero when it belongs to a superseded base or an older update
        // rule (auto-reset).
        var state = await context.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
        var reset = IsStale(state, artifact.Id);
        var weights = reset ? new float[FeatureSchema.VectorLength] : DeserializeWeights(state!.Weights);
        var bias = reset ? 0d : state!.Bias;
        var stats = StatsFor(artifact);

        var applied = 0;
        foreach (var observed in events)
        {
            if (!vectors.TryGetValue(observed.TitleId, out var raw)
                || !baseByTitle.TryGetValue(observed.TitleId, out var baseScore))
            {
                continue;
            }

            // Standardise once per event; prediction = frozen base + current residual; err drives the step.
            var standardized = stats.Standardize(raw);
            var prediction = baseScore + Residual(weights, bias, standardized);
            (weights, bias) = Step(weights, bias, standardized, observed.Target - prediction, observed.Weight);
            applied++;
        }

        if (applied == 0)
        {
            return;
        }

        var serialized = SerializeWeights(weights);

        if (state is null)
        {
            context.OnlineModelStates.Add(new OnlineModelState
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                BaseArtifactId = artifact.Id,
                AlgorithmVersion = CurrentAlgorithmVersion,
                Weights = serialized,
                Bias = bias,
                UpdateCount = applied,
                UpdatedAt = now,
            });

            // The unique index on AccountId is the arbiter of a concurrent first-write: the loser
            // gets 23505, which is the same class of conflict as the guarded update below and takes
            // the same retry.
            await context.SaveChangesAsync(ct);
            return;
        }

        // OPTIMISTIC CONCURRENCY, in application code. A row version on Postgres's xmin column would
        // be the idiomatic form, but declaring one is a model change and `ef migrations
        // has-pending-model-changes` goes dirty the moment it is — and this branch must not add a
        // migration. Predicating the UPDATE on the UpdateCount we read gives the identical guarantee
        // against the identical race: a writer who landed between our read and our write has already
        // moved that counter, so our UPDATE matches nothing and we recompute from their state
        // instead of overwriting it. ExecuteUpdate also keeps this off the change tracker entirely,
        // which matters because the head must never flush anything else this context happens to hold.
        var updateCount = (reset ? 0 : state.UpdateCount) + applied;
        var affected = await context.OnlineModelStates
            .Where(s => s.Id == state.Id && s.UpdateCount == state.UpdateCount)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.BaseArtifactId, artifact.Id)
                    .SetProperty(s => s.AlgorithmVersion, CurrentAlgorithmVersion)
                    .SetProperty(s => s.Weights, serialized)
                    .SetProperty(s => s.Bias, bias)
                    .SetProperty(s => s.UpdateCount, updateCount)
                    .SetProperty(s => s.UpdatedAt, now),
                ct);

        if (affected == 0)
        {
            throw new DbUpdateConcurrencyException(
                $"Online residual head for account {accountId} moved under us (expected UpdateCount {state.UpdateCount}).");
        }
    }

    /// <summary>
    /// Reuse the feed's briefly-cached taste state so a burst of observes (e.g. a delta sync's passive
    /// watches) build it once, not once per event. Same key + TTL as the feed paginator's build.
    /// </summary>
    private async Task<FeatureVectorBuilder.TasteState> GetOrBuildTasteAsync(
        FeatureVectorBuilder vectorBuilder, Guid accountId, DateTime now, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"feedtaste:{accountId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return await vectorBuilder.BuildTasteStateAsync(accountId, now, ct);
        }) ?? await vectorBuilder.BuildTasteStateAsync(accountId, now, ct);

    private static byte[] SerializeWeights(float[] weights) =>
        MemoryMarshal.AsBytes(weights.AsSpan()).ToArray();

    private static float[] DeserializeWeights(byte[] bytes)
    {
        var weights = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, weights, 0, weights.Length * sizeof(float));
        return weights;
    }
}
