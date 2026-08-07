using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
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
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// The serving pipeline: refresh the TMDB candidate pool → first-pass score everything
/// eligible → fully hydrate the shortlist (credits feed the affinity features) → final score
/// with per-title contributions → assemble hero + because-you-loved rows with freshness and
/// genre-diversity shaping → flip the Active snapshot and the account to FeedReady. The model
/// is never in the request path — the feed endpoint reads the snapshot.
/// </summary>
public class BuildFeedJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    CandidateGenerator candidateGenerator,
    CandidateRetriever candidateRetriever,
    FeedStarvationDiagnostic starvation,
    TitleHydrator hydrator,
    IModelEngine engine,
    OnlineResidualModel residualModel,
    IPipelineEventHub events,
    ILogger<BuildFeedJobHandler> logger) : IJobHandler
{
    // First-pass pool is now a hybrid union (~1200): a popularity coverage slice plus a personalized
    // ANN slice, deduped. The old pure-popularity pool never scored a title the user would love that
    // wasn't already popular; the ANN arm lifts that recall ceiling before the model re-ranks.
    private const int FirstPassPopPool = 700;
    private const int FirstPassAnnPool = 500;
    private const int ShortlistSize = 220;
    private const int HeroSize = 5;
    private const decimal HeroConfidenceFloor = 7.5m;
    private const int AnchorRowCount = 3;
    private const int RowSize = 10;
    private const int RowMinMembers = 4; // a row below this reads worse than no row, so it's dropped
    // Preference-tag re-rank nudge: a title whose plot embedding sits close to a declared preference
    // tag ("things I'm into") gets at most a +10% lift on top of the model's rating — enough to
    // sharpen ordering toward stated taste, never enough to float a weak prediction over a strong one.
    private const double PreferenceTagWeight = 0.10;

    public JobKind Kind => JobKind.BuildFeed;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("BuildFeed requires an account.");
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
        var residual = await residualModel.LoadAsync(accountId, artifact.Id, ct);
        var minPredictedRaw = await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == Domain.Filters.FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        var minPredicted = minPredictedRaw is not null
            && decimal.TryParse(minPredictedRaw, System.Globalization.CultureInfo.InvariantCulture, out var floor)
            ? Math.Clamp(floor, 0m, 10m) : 0m;

        // ── Phase 1: widen the pool ─────────────────────────────────────────────────────
        ReportProgress(job, accountId, 10, "Scouting fresh candidates");
        await candidateGenerator.RefreshPoolAsync(taste, account.Region, ct);

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
                (id, score: score + (float)OnlineResidualModel.Residual(residual.Weights, residual.Bias, poolVectors[id])))
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
        var candidates = ordered.Zip(scored, (title, row) =>
        {
            var adjusted = row.Score + OnlineResidualModel.Residual(residual.Weights, residual.Bias, finalVectors[title.Id]);
            return new Candidate(title, row, Math.Clamp((decimal)adjusted, 0m, 10m));
        }).ToList();
        await PersistScoresAsync(accountId, artifact.Id, candidates, now, ct);

        // ── Phase 5: assemble the feed ──────────────────────────────────────────────────
        ReportProgress(job, accountId, 85, "Assembling tonight's picks");
        var snapshot = await AssembleAsync(account, artifact, taste, candidates, minPredicted, now, ct);

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

        await db.SaveChangesAsync(ct);

        events.Publish(accountId, PipelineEventTypes.StageChanged, new Dictionary<string, object?> { ["stage"] = "FeedReady" });
        events.Publish(accountId, PipelineEventTypes.FeedReady, new Dictionary<string, object?>
        {
            ["snapshotId"] = snapshot.Id,
            ["generatedAt"] = snapshot.GeneratedAt,
        });
        events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?> { ["kind"] = "feed" });
        logger.LogInformation("Feed snapshot {SnapshotId} active for {AccountId} ({Candidates} scored).",
            snapshot.Id, accountId, candidates.Count);
    }

    private sealed record Candidate(Title Title, ScoredRow Row, decimal Predicted);

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

    private async Task<FeedSnapshot> AssembleAsync(
        Account account, ModelArtifact artifact, FeatureVectorBuilder.TasteState taste,
        IReadOnlyList<Candidate> candidates, decimal minPredicted, DateTime now, CancellationToken ct)
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

        if (degradedReason is not null)
        {
            logger.LogWarning("Feed build degraded for {AccountId}: {Reason}", account.Id, degradedReason);
        }

        // Per-title, per-role best-affinity person, restricted to people the user has actually rated
        // (count ≥ 2). Keying by role lets the why-sentence cite the person matching the contribution
        // (a directorAffinity clause names a director, never an actor); restricting to rated people
        // means no clause ever names someone outside the user's taste. The Role column is load-bearing.
        var shortIds = candidates.Select(c => c.Title.Id).ToList();
        var credits = await db.TitleCredits
            .Where(c => shortIds.Contains(c.TitleId))
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.PersonId, c.Role, p.Name })
            .ToListAsync(ct);
        var personsByTitle = credits
            .Where(c => taste.PersonRatings.TryGetValue(c.PersonId, out var r) && r.Count >= 2)
            .GroupBy(c => c.TitleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<Domain.Catalog.CreditRole, ExplanationTemplater.PersonAffinity>)g
                    .GroupBy(c => c.Role)
                    .ToDictionary(
                        rg => rg.Key,
                        rg => rg
                            .Select(c => new ExplanationTemplater.PersonAffinity(
                                c.Name, TasteMath.ShrunkenMean(taste.PersonRatings[c.PersonId], taste.UserMean)))
                            .OrderByDescending(p => p.Affinity)
                            .First()));

        // Anchors: candidate loved titles by name. Drawn from the WIDE recency-weighted anchor pool
        // as well as the strict all-time top, because the strict top of a 3,600-rating history never
        // changes and was the whole reason the same three rows kept coming back.
        var anchorCandidateIds = taste.LovedAnchorPool.Concat(taste.TopLovedTitleIds).Distinct().ToList();
        var anchorTitles = await db.Titles
            .Where(t => anchorCandidateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // What the LAST build anchored on — held back so this build cannot repeat it while any other
        // loved title is available. Read before the supersede below, which does not touch FeedItems.
        var previousAnchors = (await db.FeedItems
                .Where(i => i.AccountId == account.Id
                    && i.Row == FeedRowKind.BecauseYouLoved
                    && i.AnchorTitleId != null
                    && db.FeedSnapshots.Any(s => s.Id == i.FeedSnapshotId && s.Status == SnapshotStatus.Active))
                .Select(i => i.AnchorTitleId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

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

        var anchorIds = AnchorRotation.Select(
            driftPick,
            taste.LovedAnchorPool.Where(anchorTitles.ContainsKey).ToList(),
            taste.TopLovedTitleIds.Where(anchorTitles.ContainsKey).ToList(),
            previousAnchors,
            AnchorRotation.OffsetFor(now),
            AnchorRowCount);

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

        string WhyThis(Candidate candidate, string? anchorName)
        {
            var persons = personsByTitle.GetValueOrDefault(candidate.Title.Id) ?? ExplanationTemplater.NoPersons;
            var ratedGenres = candidate.Title.Genres.Where(taste.GenreRatings.ContainsKey).ToHashSet();
            return ExplanationTemplater.BuildSentence(new ExplanationTemplater.Context(
                candidate.Title,
                candidate.Row.TopContributions,
                persons,
                ratedGenres,
                anchorName));
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
        var hero = new List<Candidate>();
        foreach (var candidate in heroPool)
        {
            if (hero.Count >= HeroSize)
            {
                break;
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

        void AddItem(FeedRowKind row, Guid? anchorId, int rank, Candidate candidate, string whyThis)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                FeedSnapshotId = snapshot.Id,
                Row = row,
                AnchorTitleId = anchorId,
                Rank = rank,
                TitleId = candidate.Title.Id,
                PredictedRating = Math.Round(candidate.Predicted, 1),
                FinalScore = Math.Round(candidate.Predicted * Freshness(candidate.Title), 3),
                WhyThisSentence = whyThis,
                ExplanationJson = JsonSerializer.Serialize(
                    candidate.Row.TopContributions.Select(c => new { feature = c.Feature, value = Math.Round(c.Contribution, 3) })),
            });
        }

        for (var i = 0; i < hero.Count; i++)
        {
            AddItem(FeedRowKind.Hero, null, i, hero[i], WhyThis(hero[i], null));
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

        foreach (var anchor in anchors)
        {
            var members = candidates
                .Where(c => !used.Contains(c.Title.Id) && !reserved.Contains(c.Title.Id) && c.Predicted >= effectiveFloor)
                .Select(c => new { Candidate = c, Sim = Similarity(anchor, c.Title) })
                .Where(x => x.Sim > 0.15d)
                .OrderByDescending(x => (double)x.Candidate.Predicted * (0.5 + x.Sim) * PreferenceBoost(x.Candidate.Title))
                .Take(RowSize)
                .ToList();

            if (members.Count < RowMinMembers)
            {
                continue; // a thin row reads worse than no row
            }

            for (var i = 0; i < members.Count; i++)
            {
                used.Add(members[i].Candidate.Title.Id);
                AddItem(FeedRowKind.BecauseYouLoved, anchor.Id, i, members[i].Candidate, WhyThis(members[i].Candidate, anchor.Name));
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
                AddItem(FeedRowKind.DeepCuts, null, i, deepCuts[i], WhyThis(deepCuts[i], null));
            }
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
                AddItem(FeedRowKind.NewForYou, null, i, newForYou[i], WhyThis(newForYou[i], null));
            }
        }

        // Prune snapshot history beyond the last 7.
        var staleIds = await db.FeedSnapshots
            .Where(s => s.AccountId == account.Id)
            .OrderByDescending(s => s.GeneratedAt)
            .Skip(7)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (staleIds.Count > 0)
        {
            await db.FeedItems.Where(i => staleIds.Contains(i.FeedSnapshotId)).ExecuteDeleteAsync(ct);
            await db.FeedSnapshots.Where(s => staleIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }

        return snapshot;
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
