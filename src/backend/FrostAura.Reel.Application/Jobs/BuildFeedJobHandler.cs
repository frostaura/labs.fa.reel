using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
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
    IMemoryCache cache,
    IPipelineEventHub events,
    ExposureLog exposures,
    ILogger<BuildFeedJobHandler> logger) : IJobHandler
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

    // ── LLM why pass (mirrors FeedPaginator's curation budget mechanics) ────────────────────
    // Soft budget checked BETWEEN batches — the first batch always finishes so every build makes
    // forward progress into the shared 6h fit cache — plus a hard ceiling backstop against a hung
    // provider. More generous than the paginator's 4s/20s because this runs inside a background
    // job, not a request a user is staring at; the cache means successive builds converge to warm.
    private static readonly TimeSpan WhyPassBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WhyPassCeiling = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);
    private const int RerankBatch = 9;

    // Hero coherence floor: when the LLM's fit verdict for a hero card comes in at or below this,
    // "tonight's pick" and "you'll probably dislike it" are on the same card. The why pass swaps
    // such a hero for the next spare candidate (see SwapLowFitHeroesAsync) rather than captioning
    // the headline slot with a reason to skip it.
    private const double HeroFitFloor = 0.3;

    public JobKind Kind => JobKind.BuildFeed;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("BuildFeed requires an account.");
        // Build instrumentation (FeedBuildStat) is captured from here to the end of the method,
        // including the no-op path — a build that changed nothing is exactly the observation worth
        // keeping, and it was the one the old log line never made.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var capture = new BuildCapture();
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
            logger.LogWarning("BuildFeed skipped for {AccountId}: no active model artifact.", accountId);
            job.ProgressMessage = "skipped — train a model first";
            await db.SaveChangesAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
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
        var pool = (await candidateRetriever.RetrieveAsync(
            accountId,
            taste,
            lane: q => q.Where(t => t.TmdbId != null && t.PosterPath != null),
            popCount: FirstPassPopPool,
            annCount: FirstPassAnnPool,
            maturityCeiling: account.Settings.MaturityCeiling,
            ct)).ToList();

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
        logger.LogInformation("Feed snapshot {SnapshotId} active for {AccountId} ({Candidates} scored).",
            snapshot.Id, accountId, candidates.Count);
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
        // nothing at all clears it, honouring it strictly renders a blank page; relaxing it and SAYING
        // SO shows honest, lower-confidence rankings instead. Same call Ask Reel already makes
        // (LiveSearchExpansionService's MinFloorRelaxedCount arm) — the feed build was the one
        // suggestion surface still treating "empty" as a verdict rather than a failure to look harder.
        var effectiveFloor = minPredicted;
        string? degradedReason = null;
        if (minPredicted > 0m && !candidates.Any(c => c.Predicted >= minPredicted))
        {
            effectiveFloor = 0m;

            // Invariant culture, deliberately: these numbers are ratings on the same 0–10 scale the
            // settings screen shows and ContentFilterRules stores, and a server whose locale renders
            // "9,9" would quote the user a threshold they never typed.
            degradedReason = candidates.Count > 0
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Nothing scored at or above your {0:0.#} “for you” minimum, so it was relaxed for this build — "
                        + "the best of {1} matching titles is {2:0.0}.",
                    minPredicted,
                    candidates.Count,
                    candidates.Max(c => c.Predicted))
                : null;
        }

        // An empty candidate pool is a different failure with a different answer: retrieval, not
        // ranking, came back with nothing, so the feed must name the content condition responsible.
        if (candidates.Count == 0)
        {
            degradedReason = await starvation.ExplainEmptyPoolAsync(account.Id, ct)
                ?? "No candidates survived this build. Try relaxing a filter.";
        }

        capture.DegradedReason = degradedReason;
        if (degradedReason is not null)
        {
            logger.LogWarning("Feed build degraded for {AccountId}: {Reason}", account.Id, degradedReason);
        }

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

        // Hero: greedy pick with genre-diversity shaping (MMR with Jaccard until embeddings land).
        var heroPool = candidates
            .Where(c => c.Predicted >= Math.Max(HeroConfidenceFloor, effectiveFloor) && c.Title.PosterPath != null)
            .OrderByDescending(c => c.Predicted * Freshness(c.Title) * (decimal)PreferenceBoost(c.Title))
            .ToList();
        // Recorded here, before the backfill below can relax the floor: a full hero with a zero here
        // means the headline row is silently reaching, which is the founder's complaint in his words.
        capture.HeroEligibleCount = heroPool.Count;
        var candidatesById = candidates.ToDictionary(c => c.Title.Id);
        var hero = new List<Candidate>();
        if (mode == FeedBuildMode.Preserve)
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

        // Never open blank: if the 7.5 confidence floor left the hero short — a sparse-history user
        // whose best predictions all sit below it — backfill from the top remaining scored candidates
        // (poster + min-predicted only, floor dropped). The free-tier home is the hero alone, so an
        // empty hero is an empty product. These are honest model rankings, just lower-confidence.
        if (hero.Count < HeroSize)
        {
            var chosen = hero.Select(h => h.Title.Id).ToHashSet();
            hero.AddRange(candidates
                .Where(c => !chosen.Contains(c.Title.Id) && c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                .OrderByDescending(c => c.Predicted * Freshness(c.Title) * (decimal)PreferenceBoost(c.Title))
                .Take(HeroSize - hero.Count));
        }

        // Assembly writes into a buffer, not the DbContext: whether anything is persisted at all is
        // decided AFTER the whole feed exists, by comparing it against the Active snapshot. Nothing
        // is superseded until a genuinely different feed is in hand.
        var planned = new List<PlannedItem>();

        void AddItem(FeedRowKind row, Title? anchor, int rank, Candidate candidate) =>
            planned.Add(new PlannedItem(row, anchor?.Id, rank, candidate, WhyContext(candidate, anchor)));

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
                     .OrderByDescending(c => (double)c.Predicted * PreferenceBoost(c.Title))
                     .Take(RowMinMembers)
                     .Select(c => c.Title.Id))
        {
            reserved.Add(id);
        }

        foreach (var id in candidates
                     .Where(c => !used.Contains(c.Title.Id) && IsNewForYou(c))
                     .OrderByDescending(c => (double)c.Predicted * (double)Freshness(c.Title) * PreferenceBoost(c.Title))
                     .Take(RowMinMembers)
                     .Select(c => c.Title.Id))
        {
            reserved.Add(id);
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
                members.AddRange(candidates
                    .Where(c => !used.Contains(c.Title.Id) && !keptIds.Contains(c.Title.Id)
                        && !reserved.Contains(c.Title.Id) && c.Predicted >= effectiveFloor)
                    .Select(c => new { Candidate = c, Sim = Similarity(anchor, c.Title) })
                    .Where(x => x.Sim > 0.15d)
                    .OrderByDescending(x => (double)x.Candidate.Predicted * (0.5 + x.Sim) * PreferenceBoost(x.Candidate.Title))
                    .Take(RowSize - members.Count)
                    .Select(x => x.Candidate));
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
        var deepCuts = candidates
            .Where(c => !used.Contains(c.Title.Id) && IsDeepCut(c))
            .OrderByDescending(c => (double)c.Predicted * PreferenceBoost(c.Title))
            .Take(RowSize)
            .ToList();
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

        var newForYou = candidates
            .Where(c => !used.Contains(c.Title.Id) && IsNewForYou(c))
            .OrderByDescending(c => (double)c.Predicted * (double)Freshness(c.Title) * PreferenceBoost(c.Title))
            .Take(RowSize)
            .ToList();
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
            capture.ScoreUpdates = ApplyScoreDrift(previousItems, planned, Freshness);
            return null;
        }

        // Spare hero candidates for the why pass's low-fit hero swap: the same orderings the hero
        // picks themselves were drawn from — the confidence-floor pool's leftovers first (the MMR
        // loop's remainder), then the never-open-blank backfill ordering — restricted to titles not
        // placed anywhere on this feed, so a swap can never seat a card another rail already shows.
        var heroSpares = heroPool
            .Concat(candidates
                .Where(c => c.Title.PosterPath != null && c.Predicted >= effectiveFloor)
                .OrderByDescending(c => c.Predicted * Freshness(c.Title) * (decimal)PreferenceBoost(c.Title)))
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
            Id = Guid.NewGuid(),
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
                FinalScore = FinalScoreOf(item.Candidate, Freshness),
                WhyThisSentence = item.WhyThis,
                ExplanationJson = ExplanationOf(item.Candidate),
            });
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
        }
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

        // Budget and ceiling are owned here so the hero swap pass below draws on the SAME clock —
        // a swap is part of this why pass, never a second budget.
        using var whyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        whyCts.CancelAfter(WhyPassCeiling);
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (uncached.Count > 0 && agent.IsAvailable)
        {
            await CurateWhysAsync(accountId, taste, uncached, anchorNames, heroFits, started, whyCts, ct);
        }

        if (agent.IsAvailable && heroFits.Count > 0)
        {
            await SwapLowFitHeroesAsync(accountId, taste, planned, heroFits, heroSpares, heroContext, started, whyCts, ct);
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
        Guid accountId, FeatureVectorBuilder.TasteState taste, List<PlannedItem> planned,
        IReadOnlyDictionary<Guid, double> heroFits, IReadOnlyList<Candidate> heroSpares,
        Func<Candidate, ExplanationTemplater.Context> heroContext,
        System.Diagnostics.Stopwatch started, CancellationTokenSource whyCts, CancellationToken ct)
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
            if (spares.Count == 0 || started.Elapsed > WhyPassBudget)
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

        var batch = swaps
            .Select(s => new RerankInput(
                s.Replacement.Title.Id, s.Replacement.Title.Name, s.Replacement.Title.Year,
                s.Replacement.Title.MediaType.ToString(), s.Replacement.Title.Genres,
                s.Replacement.Title.Overview, s.Replacement.Predicted))
            .ToList();

        IReadOnlyList<RerankResult> results;
        try
        {
            var tasteSummary = await TasteSummary.BuildAsync(db, accountId, taste, ct);
            results = await agent.RerankBatchAsync(
                batch, FeedPaginator.QueryForRow(FeedRowKind.Hero), tasteSummary, anchorName: null, whyCts.Token);
        }
        catch (Exception ex) when (
            (ex is OperationCanceledException && whyCts.IsCancellationRequested && !ct.IsCancellationRequested)
            || ex is HttpRequestException or TaskCanceledException)
        {
            // The replacement could not be judged, so nothing is swapped — but the low-fit LLM
            // captions still go: each original hero keeps its slot with its templater sentence.
            logger.LogInformation(ex,
                "Hero swap pass could not judge replacements; {Count} low-fit heroes keep their slots with templater whys.",
                swaps.Count);
            foreach (var (item, _, _) in swaps)
            {
                item.WhyThis = ExplanationTemplater.BuildSentence(item.WhyContext);
            }

            return;
        }

        var resultByTitle = results.ToDictionary(r => r.TitleId);
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
                "Hero swap for {AccountId}: '{Replaced}' (fit {Fit:0.00} ≤ {Floor}) swapped for '{Replacement}'.",
                accountId, replacedName, oldFit, HeroFitFloor, replacement.Title.Name);
        }
    }

    /// <summary>
    /// The agent leg of the why pass: rail by rail (so the anchor name rides the prompt and the
    /// cache key stays per-rail), batched, first batch always allowed to finish, soft budget
    /// checked BETWEEN batches, hard ceiling backstop — the same mechanics as the paginator's
    /// curation pass. Accepted lines are cached for both this build and the serve path. The
    /// stopwatch and ceiling are the caller's, shared with the hero swap pass; every hero fit
    /// verdict is recorded into <paramref name="heroFits"/> whether or not its why was accepted.
    /// </summary>
    private async Task CurateWhysAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, List<PlannedItem> uncached,
        IReadOnlyDictionary<Guid, string> anchorNames, Dictionary<Guid, double> heroFits,
        System.Diagnostics.Stopwatch started, CancellationTokenSource whyCts, CancellationToken ct)
    {
        var tasteSummary = await TasteSummary.BuildAsync(db, accountId, taste, ct);
        var batchesDone = 0;
        var curated = 0;

        foreach (var rail in uncached.GroupBy(i => (i.Row, i.AnchorTitleId)))
        {
            var rowKey = FeedPaginator.RowKeyFor(rail.Key.Row, rail.Key.AnchorTitleId);
            var anchorName = rail.Key.AnchorTitleId is { } anchorId ? anchorNames.GetValueOrDefault(anchorId) : null;
            var items = rail.OrderBy(i => i.Rank).ToList();
            var byTitleId = items.ToDictionary(i => i.Candidate.Title.Id);

            for (var i = 0; i < items.Count; i += RerankBatch)
            {
                // Checked BEFORE starting a batch, never during one — a cancelled batch caches
                // nothing, so the work would simply be lost and the next build would repeat it.
                if (batchesDone > 0 && started.Elapsed > WhyPassBudget)
                {
                    logger.LogInformation(
                        "Feed why pass stopped after {Elapsed:0.0}s with {Done} of {Total} cards curated; the rest keep templater sentences.",
                        started.Elapsed.TotalSeconds, curated, uncached.Count);
                    return;
                }

                var batch = items.Skip(i).Take(RerankBatch)
                    .Select(p => new RerankInput(
                        p.Candidate.Title.Id, p.Candidate.Title.Name, p.Candidate.Title.Year,
                        p.Candidate.Title.MediaType.ToString(), p.Candidate.Title.Genres,
                        p.Candidate.Title.Overview, p.Candidate.Predicted))
                    .ToList();

                IReadOnlyList<RerankResult> results;
                try
                {
                    results = await agent.RerankBatchAsync(
                        batch, FeedPaginator.QueryForRow(rail.Key.Row), tasteSummary, anchorName, whyCts.Token);
                }
                catch (OperationCanceledException) when (whyCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "Feed why pass exceeded its {Ceiling}s ceiling after {Done} of {Total} cards; the rest keep templater sentences.",
                        WhyPassCeiling.TotalSeconds, curated, uncached.Count);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning(ex, "Feed why pass rerank failed; remaining cards keep templater sentences.");
                    return;
                }

                batchesDone++;
                foreach (var result in results)
                {
                    cache.Set(FeedPaginator.FitKey(accountId, result.TitleId, rowKey), result, FitTtl);
                    if (rail.Key.Row == FeedRowKind.Hero)
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
        FeedPaginator.RowKeyFor(item.Row, item.AnchorTitleId));

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

    /// <inheritdoc cref="DisplayRating"/>
    private static decimal FinalScoreOf(Candidate candidate, Func<Title, decimal> freshness) =>
        Math.Round(candidate.Predicted * freshness(candidate.Title), 3);

    /// <inheritdoc cref="DisplayRating"/>
    private static string ExplanationOf(Candidate candidate) =>
        JsonSerializer.Serialize(
            candidate.Row.TopContributions.Select(c => new { feature = c.Feature, value = Math.Round(c.Contribution, 3) }));

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
        IReadOnlyList<FeedItem> previous, IReadOnlyList<PlannedItem> planned, Func<Title, decimal> freshness)
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
            var explanation = ExplanationOf(candidate);
            if (!FeedScoreDrift.Changed(row.PredictedRating, rating, row.ExplanationJson, explanation))
            {
                continue;
            }

            if (FeedScoreDrift.RatingMoved(row.PredictedRating, rating))
            {
                moved.Add(new FeedScoreUpdate(row.TitleId, rating));
            }

            row.PredictedRating = rating;
            row.FinalScore = FinalScoreOf(candidate, freshness);
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
