using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    string? MediaType = null);

/// <summary>One paginated card before provider hydration — the endpoint attaches the streaming strip.</summary>
public sealed record ScoredFeedItem(Title Title, decimal Predicted, string WhyThis, bool IsReleased);

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
    IMemoryCache cache,
    ILogger<FeedPaginator> logger)
{
    // The window is a hybrid union (~160 after dedup): a popularity coverage slice plus a
    // personalized pgvector ANN slice, so paging no longer re-ranks only the popular head.
    private const int WindowPopCount = 100;
    private const int WindowAnnCount = 90;
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
        var artifact = await db.ModelArtifacts
            .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
            // Newest-first rather than an unordered FirstOrDefault: a filtered unique
            // index makes two Active artifacts unrepresentable today, but an unordered
            // read would silently start flipping predictions between models the moment
            // that index changed. Deterministic by construction costs nothing here.
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
        if (artifact is null)
        {
            return [];
        }

        var now = DateTime.UtcNow;
        // A home load fires every genre rail's first page at once — they'd each rebuild the same
        // (expensive) taste state. Cache it briefly so the rails share one build; 2 min is well
        // inside a browsing session and a rebuild/rating refreshes it soon enough.
        var taste = await cache.GetOrCreateAsync($"feedtaste:{account.Id}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return await featureBuilder.BuildTasteStateAsync(account.Id, now, ct);
        }) ?? await featureBuilder.BuildTasteStateAsync(account.Id, now, ct);
        var minPredicted = await MinPredictedAsync(account.Id, ct);
        var exclude = req.ExcludeTitleIds.ToList();
        // Online residual head for this artifact, loaded once and added to every window score below.
        var residual = await residualModel.LoadAsync(account.Id, artifact, ct);
        // The account's monotone predicted→actual map, applied after the residual so a paged-in card
        // carries the same number the snapshot build would have given it.
        var calibrator = await calibration.GetAsync(account.Id, ct);

        var pool = await FetchWindowAsync(account, req, exclude, taste, ct);
        var all = await ScoreWindowAsync(artifact, taste, pool, residual, calibrator, now, ct);
        var scored = all.Where(s => s.Predicted >= minPredicted).ToList();

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
                pool = await FetchWindowAsync(account, req, exclude, taste, ct);
                all = await ScoreWindowAsync(artifact, taste, pool, residual, calibrator, now, ct);
                scored = all.Where(s => s.Predicted >= minPredicted).ToList();
            }
        }

        // The floor is soft, exactly as in the feed build and in Ask Reel: when the window holds
        // real candidates but none clear the account's MinPredictedRating, serving nothing tells the
        // user the reel ended when it has not. Fall back to the best of what this lane actually has
        // rather than reporting a false exhaustion.
        if (scored.Count == 0 && all.Count > 0)
        {
            logger.LogInformation(
                "Feed page relaxed the {Floor} predicted-rating floor for {Row}: none of {Count} candidates cleared it.",
                minPredicted, RowKey(req), all.Count);
            scored = all;
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var boosts = await LoadPreferenceBoostsAsync(account.Id, scored, ct);
        var ranked = await RankAsync(req, taste, scored, boosts, now, ct);

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

        // LLM curation pass over the top slice: the model gives the personalised rating, the agent
        // re-orders by hyper-personal fit and supplies the natural "why this fits you" line.
        var curationPool = ranked.Take(Math.Min(ranked.Count, req.Limit + CurationBuffer)).ToList();
        var curated = await CurateAsync(account, req, taste, curationPool, anchor?.Name, ct);
        var taken = curated.Take(req.Limit).ToList();

        var whyContexts = await BuildWhyContextsAsync(taste, taken.Select(c => c.Scored).ToList(), anchor, ct);
        await PersistScoresAsync(account.Id, artifact.Id, taken.Select(c => c.Scored).ToList(), now, ct);

        // Output guard + no-repeat: a cached/LLM line that leaks third person, opens with the
        // banned "Because you loved", argues against its own pick, or exactly repeats an earlier
        // card on this page is discarded for the deterministic templater sentence — which itself
        // re-seeds until distinct.
        var takenWhys = new HashSet<string>(StringComparer.Ordinal);
        var genericUsed = false;
        var items = new List<ScoredFeedItem>(taken.Count);
        foreach (var c in taken)
        {
            string why;
            if (!string.IsNullOrWhiteSpace(c.Why) && !SecondPersonVoice.Rejects(c.Why)
                && !SecondPersonVoice.SoundsAntiRecommendation(c.Why) && takenWhys.Add(c.Why!))
            {
                why = c.Why!;
            }
            else if (whyContexts.TryGetValue(c.Scored.Title.Id, out var whyContext))
            {
                why = ExplanationTemplater.BuildDistinctSentence(whyContext, takenWhys);
            }
            else
            {
                why = ExplanationTemplater.GenericFallback;
            }

            // The generic fallback may appear at most once per page: a later card with nothing
            // verifiable to say ships an empty why rather than repeating boilerplate.
            if (string.Equals(why, ExplanationTemplater.GenericFallback, StringComparison.Ordinal))
            {
                why = genericUsed ? string.Empty : why;
                genericUsed = true;
            }

            items.Add(new ScoredFeedItem(
                c.Scored.Title, Math.Round(c.Scored.Predicted, 1), why, IsReleased(c.Scored.Title, now)));
        }

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
        Account account, MoreFeedRequest req, FeatureVectorBuilder.TasteState taste, List<Scored> pool, string? anchorName, CancellationToken ct)
    {
        if (!agent.IsAvailable || pool.Count == 0)
        {
            return pool.Select(s => new Curated(s, null)).ToList();
        }

        var rowKey = RowKey(req);
        var query = QueryFor(req);
        var fitById = new Dictionary<Guid, RerankResult>();
        var uncached = new List<RerankInput>();
        foreach (var s in pool)
        {
            if (cache.TryGetValue<RerankResult>(FitKey(account.Id, s.Title.Id, rowKey), out var hit) && hit is not null)
            {
                fitById[s.Title.Id] = hit;
            }
            else
            {
                uncached.Add(new RerankInput(s.Title.Id, s.Title.Name, s.Title.Year, s.Title.MediaType.ToString(), s.Title.Genres, s.Title.Overview, s.Predicted));
            }
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
                    var results = await agent.RerankBatchAsync(batch, query, narrative, anchorName, batchCt);
                    foreach (var r in results)
                    {
                        fitById[r.TitleId] = r;
                        cache.Set(FitKey(account.Id, r.TitleId, rowKey), r, FitTtl);
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
                var modelNorm = (double)s.Predicted / 10d;
                var blend = fit is null ? modelNorm : (0.6 * modelNorm) + (0.4 * fit.Fit);
                return (Scored: s, Fit: fit, Blend: blend);
            })
            .OrderByDescending(x => x.Blend)
            .Select(x => new Curated(x.Scored, x.Fit?.Why))
            .ToList();
    }

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

    internal static string FitKey(Guid accountId, Guid titleId, string rowKey) => $"feedfit:{accountId}:{titleId}:{rowKey}";

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
        // so it takes a dedicated retrieval path rather than the hybrid pop+ANN union.
        if (req.Context == FeedRowContext.Similar && req.AnchorTitleId is { } anchorId)
        {
            return await FetchSimilarWindowAsync(account, anchorId, exclude, taste, media, ct);
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
            ignoreGenreFilters: IgnoresGenreFilters(req));
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
        Account account, Guid anchorId, List<Guid> exclude, FeatureVectorBuilder.TasteState taste, MediaType? media, CancellationToken ct)
    {
        var anchor = await db.Titles
            .Where(t => t.Id == anchorId)
            .Select(t => new { t.MediaType, t.Genres })
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
            q = q.Where(t => t.TmdbId != null && t.PosterPath != null && t.Id != anchorId
                && t.MediaType == anchorMedia && !exclude.Contains(t.Id));
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
            return await candidateRetriever.RetrieveSimilarAsync(
                account.Id, anchorEmbedding, lane,
                count: WindowPopCount + WindowAnnCount,
                account.Settings.MaturityCeiling, ct);
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
        return items.Count;
    }

    private async Task<List<Scored>> RankAsync(
        MoreFeedRequest req, FeatureVectorBuilder.TasteState taste, List<Scored> scored,
        IReadOnlyDictionary<Guid, double> boosts, DateTime now, CancellationToken ct)
    {
        double Boost(Guid id) => boosts.TryGetValue(id, out var b) ? b : 1d;

        switch (req.Context)
        {
            // Both the home "because you loved" rail and the detail "more like this" rail rank by
            // anchor-embedding cosine × predicted — the same blend, differing only in which title is
            // the anchor (a loved title vs the one being viewed).
            case FeedRowContext.BecauseYouLoved when req.AnchorTitleId is { } lovedAnchorId:
                return await RankByAnchorSimilarityAsync(lovedAnchorId, scored, boosts, ct);

            case FeedRowContext.Similar when req.AnchorTitleId is { } viewedAnchorId:
                return await RankByAnchorSimilarityAsync(viewedAnchorId, scored, boosts, ct);

            case FeedRowContext.DeepCuts:
                {
                    var ordered = scored.OrderBy(s => s.Title.TmdbPopularity ?? 0m).ToList();
                    var threshold = ordered.Count > 0
                        ? ordered[ordered.Count * 7 / 10].Title.TmdbPopularity ?? 0m : 0m;
                    return scored
                        .Where(s => s.Predicted >= 6m && (s.Title.TmdbPopularity ?? 0m) <= threshold)
                        .OrderByDescending(s => (double)s.Predicted * Boost(s.Title.Id))
                        .ToList();
                }

            case FeedRowContext.Hero:
            case FeedRowContext.NewForYou:
                return scored.OrderByDescending(s => (double)s.Predicted * (double)Freshness(s.Title, now) * Boost(s.Title.Id)).ToList();

            default:
                return scored.OrderByDescending(s => (double)s.Predicted * Boost(s.Title.Id)).ToList();
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
        Guid anchorId, List<Scored> scored, IReadOnlyDictionary<Guid, double> boosts, CancellationToken ct)
    {
        double Boost(Guid id) => boosts.TryGetValue(id, out var b) ? b : 1d;

        var anchor = await db.Titles.FirstOrDefaultAsync(t => t.Id == anchorId, ct);
        if (anchor is null)
        {
            return scored.OrderByDescending(s => (double)s.Predicted * Boost(s.Title.Id)).ToList();
        }

        var embIds = scored.Select(s => s.Title.Id).Append(anchorId).ToList();
        var emb = (await db.TitleEmbeddings.Where(e => embIds.Contains(e.TitleId)).ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        double Sim(Title t) => emb.TryGetValue(anchorId, out var va) && emb.TryGetValue(t.Id, out var vb)
            ? Cosine(va, vb) : GenreJaccard(anchor, t);

        return scored
            .Select(s => (s, sim: Sim(s.Title)))
            .Where(x => x.sim > 0.15d)
            .OrderByDescending(x => (double)x.s.Predicted * (0.5 + x.sim) * Boost(x.s.Title.Id))
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
        FeatureVectorBuilder.TasteState taste, List<Scored> taken, AnchorInfo? anchor, CancellationToken ct)
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
                sharedGenre, toneByTitle.GetValueOrDefault(s.Title.Id));
        }

        return result;
    }

    private async Task PersistScoresAsync(Guid accountId, Guid artifactId, List<Scored> taken, DateTime now, CancellationToken ct)
    {
        if (taken.Count == 0)
        {
            return;
        }

        var ids = taken.Select(s => s.Title.Id).ToList();
        var existing = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.ModelArtifactId == artifactId && ids.Contains(s.TitleId))
            .ToDictionaryAsync(s => s.TitleId, ct);

        foreach (var s in taken)
        {
            if (!existing.TryGetValue(s.Title.Id, out var score))
            {
                score = new TitleScore { Id = Guid.NewGuid(), AccountId = accountId, TitleId = s.Title.Id, ModelArtifactId = artifactId };
                db.TitleScores.Add(score);
            }

            score.PredictedRating = Math.Round(s.Predicted, 2);
            score.ContributionsJson = JsonSerializer.Serialize(
                s.Row.TopContributions.Select(c => new { feature = c.Feature, value = Math.Round(c.Contribution, 3) }));
            score.ScoredAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<decimal> MinPredictedAsync(Guid accountId, CancellationToken ct)
    {
        var raw = await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value).FirstOrDefaultAsync(ct);
        return raw is not null && decimal.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var floor)
            ? Math.Clamp(floor, 0m, 10m) : 0m;
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

    /// <summary>A model-scored title after the LLM curation pass — Why is the agent's line, or null.</summary>
    private sealed record Curated(Scored Scored, string? Why);
}
