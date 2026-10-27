using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>The row a "load more" request is extending. Mirrors the snapshot's row kinds plus the
/// live genre rails, which only exist on the paginated path (never snapshotted).</summary>
public enum FeedRowContext
{
    Hero,
    BecauseYouLoved,
    DeepCuts,
    NewForYou,
    Genre,

    /// <summary>The detail page's "more like this" rail: semantic kNN to the VIEWED title's plot
    /// embedding (not the user's taste centroid), re-ranked by the personal model. Always carries an
    /// <see cref="MoreFeedRequest.AnchorTitleId"/> — the title being viewed.</summary>
    Similar,
}

public sealed record MoreFeedRequest(
    FeedRowContext Context,
    Guid? AnchorTitleId,
    string? Genre,
    IReadOnlyCollection<Guid> ExcludeTitleIds,
    int Limit,
    // The active browse-filter medium ("movie" | "show" | null). When set, pagination becomes
    // media-aware: the eligible window is restricted to this medium on the server AND the TMDB
    // deepen only pulls this medium — so selecting Movies fills a lane with movies (deepening
    // when the eligible slice runs thin) instead of the client filtering ~5 hero picks to nothing.
    string? MediaType = null,
    // Titles already rendered ELSEWHERE on the page (other rails, tonight's picks). Excluded from
    // this slice like ExcludeTitleIds, but never counted toward this row's depth — ExcludeTitleIds
    // is also the row's paging cursor (page index, TMDB deepen page), and mixing the page's other
    // rails into it would page a fresh rail as if it were ten pages deep.
    IReadOnlyCollection<Guid>? PageExcludeTitleIds = null,
    // One opaque nonce per page view, shared by every rail request that page makes. See
    // FeedPageClaims: it is what keeps CONCURRENT rail requests from serving one title twice.
    string? PageToken = null);

/// <summary>
/// One paginated card before provider hydration — the endpoint attaches the streaming strip and
/// writes the exposure row from the rest.
/// </summary>
/// <param name="Title">The title on the card.</param>
/// <param name="Predicted">The displayed prediction (2 dp).</param>
/// <param name="WhyThis">The rendered why-line; empty when there was nothing honest to say.</param>
/// <param name="IsReleased">Whether the title is out yet.</param>
/// <param name="WhySource">How <paramref name="WhyThis"/> came to be — typed at the point it was chosen.</param>
/// <param name="BaseScore">Base model score plus the online residual, before calibration and demotion.</param>
/// <param name="CalibrationDelta">How far the calibrator moved <paramref name="BaseScore"/>.</param>
/// <param name="ResidualApplied">The online residual head's contribution.</param>
/// <param name="DemotionApplied">The dislike demotion subtracted after calibration.</param>
/// <param name="LlmFit">The model's fit for this card, only where the MODEL produced it.</param>
/// <param name="Evidence">
/// What this card verifiably shares with the row's anchor — empty on un-anchored rows. On an
/// anchored row (because-you-loved, similar) a card only exists if this is non-empty.
/// </param>
public sealed record ScoredFeedItem(
    Title Title,
    decimal Predicted,
    string WhyThis,
    bool IsReleased,
    Ml.ClosedLoop.ExposureWhySource WhySource = default,
    decimal? BaseScore = null,
    decimal? CalibrationDelta = null,
    decimal? ResidualApplied = null,
    decimal? DemotionApplied = null,
    decimal? LlmFit = null,
    IReadOnlyList<SharedEvidence>? Evidence = null);

