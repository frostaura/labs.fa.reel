using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Ranking.Interleaving;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// The serving pipeline: refresh the TMDB candidate pool → first-pass score everything
/// eligible → fully hydrate the shortlist (credits feed the affinity features) → final score
/// with per-title contributions → assemble hero + because-you-loved rows with freshness and
/// genre-diversity shaping → flip the Active snapshot and the account to FeedReady. The model
/// is never in the request path — the feed endpoint reads the snapshot.
///
/// Assembly honours the stable-"tonight" contract (FeedBuildPolicy): a background rebuild inside
/// the same UTC day PRESERVES the Active snapshot's hero, anchors and rail members — and writes
/// nothing at all when the result is identical — while a new day, a new model, or an explicit user
/// action deals fresh rows. The job's TriggerReason is what tells those cases apart.
/// </summary>
public class BuildFeedJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    CandidateGenerator candidateGenerator,
    CandidateRetriever candidateRetriever,
    EligibilityQueryBuilder eligibility,
    FeedStarvationDiagnostic starvation,
    TitleHydrator hydrator,
    IModelEngine engine,
    OnlineResidualModel residualModel,
    RatingCalibrationService calibration,
    DislikeSignal dislikes,
    ISearchAgent agent,
    TasteFactsBuilder tasteFacts,
    PersonalisationContext personalisation,
    IMemoryCache cache,
    IPipelineEventHub events,
    ExposureLog exposures,
    ILogger<BuildFeedJobHandler> logger,
    // Optional with a disabled default so the switch is opt-in at every level at once: DI supplies
    // the configured instance, and every construction site that predates the harness — the three
    // integration tests that build this handler by hand — keeps compiling and keeps describing the
    // shipped behaviour, which is the feed exactly as it was.
    InterleaveOptions? interleaveOptions = null,
    // Optional for the same reason as the options above: the three integration tests that construct
    // this handler by hand keep compiling and keep describing the pre-B1 feed, which is exactly the
    // single-centroid path a cold account still takes.
    TasteClusterProvider? tasteClusters = null) : IJobHandler
{
    // First-pass pool is now a hybrid union (~1200): a popularity coverage slice plus a personalized
    // ANN slice, deduped. The old pure-popularity pool never scored a title the user would love that
    // wasn't already popular; the ANN arm lifts that recall ceiling before the model re-ranks.
    private const int FirstPassPopPool = 700;
    private const int FirstPassAnnPool = 500;
    private const int ShortlistSize = 220;
    private const int HeroSize = 5;
    // Single-sourced with the serve path: FeedEndpoints derives each hero card's confidence label
    // from this same bar, so a card can never be labelled against a threshold the build did not use.
    private const decimal HeroConfidenceFloor = Domain.Feed.HeroConfidence.Floor;
    private const int AnchorRowCount = 3;
    private const int RowSize = 10;
    private const int RowMinMembers = 4; // a row below this reads worse than no row, so it's dropped
    // Preference-tag re-rank nudge: a title whose plot embedding sits close to a declared preference
    // tag ("things I'm into") gets at most a +10% lift on top of the model's rating — enough to
    // sharpen ordering toward stated taste, never enough to float a weak prediction over a strong one.
    private const double PreferenceTagWeight = 0.10;

    /// <summary>
    /// How many shortlist titles must clear the account's own <c>MinPredictedRating</c> before the
    /// floor is taken at face value. Four times the hero: a floor that leaves fewer candidates than
    /// four heroes' worth is not selecting the page, it IS the page, and the whole feed below the
    /// hero then assembles out of whatever handful survived. On 2026-09-10 this was fourteen reality
    /// shows on a 220-title shortlist.
    /// </summary>
    private const int FloorRelaxMinimum = 4 * HeroSize;

    /// <summary>
    /// The quantile the floor relaxes TO. Not zero — relaxing to nothing throws away the user's
    /// setting entirely and lets the bottom of the shortlist onto rails. The 80th percentile keeps
    /// the top fifth of what was actually scored, which is a weaker claim than the user asked for
    /// and an honest one, and it is stated on the page rather than applied in silence.
    /// </summary>
    private const double FloorRelaxQuantile = 0.80d;

    /// <summary>
    /// How many shortlist candidates the pre-assembly fit pass judges on a Fresh build. Forty is
    /// eight hero seats' worth of headroom at the batch size below — enough that the hero, the deep
    /// cuts head and the top of each rail are all drawn from judged titles, and few enough that the
    /// pass costs ~5 calls.
    /// </summary>
    private const int PreAssemblyFitCandidates = 40;

    // ── LLM why pass (mirrors FeedPaginator's curation budget mechanics) ────────────────────
    // Soft budget checked BETWEEN batches — the first batch always finishes so every build makes
    // forward progress into the shared 6h fit cache — plus a hard ceiling backstop against a hung
    // provider. More generous than the paginator's 4s/20s because this runs inside a background
    // job, not a request a user is staring at; the cache means successive builds converge to warm.
    private static readonly TimeSpan WhyPassBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WhyPassCeiling = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);
    private const int RerankBatch = 9;

    // ── Pre-assembly fit pass (Fresh only) ──────────────────────────────────────────────────
    // Its own budget rather than a share of the why pass's, because the two answer to different
    // failure modes: the why pass degrading costs a caption, this pass degrading costs the ORDER of
    // the hero. Same shape though — soft budget between batches, hard ceiling inside one — and the
    // same shared 6h fit cache, so the verdicts it buys are the verdicts the why pass then reuses
    // for free instead of re-purchasing.
    private static readonly TimeSpan PreAssemblyFitBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PreAssemblyFitCeiling = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The fit a candidate is ordered as carrying when nobody judged it — the midpoint of the
    /// agent's own 0–1 scale. Chosen so that <c>0.5 + fit</c> is exactly 1.0 for an unjudged title:
    /// the LLM term then vanishes from the comparator rather than penalising everything the pass did
    /// not reach, and a build with no agent at all orders by calibrated × prior, precisely.
    /// </summary>
    private const double NeutralFit = 0.5d;

    // Hero coherence floor: when the LLM's fit verdict for a hero card comes in at or below this,
    // "tonight's pick" and "you'll probably dislike it" are on the same card. The why pass swaps
    // such a hero for the next spare candidate (see SwapLowFitHeroesAsync) rather than captioning
    // the headline slot with a reason to skip it.
    private const double HeroFitFloor = 0.3;

    /// <summary>
    /// The interleaving posture for this process. Resolved once here rather than per build so a
    /// mid-experiment configuration change cannot split one account's slates across two definitions
    /// of what the experiment is.
    /// </summary>
    private readonly InterleaveOptions _interleave = interleaveOptions ?? InterleaveOptions.Disabled;

    public JobKind Kind => JobKind.BuildFeed;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("BuildFeed requires an account.");
        // Build instrumentation (FeedBuildStat) is captured from here to the end of the method,
        // including the no-op path — a build that changed nothing is exactly the observation worth
        // keeping, and it was the one the old log line never made.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var capture = new BuildCapture();
        var now = DateTime.UtcNow;
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
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
            await SkipWithoutReadableArtifactAsync(job, accountId, now, stopwatch, ct);
            return;
        }

        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        // The online residual head for this Active artifact, loaded once and folded into every score
        // (first-pass ranking + final predicted rating). Zero head ⇒ no change from the base model.
        var residual = await residualModel.LoadAsync(accountId, artifact, ct);

        // The monotone predicted→actual map, fit on this account's own closed loop. Applied to BOTH
        // scoring passes: it cannot reorder the shortlist (it is monotone by construction), but a
        // first pass on one scale and a final pass on another would be two different numbers for the
        // same title, and the shortlist cut-off would be taken on a scale nothing else uses.
        var calibrator = await calibration.GetAsync(accountId, ct);
        var minPredictedRaw = await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == Domain.Filters.FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        var minPredicted = minPredictedRaw is not null
            && decimal.TryParse(minPredictedRaw, System.Globalization.CultureInfo.InvariantCulture, out var floor)
            ? Math.Clamp(floor, 0m, 10m) : 0m;

        // ── Phase 1: widen the pool ─────────────────────────────────────────────────────
        ReportProgress(job, accountId, 10, "Scouting fresh candidates");
        // `now` is threaded in so the seed rotation is a pure function of the build's own timestamp —
        // the same replayability contract the row anchors hold.
        var poolRefresh = await candidateGenerator.RefreshPoolAsync(accountId, taste, account.Region, now, ct);

        // ── Phase 2: first-pass score every eligible candidate ─────────────────────────
        // Hybrid retrieval: the popularity coverage slice UNIONED with a personalized pgvector ANN
        // slice over the user's taste centroids — so a well-matched but unpopular title actually
        // enters scoring — then the FastTree model re-ranks the union exactly as before. The
        // retriever applies the same eligibility rule, the poster/tmdbId lane, and the maturity gate.
        ReportProgress(job, accountId, 30, "Scoring the field");

        var retrieval = await candidateRetriever.RetrieveWithArmsAsync(
            accountId,
            taste,
            lane: q => q.Where(t => t.TmdbId != null && t.PosterPath != null),
            popCount: FirstPassPopPool,
            annCount: FirstPassAnnPool,
            maturityCeiling: account.Settings.MaturityCeiling,
            ct,
            // The full k. This is a background job with a minute's box, so it can afford the k exact
            // pgvector scans the request path cannot — see FeedPaginator.WindowMaxClusters.
            maxClusters: CandidateRetriever.AllClusters);
        var pool = retrieval.Hits.Select(hit => hit.Title).ToList();

        // Persisted from THIS path and no other: a page load is not evidence about taste, and the
        // eval harness builds five taste states per run. It is the partition the retrieval REPORTS
        // rather than one fetched separately, so the stored row can never describe a partition
        // different from the one that chose the pool. Best-effort by contract — it is already in
        // hand, so a failed write costs a diagnostic row and never a feed.
        if (tasteClusters is not null && retrieval.Clusters.Count > 0)
        {
            await tasteClusters.PersistAsync(accountId, now, retrieval.Clusters, ct);
        }

        var poolVectors = await featureBuilder.BuildAsync(taste, pool.Select(t => t.Id).ToList(), now, ct);
        var poolIds = pool.Where(t => poolVectors.ContainsKey(t.Id)).Select(t => t.Id).ToList();
        var firstPass = engine.Score(artifact.ArtifactBytes, poolIds.Select(id => poolVectors[id]).ToArray());
        var shortlistIds = poolIds.Zip(firstPass, (id, score) =>
                (id, score: calibrator.Calibrate(score + OnlineResidualModel.Residual(residual, poolVectors[id]))))
            .OrderByDescending(x => x.score)
            .Take(ShortlistSize)
            .Select(x => x.id)
            .ToHashSet();
        var shortlist = pool.Where(t => shortlistIds.Contains(t.Id)).ToList();

        // ── Phase 3: hydrate the shortlist (credits → affinity features) ────────────────
        var needsHydration = shortlist.Where(t => t.LastMetadataRefreshAt == null).ToList();
        if (needsHydration.Count > 0)
        {
            ReportProgress(job, accountId, 50, $"Enriching {needsHydration.Count} contenders");
            await hydrator.HydrateBatchAsync(needsHydration, ct);
            await db.SaveChangesAsync(ct);
        }

        // ── Phase 4: final score with contributions ─────────────────────────────────────
        ReportProgress(job, accountId, 70, "Final ranking");
        var finalVectors = await featureBuilder.BuildAsync(taste, shortlist.Select(t => t.Id).ToList(), now, ct);
        var ordered = shortlist.Where(t => finalVectors.ContainsKey(t.Id)).ToList();
        var scored = engine.ScoreWithContributions(
            artifact.ArtifactBytes,
            ordered.Select(t => finalVectors[t.Id]).ToArray(),
            FeatureSchema.AllNames());

        // The residual is a scalar correction on the numeric score used for ranking + PredictedRating;
        // the contribution list stays the base model's (a scalar can't be split into per-feature parts).
        // The dislike demotion is subtracted here — on the shortlist scores, BEFORE hero and row
        // selection — so a title similar to what the user declined cannot ride a high base score
        // into the hero or a rail. Profile cached ~10 min; reaction endpoints both evict it and
        // request this very rebuild, so a fresh dislike reshapes the next feed immediately.
        var dislikeProfile = await dislikes.GetProfileAsync(accountId, ct);
        var demotions = await dislikes.DemotionsForAsync(
            dislikeProfile, ordered.Select(t => t.Id).ToList(), ct);
        var candidates = ordered.Zip(scored, (title, row) =>
        {
            // base + residual, then the calibrator, then the demotion — the fixed order every
            // scoring surface uses. The pre-calibration value and the map's delta are carried on the
            // candidate so the exposure log (Phase 2) can persist what the model said before the
            // calibrator touched it, without re-deriving it from a number that has already moved.
            var adjusted = row.Score + OnlineResidualModel.Residual(residual, finalVectors[title.Id]);
            var calibrated = calibrator.Calibrate(adjusted);
            var predicted = DislikeSignal.ApplyDemotion(
                (decimal)calibrated, demotions.GetValueOrDefault(title.Id));
            return new Candidate(title, row, predicted, (decimal)adjusted, (decimal)(calibrated - adjusted));
        }).ToList();
        await PersistScoresAsync(accountId, artifact.Id, candidates, now, ct);

        // ── Phase 5: assemble the feed ──────────────────────────────────────────────────
        ReportProgress(job, accountId, 85, "Assembling tonight's picks");
        var snapshot = await AssembleAsync(account, artifact, taste, candidates, minPredicted, job.TriggerReason, now, capture, ct);

        // Written on the tracked context before either exit below commits, so the no-op path records
        // its no-op. The eligible-pool count is the supply ceiling: when retrieval asks for more
        // slots than this, ranking is not the problem.
        db.FeedBuildStats.Add(new FeedBuildStat
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = snapshot?.Id,
            ModelArtifactId = artifact.Id,
            BuiltAt = now,
            Mode = snapshot is null
                ? FeedBuildOutcome.NoOp
                : capture.Mode == FeedBuildMode.Preserve ? FeedBuildOutcome.Preserve : FeedBuildOutcome.Fresh,
            CandidateCount = poolRefresh.Discovered,
            NewCandidatesUpserted = poolRefresh.NewlyUpserted,
            EligiblePoolCount = await eligibility.EligibleTitles(accountId).CountAsync(ct),
            ShortlistCount = candidates.Count,
            HeroEligibleCount = capture.HeroEligibleCount,
            ShortlistP50 = Percentile(candidates, 0.50d),
            ShortlistP90 = Percentile(candidates, 0.90d),
            RailsDropped = capture.RailsDropped,
            DegradedReason = capture.DegradedReason,
            ClustersUsed = retrieval.ClusterPulls.Count,
            AnnPerClusterJson = JsonSerializer.Serialize(retrieval.ClusterPulls.Select(p => new
            {
                ordinal = p.Ordinal,
                weight = Math.Round(p.Weight, 4),
                target = p.Target,
                returned = p.Returned,
            })),
            SeedPoolSize = poolRefresh.SeedPoolSize,
            SeedsUsed = poolRefresh.SeedsUsed,
            NewlyEligiblePerArmJson = JsonSerializer.Serialize(poolRefresh.PerArm.Select(a => new
            {
                arm = a.Arm,
                discovered = a.Discovered,
                upserted = a.Upserted,
                newlyEligible = a.NewlyEligible,
            })),
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        });

        // Supply just moved, so enrichment has work. A title that is brand new to the catalogue has
        // no embedding and no attributes: it is invisible to the ANN retrieval arm and to rail
        // similarity, and 13 of its 92 feature slots read zero while every training row's read live.
        // Enrichment used to be enqueued from the onboarding chain and nowhere else, which is why
        // ~1,360 discovered titles had never been embedded. Enqueued on BOTH exits below — a
        // Preserve/no-op build discovers candidates exactly like a fresh one does, and suppressing
        // the enqueue with the snapshot would starve enrichment precisely on the quiet nights.
        if (poolRefresh.NewlyUpserted > 0)
        {
            await EnrichmentScheduler.EnqueueAsync(db, accountId, now, ct);
        }

        if (snapshot is null)
        {
            // Preserve-mode: the assembled feed is item-for-item what the Active snapshot already
            // serves, so no new snapshot is minted and no feed-ready reseed is announced — the whole
            // point of the stable "tonight" contract is that a background delta-sync poll cannot
            // churn the page the user is reading.
            //
            // The scores are a separate question. AssembleAsync has already written any moved
            // ratings onto the EXISTING rows (see ApplyScoreDrift); all that is left here is to say
            // so, on a frame that carries the snapshot's unchanged GeneratedAt precisely so the
            // client can patch in place instead of remounting. When nothing moved, this is the true
            // no-op it always was.
            var scoreUpdates = capture.ScoreUpdates;
            job.ProgressMessage = scoreUpdates.Count > 0
                ? $"kept tonight's picks — {scoreUpdates.Count} score{(scoreUpdates.Count == 1 ? "" : "s")} recalibrated"
                : "feed unchanged — kept tonight's picks";
            // Absorbing, not plain: the enqueues above are check-then-insert against the partial
            // unique index, and losing that race means the work is already scheduled. The build's
            // own writes — the stat row, the scores, the in-place rating updates — must not roll
            // back over a coalesced job.
            await db.SaveAbsorbingRebuildRaceAsync(ct);
            if (scoreUpdates.Count > 0 && capture.PreservedGeneratedAt is { } preservedAt)
            {
                events.Publish(
                    accountId,
                    PipelineEventTypes.FeedScoresUpdated,
                    FeedScoreDrift.Payload(preservedAt, scoreUpdates));
            }

            events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?>
            {
                ["kind"] = "feed",
                // Items did not move, so nothing changed pick-wise — stated rather than omitted, so
                // the post-sync copy can say "0 picks changed" instead of waiting for a number that
                // is never coming.
                ["picksChanged"] = 0,
            });
            logger.LogInformation(
                "Feed unchanged for {AccountId} — snapshot suppressed ({Recalibrated} score(s) updated in place).",
                accountId, scoreUpdates.Count);
            return;
        }

        account.PipelineStage = PipelineStage.FeedReady;
        account.PipelineStageChangedAt = now;

        // Warm streaming availability for the new feed's titles so the cards' provider strips
        // populate (bounded, 24h-cached, global-shared). Skip if one's already queued.
        var availabilityQueued = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == JobKind.RefreshAvailability
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (!availabilityQueued)
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.RefreshAvailability,
                Priority = 3,
                EnqueuedAt = now,
            });
        }

        // A NEW snapshot exists, so the titles that carry the day have changed and their lens copy
        // is owed. Enqueued here and only here — deliberately not on the Preserve path, because
        // that page is unchanged and its copy is already written. This is what "per FRESH build"
        // means for the lens volume basis: eight builds in nine minutes produce one of these, not
        // eight. Priority 3 puts it behind the feed it decorates; it never blocks the build, whose
        // why pass is already time-boxed and would turn into a timeout if generation joined it.
        await EnqueueOnceAsync(accountId, JobKind.PersonaliseTitles, priority: 3, cursor: null, now, ct);

        // And the narrative itself, when the account's taste has actually moved. The comparison is
        // against the stored fingerprint, not a clock, so an ordinary week of ratings costs nothing
        // and a genuine shift is picked up the same night rather than waiting for Sunday.
        await EnqueueNarrativeIfProfileMovedAsync(accountId, taste, now, ct);

        // See the no-op path above: the availability and enrichment enqueues both race the index.
        await db.SaveAbsorbingRebuildRaceAsync(ct);

        events.Publish(accountId, PipelineEventTypes.StageChanged, new Dictionary<string, object?> { ["stage"] = "FeedReady" });
        events.Publish(accountId, PipelineEventTypes.FeedReady, new Dictionary<string, object?>
        {
            ["snapshotId"] = snapshot.Id,
            ["generatedAt"] = snapshot.GeneratedAt,
            // The delta sync that triggered this rebuild finished minutes ago and could not know
            // this number — the coordinator enqueues the build asynchronously and must never block
            // on it. So the sync reports what it ingested, and the follow-up frame reports what that
            // did to the page. The client joins the two.
            ["picksChanged"] = capture.PicksChanged,
            ["triggerReason"] = job.TriggerReason,
        });
        events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?>
        {
            ["kind"] = "feed",
            ["picksChanged"] = capture.PicksChanged,
        });
        logger.LogInformation(
            "Feed snapshot {SnapshotId} active for {AccountId} ({Candidates} scored, mean taste-prior magnitude {PriorMagnitude:0.000}, "
                + "{Judged} candidates judged before assembly).",
            snapshot.Id, accountId, candidates.Count, capture.AveragePriorMagnitude, capture.JudgedBeforeAssembly);
    }

    /// <summary>
    /// The one exit that produces no feed: <c>db.ModelArtifacts</c> handed back nothing Active.
    ///
    /// <para><b>Why this is not a one-line log.</b> "No Active artifact" has two causes that look
    /// identical from inside the query and could not be further apart in what they mean. Either the
    /// account has never trained — in which case "train a model first" is exactly right — or an
    /// Active artifact EXISTS and the <c>ModelArtifact</c> query filter is hiding it because it was
    /// trained against a different <see cref="FeatureSchema"/> version than this binary runs. The
    /// second case is not the user's problem to solve and cannot be solved by retraining, because
    /// whatever produced the incompatible artifact will produce another one; on 2026-09-10 a stale
    /// second job runner sharing this database did exactly that in a loop, and every build this
    /// binary claimed for a day left nothing behind but the word "skipped".</para>
    ///
    /// <para>So the two cases are separated, the incompatible one is reported at <b>Error</b> with
    /// both schema versions, and either way a <see cref="FeedBuildOutcome.Skipped"/> row lands — the
    /// instrument has to be able to show a run that produced nothing, or "the feed stopped moving"
    /// stays a story told only in logs that roll over.</para>
    /// </summary>
    private async Task SkipWithoutReadableArtifactAsync(
        SyncJob job, Guid accountId, DateTime now, System.Diagnostics.Stopwatch stopwatch, CancellationToken ct)
    {
        // IgnoreQueryFilters is the whole point: the row worth reporting is precisely the one the
        // filter above removed. Projected rather than materialised — ArtifactBytes is a model blob
        // and this path must stay cheap enough to run on every claimed build.
        var hidden = await db.ModelArtifacts
            .IgnoreQueryFilters()
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => new { a.Id, a.Version, a.FeatureSchemaJson })
            .FirstOrDefaultAsync(ct);

        if (hidden is null)
        {
            logger.LogWarning("BuildFeed skipped for {AccountId}: no active model artifact.", accountId);
            job.ProgressMessage = "skipped — train a model first";
        }
        else
        {
            var storedSchema = SchemaVersionOf(hidden.FeatureSchemaJson);
            logger.LogError(
                "BuildFeed skipped for {AccountId}: model artifact {ArtifactId} (v{Version}) is Active but was trained "
                + "against feature schema {StoredSchema} while this build runs schema {CurrentSchema}, so no scoring "
                + "path can read it. Retraining will NOT fix this if another process on this database is the one "
                + "answering Train jobs — check that only one job runner is live and that it is running this build.",
                accountId, hidden.Id, hidden.Version, storedSchema?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                FeatureSchema.Version);
            job.ProgressMessage = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "skipped — the active model was trained against feature schema {0}, this build needs {1}",
                storedSchema?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown version",
                FeatureSchema.Version);
        }

        // ModelArtifactId carries the artifact this build COULD NOT use where there was one, and
        // Guid.Empty where there was genuinely nothing — the column is not nullable and a migration
        // to make it so would buy nothing the Mode column does not already say.
        db.FeedBuildStats.Add(new FeedBuildStat
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = null,
            ModelArtifactId = hidden?.Id ?? Guid.Empty,
            BuiltAt = now,
            Mode = FeedBuildOutcome.Skipped,
            DegradedReason = job.ProgressMessage,
            // Written EXPLICITLY rather than left to the defaults. A skipped build retrieved nothing
            // and discovered nothing, and an empty JSON array says that; a NULL would be read months
            // later as "this build predates the column", which is a different and wrong story.
            ClustersUsed = 0,
            AnnPerClusterJson = "[]",
            SeedPoolSize = 0,
            SeedsUsed = 0,
            NewlyEligiblePerArmJson = "[]",
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The <c>version</c> an artifact's <c>FeatureSchemaJson</c> records, or null when the document
    /// predates the field or is not the object shape <c>TrainJobHandler</c> writes. Read in memory
    /// rather than through the stored generated column, so this stays inside the Application layer.
    /// </summary>
    private static int? SchemaVersionOf(string featureSchemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(featureSchemaJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.TryGetInt32(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The in-flight guard every enqueue in this codebase uses: check the partial unique index's
    /// own predicate, then insert. Losing the race is normal and is absorbed by
    /// <c>SaveAbsorbingRebuildRaceAsync</c> at the call site — the point of the check is to avoid
    /// provoking it, not to make it impossible.
    /// </summary>
    private async Task EnqueueOnceAsync(
        Guid accountId, JobKind kind, short priority, string? cursor, DateTime now, CancellationToken ct)
    {
        var inFlight = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && j.Kind == kind
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (inFlight)
        {
            return;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Priority = priority,
            EnqueuedAt = now,
            CursorJson = cursor,
        });
    }

    /// <summary>
    /// Queues a narrative rewrite when the account's taste fingerprint no longer matches the one
    /// its current narrative was written from. Computing the fingerprint costs a handful of
    /// aggregate queries and no LLM spend, so it is cheap to ask every Fresh build; writing the
    /// narrative is what costs, and that only happens when the answer is yes.
    /// </summary>
    private async Task EnqueueNarrativeIfProfileMovedAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime now, CancellationToken ct)
    {
        try
        {
            var built = await tasteFacts.BuildAsync(accountId, taste, now, ct);
            var current = await db.AccountTasteNarratives
                .Where(n => n.AccountId == accountId)
                .OrderByDescending(n => n.Version)
                .Select(n => n.ProfileHash)
                .FirstOrDefaultAsync(ct);
            if (string.Equals(current, built.ProfileHash, StringComparison.Ordinal))
            {
                return;
            }

            await EnqueueOnceAsync(accountId, JobKind.BuildTasteNarrative, priority: 4, cursor: null, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A narrative is a decoration on a feed that has already been built and is already
            // being served. Nothing here is worth failing the build over.
            logger.LogWarning(ex, "Could not evaluate the taste fingerprint for {AccountId}; narrative not queued.", accountId);
        }
    }

    /// <summary>
    /// One scored candidate. <paramref name="Predicted"/> is what the user sees;
    /// <paramref name="BaseScore"/> is the pre-calibration value (base model + online residual) and
    /// <paramref name="CalibrationDelta"/> is how far the calibrator moved it. The latter two are
    /// carried for the exposure log the closed-loop work lands next — deliberately NOT persisted as
    /// columns on <c>TitleScore</c>, which stays the write-only serving cache it is documented to be.
    /// </summary>
    private sealed record Candidate(
        Title Title, ScoredRow Row, decimal Predicted, decimal BaseScore, decimal CalibrationDelta);

    /// <summary>
    /// The counts assembly knows and the caller needs for <see cref="FeedBuildStat"/>. A mutable
    /// bag rather than a wider return type, so instrumentation never changes what assembly RETURNS —
    /// the null-means-no-op contract is load-bearing and stays exactly as it was.
    /// </summary>
    private sealed class BuildCapture
    {
        public FeedBuildMode Mode { get; set; }

        public int HeroEligibleCount { get; set; }

        public int RailsDropped { get; set; }

        public string? DegradedReason { get; set; }

        /// <summary>
        /// How many of this feed's cards are titles the previous Active snapshot did not show. The
        /// post-sync moment's "3 picks changed" — computed here because assembly is the only place
        /// that holds both feeds at once, and reported over SSE rather than persisted, so the whole
        /// visible-recalibration change stays migration-free.
        /// </summary>
        public int PicksChanged { get; set; }

        /// <summary>
        /// Set on the score-only Preserve path: the UNCHANGED GeneratedAt of the snapshot whose rows
        /// were updated in place. Null on every other path, including a true no-op.
        /// </summary>
        public DateTime? PreservedGeneratedAt { get; set; }

        /// <summary>Cards whose displayed rating moved on the score-only Preserve path.</summary>
        public IReadOnlyList<FeedScoreUpdate> ScoreUpdates { get; set; } = [];

        /// <summary>
        /// Mean |1 − taste prior| across the scored shortlist — how hard the product rule had to
        /// push this build. <c>FeedBuildStat</c> has no column for it and this change takes no
        /// migration, so it goes to the log; the number matters because the prior is explicitly
        /// meant to SHRINK as the model earns its own discrimination, and a magnitude that is still
        /// where it started in six months is a finding about the model rather than a success of the
        /// rule. Promote it to a column the next time this table is migrated for another reason.
        /// </summary>
        public double AveragePriorMagnitude { get; set; }

        /// <summary>How many shortlist candidates the pre-assembly fit pass actually judged.</summary>
        public int JudgedBeforeAssembly { get; set; }
    }

    /// <summary>
    /// Nearest-rank percentile over the shortlist's predicted ratings — no interpolation, so every
    /// reported value is a number the model actually produced for a real title.
    /// </summary>
    private static decimal? Percentile(IReadOnlyList<Candidate> candidates, double quantile)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var sorted = candidates.Select(c => c.Predicted).OrderBy(p => p).ToList();
        var index = Math.Clamp((int)Math.Ceiling(quantile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private async Task PersistScoresAsync(Guid accountId, Guid artifactId, IReadOnlyList<Candidate> candidates, DateTime now, CancellationToken ct)
    {
        var titleIds = candidates.Select(c => c.Title.Id).ToList();
        var existing = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.ModelArtifactId == artifactId && titleIds.Contains(s.TitleId))
            .ToDictionaryAsync(s => s.TitleId, ct);

        foreach (var candidate in candidates)
        {
            if (!existing.TryGetValue(candidate.Title.Id, out var score))
            {
                score = new TitleScore
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = candidate.Title.Id,
                    ModelArtifactId = artifactId,
                };
                db.TitleScores.Add(score);
            }

            score.PredictedRating = Math.Round(candidate.Predicted, 2);
            score.ContributionsJson = JsonSerializer.Serialize(
                candidate.Row.TopContributions.Select(c => new { feature = c.Feature, value = Math.Round(c.Contribution, 3) }));
            score.ScoredAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds the new feed and returns its snapshot — or returns null WITHOUT superseding anything
    /// when the build ran in Preserve mode and assembled exactly what the Active snapshot already
    /// shows (the no-op suppression half of the stable-"tonight" contract; see FeedBuildPolicy).
    /// </summary>
    private async Task<FeedSnapshot?> AssembleAsync(
        Account account, ModelArtifact artifact, FeatureVectorBuilder.TasteState taste,
        IReadOnlyList<Candidate> candidates, decimal minPredicted, string? triggerReason, DateTime now,
        BuildCapture capture, CancellationToken ct)
    {
        // ── The floor is soft; the content rules are not ────────────────────────────────────
        // Content filters are the user's statement about CONTENT — they bind absolutely and are
        // already applied in retrieval. MinPredictedRating is a statement about the MODEL's opinion,
        // and the model's output scale is not something the user can see or calibrate against. When
        // almost nothing clears it, honouring it strictly renders a blank — or worse, a page built
        // entirely out of the handful of titles that happened to squeak over; relaxing it and SAYING
        // SO shows honest, lower-confidence rankings instead. Same call Ask Reel already makes
        // (LiveSearchExpansionService's MinFloorRelaxedCount arm) — the feed build was the one
        // suggestion surface still treating "empty" as a verdict rather than a failure to look harder.
        //
        // TWO things changed here on 2026-09-10, both from the same live build.
        //
        // The TRIGGER is no longer "nothing clears it". A floor that admits fourteen titles out of a
        // 220-title shortlist is not selecting a page, it is BEING the page — hero, deep cuts and
        // every rail assemble out of those fourteen, and on the founder's account all fourteen were
        // reality shows. The bar is now FloorRelaxMinimum: four heroes' worth of supply, or the floor
        // is treated as the failure it is.
        //
        // The DESTINATION is no longer zero. Relaxing to nothing discards the user's setting whole
        // and lets the bottom of the shortlist onto rails; relaxing to the shortlist's 80th
        // percentile keeps the top fifth of what the model actually scored. Guarded with Math.Min so
        // a "relaxation" can never RAISE the floor above what the user asked for.
        var effectiveFloor = minPredicted;
        string? degradedReason = null;
        var clearingFloor = candidates.Count(c => c.Predicted >= minPredicted);
        if (minPredicted > 0m && clearingFloor < FloorRelaxMinimum)
        {
            effectiveFloor = Math.Min(minPredicted, Percentile(candidates, FloorRelaxQuantile) ?? 0m);

            // Invariant culture, deliberately: these numbers are ratings on the same 0–10 scale the
            // settings screen shows and ContentFilterRules stores, and a server whose locale renders
            // "9,9" would quote the user a threshold they never typed.
            degradedReason = candidates.Count > 0
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Your ≥{0:0.0} “for you” floor left only {1} title{2} tonight; relaxed to {3:0.0} for this build.",
                    minPredicted,
                    clearingFloor,
                    clearingFloor == 1 ? string.Empty : "s",
                    effectiveFloor)
                : null;
        }

        // An empty candidate pool is a different failure with a different answer: retrieval, not
        // ranking, came back with nothing, so the feed must name the content condition responsible.
        if (candidates.Count == 0)
        {
            degradedReason = await starvation.ExplainEmptyPoolAsync(account.Id, ct)
                ?? "No candidates survived this build. Try relaxing a filter.";
        }

        // ── The taste prior ─────────────────────────────────────────────────────────────────
        // A multiplicative RANKING adjustment on top of the calibrated prediction, and on nothing
        // else — PredictedRating, the card's number, is untouched by every line below. See
        // TastePrior for what it corrects and why it is meant to shrink toward 1.0 as the model
        // earns its own discrimination. Computed once per build over the shortlist; the components
        // are kept (not just the product) so the card can explain the demotion in the user's own
        // terms — "you have rated 3 reality titles ever".
        var tastePrior = TastePrior.From(taste);
        var priorByTitle = candidates
            .GroupBy(c => c.Title.Id)
            .ToDictionary(g => g.Key, g => tastePrior.ComponentsFor(g.First().Title));
        TastePrior.Components PriorOf(Title title) =>
            priorByTitle.TryGetValue(title.Id, out var components)
                ? components
                : tastePrior.ComponentsFor(title);
        double Prior(Title title) => PriorOf(title).Multiplier;
        capture.AveragePriorMagnitude = priorByTitle.Count > 0
            ? priorByTitle.Values.Average(p => Math.Abs(1d - p.Multiplier))
            : 0d;

        // Per-title, per-role best-affinity person, restricted to people the user has actually rated
        // (count ≥ 2). Keying by role lets the why-sentence cite the person matching the contribution
        // (a directorAffinity clause names a director, never an actor); restricting to rated people
        // means no clause ever names someone outside the user's taste. The Role column is load-bearing.
        // Single-sourced in VerifiedPeople — the title endpoint's tap-through breakdown reads the
        // same data, so the card sentence and the detail chips can never disagree.
        var shortIds = candidates.Select(c => c.Title.Id).ToList();
        var personsByTitle = VerifiedPeople.BestByRole(await VerifiedPeople.ForTitlesAsync(db, taste, shortIds, ct));

        // LLM-derived tone per candidate, for the templater's anchor-link clause. The templater
        // prints a tone ONLY when it passes the closed vocabulary — rows extracted before
        // vocabulary enforcement carry polluted free-text labels, so the column is never trusted raw.
        var toneByTitle = await db.TitleAttributes
            .Where(a => shortIds.Contains(a.TitleId) && a.Tone != null)
            .ToDictionaryAsync(a => a.TitleId, a => a.Tone, ct);

        // Anchors: candidate loved titles by name. Drawn from the WIDE recency-weighted anchor pool
        // as well as the strict all-time top, because the strict top of a 3,600-rating history never
        // changes and was the whole reason the same three rows kept coming back.
        var anchorCandidateIds = taste.LovedAnchorPool.Concat(taste.TopLovedTitleIds).Distinct().ToList();
        var anchorTitles = await db.Titles
            .Where(t => anchorCandidateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // ── Continuity inputs: what is on the page NOW, and what yesterday looked like ──────
        // All read BEFORE the supersede below. The Active snapshot is both the Preserve-mode
        // template (its hero and anchors ARE tonight's contract) and the no-op baseline; the newest
        // snapshot from any EARLIER UTC day supplies the hold-back set that makes a new day rotate
        // for real instead of re-dealing yesterday's rows.
        var previousSnapshot = await db.FeedSnapshots
            .Where(s => s.AccountId == account.Id && s.Status == SnapshotStatus.Active)
            .OrderByDescending(s => s.GeneratedAt)
            .FirstOrDefaultAsync(ct);
        var previousItems = previousSnapshot is null
            ? []
            : await db.FeedItems
                .Where(i => i.FeedSnapshotId == previousSnapshot.Id)
                .OrderBy(i => i.Row).ThenBy(i => i.Rank)
                .ToListAsync(ct);

        // What the current Active snapshot anchors on — the Preserve keep-list, and the hold-back
        // set for a user-forced Fresh build (a manual refresh must visibly rotate away from the
        // page it replaces).
        var previousAnchors = previousItems
            .Where(i => i.Row == FeedRowKind.BecauseYouLoved && i.AnchorTitleId != null)
            .Select(i => i.AnchorTitleId!.Value)
            .Distinct()
            .ToList();

        // What YESTERDAY anchored on — held back by the daily rotation so day N+1 cannot repeat
        // day N while any other loved title is available. Empty for a first-ever build; possibly
        // empty for an account that rebuilt 7+ times today (pruning keeps only the last 7), which
        // degrades to ordinary rotation rather than failing.
        var previousDaySnapshotId = await db.FeedSnapshots
            .Where(s => s.AccountId == account.Id && s.GeneratedAt < now.Date)
            .OrderByDescending(s => s.GeneratedAt)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        var previousDayAnchors = previousDaySnapshotId is null
            ? []
            : (await db.FeedItems
                .Where(i => i.FeedSnapshotId == previousDaySnapshotId
                    && i.Row == FeedRowKind.BecauseYouLoved
                    && i.AnchorTitleId != null)
                .Select(i => i.AnchorTitleId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var mode = FeedBuildPolicy.Decide(
            previousSnapshot?.GeneratedAt, previousSnapshot?.ModelArtifactId, artifact.Id, triggerReason, now);
        capture.Mode = mode;

        // The drift pick: the loved title whose plot embedding sits nearest the user's RECENT-taste
        // centroid, so one row always reflects where taste is NOW. Null when there is no recent
        // signal or the loved set carries no embeddings.
        Guid? driftPick = null;
        if (taste.RecentCentroid is { } recentCentroid)
        {
            driftPick = taste.LovedEmbeddings
                .Where(kv => anchorTitles.ContainsKey(kv.Key))
                .Select(kv => (Id: kv.Key, Sim: Cosine(kv.Value, recentCentroid)))
                .OrderByDescending(x => x.Sim)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefault();
        }

        var anchorPool = taste.LovedAnchorPool.Where(anchorTitles.ContainsKey).ToList();
        var allTimeOrder = taste.TopLovedTitleIds.Where(anchorTitles.ContainsKey).ToList();
        var rotationOffset = AnchorRotation.OffsetFor(now);

        List<Guid> anchorIds;
        if (mode == FeedBuildMode.Preserve)
        {
            // Tonight's contract: the anchors the user is already looking at survive the rebuild
            // for as long as they stay anchor-eligible. A slot only vacates when its anchor left
            // the loved pool, and only that slot is re-drawn — with today's offset, holding back
            // both yesterday's rows and the anchors being kept, so the replacement is genuinely new.
            anchorIds = previousAnchors.Where(anchorTitles.ContainsKey).Take(AnchorRowCount).ToList();
            if (anchorIds.Count < AnchorRowCount)
            {
                var holdBack = new HashSet<Guid>(previousDayAnchors);
                holdBack.UnionWith(anchorIds);
                anchorIds.AddRange(AnchorRotation
                    .Select(driftPick, anchorPool, allTimeOrder, holdBack, rotationOffset, AnchorRowCount)
                    .Where(id => !anchorIds.Contains(id))
                    .Take(AnchorRowCount - anchorIds.Count));
            }
        }
        else
        {
            // Fresh. A user-forced build (manual refresh / filters changed) must visibly rotate
            // away from the page it replaces, so it holds back the superseded snapshot's anchors;
            // the daily and new-model cases hold back yesterday's, which is what guarantees a new
            // day cannot re-deal the old one.
            var holdBack = FeedBuildPolicy.IsUserForced(triggerReason)
                ? previousAnchors.ToHashSet()
                : previousDayAnchors;
            anchorIds = AnchorRotation.Select(driftPick, anchorPool, allTimeOrder, holdBack, rotationOffset, AnchorRowCount);
        }

        var anchors = anchorIds.Select(id => anchorTitles[id]).ToList();

        // Plot-embedding cosine for "because you loved" similarity + hero diversity — far richer
        // than genre overlap. Falls back to genre Jaccard for any title not yet embedded.
        var embIds = candidates.Select(c => c.Title.Id).Concat(anchors.Select(a => a.Id)).Distinct().ToList();
        var embByTitle = (await db.TitleEmbeddings.Where(e => embIds.Contains(e.TitleId)).ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());
        double Similarity(Title a, Title b) =>
            embByTitle.TryGetValue(a.Id, out var va) && embByTitle.TryGetValue(b.Id, out var vb)
                ? Cosine(va, vb)
                : GenreJaccard(a, b);

        // Post-scoring preference-tag boost. The declared tags never touched the feed before — only
        // Ask Reel. Here they become a bounded, multiplicative re-rank nudge on TOP of the model's
        // rating: a title whose plot embedding is close to any tag lifts by ≤ PreferenceTagWeight.
        // Tag vectors load once per build; titles without an embedding (or users without tags) get a
        // neutral 1.0 — this only sharpens ordering, it never changes the displayed model rating.
        var tagVectors = await LoadPreferenceTagVectorsAsync(account.Id, ct);
        double PreferenceBoost(Title t) =>
            tagVectors.Count > 0 && embByTitle.TryGetValue(t.Id, out var vec)
                ? 1d + (PreferenceTagWeight * TagAffinity(vec, tagVectors))
                : 1d;

        ExplanationTemplater.Context WhyContext(Candidate candidate, Title? anchor)
        {
            var persons = personsByTitle.GetValueOrDefault(candidate.Title.Id) ?? ExplanationTemplater.NoPersons;
            var ratedGenres = candidate.Title.Genres.Where(taste.GenreRatings.ContainsKey).ToHashSet();
            var sharedGenre = anchor is null
                ? null
                : candidate.Title.Genres.FirstOrDefault(g => anchor.Genres.Contains(g, StringComparer.OrdinalIgnoreCase));
            return new ExplanationTemplater.Context(
                candidate.Title,
                candidate.Row.TopContributions,
                persons,
                ratedGenres,
                anchor?.Name,
                sharedGenre,
                toneByTitle.GetValueOrDefault(candidate.Title.Id));
        }

        decimal Freshness(Title title)
        {
            var released = title.ReleasedAt ?? title.FirstAiredAt;
            if (released is null)
            {
                return 1m;
            }

            var ageDays = Math.Max(0d, (now - released.Value).TotalDays);
            return ageDays > 365 ? 1m : 1m + (decimal)(0.10 * Math.Exp(-ageDays / 180d));
        }

        // The ranking key the whole page is ordered by, once, in one place: the calibrated
        // prediction the card shows, times the account-level taste prior, times the two bounded
        // presentation nudges that were already here. Every ordering below multiplies this by its
        // own slate term (freshness, anchor similarity) rather than re-deriving the common part.
        decimal RankKey(Candidate c) =>
            c.Predicted * (decimal)Prior(c.Title) * (decimal)PreferenceBoost(c.Title);

        // ── Pre-assembly fit pass (Fresh only) ──────────────────────────────────────────────
        // The narrative-conditioned fit verdict used to arrive AFTER the page was assembled, which
        // made it a caption and a last-resort hero swap — it could say a hero was wrong but never
        // stop it being chosen. Judging the top slice first turns the same call into a ranking
        // signal: the hero is ordered by calibrated × prior × (0.5 + fit), so a title the account's
        // own taste narrative says nothing for cannot lead the page on a 0.2-point scoring edge.
        //
        // Fresh only. A Preserve rebuild keeps tonight's seats by contract, so re-judging them could
        // only produce an order it is not allowed to use — and would spend for the privilege.
        // Every verdict lands in the shared 6h fit cache, so the why pass below reads them back for
        // free and the pass costs the build ~5 calls in total rather than two passes' worth.
        var preAssemblyFits = mode == FeedBuildMode.Fresh
            ? await JudgeFitBeforeAssemblyAsync(account.Id, taste, candidates, effectiveFloor, RankKey, ct)
            : new Dictionary<Guid, double>();
        capture.JudgedBeforeAssembly = preAssemblyFits.Count;

        // The LLM term, as a multiplier. An unjudged title carries NeutralFit, which makes this
        // exactly 1.0 — so a build with no agent, or one whose budget ran out, orders by
        // calibrated × prior and nothing else. That is the documented fallback, not a degradation.
        double FitFactor(Guid titleId) =>
            NeutralFit + (preAssemblyFits.TryGetValue(titleId, out var fit) ? fit : NeutralFit);

        decimal HeroKey(Candidate c) =>
            RankKey(c) * Freshness(c.Title) * (decimal)FitFactor(c.Title.Id);

        // ── Team-draft interleaving ─────────────────────────────────────────────────────────
        // Off unless INTERLEAVE_ENABLED says otherwise, and Fresh-only: a Preserve rebuild's whole
        // contract is that tonight's seats do not move, so re-drafting them would be a different
        // page under the same GeneratedAt. Preserve instead CARRIES the attribution forward from
        // the snapshot it preserves (below), which keeps a kept card counting for the ranker that
        // actually put it there.
        //
        // `interleaver` being null is the single branch that decides this. Every `else` below is
        // the code that shipped before this harness existed, unchanged.
        var interleaver = mode == FeedBuildMode.Fresh ? SlateInterleaver.For(_interleave) : null;

        // Minted here rather than at the write below because a slate's seed and its InterleaveGroup
        // are both derived from it. Harmlessly discarded on the Preserve no-op path, which returns
        // before any of it is used.
        var snapshotId = Guid.NewGuid();

        // Which ranker contributed each title, filled by the drafts and read by AddItem. Keyed by
        // title and not by (row, rank) because the shared `used` set already guarantees a title
        // appears at most once on the whole feed.
        var attribution = new Dictionary<Guid, (string PolicyId, Guid Group)>();

        // One slate, drafted between the two policies. `phases` are admissibility predicates tried
        // in order: the hero passes three of them (diversity + confidence floor, then the floor
        // alone, then anything) so the draft reproduces the same three-stage selection the
        // non-interleaved hero has always done, with the alternation carrying across the stages so
        // relaxing never costs balance. A rail passes none and takes the pool as it stands.
        List<Candidate> DraftSlate(
            FeedRowKind row,
            Guid? anchorId,
            IReadOnlyList<(Candidate Candidate, decimal Fresh, double Affinity)> pool,
            int size,
            params Func<Candidate, IReadOnlyList<Candidate>, bool>[] phases)
        {
            var byId = pool.ToDictionary(x => x.Candidate.Title.Id);
            var draft = interleaver!.Begin(
                snapshotId,
                FeedPaginator.RowKeyFor(row, anchorId),
                [.. pool.Select(x => new PolicyCandidate(
                    x.Candidate.Title.Id,
                    x.Candidate.Predicted,
                    x.Fresh,
                    PreferenceBoost(x.Candidate.Title),
                    x.Candidate.Title.TmdbPopularity ?? 0m,
                    x.Affinity,
                    Prior(x.Candidate.Title),
                    // Null, not NeutralFit, where the pass did not reach this title: null is what
                    // makes ProfileRerankPolicy fall back to the calibrated key for it rather than
                    // ordering it as a genuine mid-scoring verdict it never received.
                    preAssemblyFits.TryGetValue(x.Candidate.Title.Id, out var judged) ? judged : null))]);

            List<Candidate> Selected() => [.. draft.Selected.Select(id => byId[id].Candidate)];
            var stages = phases.Length > 0
                ? phases
                : [(Func<Candidate, IReadOnlyList<Candidate>, bool>)((_, _) => true)];
            foreach (var phase in stages)
            {
                draft.Fill(size, id => phase(byId[id].Candidate, Selected()));
            }

            foreach (var (titleId, policyId) in draft.Picks)
            {
                attribution[titleId] = (policyId, draft.GroupId);
            }

            return Selected();
        }

        // Hero: greedy pick with genre-diversity shaping (MMR with Jaccard until embeddings land).
        // Ordered by HeroKey — calibrated × prior × freshness × preference × (0.5 + fit) — so the
        // headline row is the one place all three ranking layers speak at once. The ADMISSION
        // predicate is deliberately still c.Predicted: the confidence floor is a claim about what
        // the model believes, and a title must not be admitted to the hero's own bar by a prior or
        // an LLM opinion the label "confident" was never measured against.
        var heroPool = candidates
            .Where(c => c.Predicted >= Math.Max(HeroConfidenceFloor, effectiveFloor) && c.Title.PosterPath != null)
            .OrderByDescending(HeroKey)
            .ToList();
        // Recorded here, before the backfill below can relax the floor: a full hero with a zero here
        // means the headline row is silently reaching, which is the founder's complaint in his words.
        capture.HeroEligibleCount = heroPool.Count;
        var candidatesById = candidates.ToDictionary(c => c.Title.Id);
        var hero = new List<Candidate>();
        if (interleaver is not null)
        {
            // The interleaved hero. The pool is the WIDE one — the never-open-blank set — and the
            // confidence floor is applied as an admissibility phase rather than as a filter, so the
            // three stages the hero has always had (diversity inside the floor → the floor alone →
            // anything with a poster) become three phases of one draft. Every seat therefore has a
            // contributing ranker, including the ones the floor had to be relaxed to fill, which is
            // what makes "PolicyId on every item" a property rather than a hope.
            var heroFloor = Math.Max(HeroConfidenceFloor, effectiveFloor);
            hero = DraftSlate(
                FeedRowKind.Hero,
                null,
                [.. candidates
                    .Where(c => c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                    .Select(c => (Candidate: c, Fresh: Freshness(c.Title), Affinity: 1d))],
                HeroSize,
                (c, chosen) => c.Predicted >= heroFloor
                    && (chosen.Count == 0 || chosen.Max(h => Similarity(h.Title, c.Title)) < 0.8d),
                (c, _) => c.Predicted >= heroFloor,
                (_, _) => true);
        }
        else if (mode == FeedBuildMode.Preserve)
        {
            // Hero continuity: last build's picks in their order, minus anything that left the
            // candidate set — a rated or not-interested title falls out of eligibility upstream, so
            // a reaction vacates exactly its own slot and the MMR loop below refills only the gap.
            hero.AddRange(previousItems
                .Where(i => i.Row == FeedRowKind.Hero)
                .OrderBy(i => i.Rank)
                .Where(i => candidatesById.ContainsKey(i.TitleId))
                .Select(i => candidatesById[i.TitleId])
                .Take(HeroSize));
        }

        if (interleaver is null)
        {
            foreach (var candidate in heroPool)
            {
                if (hero.Count >= HeroSize)
                {
                    break;
                }

                if (hero.Contains(candidate))
                {
                    continue; // already kept from the previous build
                }

                var maxSim = hero.Count == 0 ? 0d : hero.Max(h => Similarity(h.Title, candidate.Title));
                if (maxSim < 0.8d)
                {
                    hero.Add(candidate);
                }
            }

            if (hero.Count < HeroSize)
            {
                hero.AddRange(heroPool.Except(hero).Take(HeroSize - hero.Count));
            }
        }

        // Never open blank: if the 7.5 confidence floor left the hero short — a sparse-history user
        // whose best predictions all sit below it — backfill from the top remaining scored candidates
        // (poster + min-predicted only, floor dropped). The free-tier home is the hero alone, so an
        // empty hero is an empty product. These are honest model rankings, just lower-confidence.
        if (interleaver is null && hero.Count < HeroSize)
        {
            var chosen = hero.Select(h => h.Title.Id).ToHashSet();
            hero.AddRange(candidates
                .Where(c => !chosen.Contains(c.Title.Id) && c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                .OrderByDescending(HeroKey)
                .Take(HeroSize - hero.Count));
        }

        // ── Hero media balance ──────────────────────────────────────────────────────────────
        // The prior demotes what the account has no history in; this covers the residual case where
        // a whole MEDIUM is squeezed out by a fraction of a point. A 90%-movies account gets a hero
        // that carries at least round(HeroSize × MovieRatedShare) movies — capped one seat below the
        // row, so this can never make the hero single-medium by itself.
        //
        // The spares are drawn from the same admissible set the never-open-blank backfill uses:
        // poster + effectiveFloor, ordered by HeroKey. Nothing here reaches below the floor to hit a
        // mix number, and when the shortlist genuinely could not supply the medium, the page SAYS so
        // instead of quietly missing the target.
        //
        // FRESH and non-interleaved only, for two separate contracts. A Preserve rebuild's seats do
        // not move — swapping one for media balance would be a different page under the same
        // GeneratedAt, which is exactly what the stable-"tonight" rule forbids; the mix is applied on
        // the next Fresh build instead. And an interleaved build's every seat must carry the ranker
        // that drafted it, so a post-draft swap would orphan a seat and quietly bias the comparison
        // toward whichever arm happened to supply the medium (stated in docs/interleaving.md).
        var heroTarget = MediaMix.TargetFor(HeroSize, taste.MovieRatedShare);
        if (interleaver is null && mode == FeedBuildMode.Fresh && heroTarget.Count > 0 && hero.Count > 0)
        {
            var seated = hero.Select(h => h.Title.Id).ToHashSet();
            var heroSparePool = candidates
                .Where(c => !seated.Contains(c.Title.Id) && c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                .OrderByDescending(HeroKey)
                .ToList();
            var (balanced, met) = MediaMix.Balance(
                hero,
                heroSparePool,
                c => (c.Title.MediaType == MediaType.Movie) == heroTarget.WantMovies,
                Math.Min(heroTarget.Count, hero.Count));

            // Re-sorted rather than left where the swap landed: a replacement that outranks the seat
            // it took should lead, and the hero's order is a claim about ranking, not about history.
            hero = [.. balanced.OrderByDescending(HeroKey)];
            if (!met)
            {
                degradedReason = Append(
                    degradedReason,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Tonight's picks are short on {0} — the shortlist had too few above your floor to reach {1} of {2}.",
                        heroTarget.WantMovies ? "films" : "shows",
                        heroTarget.Count,
                        HeroSize));
            }
        }

        capture.DegradedReason = degradedReason;
        if (degradedReason is not null)
        {
            logger.LogWarning("Feed build degraded for {AccountId}: {Reason}", account.Id, degradedReason);
        }

        // Assembly writes into a buffer, not the DbContext: whether anything is persisted at all is
        // decided AFTER the whole feed exists, by comparing it against the Active snapshot. Nothing
        // is superseded until a genuinely different feed is in hand.
        var planned = new List<PlannedItem>();

        void AddItem(FeedRowKind row, Title? anchor, int rank, Candidate candidate)
        {
            var item = new PlannedItem(row, anchor?.Id, rank, candidate, WhyContext(candidate, anchor));
            if (attribution.TryGetValue(candidate.Title.Id, out var drafted))
            {
                item.PolicyId = drafted.PolicyId;
                item.InterleaveGroup = drafted.Group;
            }

            planned.Add(item);
        }

        for (var i = 0; i < hero.Count; i++)
        {
            AddItem(FeedRowKind.Hero, null, i, hero[i]);
        }

        var used = hero.Select(h => h.Title.Id).ToHashSet();

        // The discover pool skews popular, so the deep-cuts bar is the lower-popularity 70th
        // percentile + a modest predicted floor. Computed here (not just before the deep-cuts row)
        // so the quota-reservation pass below can see the same threshold.
        var popThreshold = candidates.Count > 0
            ? candidates.Select(c => c.Title.TmdbPopularity ?? 0m).OrderBy(p => p).ElementAt(candidates.Count * 7 / 10)
            : 0m;

        // Diversity / anti-starvation: the Because-you-loved rows greedily consume the best-matching
        // candidates against the SHARED `used` set. On a thin model that drains the pool before Deep
        // cuts / New for you can reach their RowMinMembers floor, so those rows silently vanish. We
        // reserve each later row's top-few candidates BEFORE the anchor fill so they survive. The
        // shared `used` set still guarantees no title repeats across the feed — reserved titles are
        // only WITHHELD from the greedy loved fill here, never pre-added, and remain free to place.
        bool IsDeepCut(Candidate c) => c.Predicted >= Math.Max(6m, effectiveFloor)
            && (c.Title.TmdbPopularity ?? 0m) < popThreshold;
        bool IsNewForYou(Candidate c) => c.Predicted >= effectiveFloor
            && (c.Title.ReleasedAt ?? c.Title.FirstAiredAt) is { } r && (now - r).TotalDays <= 900;

        var reserved = new HashSet<Guid>();
        foreach (var id in candidates
                     .Where(c => !used.Contains(c.Title.Id) && IsDeepCut(c))
                     .OrderByDescending(RankKey)
                     .Take(RowMinMembers)
                     .Select(c => c.Title.Id))
        {
            reserved.Add(id);
        }

        foreach (var id in candidates
                     .Where(c => !used.Contains(c.Title.Id) && IsNewForYou(c))
                     .OrderByDescending(c => RankKey(c) * Freshness(c.Title))
                     .Take(RowMinMembers)
                     .Select(c => c.Title.Id))
        {
            reserved.Add(id);
        }

        // Rails carry the same media-mix floor the hero does, at their own width. A rail is a
        // narrower promise than the hero, so a miss here is not worth a banner — the target is
        // simply met from what the rail's own pool holds, or it is not.
        // Preserve is excluded here for the same reason the hero is: a kept rail's members are
        // tonight's contract. Only the interleaver-free FRESH fill balances.
        var rowTarget = mode == FeedBuildMode.Fresh
            ? MediaMix.TargetFor(RowSize, taste.MovieRatedShare)
            : (WantMovies: true, Count: 0);
        List<Candidate> BalanceRow(IReadOnlyList<Candidate> members, IReadOnlyList<Candidate> spares)
        {
            if (rowTarget.Count == 0 || members.Count == 0)
            {
                return [.. members];
            }

            var (balanced, _) = MediaMix.Balance(
                members,
                spares,
                c => (c.Title.MediaType == MediaType.Movie) == rowTarget.WantMovies,
                Math.Min(rowTarget.Count, members.Count));
            return balanced;
        }

        // Rail continuity mirrors hero continuity. Members are keyed to their anchor from the
        // PREVIOUS snapshot rather than re-dealt greedily, because the greedy fill consumes a shared
        // candidate pool in anchor-iteration order — and that order is not recoverable from FeedItems
        // (rails tie on (Row, Rank), so the read-back order is whatever the database felt like). A
        // Preserve rebuild that re-dealt would shuffle members BETWEEN rails on identical inputs,
        // which both churns the page and defeats the no-op suppression below. Kept members keep
        // their seats; only vacated slots go back through the similarity fill.
        var previousRailMembers = previousItems
            .Where(i => i.Row == FeedRowKind.BecauseYouLoved && i.AnchorTitleId != null)
            .GroupBy(i => i.AnchorTitleId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Rank).Select(i => i.TitleId).ToList());

        foreach (var anchor in anchors)
        {
            var members = new List<Candidate>();
            if (mode == FeedBuildMode.Preserve && previousRailMembers.TryGetValue(anchor.Id, out var previousMemberIds))
            {
                foreach (var titleId in previousMemberIds)
                {
                    if (members.Count >= RowSize)
                    {
                        break;
                    }

                    // No similarity re-check for kept members — they earned this rail when it was
                    // dealt, and their similarity to the anchor cannot have changed since.
                    if (candidatesById.TryGetValue(titleId, out var keptMember)
                        && !used.Contains(titleId)
                        && keptMember.Predicted >= effectiveFloor)
                    {
                        members.Add(keptMember);
                    }
                }
            }

            if (members.Count < RowSize)
            {
                var keptIds = members.Select(m => m.Title.Id).ToHashSet();
                var railPool = candidates
                    .Where(c => !used.Contains(c.Title.Id) && !keptIds.Contains(c.Title.Id)
                        && !reserved.Contains(c.Title.Id) && c.Predicted >= effectiveFloor)
                    .Select(c => new { Candidate = c, Sim = Similarity(anchor, c.Title) })
                    .Where(x => x.Sim > 0.15d)
                    .ToList();

                // The rail's ordering key folds the anchor-similarity term into SlateAffinity, so
                // the calibrated policy reproduces this row's historical comparator exactly rather
                // than substituting the hero's.
                var railOrder = railPool
                    .OrderByDescending(x => (double)RankKey(x.Candidate) * (0.5d + x.Sim))
                    .Select(x => x.Candidate)
                    .ToList();
                var taking = RowSize - members.Count;
                members.AddRange(interleaver is not null
                    ? DraftSlate(
                        FeedRowKind.BecauseYouLoved,
                        anchor.Id,
                        [.. railPool.Select(x => (x.Candidate, Fresh: 1m, Affinity: 0.5d + x.Sim))],
                        taking)
                    : BalanceRow(
                        [.. railOrder.Take(taking)],
                        [.. railOrder.Skip(taking)]));
            }

            if (members.Count < RowMinMembers)
            {
                capture.RailsDropped++;
                continue; // a thin row reads worse than no row
            }

            for (var i = 0; i < members.Count; i++)
            {
                used.Add(members[i].Title.Id);
                AddItem(FeedRowKind.BecauseYouLoved, anchor, i, members[i]);
            }
        }

        // ── v1.1 rows: Deep cuts (high predicted × low popularity) + New & for you ──────────
        // Reserved quotas above guarantee these rows can still reach their floor even after the
        // loved fill; the greedy top-slice selection is otherwise unchanged.
        var deepCutPool = candidates.Where(c => !used.Contains(c.Title.Id) && IsDeepCut(c)).ToList();
        var deepCuts = interleaver is not null
            ? DraftSlate(
                FeedRowKind.DeepCuts,
                null,
                [.. deepCutPool.Select(c => (Candidate: c, Fresh: 1m, Affinity: 1d))],
                RowSize)
            : BalanceRow(
                [.. deepCutPool.OrderByDescending(RankKey).Take(RowSize)],
                [.. deepCutPool.OrderByDescending(RankKey).Skip(RowSize)]);
        if (deepCuts.Count >= RowMinMembers)
        {
            for (var i = 0; i < deepCuts.Count; i++)
            {
                used.Add(deepCuts[i].Title.Id);
                AddItem(FeedRowKind.DeepCuts, null, i, deepCuts[i]);
            }
        }
        else
        {
            capture.RailsDropped++;
        }

        var newForYouPool = candidates.Where(c => !used.Contains(c.Title.Id) && IsNewForYou(c)).ToList();
        var newForYou = interleaver is not null
            ? DraftSlate(
                FeedRowKind.NewForYou,
                null,
                [.. newForYouPool.Select(c => (Candidate: c, Fresh: Freshness(c.Title), Affinity: 1d))],
                RowSize)
            : BalanceRow(
                [.. newForYouPool.OrderByDescending(c => RankKey(c) * Freshness(c.Title)).Take(RowSize)],
                [.. newForYouPool.OrderByDescending(c => RankKey(c) * Freshness(c.Title)).Skip(RowSize)]);
        if (newForYou.Count >= RowMinMembers)
        {
            for (var i = 0; i < newForYou.Count; i++)
            {
                used.Add(newForYou[i].Title.Id);
                AddItem(FeedRowKind.NewForYou, null, i, newForYou[i]);
            }
        }
        else
        {
            capture.RailsDropped++;
        }

        // ── No-op suppression (Preserve only) ───────────────────────────────────────────────
        // A background rebuild that assembled exactly what is already Active mints no new snapshot:
        // no supersede, no feed-ready event — the caller sees null and skips the reseed.
        // DegradedReason participates in the comparison so a build that changed only the page's
        // explanation banner still ships.
        //
        // "No new snapshot" is NOT the same as "write nothing", and conflating the two is what made
        // recalibration invisible. If the model's opinion of a kept card has genuinely moved, the
        // EXISTING snapshot's rows are updated in place — same snapshot id, same GeneratedAt — and
        // the caller announces it on its own SSE frame. See FeedScoreDrift for why the two decisions
        // are separate. When nothing moved, this stays the true no-op it has always been.
        if (mode == FeedBuildMode.Preserve
            && previousSnapshot is not null
            && string.Equals(previousSnapshot.DegradedReason, degradedReason, StringComparison.Ordinal)
            && SameFeed(previousItems, planned))
        {
            capture.PreservedGeneratedAt = previousSnapshot.GeneratedAt;
            capture.ScoreUpdates = ApplyScoreDrift(previousItems, planned, Freshness, Prior, PriorOf);
            return null;
        }

        // Spare hero candidates for the why pass's low-fit hero swap: the same orderings the hero
        // picks themselves were drawn from — the confidence-floor pool's leftovers first (the MMR
        // loop's remainder), then the never-open-blank backfill ordering — restricted to titles not
        // placed anywhere on this feed, so a swap can never seat a card another rail already shows.
        var heroSpares = heroPool
            .Concat(candidates
                .Where(c => c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                .OrderByDescending(HeroKey))
            .Where(c => !used.Contains(c.Title.Id))
            .DistinctBy(c => c.Title.Id)
            .Take(HeroSize)
            .ToList();

        // ── LLM why pass ────────────────────────────────────────────────────────────────────
        // AFTER the no-op decision, so a suppressed rebuild never spends a call: kept cards reuse
        // their persisted whys (Preserve), the rest go through the agent within a time-boxed budget
        // into the same 6h fit cache the paginator uses, and every rail ends repeat-free.
        await ApplyCuratedWhysAsync(
            account.Id, taste, planned, previousItems, mode,
            anchors.ToDictionary(a => a.Id, a => a.Name),
            heroSpares, candidate => WhyContext(candidate, null), ct);

        // Supersede previous snapshots, write the new one.
        foreach (var stale in await db.FeedSnapshots
                     .Where(s => s.AccountId == account.Id && s.Status == SnapshotStatus.Active)
                     .ToListAsync(ct))
        {
            stale.Status = SnapshotStatus.Superseded;
        }

        var snapshot = new FeedSnapshot
        {
            Id = snapshotId,
            AccountId = account.Id,
            ModelArtifactId = artifact.Id,
            GeneratedAt = now,
            Status = SnapshotStatus.Active,
            DegradedReason = degradedReason,
        };
        db.FeedSnapshots.Add(snapshot);

        // How much of the page is genuinely different from what it replaces — the "3 picks changed"
        // the post-sync moment reports. Counted before the write so it reads the OLD snapshot's
        // titles, and by title rather than by slot: a card that merely moved rank is the same pick.
        var previousTitleIds = previousItems.Select(i => i.TitleId).ToHashSet();
        capture.PicksChanged = planned.Count(i => !previousTitleIds.Contains(i.Candidate.Title.Id));

        foreach (var item in planned)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                FeedSnapshotId = snapshot.Id,
                Row = item.Row,
                AnchorTitleId = item.AnchorTitleId,
                Rank = item.Rank,
                TitleId = item.Candidate.Title.Id,
                PredictedRating = DisplayRating(item.Candidate),
                FinalScore = FinalScoreOf(item.Candidate, Freshness, Prior),
                WhyThisSentence = item.WhyThis,
                ExplanationJson = ExplanationOf(item.Candidate, PriorOf(item.Candidate.Title)),
            });
        }

        // ── Interleaving attribution carried across a Preserve rebuild ──────────────────────
        // A Preserve rebuild keeps tonight's seats, so the cards it re-stages are the SAME cards
        // the interleaved Fresh build drafted — and an exposure row for them with no PolicyId would
        // quietly drop them out of the experiment while they are still on screen. The attribution
        // is read back from the snapshot being preserved rather than stored a second time on
        // FeedItem: it is already durable on the append-only log, and one nullable-column pair is
        // enough schema for this.
        //
        // Runs whether or not the switch is currently on. Turning the experiment off must stop new
        // slates from being drafted; it must not orphan the ones already in flight.
        if (mode == FeedBuildMode.Preserve && previousSnapshot is not null)
        {
            var carried = (await db.ExposureEvents
                    .Where(e => e.AccountId == account.Id
                        && e.FeedSnapshotId == previousSnapshot.Id
                        && e.InterleaveGroup != null)
                    .Select(e => new { e.TitleId, e.PolicyId, e.InterleaveGroup })
                    .ToListAsync(ct))
                .GroupBy(e => e.TitleId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in planned.Where(i => i.PolicyId is null))
            {
                if (carried.TryGetValue(item.Candidate.Title.Id, out var previous))
                {
                    item.PolicyId = previous.PolicyId;
                    item.InterleaveGroup = previous.InterleaveGroup;
                }
            }
        }

        // ── Exposure log ────────────────────────────────────────────────────────────────────
        // Staged on the SAME tracked context as the snapshot rows above, so the exposures and the
        // cards they describe commit or roll back together — one round trip, no new transaction.
        // Staged, never saved here: this method's contract is that AssembleAsync writes nothing to
        // the database on its own, and the caller's SaveChanges is what makes any of it real.
        //
        // Collision is structurally impossible on this path: the snapshot id was minted eight lines
        // ago, so no row can already carry it. The Preserve no-op path returns above without ever
        // reaching here, which is correct — it renders nothing new, and the exposure that matters
        // for a preserved page is the FeedRefill/serve row the endpoint writes when it is actually
        // fetched.
        await exposures.Stage(
            account.Id,
            ExposureSurface.FeedHero,
            snapshot.Id,
            artifact.Id,
            now,
            planned.Where(i => i.Row == FeedRowKind.Hero).Select(CardOf).ToList(),
            ct);
        await exposures.Stage(
            account.Id,
            ExposureSurface.FeedRow,
            snapshot.Id,
            artifact.Id,
            now,
            planned.Where(i => i.Row != FeedRowKind.Hero).Select(CardOf).ToList(),
            ct);

        // Prune snapshot history beyond the last 7 (see FeedSnapshotRetention — same behaviour,
        // extracted so the policy is testable without driving a whole feed build). This is now
        // SAFE in a way it was not before: the durable record of what was served has moved out of
        // FeedItem, so evicting a snapshot no longer destroys history — ExposureEvent carries no
        // foreign key to it.
        await FeedSnapshotRetention.PruneAsync(db, account.Id, ct);

        return snapshot;
    }

    /// <summary>
    /// One assembled-but-not-yet-persisted feed item; TitleId lives on the Candidate. Carries the
    /// verified templater context rather than a finished sentence, so the why pass can upgrade the
    /// card to an LLM line, reuse a kept one, or re-seed a phrasing collision — and WhyThis is the
    /// deterministic templater sentence until it does.
    /// </summary>
    private sealed class PlannedItem(
        FeedRowKind row, Guid? anchorTitleId, int rank, Candidate candidate, ExplanationTemplater.Context whyContext)
    {
        public FeedRowKind Row { get; } = row;

        public Guid? AnchorTitleId { get; } = anchorTitleId;

        public int Rank { get; } = rank;

        public Candidate Candidate { get; private set; } = candidate;

        public ExplanationTemplater.Context WhyContext { get; private set; } = whyContext;

        public string WhyThis { get; set; } = ExplanationTemplater.BuildSentence(whyContext);

        /// <summary>
        /// Where <see cref="WhyThis"/> came from, for the exposure log. Defaults to the templater
        /// because that is what the constructor just wrote; every upgrade path re-stamps it.
        /// </summary>
        public string WhySource { get; set; } = ExposureWhySource.Templater;

        /// <summary>The agent's fit verdict for this card where one was observed — otherwise discarded.</summary>
        public decimal? LlmFit { get; set; }

        /// <summary>
        /// The ranking policy that drafted this seat, on an interleaved Fresh build. Null on every
        /// other path — including a card the why pass swapped in, which neither ranker chose.
        /// </summary>
        public string? PolicyId { get; set; }

        /// <summary>The slate the draft belonged to. Travels with <see cref="PolicyId"/> or not at all.</summary>
        public Guid? InterleaveGroup { get; set; }

        /// <summary>
        /// The low-fit hero swap: this slot now carries a different candidate. The why resets to
        /// the replacement's deterministic templater sentence; the caller upgrades it to an agent
        /// line when one is earned within budget.
        /// </summary>
        public void Replace(Candidate candidate, ExplanationTemplater.Context whyContext)
        {
            Candidate = candidate;
            WhyContext = whyContext;
            WhyThis = ExplanationTemplater.BuildSentence(whyContext);
            WhySource = ExposureWhySource.Templater;
            LlmFit = null;
            // The interleaving attribution goes with it. A low-fit hero swap is a PRODUCT guard, not
            // a ranking decision: the replacement was drafted by nobody, and crediting the team
            // whose seat it took would feed the sequential test an outcome neither ranker earned.
            // The slate loses that seat instead — a small, stated cost, paid on the honest side.
            PolicyId = null;
            InterleaveGroup = null;
        }
    }

    /// <summary>
    /// Joins a second sentence onto a degraded reason. The banner is one paragraph on the page, so
    /// two independent honesties about the same build read as one explanation rather than replacing
    /// each other — which is what a naive assignment would do, and the relaxation notice is the one
    /// that would have been lost.
    /// </summary>
    private static string Append(string? existing, string sentence) =>
        string.IsNullOrWhiteSpace(existing) ? sentence : $"{existing} {sentence}";

    /// <summary>
    /// The pre-assembly fit pass: ask the agent how well the top slice of the shortlist fits THIS
    /// account's taste narrative, before the hero and the rails are chosen, and return the verdicts
    /// by title.
    ///
    /// <para>This is the same call, the same prompt (<c>RerankSystemPrompt</c> via
    /// <see cref="ISearchAgent.RerankBatchAsync"/>) and the same purpose on the token ledger
    /// (<c>rerank</c>) the why pass has always made — moved earlier. That move is the whole point:
    /// after assembly, a fit verdict can only caption a pick or trigger a last-resort swap, and the
    /// swap fires at a fit of 0.3 or below, which a mediocre-but-not-damning card never reaches. The
    /// founder's hero on 2026-09-10 was five titles the model liked by a fraction of a point and his
    /// own narrative had nothing to say for; nothing downstream of assembly could have stopped it.</para>
    ///
    /// <para>Every verdict is cached under the SHARED hero fit key, so the why pass below reads them
    /// back instead of buying them twice, and the paginator's hero lane gets them free for six
    /// hours. Never throws and never blocks: an unavailable agent, a stub, an exhausted budget or a
    /// hung provider all return what was gathered so far, and an empty map means the build orders by
    /// calibrated × prior — the documented fallback.</para>
    /// </summary>
    private async Task<Dictionary<Guid, double>> JudgeFitBeforeAssemblyAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, IReadOnlyList<Candidate> candidates,
        decimal effectiveFloor, Func<Candidate, decimal> rankKey, CancellationToken ct)
    {
        var fits = new Dictionary<Guid, double>();
        if (!agent.IsAvailable || candidates.Count == 0)
        {
            return fits;
        }

        // Ordered by calibrated × prior — the best guess available BEFORE any fit exists, and
        // therefore the honest way to spend a bounded number of calls on the titles most likely to
        // reach the page.
        var slice = candidates
            .Where(c => c.Predicted >= effectiveFloor)
            .OrderByDescending(rankKey)
            .ThenBy(c => c.Title.Id)
            .Take(PreAssemblyFitCandidates)
            .ToList();
        if (slice.Count == 0)
        {
            return fits;
        }

        var rowKey = FeedPaginator.RowKeyFor(FeedRowKind.Hero, null);

        // A verdict already in the shared cache is free and is used as-is — a build ten minutes
        // after the last one re-judges nothing.
        var uncached = new List<Candidate>(slice.Count);
        foreach (var candidate in slice)
        {
            if (cache.TryGetValue<RerankResult>(FeedPaginator.FitKey(accountId, candidate.Title.Id, rowKey), out var hit)
                && hit is not null)
            {
                fits[candidate.Title.Id] = hit.Fit;
            }
            else
            {
                uncached.Add(candidate);
            }
        }

        if (uncached.Count == 0)
        {
            return fits;
        }

        try
        {
            var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
            var contexts = await RerankContextsAsync(slice, ct);
            var byTitleId = uncached.ToDictionary(c => c.Title.Id);
            var debiased = new PositionDebiasedFit();
            using var loop = new LlmBudgetLoop(
                new LlmBudgetOptions(PreAssemblyFitBudget, PreAssemblyFitCeiling, RerankBatch), ct);

            var stop = await loop.RunAsync(uncached, async (batch, batchCt) =>
            {
                var inputs = batch.Select(c => RerankInputFor(c, contexts)).ToList();
                var results = await agent.RerankBatchAsync(
                    inputs, FeedPaginator.QueryForRow(FeedRowKind.Hero), persona, anchorName: null, batchCt);

                foreach (var raw in results)
                {
                    // The same deterministic unscripted-format correction the why pass applies, for
                    // the same reason and revocable the same way (see UnscriptedFormats): an LLM's
                    // sense of how well a long-running panel show "fits" someone is contaminated by
                    // how many people watch it, and this pass now decides the hero.
                    var adjusted = byTitleId.TryGetValue(raw.TitleId, out var scored)
                        ? raw with { Fit = UnscriptedFormats.Adjust(raw.Fit, scored.Title.Genres, persona.Taste) }
                        : raw;
                    debiased.Observe(adjusted);
                    var result = debiased.For(raw.TitleId)!;
                    cache.Set(FeedPaginator.FitKey(accountId, result.TitleId, rowKey), result, FitTtl);
                    fits[result.TitleId] = result.Fit;
                }
            });

            logger.LogInformation(
                "Pre-assembly fit pass for {AccountId} judged {Judged} of {Total} shortlist candidates ({Stop}, {Elapsed:0.0}s).",
                accountId, fits.Count, slice.Count, stop, loop.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A fit verdict sharpens an order the model has already personalised. Losing it costs a
            // slightly worse hero, never a build — exactly how every other agent path here behaves.
            logger.LogWarning(ex, "Pre-assembly fit pass failed for {AccountId}; ordering by calibrated × prior.", accountId);
        }

        return fits;
    }

    /// <summary>
    /// True when an LLM why-line may be shown on a pick surface: the shared voice/shape guard
    /// (<see cref="SecondPersonVoice.Rejects"/>) plus the anti-recommendation guard — a feed card
    /// must never argue against its own pick, however honest the argument.
    /// </summary>
    private static bool AcceptsWhy(string? why) =>
        !string.IsNullOrWhiteSpace(why)
        && !SecondPersonVoice.Rejects(why)
        && !SecondPersonVoice.SoundsAntiRecommendation(why);

    /// <summary>
    /// Fills every planned card's final why-line. Order of preference: the previous snapshot's
    /// persisted why for a kept card (Preserve mode — the stable-"tonight" contract, and zero
    /// spend), a cached agent verdict from the shared 6h fit cache, a fresh agent line within the
    /// time-boxed budget, then the deterministic templater sentence the item already carries.
    /// Every reused or generated line passes <see cref="AcceptsWhy"/> — which is also the upgrade
    /// path for old persisted "Because you loved …" and anti-recommendation sentences — a hero the
    /// agent scored at or below the fit floor is swapped for a spare, and every rail ends with the
    /// no-repeat guard. Never throws: a failed agent leaves templater sentences.
    /// </summary>
    private async Task ApplyCuratedWhysAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, List<PlannedItem> planned,
        IReadOnlyList<FeedItem> previousItems, FeedBuildMode mode, IReadOnlyDictionary<Guid, string> anchorNames,
        IReadOnlyList<Candidate> heroSpares, Func<Candidate, ExplanationTemplater.Context> heroContext, CancellationToken ct)
    {
        // Preserve: a kept card keeps its persisted sentence and is never re-spent on.
        var previousWhys = new Dictionary<(FeedRowKind Row, Guid? AnchorTitleId, Guid TitleId), string>();
        if (mode == FeedBuildMode.Preserve)
        {
            foreach (var item in previousItems)
            {
                previousWhys[(item.Row, item.AnchorTitleId, item.TitleId)] = item.WhyThisSentence;
            }
        }

        var pending = new List<PlannedItem>(planned.Count);
        foreach (var item in planned)
        {
            if (previousWhys.TryGetValue((item.Row, item.AnchorTitleId, item.Candidate.Title.Id), out var kept)
                && AcceptsWhy(kept))
            {
                item.WhyThis = kept;
                item.WhySource = ExposureWhySource.Preserved;
            }
            else
            {
                pending.Add(item);
            }
        }

        // Every fit verdict observed for a hero card, cached or fresh — the input to the low-fit
        // hero swap below. Kept Preserve cards never land here on purpose: tonight's contract
        // outranks a retrospective fit opinion on a card the user is already looking at.
        var heroFits = new Dictionary<Guid, double>();

        // Shared fit cache next — a rail the paginator (or a previous build) already curated is free.
        var uncached = new List<PlannedItem>();
        foreach (var item in pending)
        {
            var rowKey = FeedPaginator.RowKeyFor(item.Row, item.AnchorTitleId);
            var cachedHit = cache.TryGetValue<RerankResult>(
                FeedPaginator.FitKey(accountId, item.Candidate.Title.Id, rowKey), out var hit) && hit is not null;
            if (cachedHit && item.Row == FeedRowKind.Hero)
            {
                heroFits[item.Candidate.Title.Id] = hit!.Fit;
            }

            if (cachedHit)
            {
                item.LlmFit = (decimal)hit!.Fit;
            }

            if (cachedHit && AcceptsWhy(hit!.Why))
            {
                item.WhyThis = hit.Why;
                item.WhySource = ExposureWhySource.Cache;
            }
            else
            {
                uncached.Add(item);
            }
        }

        // ONE budget for the whole why pass, including the hero swap that follows it — a swap is
        // part of this pass, never a second budget. The mechanics (first batch always runs, soft
        // budget between batches, hard ceiling inside one) live in LlmBudgetLoop, shared with the
        // paginator, Ask Reel and lens generation.
        using var loop = new LlmBudgetLoop(new LlmBudgetOptions(WhyPassBudget, WhyPassCeiling, RerankBatch), ct);

        if (uncached.Count > 0 && agent.IsAvailable)
        {
            var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
            var contexts = await RerankContextsAsync([.. planned.Select(i => i.Candidate)], ct);

            // A title can sit on two rails, so it is judged twice at two different prompt
            // positions. Averaging the verdicts is the caller's half of the position-bias
            // mitigation — see PositionDebiasedFit.
            var debiased = new PositionDebiasedFit();
            await CurateWhysAsync(accountId, persona, uncached, anchorNames, heroFits, contexts, debiased, loop, ct);

            if (heroFits.Count > 0)
            {
                await SwapLowFitHeroesAsync(
                    accountId, persona, planned, heroFits, heroSpares, heroContext, contexts, debiased, loop, ct);
            }
        }

        // No-repeat guard, per rail: two cards rendering IDENTICAL text is exactly the founder's
        // complaint, so the later card re-seeds its templater variant until distinct (bounded).
        foreach (var rail in planned.GroupBy(i => (i.Row, i.AnchorTitleId)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in rail.OrderBy(i => i.Rank))
            {
                if (!seen.Add(item.WhyThis))
                {
                    item.WhyThis = ExplanationTemplater.BuildDistinctSentence(item.WhyContext, seen);
                    item.WhySource = ExposureWhySource.Templater;
                }
            }
        }
    }

    /// <summary>
    /// The enrichment the rerank prompt used to leave on the floor: tone, era, themes, the seven
    /// scalars, the credited people and the model's own top contributions, for every planned card.
    /// Three queries for the whole page — the prompt was previously handed genres and 300
    /// characters of overview and asked to judge personal fit from that.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, RerankTitleContext>> RerankContextsAsync(
        IReadOnlyList<Candidate> scored, CancellationToken ct)
    {
        var ids = scored.Select(c => c.Title.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, RerankTitleContext>();
        }

        var attributes = await db.TitleAttributes
            .Where(a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .ToDictionaryAsync(a => a.TitleId, ct);

        var credits = (await db.TitleCredits
                .Where(c => ids.Contains(c.TitleId))
                .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.Role, c.CastOrder, p.Name })
                .ToListAsync(ct))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.TitleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var contributionsByTitle = scored
            .GroupBy(c => c.Title.Id)
            .ToDictionary(
                g => g.Key,
                g => g.First().Row.TopContributions
                    .OrderByDescending(c => Math.Abs(c.Contribution))
                    .Take(3)
                    .Select(c => c.Feature)
                    .ToList());

        var contexts = new Dictionary<Guid, RerankTitleContext>();
        foreach (var id in ids)
        {
            attributes.TryGetValue(id, out var attrs);
            var titleCredits = credits.GetValueOrDefault(id) ?? [];
            contexts[id] = new RerankTitleContext(
                // Only a closed-vocabulary tone is trusted: rows written before enforcement carry
                // polluted free-text labels (see AttributeVocabulary).
                AttributeVocabulary.IsClosedVocabTone(attrs?.Tone) ? attrs!.Tone : null,
                attrs?.Era,
                attrs?.Themes ?? [],
                attrs?.Darkness,
                attrs?.Pacing,
                attrs?.Complexity,
                attrs?.EmotionalIntensity,
                attrs?.Humor,
                attrs?.Optimism,
                attrs?.EnsembleVsSolo,
                titleCredits.Where(c => c.Role == CreditRole.Director)
                    .OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name).FirstOrDefault(),
                titleCredits.Where(c => c.Role == CreditRole.Writer)
                    .OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name).FirstOrDefault(),
                titleCredits.Where(c => c.Role == CreditRole.Actor)
                    .OrderBy(c => c.CastOrder ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => c.Name).Take(3).ToList(),
                contributionsByTitle.GetValueOrDefault(id) ?? []);
        }

        return contexts;
    }

    /// <summary>One candidate in the shape the agent judges, with everything the catalogue knows about it.</summary>
    private static RerankInput RerankInputFor(
        Candidate candidate, IReadOnlyDictionary<Guid, RerankTitleContext> contexts) =>
        new(
            candidate.Title.Id, candidate.Title.Name, candidate.Title.Year,
            candidate.Title.MediaType.ToString(), candidate.Title.Genres,
            candidate.Title.Overview, candidate.Predicted,
            contexts.GetValueOrDefault(candidate.Title.Id));

    /// <summary>
    /// The hero coherence floor. A hero card whose agent fit came in at or below
    /// <see cref="HeroFitFloor"/> reads as "here's tonight's pick; you'll probably dislike it" —
    /// so the slot is swapped for the next spare candidate (drawn from the hero orderings'
    /// leftovers, never a title already on the feed), and the replacement earns its own agent why
    /// inside the pass's remaining budget. With no spare left, or no budget to judge a
    /// replacement, the original hero keeps its slot but drops the low-fit LLM caption for the
    /// deterministic templater sentence — the card then argues FOR itself with verifiable facts.
    /// Deterministic: low-fit heroes are handled in rank order, spares in their ranking order.
    /// </summary>
    private async Task SwapLowFitHeroesAsync(
        Guid accountId, LensPersona persona, List<PlannedItem> planned,
        IReadOnlyDictionary<Guid, double> heroFits, IReadOnlyList<Candidate> heroSpares,
        Func<Candidate, ExplanationTemplater.Context> heroContext,
        IReadOnlyDictionary<Guid, RerankTitleContext> contexts, PositionDebiasedFit debiased,
        LlmBudgetLoop loop, CancellationToken ct)
    {
        var lowFit = planned
            .Where(i => i.Row == FeedRowKind.Hero
                && heroFits.TryGetValue(i.Candidate.Title.Id, out var fit) && fit <= HeroFitFloor)
            .OrderBy(i => i.Rank)
            .ToList();
        if (lowFit.Count == 0)
        {
            return;
        }

        var spares = new Queue<Candidate>(heroSpares);
        var swaps = new List<(PlannedItem Item, Candidate Replacement, double OldFit)>();
        foreach (var item in lowFit)
        {
            if (spares.Count == 0 || loop.BudgetExhausted)
            {
                // No spare, or no budget to earn the replacement a why: the pick stays, but its
                // caption must not argue against it — fall back to the templater sentence.
                item.WhyThis = ExplanationTemplater.BuildSentence(item.WhyContext);
                continue;
            }

            swaps.Add((item, spares.Dequeue(), heroFits[item.Candidate.Title.Id]));
        }

        if (swaps.Count == 0)
        {
            return;
        }

        var batch = swaps.Select(s => RerankInputFor(s.Replacement, contexts)).ToList();
        var resultByTitle = new Dictionary<Guid, RerankResult>();
        var stop = await loop.RunAsync(batch, async (inputs, batchCt) =>
        {
            var results = await agent.RerankBatchAsync(
                inputs, FeedPaginator.QueryForRow(FeedRowKind.Hero), persona, anchorName: null, batchCt);
            foreach (var result in results)
            {
                debiased.Observe(result);
                resultByTitle[result.TitleId] = debiased.For(result.TitleId)!;
            }
        });

        if (resultByTitle.Count == 0)
        {
            // The replacement could not be judged, so nothing is swapped — but the low-fit LLM
            // captions still go: each original hero keeps its slot with its templater sentence.
            logger.LogInformation(
                "Hero swap pass could not judge replacements ({Stop}); {Count} low-fit heroes keep their slots with templater whys.",
                stop, swaps.Count);
            foreach (var (item, _, _) in swaps)
            {
                item.WhyThis = ExplanationTemplater.BuildSentence(item.WhyContext);
            }

            return;
        }

        var rowKey = FeedPaginator.RowKeyFor(FeedRowKind.Hero, null);
        foreach (var (item, replacement, oldFit) in swaps)
        {
            var replacedName = item.Candidate.Title.Name;
            item.Replace(replacement, heroContext(replacement));
            if (resultByTitle.TryGetValue(replacement.Title.Id, out var verdict))
            {
                cache.Set(FeedPaginator.FitKey(accountId, replacement.Title.Id, rowKey), verdict, FitTtl);
                item.LlmFit = (decimal)verdict.Fit;
                if (AcceptsWhy(verdict.Why))
                {
                    item.WhyThis = verdict.Why;
                    item.WhySource = ExposureWhySource.Llm;
                }
            }

            logger.LogInformation(
                "Hero swap for {AccountId}: '{Replaced}' (fit {Fit:0.00} <= {Floor}) swapped for '{Replacement}'.",
                accountId, replacedName, oldFit, HeroFitFloor, replacement.Title.Name);
        }
    }

    /// <summary>
    /// The agent leg of the why pass: rail by rail (so the anchor name rides the prompt and the
    /// cache key stays per-rail), batched through the shared budget loop. Accepted lines are cached
    /// for both this build and the serve path. Every hero fit verdict is recorded into
    /// <paramref name="heroFits"/> whether or not its why was accepted, and every verdict goes
    /// through <paramref name="debiased"/> first, so a title judged on two rails is scored on the
    /// average of its two prompt positions rather than on whichever landed last.
    /// </summary>
    private async Task CurateWhysAsync(
        Guid accountId, LensPersona persona, List<PlannedItem> uncached,
        IReadOnlyDictionary<Guid, string> anchorNames, Dictionary<Guid, double> heroFits,
        IReadOnlyDictionary<Guid, RerankTitleContext> contexts, PositionDebiasedFit debiased,
        LlmBudgetLoop loop, CancellationToken ct)
    {
        var curated = 0;

        foreach (var rail in uncached.GroupBy(i => (i.Row, i.AnchorTitleId)))
        {
            var rowKey = FeedPaginator.RowKeyFor(rail.Key.Row, rail.Key.AnchorTitleId);
            var anchorName = rail.Key.AnchorTitleId is { } anchorId ? anchorNames.GetValueOrDefault(anchorId) : null;
            var items = rail.OrderBy(i => i.Rank).ToList();
            var byTitleId = items.ToDictionary(i => i.Candidate.Title.Id);
            var isHeroRail = rail.Key.Row == FeedRowKind.Hero;

            var stop = await loop.RunAsync(items, async (batch, batchCt) =>
            {
                var inputs = batch.Select(p => RerankInputFor(p.Candidate, contexts)).ToList();
                var results = await agent.RerankBatchAsync(
                    inputs, FeedPaginator.QueryForRow(rail.Key.Row), persona, anchorName, batchCt);

                foreach (var raw in results)
                {
                    // The deterministic half of the prompt's "judge fit against who they are, not
                    // against reach" instruction — see UnscriptedFormats for why it is conditional
                    // on the account's own narrative and therefore revocable by editing it.
                    var adjusted = byTitleId.TryGetValue(raw.TitleId, out var candidate)
                        ? raw with { Fit = UnscriptedFormats.Adjust(raw.Fit, candidate.Candidate.Title.Genres, persona.Taste) }
                        : raw;
                    debiased.Observe(adjusted);
                    var result = debiased.For(raw.TitleId)!;
                    cache.Set(FeedPaginator.FitKey(accountId, result.TitleId, rowKey), result, FitTtl);
                    if (isHeroRail)
                    {
                        heroFits[result.TitleId] = result.Fit;
                    }

                    if (byTitleId.TryGetValue(result.TitleId, out var scored))
                    {
                        scored.LlmFit = (decimal)result.Fit;
                    }

                    if (AcceptsWhy(result.Why) && byTitleId.TryGetValue(result.TitleId, out var item))
                    {
                        item.WhyThis = result.Why;
                        item.WhySource = ExposureWhySource.Llm;
                        curated++;
                    }
                }
            });

            if (stop != LlmBudgetStop.Completed)
            {
                logger.LogInformation(
                    "Feed why pass stopped ({Stop}) after {Elapsed:0.0}s with {Done} of {Total} cards curated; the rest keep templater sentences.",
                    stop, loop.Elapsed.TotalSeconds, curated, uncached.Count);
                return;
            }
        }
    }

    /// <summary>
    /// Item-for-item equality on what the page actually renders from: (Row, Anchor, Rank, Title).
    /// Both sides are canonically ordered first, so a mere permutation of equally-ranked rails does
    /// not count as change. Scores and why-sentences are deliberately excluded — they drift by
    /// epsilon on every rebuild and would defeat the suppression that keeps tonight's page still.
    /// </summary>
    /// <summary>
    /// A planned card in the shape the exposure log takes. The residual and the demotion are
    /// recovered by arithmetic rather than carried as extra fields on <c>Candidate</c>: the scoring
    /// order is fixed and documented (base + residual → calibrator → demotion), so
    /// <c>BaseScore − rawScore</c> is the residual and <c>(BaseScore + CalibrationDelta) − Predicted</c>
    /// is the demotion, exactly. Widening the record for two derivable numbers would be a change to
    /// the type every scoring surface passes around, for nothing.
    ///
    /// <para><c>PredictedRating</c> is the FULL-precision prediction; <c>ExposureLog</c> chooses the
    /// stored resolution (2 dp) for every surface. <see cref="DisplayRating"/> now also stores 2 dp
    /// on <c>FeedItem</c>, so a calibration join across <c>FeedItem</c>, <c>TitleScore</c> and the
    /// exposure log no longer inherits a quantisation floor from whichever side is coarsest.</para>
    /// </summary>
    private static ExposureCard CardOf(PlannedItem item) => new(
        item.Candidate.Title.Id,
        item.Rank,
        item.Candidate.Predicted,
        item.Candidate.BaseScore,
        item.Candidate.CalibrationDelta,
        item.Candidate.BaseScore - (decimal)item.Candidate.Row.Score,
        item.Candidate.BaseScore + item.Candidate.CalibrationDelta - item.Candidate.Predicted,
        item.LlmFit,
        item.WhyThis,
        item.WhySource,
        FeedPaginator.RowKeyFor(item.Row, item.AnchorTitleId),
        0,
        item.PolicyId,
        item.InterleaveGroup);

    /// <summary>
    /// What the card shows. Rounded here, once, so the write path and the drift path agree.
    /// <para>
    /// TWO decimals, matching <c>TitleScore.PredictedRating</c> and the exposure log. It was one
    /// until 2026-09-10, and the odd resolution out is what mattered: a calibration or accuracy
    /// join that spans <c>FeedItem</c> and <c>TitleScore</c> inherits the COARSER of the two, so a
    /// 1 dp column put a 0.05 quantisation floor under every residual measured across that join —
    /// which is the same order as the calibration deltas being measured. The card is unaffected:
    /// the frontend badge renders <c>toFixed(1)</c> and always has, so this changes what is
    /// measurable without changing what is shown. The column is unconstrained <c>numeric</c>, so
    /// no migration.
    /// </para>
    /// </summary>
    private static decimal DisplayRating(Candidate candidate) => Math.Round(candidate.Predicted, 2);

    /// <summary>
    /// The RANKING score behind the card — predicted × the taste prior × freshness. Distinct from
    /// <see cref="DisplayRating"/> on purpose and now more so: this is the number the page was
    /// ordered by, and it has to carry the prior or the persisted ordering key would disagree with
    /// the order actually served. Nothing renders it; it exists so a later reader can reconstruct
    /// why a card sat where it sat.
    /// </summary>
    private static decimal FinalScoreOf(
        Candidate candidate, Func<Title, decimal> freshness, Func<Title, double> prior) =>
        Math.Round(candidate.Predicted * (decimal)prior(candidate.Title) * freshness(candidate.Title), 3);

    /// <summary>
    /// The card's contribution list: the model's own top features, plus the taste prior's factors
    /// as synthetic signed entries (a ×0.6 demotion reads as −0.400). They are appended rather than
    /// blended in because they are a different KIND of number — the model's are additive parts of a
    /// score, the prior's are multiplicative parts of an ordering — and the card's "why not" needs
    /// to be able to name the demotion in the user's own terms rather than imply the model produced
    /// it. <see cref="DisplayRating"/> is unaffected by every one of them.
    /// </summary>
    private static string ExplanationOf(Candidate candidate, TastePrior.Components prior) =>
        JsonSerializer.Serialize(
            candidate.Row.TopContributions
                .Select(c => new { feature = c.Feature, value = Math.Round(c.Contribution, 3) })
                .Concat(TastePrior.ContributionEntries(prior)
                    .Select(e => new { feature = e.Feature, value = Math.Round(e.Value, 3) })));

    /// <summary>
    /// The score-only Preserve write. Called ONLY once <see cref="SameFeed"/> has proved the two
    /// feeds are item-for-item identical, so previous and planned align exactly under the same
    /// ordering key — which is what lets this pair them up without a second identity check.
    ///
    /// <para>Updates <c>PredictedRating</c>, <c>FinalScore</c> and <c>ExplanationJson</c> on the
    /// EXISTING rows (tracked entities of the Active snapshot — no new snapshot, no new
    /// <c>GeneratedAt</c>, no supersede). <c>WhyThisSentence</c> is deliberately untouched: the why
    /// pass runs after the no-op decision and never fires on this path, so there is no regenerated
    /// sentence to write, and overwriting a kept card's persisted why would break the stable-tonight
    /// contract for no gain.</para>
    ///
    /// <para>Returns only the cards whose displayed RATING moved — a contributions-only change is
    /// persisted but has no number on screen to patch, so it is not broadcast.</para>
    /// </summary>
    private static IReadOnlyList<FeedScoreUpdate> ApplyScoreDrift(
        IReadOnlyList<FeedItem> previous, IReadOnlyList<PlannedItem> planned, Func<Title, decimal> freshness,
        Func<Title, double> prior, Func<Title, TastePrior.Components> priorOf)
    {
        var before = previous
            .OrderBy(i => i.Row).ThenBy(i => i.AnchorTitleId).ThenBy(i => i.Rank)
            .ToList();
        var after = planned
            .OrderBy(i => i.Row).ThenBy(i => i.AnchorTitleId).ThenBy(i => i.Rank)
            .ToList();

        var moved = new List<FeedScoreUpdate>();
        for (var i = 0; i < before.Count; i++)
        {
            var row = before[i];
            var candidate = after[i].Candidate;
            var rating = DisplayRating(candidate);
            var explanation = ExplanationOf(candidate, priorOf(candidate.Title));
            if (!FeedScoreDrift.Changed(row.PredictedRating, rating, row.ExplanationJson, explanation))
            {
                continue;
            }

            if (FeedScoreDrift.RatingMoved(row.PredictedRating, rating))
            {
                moved.Add(new FeedScoreUpdate(row.TitleId, rating));
            }

            row.PredictedRating = rating;
            row.FinalScore = FinalScoreOf(candidate, freshness, prior);
            row.ExplanationJson = explanation;
        }

        return moved;
    }

    private static bool SameFeed(IReadOnlyList<FeedItem> previous, IReadOnlyList<PlannedItem> planned)
    {
        if (previous.Count != planned.Count)
        {
            return false;
        }

        var before = previous
            .OrderBy(i => i.Row).ThenBy(i => i.AnchorTitleId).ThenBy(i => i.Rank)
            .Select(i => (i.Row, i.AnchorTitleId, i.Rank, i.TitleId));
        var after = planned
            .OrderBy(i => i.Row).ThenBy(i => i.AnchorTitleId).ThenBy(i => i.Rank)
            .Select(i => (i.Row, i.AnchorTitleId, i.Rank, TitleId: i.Candidate.Title.Id));
        return before.SequenceEqual(after);
    }

    /// <summary>The account's embedded preference-tag vectors — the source of the ranking boost.</summary>
    private async Task<List<float[]>> LoadPreferenceTagVectorsAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.UserPreferenceTags
            .Where(t => t.AccountId == accountId && t.Embedding != null)
            .Select(t => t.Embedding!)
            .ToListAsync(ct);
        return rows.Select(v => v.ToArray()).ToList();
    }

    /// <summary>Max cosine of a title's plot embedding to any preference tag (0 when there are none).</summary>
    private static double TagAffinity(float[] titleVec, IReadOnlyList<float[]> tagVecs)
    {
        var best = 0d;
        foreach (var tag in tagVecs)
        {
            best = Math.Max(best, Cosine(titleVec, tag));
        }

        return best;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0d : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static double GenreJaccard(Title a, Title b)
    {
        if (a.Genres.Length == 0 || b.Genres.Length == 0)
        {
            return 0d;
        }

        var setA = a.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intersection = b.Genres.Count(setA.Contains);
        var union = setA.Count + b.Genres.Length - intersection;
        return union == 0 ? 0d : (double)intersection / union;
    }

    private void ReportProgress(SyncJob job, Guid accountId, decimal pct, string message)
    {
        job.ProgressPct = pct;
        job.ProgressMessage = message;
        events.Publish(accountId, PipelineEventTypes.JobProgress, new Dictionary<string, object?>
        {
            ["kind"] = "feed",
            ["pct"] = pct,
            ["message"] = message,
        });
    }
}