/// <summary>
/// The infinite-scroll engine. Where <see cref="Jobs.BuildFeedJobHandler"/> materializes a fixed
/// snapshot, this serves the *next page* of any row on demand: take the next slice of eligible
/// titles (popularity-ordered for stable, repeat-free paging — the client passes what it has
/// already seen), score them live through the request-path model cache, rank them for the row's
/// intent, then hand the top slice to the LLM for a hyper-personal curation pass (fit re-rank +
/// a natural "why this fits you", cached per row so re-scrolls never re-spend) — and, when a row
/// runs thin, deepen the catalog straight from TMDB so a reel never ends. Goes through the same
/// <see cref="EligibilityQueryBuilder"/> as every other surface, so the "never show things I've
/// already seen" rule and all content filters hold here too.
/// </summary>
public sealed class FeedPaginator(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    CandidateRetriever candidateRetriever,
    IModelEngine engine,
    OnlineResidualModel residualModel,
    RatingCalibrationService calibration,
    DislikeSignal dislikes,
    CandidateGenerator candidateGenerator,
    ITmdbClient tmdb,
    ISearchAgent agent,
    PersonalisationContext personalisation,
    CrossReference.RerankBudget rerankBudget,
    IMemoryCache cache,
    ILogger<FeedPaginator> logger,
    IServiceScopeFactory? scopeFactory = null,
    Search.Retrieval.KeywordNeighbours? keywordNeighbours = null)
{
    /// <summary>
    /// Pages whose cold curation pass is running off the request right now, keyed by account and
    /// row, so a burst of loads of one rail starts the pass once.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CurationInFlight = new();

    // The window is a hybrid union (~160 after dedup): a popularity coverage slice plus a
    // personalized pgvector ANN slice, so paging no longer re-ranks only the popular head.
    private const int WindowPopCount = 100;
    private const int WindowAnnCount = 90;

    /// <summary>
    /// Taste clusters a PAGINATED window may pull from. See the call site for why this is not the
    /// full k: the paginator's p95 is a number a user is watching.
    /// </summary>
    private const int WindowMaxClusters = 3;
    private const int DeepenThreshold = 40; // usable window thinner than this → pull more from TMDB

    /// <summary>
    /// How long a page request may spend deepening from TMDB before it gives up and serves what it
    /// already has. Deepening is an optimisation; the user waiting for the row is not.
    /// </summary>
    private static readonly TimeSpan DeepenBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Soft budget, checked BETWEEN curation batches. The first batch is always allowed to finish:
    /// a budget that can cancel mid-batch caches nothing, so every later request pays the same
    /// timeout again and the row never warms — measured, a 4s cap against a ~7s batch pinned every
    /// request at 4s for ever, which is worse in steady state than one slow request followed by
    /// 46ms. Letting a batch complete guarantees forward progress: each page view caches what it
    /// curated, so a row converges to warm.
    /// </summary>
    private static readonly TimeSpan CurationBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Hard ceiling on the whole curation pass, so a hung provider cannot hold the connection
    /// indefinitely. Deliberately well above one batch — this is the backstop, not the budget.
    /// </summary>
    private static readonly TimeSpan CurationCeiling = TimeSpan.FromSeconds(20);
    private const int MaxDeepenPage = 25;
    private const int CurationBuffer = 8; // model-rank this many extra so the LLM has room to re-order

    /// <summary>How long a TMDB walk of one lane page is remembered before it may be repeated.</summary>
    private static readonly TimeSpan DeepenMemo = TimeSpan.FromMinutes(30);

    /// <summary>How long one lane's scored, unexcluded window is reused across requests.</summary>
    private static readonly TimeSpan WindowTtl = TimeSpan.FromMinutes(1);

    /// <summary>The most a Similar (or because-you-loved) window may spend retrieving before it serves what it has.</summary>
    private static readonly TimeSpan SimilarWindowBudget = TimeSpan.FromSeconds(2);

    /// <summary>Keyword-sharing titles offered beside an anchored window's plot neighbourhood.</summary>
    private const int KeywordArmCount = 40;
    private const int RerankBatch = 9;
    private static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);

    // Preference-tag re-rank nudge (mirrors the feed build): a title whose plot embedding is close to
    // a declared preference tag lifts by at most +10% on top of the model's rating — enough to sharpen
    // paging order toward stated taste, never enough to float a weak prediction over a strong one.
    private const double PreferenceTagWeight = 0.10;
    private static readonly IReadOnlyDictionary<Guid, double> NeutralBoosts = new Dictionary<Guid, double>();

    // Bound concurrent LLM curation passes process-wide: a home load fires every row's first page
    // at once, and we'd rather queue than stampede OpenRouter. Each row still shows its shimmer
    // until its page resolves, so the cap costs latency, never correctness.
    private static readonly SemaphoreSlim CurationGate = new(4);

    public async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(Account account, MoreFeedRequest req, CancellationToken ct)
    {
        // Phase timings for the one log line at the end: /feed/more's p95 is an acceptance number,
        // and "which phase was slow" is the first question every miss raises.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var activeArtifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
            // Newest-first rather than an unordered FirstOrDefault: a filtered unique
            // index makes two Active artifacts unrepresentable today, but an unordered
            // read would silently start flipping predictions between models the moment
            // that index changed. Deterministic by construction costs nothing here.
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (activeArtifactId is not { } artifactId)
        {
            return [];
        }

        // The artifact row carries the model's bytes (megabytes); a Home load fired every rail at
        // once and each read them, which put the setup phase's p95 at 1.4 s. An artifact never
        // changes once trained, so the row is read once per process and the id above — a tiny read
        // — is what decides which one is Active.
        var artifact = await cache.GetOrCreateAsync($"feedartifact:{artifactId:N}", async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            return await db.ModelArtifacts.AsNoTracking().FirstAsync(a => a.Id == artifactId, ct);
        }) ?? await db.ModelArtifacts.AsNoTracking().FirstAsync(a => a.Id == artifactId, ct);

        var now = DateTime.UtcNow;
        // A home load fires every genre rail's first page at once — they'd each rebuild the same
        // (expensive) taste state. Cache it briefly so the rails share one build; 2 min is well
        // inside a browsing session and a rebuild/rating refreshes it soon enough.
        var taste = await cache.GetOrCreateAsync($"feedtaste:{account.Id}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return await featureBuilder.BuildTasteStateAsync(account.Id, now, ct);
        }) ?? await featureBuilder.BuildTasteStateAsync(account.Id, now, ct);
        var minPredicted = await ForYouBar.ReadAsync(db, account.Id, ct) ?? 0m;
        var exclude = req.ExcludeTitleIds.ToList();
        // Online residual head for this artifact, loaded once and added to every window score below.
        var residual = await residualModel.LoadAsync(account.Id, artifact, ct);
        // The account's monotone predicted→actual map, applied after the residual so a paged-in card
        // carries the same number the snapshot build would have given it.
        var calibrator = await calibration.GetAsync(account.Id, ct);

        // The window excludes the page's other rails too; the depth cursor (exclude.Count) does not.
        var windowExclude = req.PageExcludeTitleIds is { Count: > 0 } pageExclude
            ? exclude.Concat(pageExclude).Distinct().ToList()
            : exclude;
        var setupMs = clock.ElapsedMilliseconds;

        // The scored window, reused for a minute (WS-5, the /feed/more p95). A Home load fires every
        // rail at once and each paid 0.7–1.9 s retrieving and scoring the same unexcluded lane the
        // previous load had just scored; the window does not depend on the page's exclusions, so
        // the lane is scored once per minute and each request removes its own exclusions in memory.
        // A minute keeps the numbers as fresh as the two-minute taste cache already allows; the
        // claim-time eligibility re-check covers a title rated inside it. A deep page whose
        // exclusions leave the cached window thin reads the lane live, exactly as before.
        var windowKey = $"feedwindow:{account.Id:N}:{artifact.Id:N}:{RowKey(req)}:{req.MediaType}";
        List<Scored> all;
        if (cache.TryGetValue(windowKey, out List<Scored>? cachedWindow) && cachedWindow is not null
            && WithoutExcluded(cachedWindow, windowExclude) is { Count: >= DeepenThreshold } fromCache)
        {
            all = fromCache;
        }
        else
        {
            var unexcluded = await ScoreWindowAsync(
                artifact, taste, await FetchWindowAsync(account, req, [], taste, ct), residual, calibrator, now, ct);
            cache.Set(windowKey, unexcluded, WindowTtl);
            all = WithoutExcluded(unexcluded, windowExclude);
            if (all.Count < DeepenThreshold && windowExclude.Count > 0)
            {
                // Deep in the lane: the exclusions ate the unexcluded window, so read past them.
                all = await ScoreWindowAsync(
                    artifact, taste, await FetchWindowAsync(account, req, windowExclude, taste, ct), residual, calibrator, now, ct);
            }
        }

        var windowMs = clock.ElapsedMilliseconds;
        var scored = all.Where(s => ForYouBar.Clears(s.Predicted, minPredicted)).ToList();

        // Deepen on the USABLE (post-scoring, post-MinPredictedRating) count — not the raw retrieved
        // count. A high predicted-rating floor can gut a window that looked full: the old gate saw a
        // healthy pool.Count, skipped deepening, and left that rail starved of cards it could show.
        // A thin usable window means we've nearly exhausted this lane, so grow it straight from TMDB
        // (new rows are summary-only — no credits/embeddings — but still score, render, and hydrate
        // lazily when opened) and re-fetch + re-score.
        if (scored.Count < DeepenThreshold)
        {
            // Time-boxed, because deepening is an OPTIONAL improvement to this page and the caller
            // is a user waiting on a row. It reaches out to TMDB through a shared rate gate, so when
            // that gate is busy — a catalogue-wide re-hydration, a burst of searches — a single lane
            // can hold its connection for ten to twenty seconds. Measured at 6.8-12.1s per lane
            // during exactly that. The browser then runs out of connections to the origin and
            // UNRELATED requests queue behind it: opening a title took 4.9s while three of these
            // were in flight, which reads as "the app is slow" rather than "this row is still
            // filling".
            //
            // On timeout we keep whatever the window already held. A slightly shorter row is a much
            // smaller harm than a page that appears frozen, and the next page request retries.
            using var deepenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deepenCts.CancelAfter(DeepenBudget);
            var grew = 0;
            try
            {
                grew = await DeepenAsync(req, taste, account.Region, exclude.Count, deepenCts.Token);
            }
            catch (OperationCanceledException) when (deepenCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Feed page deepening for {Row} exceeded {Budget}s; serving the {Count} candidates already scored.",
                    RowKey(req), DeepenBudget.TotalSeconds, scored.Count);
            }

            if (grew > 0)
            {
                cache.Remove(windowKey);
                all = await ScoreWindowAsync(
                    artifact, taste, await FetchWindowAsync(account, req, windowExclude, taste, ct), residual, calibrator, now, ct);
                scored = all.Where(s => ForYouBar.Clears(s.Predicted, minPredicted)).ToList();
            }
        }

        // The bar holds on a paged rail exactly as it does on tonight's picks (FD-4's default: a card
        // under the user's own "for you" bar never reaches a main grid). This used to fall back to
        // the best of the lane when nothing cleared it, which served "load more" cards under the bar
        // on every rail the build itself had held to it. A lane with nothing above the bar has
        // genuinely run out of what the user asked to see; it was already deepened above.
        if (scored.Count == 0)
        {
            return [];
        }

        var deepenMs = clock.ElapsedMilliseconds;
        var boosts = await LoadPreferenceBoostsAsync(account.Id, scored, ct);
        // The same account-level taste prior the snapshot build ranks by. Page 2 of a row is the
        // same feed as page 1: a rule that keeps a no-history genre out of the hero and then lets it
        // straight back in on scroll is not a rule, it is a delay. Ranking only — the card's
        // PredictedRating below is untouched by it.
        var prior = TastePrior.From(taste);
        var ranked = await RankAsync(req, taste, scored, boosts, prior, now, ct);

        // ── The anchored-row gate ───────────────────────────────────────────────────────
        // A because-you-loved or more-like-this page admits a card only when it sits inside the
        // anchor's own 50-neighbour radius AND shares a person, a keyword or a theme with it
        // (AnchorRowGate). The evidence that admitted it travels to the card as its chips. A page
        // that comes up empty after the gate is honest: the anchor has nothing else to give here.
        var evidence = new Dictionary<Guid, IReadOnlyList<SharedEvidence>>();
        if (IsAnchored(req) && req.AnchorTitleId is { } gateAnchorId)
        {
            var admissions = await AnchorRowGate.AdmitAsync(
                db, cache, gateAnchorId, ranked.Select(r => r.Title.Id).ToList(), ct);
            ranked = ranked
                .Where(r => admissions.TryGetValue(r.Title.Id, out var admission) && admission.Admitted)
                .ToList();
            foreach (var r in ranked)
            {
                evidence[r.Title.Id] = admissions[r.Title.Id].Evidence;
            }

            if (ranked.Count == 0)
            {
                logger.LogInformation(
                    "Anchored page {Row}: no candidate cleared the anchor's neighbour floor with shared evidence.",
                    RowKey(req));
                return [];
            }
        }

        // The rail's anchor (name + genres), loaded once: the curation prompt names it so the LLM
        // can state the concrete link, and the templater fallback cites a genuinely shared genre.
        AnchorInfo? anchor = null;
        if (req.Context is FeedRowContext.BecauseYouLoved or FeedRowContext.Similar && req.AnchorTitleId is { } anchorTitleId)
        {
            anchor = await db.Titles
                .Where(t => t.Id == anchorTitleId)
                .Select(t => new AnchorInfo(t.Name, t.Genres))
                .FirstOrDefaultAsync(ct);
        }

        // Titles another rail of this page already holds leave the lane BEFORE the curation slice
        // is cut, so a deep lane still fills: cutting the slice first and claiming after left
        // three same-genre rails on one page with 12, 8 and 0 cards over a 60-title pool.
        var rowKey = RowKey(req);
        var heldElsewhere = await FeedPageClaims.HeldElsewhereAsync(
            db, account.Id, req.PageToken, rowKey, ranked.Select(r => r.Title.Id).ToList(), now, ct);
        if (heldElsewhere.Count > 0)
        {
            ranked = ranked.Where(r => !heldElsewhere.Contains(r.Title.Id)).ToList();
            if (ranked.Count == 0)
            {
                logger.LogInformation(
                    "Feed page {Row}: every candidate in the window is already on another rail of this page.", rowKey);
                return [];
            }
        }

        // The account's media mix, on every unanchored page with no medium picked (WS-5 task 9):
        // the build balanced the hero and its rails, and the pages that follow them did not.
        var balanceMedia = !IsAnchored(req) && ParseMedia(req.MediaType) is null;
        if (balanceMedia)
        {
            ranked = MediaMix.BalanceOrder(
                ranked, req.Limit + CurationBuffer, r => r.Title.MediaType == MediaType.Movie, taste.MovieRatedShare);
        }

        // LLM curation pass over the top slice: the model gives the personalised rating, the agent
        // re-orders by hyper-personal fit and supplies the natural "why this fits you" line.
        var curationPool = ranked.Take(Math.Min(ranked.Count, req.Limit + CurationBuffer)).ToList();
        var rankMs = clock.ElapsedMilliseconds;
        var curated = await CurateAsync(account, req, taste, curationPool, prior, anchor?.Name, evidence, ct);
        var curationMs = clock.ElapsedMilliseconds;

        // Claimed against the page, not just taken: a title another rail claimed between the read
        // above and now is skipped for the next best — the curated slice first, then the rest of
        // the ranked lane uncurated — so a lost race shortens nothing that the lane could fill.
        var curatedById = new Dictionary<Guid, Curated>();
        foreach (var c in curated)
        {
            curatedById.TryAdd(c.Scored.Title.Id, c);
        }

        var claimOrder = curated.Select(c => c.Scored.Title.Id)
            .Concat(ranked.Skip(curationPool.Count).Select(r => r.Title.Id))
            .ToList();
        var rankedById = new Dictionary<Guid, Scored>();
        foreach (var r in ranked)
        {
            rankedById.TryAdd(r.Title.Id, r);
        }

        // The curator re-orders by fit and may lead with one medium; the claim order keeps the mix.
        if (balanceMedia)
        {
            claimOrder = MediaMix.BalanceOrder(
                claimOrder, req.Limit, id => rankedById[id].Title.MediaType == MediaType.Movie, taste.MovieRatedShare);
        }

        // Serve-time eligibility (WS-5 task 10): the window was read seconds ago, and a title rated,
        // watched or set aside since (another tab, a sync landing) must not be claimed and served.
        var stillEligible = await candidateRetriever.StillEligibleAsync(
            account.Id, claimOrder, IgnoresGenreFilters(req), account.Settings.MaturityCeiling, ct);
        if (stillEligible.Count < claimOrder.Count)
        {
            logger.LogInformation(
                "Feed page {Row}: {Count} candidate(s) stopped being eligible between the window read and the serve.",
                rowKey, claimOrder.Count - stillEligible.Count);
            claimOrder = [.. claimOrder.Where(stillEligible.Contains)];
        }

        var claimedIds = await FeedPageClaims.ClaimAsync(
            db, account.Id, req.PageToken, rowKey, claimOrder, req.Limit, now, ct);
        var taken = claimedIds
            .Select(id => curatedById.TryGetValue(id, out var c) ? c : new Curated(rankedById[id], null, Attempted: false))
            .ToList();

        var whyContexts = await BuildWhyContextsAsync(taste, taken.Select(c => c.Scored).ToList(), anchor, evidence, ct);
        await PersistScoresAsync(account.Id, artifact.Id, taken.Select(c => c.Scored).ToList(), now, ct);

        // Output guard + no-repeat: a cached/LLM line that leaks third person, opens with the
        // banned "Because you loved", argues against its own pick, or exactly repeats an earlier
        // card on this page is discarded for the deterministic templater sentence — which itself
        // re-seeds until distinct.
        var takenWhys = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ScoredFeedItem>(taken.Count);
        foreach (var c in taken)
        {
            string why;
            ExposureWhySource source;
            if (c.ModelWhy is { } modelWhy && FeedWhyGuard.Accepts(modelWhy) && takenWhys.Add(modelWhy))
            {
                why = modelWhy;
                source = ExposureWhySource.Llm;
            }
            else if (whyContexts.TryGetValue(c.Scored.Title.Id, out var whyContext))
            {
                why = ExplanationTemplater.BuildDistinctSentence(whyContext, takenWhys);
                source = c.Attempted ? ExposureWhySource.Fallback : ExposureWhySource.Templater;
            }
            else
            {
                // No context, nothing verifiable to cite: no line. The source still says whether a
                // model pass was attempted and failed for this card.
                why = string.Empty;
                source = c.Attempted ? ExposureWhySource.Fallback : ExposureWhySource.None;
            }

            // A card whose model pass was attempted and failed shows the templated ANCHORED line
            // (built from the gate's evidence, so it is checkable) or nothing at all: off an
            // anchored row the templater has only soft contribution copy to offer, and serving it in
            // the model's place reads as a reason nobody actually checked. The source stays
            // Fallback — the attempt and its failure are what the log must keep.
            if (source == ExposureWhySource.Fallback && !IsAnchored(req))
            {
                why = string.Empty;
            }

            // The templater found nothing to cite and wrote no line: the card shows none, and the
            // log records that no line was rendered rather than crediting the templater with one.
            if (why.Length == 0 && source == ExposureWhySource.Templater)
            {
                source = ExposureWhySource.None;
            }

            // 2 dp, matching BuildFeedJobHandler.DisplayRating and the exposure log. Page 2+ is the
            // same feed as page 1, so it must not be the one surface that quantises coarser: a
            // calibration join across FeedItem, TitleScore and ExposureLog would inherit the floor
            // from whichever side is coarsest. Invisible to users — the badge is toFixed(1).
            //
            // The breakdown is recovered by the same arithmetic the build's exposure rows use —
            // the scoring order is fixed (base + residual → calibrator → demotion) — so a paginated
            // card is no longer the one surface that logs zeros for a decomposition it actually had.
            items.Add(new ScoredFeedItem(
                c.Scored.Title,
                Math.Round(c.Scored.Predicted, 2),
                why,
                IsReleased(c.Scored.Title, now),
                source,
                BaseScore: c.Scored.BaseScore,
                CalibrationDelta: c.Scored.CalibrationDelta,
                ResidualApplied: c.Scored.BaseScore - (decimal)c.Scored.Row.Score,
                DemotionApplied: c.Scored.BaseScore + c.Scored.CalibrationDelta - c.Scored.Predicted,
                LlmFit: c.Fit is { FitSource: ResultSource.Llm } fit ? (decimal)fit.Fit : null,
                Evidence: evidence.GetValueOrDefault(c.Scored.Title.Id)));
        }

        logger.LogInformation(
            "Feed page {Row}: {TotalMs} ms — setup {SetupMs}, window+score {WindowMs}, deepen {DeepenMs}, rank+gate {RankMs}, "
            + "curation {CurationMs}, claim+why {ClaimMs}; {Count} card(s).",
            rowKey, clock.ElapsedMilliseconds, setupMs, windowMs - setupMs, deepenMs - windowMs, rankMs - deepenMs,
            curationMs - rankMs, clock.ElapsedMilliseconds - curationMs, items.Count);
        return items;
    }

    /// <summary>
    /// Hyper-personal curation: ask the agent to score each candidate's fit (0–1) for this user and
    /// this row's intent, then blend that with the model's predicted rating to re-order. Fits are
    /// cached per (account, title, row) for 6h so paging back and forth never re-spends; the agent
    /// is batched and concurrency-bounded. Degrades to the pure model order if the agent is
    /// unavailable or errors — the cards are already personalised, curation only sharpens them.
    /// </summary>
    private async Task<List<Curated>> CurateAsync(
        Account account, MoreFeedRequest req, FeatureVectorBuilder.TasteState taste, List<Scored> pool,
        TastePrior prior, string? anchorName, IReadOnlyDictionary<Guid, IReadOnlyList<SharedEvidence>> evidence,
        CancellationToken ct)
    {
        if (!agent.IsAvailable || pool.Count == 0)
        {
            return pool.Select(s => new Curated(s, null, Attempted: false)).ToList();
        }

        var rowKey = RowKey(req);
        var query = QueryFor(req);
        var fitById = new Dictionary<Guid, RerankResult>();

        // Titles a model pass was attempted for in this request, whatever came back — the exposure
        // row says Fallback (attempted, failed) rather than Templater (never asked) for those.
        var attempted = new HashSet<Guid>();
        var uncached = new List<RerankInput>();
        var fitSalt = agent.CacheSalt(ApiUsagePurpose.RerankPage);
        foreach (var s in pool)
        {
            if (cache.TryGetValue<RerankResult>(FitKey(fitSalt, account.Id, s.Title.Id, rowKey), out var hit)
                && hit is { FitSource: ResultSource.Llm })
            {
                fitById[s.Title.Id] = hit;
            }
            else
            {
                uncached.Add(new RerankInput(
                    s.Title.Id, s.Title.Name, s.Title.Year, s.Title.MediaType.ToString(), s.Title.Genres, s.Title.Overview, s.Predicted,
                    AnchorEvidencePrompt.ContextFor(evidence.GetValueOrDefault(s.Title.Id), anchorName)));
            }
        }

        // The family ceiling. Curation SHARPENS an order the model has already personalised, so a
        // page that ships without it is a slightly worse order and not a wrong one — the same way
        // the agent-unavailable and rerank-failed paths already behave. This is a request path, so
        // it gets the full ceiling rather than the background fraction.
        if (uncached.Count > 0)
        {
            var allowance = await rerankBudget.AllowanceAsync(account.Id, ct);
            if (!allowance.HasHeadroom)
            {
                logger.LogInformation(
                    "Feed curation for {Row} skipped: the re-rank family ceiling is spent ({Allowance}). "
                    + "The page serves model order and is curated on a later day.",
                    rowKey, allowance.Describe());
                uncached.Clear();
            }
        }

        // OFF the request when it can be (WS-5, the /feed/more p95): a cold page serves the model's
        // order now, with templated lines, and its curation pass runs in the background and fills
        // the fit cache, so the next view of the rail is curated. Measured on the founder's copy,
        // 20 cold Home loads put /feed/more at p50 1.7 s and p95 19.5 s, and the curation phase was
        // the whole of the tail (p95 16.2 s, up to the 20 s ceiling, eleven rails queuing on a
        // four-slot gate); the warm window itself was 0.5–1.6 s. Curation only SHARPENS an order the
        // model already personalised, so the first view loses a little order and none of its
        // honesty: the exposure row says Templater (never asked in this request), not Llm.
        if (uncached.Count > 0 && scopeFactory is not null)
        {
            StartCurationOffRequest(account.Id, rowKey, query, anchorName, taste, [.. uncached]);
            uncached.Clear();
        }

        if (uncached.Count > 0)
        {
            // Time-boxed for the same reason deepening is: this is an LLM round-trip on the critical
            // path of a row the user is waiting for, and the fit cache is an in-process IMemoryCache
            // that every deploy empties. Measured after a restart, a cold lane took 13.4s; the same
            // lane once warm takes 46ms. Three cold lanes in flight exhaust the browser's
            // connections to the origin and stall unrelated requests — a title detail that answers
            // in 20ms took 4.9s behind them.
            //
            // Curation only SHARPENS an order the model has already personalised, so a page that
            // ships without it is a slightly worse order, not a wrong one — which is exactly how
            // the existing agent-unavailable and rerank-failed paths already behave. Whatever
            // batches completed inside the budget are still applied and cached, so the next page
            // over the same titles is warm.
            //
            // The budget/ceiling/first-batch mechanics themselves live in LlmBudgetLoop — one copy
            // shared with the feed why pass, Ask Reel's rerank stream and lens generation.
            using var loop = new LlmBudgetLoop(
                new LlmBudgetOptions(CurationBudget, CurationCeiling, RerankBatch, CurationGate), ct);
            var narrative = await personalisation.ContextForAsync(account.Id, taste, ct);

            if (!await loop.TryAcquireGateAsync())
            {
                // Every slot is busy and the budget expired before one freed. Fall through rather
                // than returning early: whatever fits were already cached still apply, and the
                // blend/ordering below is the single place that decides final order.
                logger.LogInformation(
                    "Feed curation for {Row} gave up waiting for a slot within {Budget}s; serving model order.",
                    rowKey, CurationBudget.TotalSeconds);
            }
            else
            {
                var stop = await loop.RunAsync(uncached, async (batch, batchCt) =>
                {
                    var results = await agent.RerankBatchAsync(
                        batch, query, narrative, anchorName, ApiUsagePurpose.RerankPage, batchCt);
                    foreach (var r in results)
                    {
                        attempted.Add(r.TitleId);

                        // A fallback fit is the stub's predicted/10. Blending it in counts the
                        // prediction twice under a model label, and caching it would replay the
                        // failure for six hours — so it is neither used nor cached.
                        if (r.FitSource != ResultSource.Llm)
                        {
                            continue;
                        }

                        fitById[r.TitleId] = r;
                        cache.Set(FitKey(fitSalt, account.Id, r.TitleId, rowKey), r, FitTtl);
                    }
                });

                if (stop != LlmBudgetStop.Completed)
                {
                    logger.LogInformation(
                        "Feed curation for {Row} stopped ({Stop}) after {Elapsed:0.0}s with {Done} of {Total} titles curated; the rest keep model order and are curated on a later page.",
                        rowKey, stop, loop.Elapsed.TotalSeconds, fitById.Count, pool.Count);
                }
            }
        }

        // Blend model-led: the FastTree rating — which alone sees cast/crew affinity, plot embeddings
        // and contrarian calibration — leads; the agent's fit (a genre/overview-level judgment that
        // sees none of those deep features) sharpens the order rather than overriding it. Inverting
        // this (letting a shallow fit outvote the deep signal) demoted genuinely well-matched titles.
        return pool
            .Select(s =>
            {
                var fit = fitById.GetValueOrDefault(s.Title.Id);
                // The prior rides the MODEL side of the blend, not the fit side: it is a statement
                // about this account's evidence, which is exactly what the FastTree half is supposed
                // to represent and the agent's genre-level judgment cannot see. Folding it in here
                // rather than only in RankAsync is load-bearing — this blend RE-ORDERS the pool, so
                // a prior applied only upstream would decide which titles are curated and then be
                // thrown away deciding which are shown.
                // The crowd term rides the model side for the same reason: it is part of the snapshot
                // build's RankKey, so page 2 of a rail has to be ordered by it too.
                var modelNorm = (double)s.Predicted * prior.MultiplierFor(s.Title) * CrowdPrior.FactorFor(s.Title) / 10d;
                var blend = fit is null ? modelNorm : (0.6 * modelNorm) + (0.4 * fit.Fit);
                return (Scored: s, Fit: fit, Blend: blend);
            })
            .OrderByDescending(x => x.Blend)
            .Select(x => new Curated(x.Scored, x.Fit, attempted.Contains(x.Scored.Title.Id)))
            .ToList();
    }

    /// <summary>
    /// Runs one rail's curation pass in its own scope, off the request, caching every model fit it
    /// returns exactly as the inline pass does. One pass per (account, row) at a time; any failure is
    /// logged and dropped — the rail already served model order, and the next view tries again.
    /// </summary>
    private void StartCurationOffRequest(
        Guid accountId, string rowKey, string query, string? anchorName, FeatureVectorBuilder.TasteState taste,
        IReadOnlyList<RerankInput> inputs)
    {
        var key = $"{accountId:N}:{rowKey}";
        if (scopeFactory is null || !CurationInFlight.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var provider = scope.ServiceProvider;
                provider.GetRequiredService<IAccountContext>().SetAccount(accountId);
                var scopedAgent = provider.GetRequiredService<ISearchAgent>();
                var narrative = await provider.GetRequiredService<PersonalisationContext>()
                    .ContextForAsync(accountId, taste, CancellationToken.None);

                // The same batches, gate and ceiling as the inline pass; no soft budget, because
                // nobody is waiting on this one.
                using var loop = new LlmBudgetLoop(
                    new LlmBudgetOptions(CurationCeiling, CurationCeiling, RerankBatch, CurationGate), CancellationToken.None);
                if (!await loop.TryAcquireGateAsync())
                {
                    return;
                }

                var cached = 0;
                var fitSalt = scopedAgent.CacheSalt(ApiUsagePurpose.RerankPage);
                await loop.RunAsync(inputs, async (batch, batchCt) =>
                {
                    var results = await scopedAgent.RerankBatchAsync(
                        batch, query, narrative, anchorName, ApiUsagePurpose.RerankPage, batchCt);
                    foreach (var r in results.Where(r => r.FitSource == ResultSource.Llm))
                    {
                        cache.Set(FitKey(fitSalt, accountId, r.TitleId, rowKey), r, FitTtl);
                        cached++;
                    }
                });

                logger.LogInformation(
                    "Feed curation for {Row} ran off the request: {Cached} of {Total} titles curated for the next view.",
                    rowKey, cached, inputs.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Off-request feed curation for {Row} failed; the rail keeps model order.", rowKey);
            }
            finally
            {
                CurationInFlight.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// The Similar keyword arm through <see cref="Search.Retrieval.KeywordNeighbours"/>: the anchor's
    /// most specific eligible neighbours by shared keyword and theme, best first, narrowed to the rail's
    /// lane (medium, renderable card, page exclusions) and the maturity ceiling.
    /// </summary>
    private async Task<IReadOnlyList<Title>> KeywordNeighbourWindowAsync(
        Account account, Guid anchorId, Func<IQueryable<Title>, IQueryable<Title>> lane,
        IReadOnlyCollection<Guid> exclude, CancellationToken ct)
    {
        var neighbours = await keywordNeighbours!.ForAsync(account.Id, anchorId, KeywordArmCount * 2, exclude, ct);
        if (neighbours.Count == 0)
        {
            return [];
        }

        var ids = neighbours.Select(n => n.TitleId).ToList();
        var titles = await lane(db.Titles.Where(t => ids.Contains(t.Id))).ToListAsync(ct);
        var rank = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return [.. titles
            .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
            .OrderBy(t => rank[t.Id])
            .Take(KeywordArmCount)];
    }

    private static List<Scored> WithoutExcluded(IReadOnlyList<Scored> window, IReadOnlyCollection<Guid> excluded)
    {
        if (excluded.Count == 0)
        {
            return [.. window];
        }

        var set = excluded as IReadOnlySet<Guid> ?? excluded.ToHashSet();
        return [.. window.Where(s => !set.Contains(s.Title.Id))];
    }

    /// <summary>Rows whose every card must be admitted by <see cref="AnchorRowGate"/>.</summary>
    private static bool IsAnchored(MoreFeedRequest req) =>
        req.Context is FeedRowContext.BecauseYouLoved or FeedRowContext.Similar;

    private static string QueryFor(MoreFeedRequest req) => req.Context switch
    {
        FeedRowContext.Hero => "the best titles for you to watch tonight",
        FeedRowContext.Genre => $"great {req.Genre?.Replace('-', ' ')} titles for you",
        FeedRowContext.BecauseYouLoved => "titles in the same vein as ones you loved",
        FeedRowContext.Similar => "titles a lot like the one you're looking at",
        FeedRowContext.DeepCuts => "under-the-radar hidden gems you'd rate highly",
        FeedRowContext.NewForYou => "recent releases worth your time",
        _ => "titles personalised to your taste",
    };

    private static string RowKey(MoreFeedRequest req) => req.Context switch
    {
        FeedRowContext.Genre => $"genre:{req.Genre}",
        FeedRowContext.BecauseYouLoved => $"loved:{req.AnchorTitleId}",
        FeedRowContext.Similar => $"similar:{req.AnchorTitleId}",
        _ => req.Context.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Row keys for the SNAPSHOT rows, kept format-identical to <see cref="RowKey"/> (and therefore
    /// to <see cref="FitKey"/>) on purpose: the feed build's why pass and this paginator's curation
    /// pass share one 6h fit cache, so a rail warmed at build time serves its later pages without
    /// re-spending, and vice versa. Stable per (rowKind, anchor) across rebuilds.
    /// <para>Public rather than internal because the exposure log records this exact string as a
    /// card's <c>RowKey</c> from the API layer too. A second copy of the format in another assembly
    /// is how a rail's cache key and its exposure key start disagreeing about which rail it was.</para>
    /// </summary>
    public static string RowKeyFor(Domain.Feed.FeedRowKind row, Guid? anchorTitleId) => row switch
    {
        Domain.Feed.FeedRowKind.BecauseYouLoved => $"loved:{anchorTitleId}",
        Domain.Feed.FeedRowKind.Hero => "hero",
        Domain.Feed.FeedRowKind.DeepCuts => "deepcuts",
        Domain.Feed.FeedRowKind.NewForYou => "newforyou",
        _ => row.ToString().ToLowerInvariant(),
    };

    /// <summary>The row-intent phrasing for a snapshot row — mirrors <see cref="QueryFor"/> so the
    /// build-time why pass asks the agent the same stable question the paginated path asks.</summary>
    internal static string QueryForRow(Domain.Feed.FeedRowKind row) => row switch
    {
        Domain.Feed.FeedRowKind.Hero => "the best titles for you to watch tonight",
        Domain.Feed.FeedRowKind.BecauseYouLoved => "titles in the same vein as ones you loved",
        Domain.Feed.FeedRowKind.DeepCuts => "under-the-radar hidden gems you'd rate highly",
        Domain.Feed.FeedRowKind.NewForYou => "recent releases worth your time",
        _ => "titles personalised to your taste",
    };

    /// <summary>
    /// The key one feed fit verdict is cached under, built by <see cref="RerankCacheKey.For"/> like
    /// every other re-rank cache (WS-1 CR-3): <paramref name="salt"/> is the agent's
    /// <see cref="ISearchAgent.CacheSalt"/> for the purpose that wrote the verdict, so a verdict from
    /// another model, effort, prompt or result format misses instead of serving. The build
    /// (<c>RerankFeed</c>) and this paginator (<c>RerankPage</c>) still share an entry exactly when
    /// their salts agree, which they do while both purposes run the same model, effort and prompt.
    /// <para>The candidate set is <see cref="PooledVerdict"/> on purpose: a feed verdict is one per
    /// (row, title), pooled across the batches it was judged in (the build caches the position-debiased
    /// mean), and the lookup happens before any batch exists. Keying it on one batch would make every
    /// later page a miss and re-spend the family ceiling for the same judgement.</para>
    /// </summary>
    internal static string FitKey(string salt, Guid accountId, Guid titleId, string rowKey) =>
        RerankCacheKey.For("feedfit", salt, accountId, titleId, rowKey, PooledVerdict);

    /// <summary>The candidate set a pooled feed verdict is keyed on: none, because it is not one batch's.</summary>
    private static readonly Guid[] PooledVerdict = [];

    /// <summary>The active browse-filter medium as an enum, or null for "all". Anything unrecognised
    /// (including "all") maps to null so the window and deepen stay both-media.</summary>
    private static MediaType? ParseMedia(string? media) => media switch
    {
        "movie" => MediaType.Movie,
        "show" => MediaType.Show,
        _ => null,
    };

    /// <summary>
    /// The lane's candidate window: the hybrid popularity + ANN union from <see cref="CandidateRetriever"/>,
    /// with this row's lane predicate (poster/tmdbId gate, already-seen exclusions, genre rail,
    /// new-for-you recency) applied to both arms. Same eligibility rule + maturity gate as every
    /// other surface — the ANN arm just lifts personalized titles the popularity sort would bury.
    /// </summary>
    private async Task<IReadOnlyList<Title>> FetchWindowAsync(
        Account account, MoreFeedRequest req, List<Guid> exclude, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var media = ParseMedia(req.MediaType);

        // "More like this" is anchored to the VIEWED title's embedding, not the user's taste centroid,
        // so it takes a dedicated retrieval path rather than the hybrid pop+ANN union. A
        // because-you-loved page takes the same path: its gate admits only the anchor's own
        // neighbourhood, so a window drawn from the taste centroid would be almost entirely refused.
        if (IsAnchored(req) && req.AnchorTitleId is { } anchorId)
        {
            return await FetchSimilarWindowAsync(
                account, anchorId, exclude, taste, media, sameMedium: req.Context == FeedRowContext.Similar, ct);
        }

        return await candidateRetriever.RetrieveAsync(
            account.Id,
            taste,
            lane: q =>
            {
                q = q.Where(t => t.TmdbId != null && t.PosterPath != null && !exclude.Contains(t.Id));

                // Media-aware paging: when the browse filter picks a medium, restrict the eligible
                // window to it so a "Movies" lane is scored from movies (and, below, deepened with
                // movies) rather than the client filtering a mostly-shows page down to nothing.
                if (media is { } m)
                {
                    q = q.Where(t => t.MediaType == m);
                }

                if (req.Context == FeedRowContext.Genre && !string.IsNullOrWhiteSpace(req.Genre))
                {
                    var genre = req.Genre;
                    q = q.Where(t => t.Genres.Contains(genre));
                }

                if (req.Context == FeedRowContext.NewForYou)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-900);
                    q = q.Where(t => (t.ReleasedAt ?? t.FirstAiredAt) != null && (t.ReleasedAt ?? t.FirstAiredAt) >= cutoff);
                }

                return q;
            },
            popCount: WindowPopCount,
            annCount: WindowAnnCount,
            maturityCeiling: account.Settings.MaturityCeiling,
            ct,
            ignoreGenreFilters: IgnoresGenreFilters(req),
            // Three clusters, not the full k. This is a REQUEST path and a home load fires every
            // rail at once: at k = 9 that is six rails × nine exact pgvector scans over a
            // lane-restricted id set, which HNSW cannot serve. Three keeps the account's heaviest
            // pockets represented on page 2 — a rail that ranked by one taste on page 1 and another
            // on page 2 is not a rail — at a cost the browser is willing to wait for. The feed BUILD,
            // which is a background job with a minute's box, passes the full k.
            maxClusters: WindowMaxClusters);
    }

    /// <summary>
    /// True for a genre rail, and only for a genre rail. Founder, 2026-08-07: "the filters should
    /// cascade onto the genre specific suggestions too. (of course in those cases the genre filters
    /// would be ignored)". Every other condition — crowd rating, predicted rating, year window,
    /// language, maturity, keywords — still binds; only ExcludeGenre/IncludeGenre step aside,
    /// because the rail's genre IS the user's request in that moment and a standing genre preference
    /// would otherwise make the rail permanently empty rather than merely filtered.
    /// </summary>
    public static bool IgnoresGenreFilters(MoreFeedRequest req) =>
        req.Context == FeedRowContext.Genre && !string.IsNullOrWhiteSpace(req.Genre);

    /// <summary>
    /// The "more like this" window: the eligible catalog semantically nearest to the VIEWED title's
    /// plot embedding by pgvector cosine kNN — the intelligent acquisition that makes this rail
    /// smarter than TMDB's flat similar-list. Excludes the anchor itself and everything already shown,
    /// through the same poster/tmdbId gate + eligibility + maturity composition as every other lane.
    /// When the anchor has no embedding yet, degrades to a genre-overlap pool ordered by popularity
    /// (annCount 0, so the taste centroid is untouched) so the rail still fills.
    /// </summary>
    private async Task<IReadOnlyList<Title>> FetchSimilarWindowAsync(
        Account account, Guid anchorId, List<Guid> exclude, FeatureVectorBuilder.TasteState taste, MediaType? media,
        bool sameMedium, CancellationToken ct)
    {
        var anchor = await db.Titles
            .Where(t => t.Id == anchorId)
            .Select(t => new { t.MediaType, t.Genres, t.Keywords })
            .FirstOrDefaultAsync(ct);
        if (anchor is null)
        {
            return [];
        }

        // "Similar movies" means same medium: a movie's rail shouldn't fill with shows. For a heavy
        // movie-watcher most similar movies are already seen, so the unfiltered eligible neighbours
        // skew to shows — the media-type gate keeps the rail honest, and the TMDB-recommendations
        // deepen (same medium) tops it up when the eligible slice thins. A browse-filter medium
        // narrows this further to that medium (redundant with the anchor's own medium, but honoured
        // for parity with every other lane).
        var anchorMedia = anchor.MediaType;
        Func<IQueryable<Title>, IQueryable<Title>> lane = q =>
        {
            q = q.Where(t => t.TmdbId != null && t.PosterPath != null && t.Id != anchorId && !exclude.Contains(t.Id));
            if (sameMedium)
            {
                q = q.Where(t => t.MediaType == anchorMedia);
            }

            if (media is { } m)
            {
                q = q.Where(t => t.MediaType == m);
            }

            return q;
        };

        var anchorEmbedding = await db.TitleEmbeddings
            .Where(e => e.TitleId == anchorId)
            .Select(e => e.Embedding)
            .FirstOrDefaultAsync(ct);
        if (anchorEmbedding is not null)
        {
            // Time-boxed (WS-5 task 10): the Similar rail sits under a title the user is reading, so
            // a slow pgvector scan serves what already returned rather than holding the page.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SimilarWindowBudget);
            var window = new List<Title>();
            try
            {
                window.AddRange(await candidateRetriever.RetrieveSimilarAsync(
                    account.Id, anchorEmbedding, lane,
                    count: WindowPopCount + WindowAnnCount,
                    account.Settings.MaturityCeiling, budget.Token));

                // The keyword arm: titles sharing specific evidence with the anchor — the ones the
                // evidence gate can actually admit — offered beside the plot neighbourhood rather
                // than only when it happens to contain them. WS-3's KeywordNeighbours seeds it (WS-3
                // CR-6): keywords AND themes, scored by how specific the shared label is, so two
                // films sharing "heist" and "safecracker" lead two sharing "based on novel or book".
                // Without it (a test-built paginator), the distinctive-keyword ANN arm.
                var seen = window.Select(t => t.Id).ToHashSet();
                window.AddRange((keywordNeighbours is not null
                        ? await KeywordNeighbourWindowAsync(account, anchorId, lane, exclude, budget.Token)
                        : await candidateRetriever.RetrieveSharingKeywordsAsync(
                            account.Id, anchorEmbedding, anchor.Keywords.Where(AnchorRowGate.IsDistinctiveKeyword).ToList(),
                            lane, KeywordArmCount, account.Settings.MaturityCeiling, budget.Token))
                    .Where(t => seen.Add(t.Id)));
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Similar window for {AnchorId} exceeded {Budget}s; serving the {Count} titles already retrieved.",
                    anchorId, SimilarWindowBudget.TotalSeconds, window.Count);
            }

            return window;
        }

        // No embedding on the anchor yet → genre-overlap by popularity. Reuses RetrieveAsync with the
        // ANN arm off, so the taste centroid never enters a similarity rail it shouldn't shape.
        if (anchor.Genres.Length == 0)
        {
            return [];
        }

        var genres = anchor.Genres;
        return await candidateRetriever.RetrieveAsync(
            account.Id,
            taste,
            lane: q => lane(q).Where(t => t.Genres.Any(g => genres.Contains(g))),
            popCount: WindowPopCount + WindowAnnCount,
            annCount: 0,
            maturityCeiling: account.Settings.MaturityCeiling,
            ct);
    }

    /// <summary>
    /// Builds vectors for the window and scores them through the request-path model cache. Returns
    /// EVERY scored title — the caller applies the MinPredictedRating floor, so it can tell "this
    /// lane is empty" apart from "this lane is full but nothing cleared the floor" and relax rather
    /// than report a false exhaustion. Extracted so the deepen decision can gate on the usable
    /// post-floor count and re-run identically after growing the catalog.
    /// </summary>
    private async Task<List<Scored>> ScoreWindowAsync(
        ModelArtifact artifact, FeatureVectorBuilder.TasteState taste,
        IReadOnlyList<Title> pool, OnlineResidualModel.ResidualState residual, RatingCalibrator calibrator,
        DateTime now, CancellationToken ct)
    {
        if (pool.Count == 0)
        {
            return [];
        }

        var vectors = await featureBuilder.BuildAsync(taste, pool.Select(t => t.Id).ToList(), now, ct);
        var scorable = pool.Where(t => vectors.ContainsKey(t.Id)).ToList();
        if (scorable.Count == 0)
        {
            return [];
        }

        var rows = engine.ScoreWithContributionsCached(
            artifact.Id, artifact.ArtifactBytes,
            scorable.Select(t => vectors[t.Id]).ToArray(), FeatureSchema.AllNames());

        // Same dislike demotion as the snapshot build and OnDemandScorer — a paged-in card must
        // not ignore a dislike the rest of the feed honours. Profile is ~10-min cached, so the
        // per-window cost is three id-batched lookups.
        var profile = await dislikes.GetProfileAsync(artifact.AccountId, ct);
        var demotions = await dislikes.DemotionsForAsync(
            profile, scorable.Select(t => t.Id).ToList(), ct);

        return scorable.Zip(rows, (title, row) =>
            {
                // base + residual → calibrator → demotion: the same fixed order as the snapshot
                // build and OnDemandScorer. The pre-calibration value and the map's delta ride along
                // for the exposure log, and are never written to a column.
                var adjusted = row.Score + OnlineResidualModel.Residual(residual, vectors[title.Id]);
                var calibrated = calibrator.Calibrate(adjusted);
                var predicted = DislikeSignal.ApplyDemotion(
                    (decimal)calibrated, demotions.GetValueOrDefault(title.Id));
                return new Scored(title, row, predicted, (decimal)adjusted, (decimal)(calibrated - adjusted));
            })
            .ToList();
    }

    /// <summary>Pull another TMDB page into the catalog for this lane. Returns rows newly created.</summary>
    private async Task<int> DeepenAsync(MoreFeedRequest req, FeatureVectorBuilder.TasteState taste, string region, int seenCount, CancellationToken ct)
    {
        // Walk deeper into TMDB as the user scrolls: each ~20 cards seen advances a page. The
        // arithmetic lives in DiscoveryPaging, which the feed BUILD now reads from too — it used to
        // exist only here, which is why the build read page 1 for ever.
        var page = DiscoveryPaging.DeepenPage(seenCount, MaxDeepenPage);

        // One walk per lane page per half hour (WS-5, the /feed/more p95): a lane whose candidates
        // all sit under the floor (New for you, measured) asked TMDB for the same page on EVERY
        // request — 1.2–2.4 s each — and got back titles the catalogue already held. TMDB's discover
        // page does not change on that timescale, so a recent walk of it is remembered and skipped.
        var walkKey = $"feeddeepen:{RowKey(req)}:{req.MediaType}:{region}:{page}";
        if (cache.TryGetValue(walkKey, out _))
        {
            return 0;
        }
        // Media-aware deepen: a "Movies" lane pulls ONLY movies from TMDB (and vice versa), so a thin
        // movies lane grows with movies instead of half its deepen budget landing shows the media
        // window would then drop. Null medium keeps the original both-media behaviour.
        var media = ParseMedia(req.MediaType);
        var wantMovies = media != MediaType.Show;
        var wantTv = media != MediaType.Movie;
        var batches = new List<IReadOnlyList<TmdbListItem>>();
        try
        {
            if (req.Context == FeedRowContext.Genre && !string.IsNullOrWhiteSpace(req.Genre)
                && TmdbGenres.SlugToIds.TryGetValue(req.Genre, out var ids))
            {
                if (wantMovies && ids.Movie is { } movieGenre)
                {
                    batches.Add(await tmdb.DiscoverAsync(movies: true, movieGenre, region, releasedAfter: null, page, ct));
                }

                if (wantTv && ids.Tv is { } tvGenre)
                {
                    batches.Add(await tmdb.DiscoverAsync(movies: false, tvGenre, region: null, releasedAfter: null, page, ct));
                }
            }
            else if (req.Context == FeedRowContext.Similar && req.AnchorTitleId is { } anchorId)
            {
                // Deepen a "more like this" rail straight from TMDB's collaborative recommendations for
                // the anchor — real "people who liked this also liked…" signal to grow the pool the
                // embedding kNN then re-ranks. Needs the anchor's TmdbId + media type. The rail is
                // single-medium (the anchor's), so only deepen when that medium is wanted.
                var anchor = await db.Titles
                    .Where(t => t.Id == anchorId)
                    .Select(t => new { t.TmdbId, t.MediaType })
                    .FirstOrDefaultAsync(ct);
                var anchorWanted = anchor is not null && (anchor.MediaType == MediaType.Movie ? wantMovies : wantTv);
                if (anchorWanted && anchor!.TmdbId is { } anchorTmdbId)
                {
                    batches.Add(await tmdb.GetRecommendationsAsync(
                        anchor.MediaType == MediaType.Movie, anchorTmdbId, ct));
                }
            }
            else if (req.Context == FeedRowContext.NewForYou)
            {
                if (wantMovies)
                {
                    batches.Add(await tmdb.DiscoverAsync(movies: true, genreId: null, region, DateTime.UtcNow.AddDays(-365), page, ct));
                }

                if (wantTv)
                {
                    batches.Add(await tmdb.DiscoverAsync(movies: false, genreId: null, region: null, DateTime.UtcNow.AddDays(-365), page, ct));
                }
            }
            else
            {
                // Hero / deep-cuts / loved fall back to the user's top genres — the same lanes the
                // build pool walks, just deeper. Pull BOTH movies and TV per genre by default: a
                // movie-only pull left every TV title out of the deepened catalog, so a shows-leaning
                // user's hero / deep-cuts / because-you-loved rails ran dry against the reel-never-ends
                // promise. A browse-filter medium narrows this to that medium alone.
                foreach (var slug in TopGenres(taste).Take(2))
                {
                    if (!TmdbGenres.SlugToIds.TryGetValue(slug, out var g))
                    {
                        continue;
                    }

                    if (wantMovies && g.Movie is { } gm)
                    {
                        batches.Add(await tmdb.DiscoverAsync(movies: true, gm, region, releasedAfter: null, page, ct));
                    }

                    if (wantTv && g.Tv is { } gt)
                    {
                        batches.Add(await tmdb.DiscoverAsync(movies: false, gt, region: null, releasedAfter: null, page, ct));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Feed deepen skipped (TMDB unavailable) for {Context}.", req.Context);
            return 0;
        }

        var items = batches.SelectMany(b => b).GroupBy(i => (i.IsMovie, i.Id)).Select(g => g.First()).ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        var added = await candidateGenerator.UpsertCandidatesAsync(items, ct);
        logger.LogInformation("Feed deepen: {Items} TMDB items ({Added} new) for {Context} page {Page}.",
            items.Count, added, req.Context, page);

        // Remembered only once the walk completed: a walk the time box cut short is retried by the
        // next request. The caller re-reads the window only when this grew it — titles the
        // catalogue already held were already in the window it scored.
        cache.Set(walkKey, true, DeepenMemo);
        return added;
    }

    private async Task<List<Scored>> RankAsync(
        MoreFeedRequest req, FeatureVectorBuilder.TasteState taste, List<Scored> scored,
        IReadOnlyDictionary<Guid, double> boosts, TastePrior prior, DateTime now, CancellationToken ct)
    {
        double Boost(Guid id) => boosts.TryGetValue(id, out var b) ? b : 1d;
        double Weight(Title t) => Boost(t.Id) * prior.MultiplierFor(t) * CrowdPrior.FactorFor(t);

        switch (req.Context)
        {
            // Both the home "because you loved" rail and the detail "more like this" rail rank by
            // anchor-embedding cosine × predicted — the same blend, differing only in which title is
            // the anchor (a loved title vs the one being viewed).
            case FeedRowContext.BecauseYouLoved when req.AnchorTitleId is { } lovedAnchorId:
                return await RankByAnchorSimilarityAsync(lovedAnchorId, scored, boosts, prior, ct);

            case FeedRowContext.Similar when req.AnchorTitleId is { } viewedAnchorId:
                return await RankByAnchorSimilarityAsync(viewedAnchorId, scored, boosts, prior, ct);

            case FeedRowContext.DeepCuts:
                {
                    var ordered = scored.OrderBy(s => s.Title.TmdbPopularity ?? 0m).ToList();
                    var threshold = ordered.Count > 0
                        ? ordered[ordered.Count * 7 / 10].Title.TmdbPopularity ?? 0m : 0m;
                    return scored
                        .Where(s => ForYouBar.Clears(s.Predicted, 6m) && (s.Title.TmdbPopularity ?? 0m) <= threshold)
                        .OrderByDescending(s => (double)s.Predicted * Weight(s.Title))
                        .ToList();
                }

            case FeedRowContext.Hero:
            case FeedRowContext.NewForYou:
                return scored.OrderByDescending(s => (double)s.Predicted * (double)Freshness(s.Title, now) * Weight(s.Title)).ToList();

            default:
                return scored.OrderByDescending(s => (double)s.Predicted * Weight(s.Title)).ToList();
        }
    }

    /// <summary>
    /// Rank a window by closeness to a single anchor title: cosine of each candidate's plot embedding
    /// to the anchor's (Jaccard genre-overlap when either lacks an embedding), multiplied by the
    /// personal model's predicted rating — which already folds in the online residual head via the
    /// shared scoring path. The <c>0.5 + sim</c> factor keeps a strong prediction relevant even at
    /// moderate similarity, and the <c>&gt; 0.15</c> gate drops off-topic titles the model still liked.
    /// Shared by the home "because you loved" rail and the detail "more like this" rail.
    /// </summary>
    private async Task<List<Scored>> RankByAnchorSimilarityAsync(
        Guid anchorId, List<Scored> scored, IReadOnlyDictionary<Guid, double> boosts, TastePrior prior, CancellationToken ct)
    {
        double Boost(Guid id) => boosts.TryGetValue(id, out var b) ? b : 1d;
        double Weight(Title t) => Boost(t.Id) * prior.MultiplierFor(t) * CrowdPrior.FactorFor(t);

        var anchor = await db.Titles.FirstOrDefaultAsync(t => t.Id == anchorId, ct);
        if (anchor is null)
        {
            return scored.OrderByDescending(s => (double)s.Predicted * Weight(s.Title)).ToList();
        }

        var embIds = scored.Select(s => s.Title.Id).Append(anchorId).ToList();
        var emb = (await db.TitleEmbeddings.Where(e => embIds.Contains(e.TitleId)).ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        double Sim(Title t) => emb.TryGetValue(anchorId, out var va) && emb.TryGetValue(t.Id, out var vb)
            ? Cosine(va, vb) : GenreJaccard(anchor, t);

        return scored
            .Select(s => (s, sim: Sim(s.Title)))
            .Where(x => x.sim > 0.15d)
            .OrderByDescending(x => (double)x.s.Predicted * (0.5 + x.sim) * Weight(x.s.Title))
            .Select(x => x.s)
            .ToList();
    }

    /// <summary>
    /// Per-title preference-tag re-rank multipliers for this window. A title whose plot embedding sits
    /// close to a declared preference tag gets a small, bounded lift (≤ +10%) so paging order leans
    /// toward stated taste without a weak prediction ever overtaking a strong one. Returns an empty
    /// (all-neutral) map when the user has no embedded tags — the common case, so no cost is paid.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, double>> LoadPreferenceBoostsAsync(
        Guid accountId, IReadOnlyList<Scored> scored, CancellationToken ct)
    {
        if (scored.Count == 0)
        {
            return NeutralBoosts;
        }

        var tagVectors = (await db.UserPreferenceTags
                .Where(t => t.AccountId == accountId && t.Embedding != null)
                .Select(t => t.Embedding!)
                .ToListAsync(ct))
            .Select(v => v.ToArray())
            .ToList();
        if (tagVectors.Count == 0)
        {
            return NeutralBoosts;
        }

        var ids = scored.Select(s => s.Title.Id).ToList();
        var embByTitle = (await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        var result = new Dictionary<Guid, double>(scored.Count);
        foreach (var s in scored)
        {
            result[s.Title.Id] = embByTitle.TryGetValue(s.Title.Id, out var vec)
                ? 1d + (PreferenceTagWeight * TagAffinity(vec, tagVectors))
                : 1d;
        }

        return result;
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

    /// <summary>
    /// Builds each taken title's fully-verified templater context: role-matched user-rated people,
    /// rated genres, the rail's anchor with a genuinely shared genre, and the title's LLM-derived
    /// tone (used by the templater only when it passes the closed vocabulary — rows extracted
    /// before vocabulary enforcement carry polluted labels). Returned as contexts, not sentences,
    /// so the caller's no-repeat guard can re-seed a collision.
    /// </summary>
    private async Task<Dictionary<Guid, ExplanationTemplater.Context>> BuildWhyContextsAsync(
        FeatureVectorBuilder.TasteState taste, List<Scored> taken, AnchorInfo? anchor,
        IReadOnlyDictionary<Guid, IReadOnlyList<SharedEvidence>> evidence, CancellationToken ct)
    {
        var titleIds = taken.Select(s => s.Title.Id).ToList();
        // Per-title, per-role best-affinity person among people the user has actually rated (count ≥ 2),
        // so the templater names the person matching each contribution's role and never invents a name.
        // Single-sourced in VerifiedPeople, shared with the feed builder and the title endpoint.
        var personsByTitle = VerifiedPeople.BestByRole(await VerifiedPeople.ForTitlesAsync(db, taste, titleIds, ct));

        var toneByTitle = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId) && a.Tone != null)
            .ToDictionaryAsync(a => a.TitleId, a => a.Tone, ct);

        var anchorGenres = anchor?.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, ExplanationTemplater.Context>(taken.Count);
        foreach (var s in taken)
        {
            var persons = personsByTitle.GetValueOrDefault(s.Title.Id) ?? ExplanationTemplater.NoPersons;
            var ratedGenres = s.Title.Genres.Where(taste.GenreRatings.ContainsKey).ToHashSet();
            var sharedGenre = anchorGenres is null ? null : s.Title.Genres.FirstOrDefault(anchorGenres.Contains);
            result[s.Title.Id] = new ExplanationTemplater.Context(
                s.Title, s.Row.TopContributions, persons, ratedGenres, anchor?.Name,
                sharedGenre, toneByTitle.GetValueOrDefault(s.Title.Id),
                // The anchored-row gate's evidence names the link, exactly as on the built rail.
                evidence.GetValueOrDefault(s.Title.Id),
                taste.OverallContrarianOffset);
        }

        return result;
    }

    /// <summary>
    /// Upserts the served slice's scores (<see cref="TitleScoreWriter"/>): nine rails scoring
    /// overlapping titles in parallel used to race a load-then-insert into a 23505 and a 500.
    /// </summary>
    private async Task PersistScoresAsync(Guid accountId, Guid artifactId, List<Scored> taken, DateTime now, CancellationToken ct)
    {
        if (taken.Count == 0)
        {
            return;
        }

        await TitleScoreWriter.UpsertAsync(
            db,
            accountId,
            artifactId,
            taken.Select(s => new TitleScoreWriter.Score(
                s.Title.Id,
                Math.Round(s.Predicted, 2),
                TitleScoreWriter.SerializeContributions(s.Row.TopContributions))),
            now,
            ct);
    }

    private static IEnumerable<string> TopGenres(FeatureVectorBuilder.TasteState taste) =>
        taste.GenreRatings
            .Select(kv => new
            {
                Genre = kv.Key,
                Score = (double)TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean) * Math.Log(1 + kv.Value.Count),
            })
            .OrderByDescending(g => g.Score)
            .Select(g => g.Genre);

    private static bool IsReleased(Title title, DateTime now)
    {
        var release = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
        return release is null || release <= now;
    }

    private static decimal Freshness(Title title, DateTime now)
    {
        var released = title.ReleasedAt ?? title.FirstAiredAt;
        if (released is null)
        {
            return 1m;
        }

        var ageDays = Math.Max(0d, (now - released.Value).TotalDays);
        return ageDays > 365 ? 1m : 1m + (decimal)(0.10 * Math.Exp(-ageDays / 180d));
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

    /// <summary>The rail's anchor as the why-lines need it: its name and its genres.</summary>
    private sealed record AnchorInfo(string Name, string[] Genres);

    /// <summary>
    /// One scored title in a page window. <paramref name="Predicted"/> is what the user sees;
    /// <paramref name="BaseScore"/> is the pre-calibration value (base model + online residual) and
    /// <paramref name="CalibrationDelta"/> how far the calibrator moved it — carried for the exposure
    /// log the closed-loop work lands next, never persisted as a column.
    /// </summary>
    private sealed record Scored(
        Title Title, ScoredRow Row, decimal Predicted, decimal BaseScore, decimal CalibrationDelta);

    /// <summary>
    /// A model-scored title after the LLM curation pass. <paramref name="Fit"/> is the model's
    /// verdict (only ever a model-sourced fit) or null; <paramref name="Attempted"/> says whether a
    /// model pass was tried for it in this request at all.
    /// </summary>
    private sealed record Curated(Scored Scored, RerankResult? Fit, bool Attempted)
    {
        /// <summary>The model's own sentence, or null — the source is read, never inferred.</summary>
        public string? ModelWhy => Fit is { WhySource: ResultSource.Llm } fit ? fit.Why : null;
    }
}
