using FrostAura.Reel.Application.Abstractions;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// Live "Ask Reel": pull titles matching a natural-language turn straight from TMDB on demand,
/// score them for the user, then re-rank every one with the conversational agent — streamed as
/// it resolves, never bounded by what's already in the catalogue. The flow, emitted as SSE events:
/// assistant-message (the agent's reply) → phase → candidate → candidate-scored → candidate-reranked
/// → done. Catalog rows are global, so every search warms the catalogue for all tenants; the
/// per-query TMDB fetch is shared via <see cref="ICatalogWorkCoordinator"/>, and per-title LLM
/// fit is cached so re-asks never re-spend.
/// </summary>
public sealed class LiveSearchExpansionService(
    IReelDbContext db,
    IAccountContext accountContext,
    ISearchAgent agent,
    ITmdbClient tmdb,
    IEmbeddingProvider embeddings,
    EligibilityQueryBuilder eligibility,
    CandidateGenerator candidateGenerator,
    TitleHydrator hydrator,
    FeatureVectorBuilder featureBuilder,
    OnDemandScorer scorer,
    LexicalSearchService lexical,
    ICatalogWorkCoordinator coordinator,
    IMemoryCache cache,
    ILogger<LiveSearchExpansionService> logger)
{
    private const int MaxCandidates = 40;
    private const int ReturnCount = 24;
    private const int PersonReturnCount = 40; // a filmography can run long — show more than a vibe match
    private const int HydrateScoreSlice = 12;
    private const int SemanticNeighbourCount = 40; // semantic kNN pull size — mirrors the TMDB arm's ceiling
    private const int RerankBatch = 6;
    private const int SearchPages = 1;

    /// <summary>
    /// The floor that makes "no results" a bug rather than an outcome. If a turn's eligible pool
    /// comes in under this, discovery re-runs at a wider relaxation level rather than reporting
    /// nothing — a request is always relative to *something*, so an empty answer is a failure to
    /// look hard enough, not an honest verdict.
    /// </summary>
    private const int MinResults = 12;

    /// <summary>How many progressively-wider discovery passes a turn may make before giving up.</summary>
    private const int MaxRelaxations = 3;

    /// <summary>Penalty applied to the blend per matched negation ("less horror", "no ghosts").</summary>
    private const double NegationPenalty = 0.22;
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);

    /// <summary>An SSE event emitter — (eventName, payload). The endpoint writes it to the stream.</summary>
    public delegate Task Emit(string eventName, object payload);

    /// <summary>Single-turn convenience overload (no conversation history / shown set).</summary>
    public Task StreamAsync(string message, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(message, [], [], region, emit, ct);

    /// <summary>Overload without an explicit media filter — the intent's own inference applies.</summary>
    public Task StreamAsync(
        string message, IReadOnlyList<ChatTurn> history, IReadOnlyList<long> shownTmdbIds, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(message, history, shownTmdbIds, region, [], emit, ct);

    /// <summary>
    /// Runs a conversational turn end-to-end, emitting the SSE event stream. Never throws for an
    /// ordinary failure — emits a graceful done with a reason instead.
    /// </summary>
    public async Task StreamAsync(
        string message,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<long> shownTmdbIds,
        string? region,
        IReadOnlyList<MediaType> mediaFilter,
        Emit emit,
        CancellationToken ct)
    {
        var accountId = accountContext.AccountId
            ?? throw new InvalidOperationException("Live search requires an account.");
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            await emit("done", new { results = Array.Empty<object>(), reason = "empty query" });
            return;
        }

        try
        {
            await emit("phase", new { stage = "searching", found = 0, scored = 0 });

            // No embeddings provider → can't rank semantically. Fall back to the local lexical
            // engine so Ask Reel still answers (keyless installs); stream those, unscored-by-rank.
            if (!embeddings.IsAvailable)
            {
                await StreamLexicalFallbackAsync(accountId, trimmed, emit, ct);
                return;
            }

            var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
            var tasteSummary = await BuildTasteSummaryAsync(taste, ct);

            // ── Interpret the turn → conversational reply + discovery intent ─────────────────
            var shownNames = await ShownTitleNamesAsync(shownTmdbIds, ct);
            var agentReply = await agent.InterpretAsync(history, trimmed, tasteSummary, shownNames, ct);
            await emit("assistant-message", new { text = agentReply.Reply });

            // A named-person query ("all movies Nolan directs") is a lookup: it relaxes the
            // unseen-only rule (ContentFilteredTitles keeps watched titles, badged) so the
            // filmography is complete, and it skips the semantic arm (a filmography is not a vibe
            // match). Every other query stays unseen-only (EligibleTitles).
            var isPersonQuery = agentReply.Intent.People is { Count: > 0 };

            // ── Discover candidate titles — TWO arms unioned ─────────────────────────────────
            //   • TMDB discovery (cached per intent; the firehose single-flighted) — genre / keyword
            //     / title-search / person-credit pulls that widen the catalogue from the outside.
            //   • Semantic kNN (this call — cheap, always fresh, kept OUTSIDE the TMDB cache so the
            //     cache key stays a pure function of intent) — pgvector cosine over the user's own
            //     embedded UNSEEN catalogue against a concept embedding of the turn. THE recall fix:
            //     a vibe like "fantastically futuristic, tense" that TMDB genre/keyword search whiffs
            //     on — and whose few hits a heavy watcher has already seen — still surfaces the
            //     nearest-by-meaning unseen titles we already hold. Both flow through the SAME
            //     eligible → embed → cosine-prerank → score → rerank pipeline below.
            // An explicit media filter from the UI is a direct instruction and outranks whatever the
            // model inferred from the prose. Threading it here (rather than filtering the rendered
            // cards client-side, as this used to) is what stops "Movies" turning a turn that found
            // eight shows into an empty screen: discovery now goes looking for movies instead.
            var intent = mediaFilter.Count > 0
                ? agentReply.Intent with { MediaTypes = mediaFilter.Distinct().ToList() }
                : agentReply.Intent;
            agentReply = agentReply with { Intent = intent };

            var shownSet = shownTmdbIds.ToHashSet();
            var baseQuery = isPersonQuery
                ? eligibility.ContentFilteredTitles(accountId)
                : eligibility.EligibleTitles(accountId);

            // ── Discover, widening until there is enough to rank ─────────────────────────────
            // Each pass relaxes one more constraint. An under-filled pool is treated as "we did not
            // look hard enough yet", never as "there is nothing" — the ladder bottoms out at a pull
            // that cannot come back empty for an account with any eligible catalogue at all.
            var discovered = new List<Guid>();
            var eligible = new List<Title>();
            var relaxation = 0;
            for (; relaxation < MaxRelaxations; relaxation++)
            {
                var pass = Relax(intent, relaxation);
                var candidateIds = await DiscoverCandidateIdsAsync(pass, region, ct);
                var semanticIds = isPersonQuery
                    ? new List<Guid>()
                    : await SemanticNeighbourIdsAsync(accountId, agentReply with { Intent = pass }, ct);
                discovered = discovered.Concat(candidateIds).Concat(semanticIds).Distinct().ToList();
                await emit("phase", new { stage = "discovered", found = discovered.Count, scored = 0 });

                eligible = (await baseQuery
                        .Where(t => discovered.Contains(t.Id))
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    .Where(t => t.TmdbId == null || !shownSet.Contains(t.TmdbId.Value))
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .ToList();

                if (eligible.Count >= MinResults || isPersonQuery)
                {
                    break;
                }
            }

            // Last resort: the user's own eligible catalogue, nearest-by-taste. Reaching here means
            // discovery genuinely failed, and showing loosely-related titles the user has not seen
            // beats an empty screen — the request is relative, so something is always closer than
            // nothing. Only a truly empty eligible catalogue can still come back with zero.
            if (eligible.Count == 0)
            {
                eligible = (await baseQuery
                        .Where(t => t.TmdbPopularity != null)
                        .OrderByDescending(t => t.TmdbPopularity)
                        .Take(MaxCandidates * 2)
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    .Where(t => t.TmdbId == null || !shownSet.Contains(t.TmdbId.Value))
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .ToList();
                relaxation = MaxRelaxations;
            }

            if (eligible.Count == 0)
            {
                await emit("done", new { results = Array.Empty<object>(), reason = "no fresh matches — try another angle" });
                return;
            }

            // Person queries are a filmography, not a vibe match: order chronologically, badge what
            // you've seen, score for the predicted-rating badge — but never re-order or cull by fit.
            if (isPersonQuery)
            {
                await StreamFilmographyAsync(accountId, agentReply.Intent, eligible, taste, now, emit, ct);
                return;
            }

            // ── Embed eligible candidates that lack a vector, then cosine pre-rank ───────────
            await emit("phase", new { stage = "embedding", found = eligible.Count, scored = 0 });
            await EnsureEmbeddedAsync(eligible, ct);
            var vectorsByTitle = await LoadVectorsAsync(eligible.Select(t => t.Id).ToList(), ct);
            var tagVectors = await LoadPreferenceTagVectorsAsync(accountId, ct);
            var queryVec = (await embeddings.EmbedAsync([trimmed], ct))[0];
            var ranked = eligible
                .Where(t => vectorsByTitle.ContainsKey(t.Id))
                .Select(t => (Title: t, Sim: Cosine(queryVec, vectorsByTitle[t.Id])))
                .OrderByDescending(x => x.Sim)
                .Take(ReturnCount)
                .ToList();

            foreach (var (title, sim) in ranked)
            {
                await emit("candidate", Card(title, sim, predicted: null));
            }

            // ── Hydrate + personally score EVERY returned card ───────────────────────────────
            // Score all ReturnCount cards, not just the head slice — otherwise every card past the
            // first slice renders a default ~6.0 predicted-rating badge instead of a real per-user
            // score. OnDemandScorer batches, so one call covers the whole set.
            await emit("phase", new { stage = "scoring", found = ranked.Count, scored = 0 });
            var sliceIds = ranked.Select(x => x.Title.Id).ToList();
            await HydrateAsync(sliceIds, ct);
            var scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);
            foreach (var id in sliceIds)
            {
                await emit("candidate-scored", new { titleId = id, predictedRating = scores.TryGetValue(id, out var p) ? (decimal?)p : null });
            }

            // ── Live per-movie LLM re-rank (the hyper-personal layer) ────────────────────────
            await emit("phase", new { stage = "ranking", found = ranked.Count, scored = sliceIds.Count });
            var fitById = await RerankStreamAsync(accountId, trimmed, tasteSummary, ranked, scores, emit, ct);

            // ── Final blend: relevance × personal score × hyper-personal fit ─────────────────
            var final = ranked
                .Select(x =>
                {
                    var predicted = scores.TryGetValue(x.Title.Id, out var p) ? (decimal?)p : null;
                    var fit = fitById.TryGetValue(x.Title.Id, out var fr) ? fr : null;
                    var tagBoost = vectorsByTitle.TryGetValue(x.Title.Id, out var vec) ? TagAffinity(vec, tagVectors) : 0d;
                    // Negations demote, they never cull — a rejected trait should push a title down
                    // the list, not risk emptying it.
                    var penalty = NegationDemerit(x.Title, intent);
                    var blend = (x.Sim * 0.32) + ((double)(predicted ?? 6m) / 10d * 0.23) + ((fit?.Fit ?? 0.5d) * 0.35) + (tagBoost * 0.10) - penalty;
                    return (x.Title, x.Sim, predicted, Fit: fit, Blend: blend);
                })
                .OrderByDescending(x => x.Blend)
                .Select(x => Card(x.Title, x.Sim, x.predicted, x.Fit?.Fit, x.Fit?.Why))
                .ToList();

            // Report what was RETURNED, not what was pulled. The old wording was derived purely
            // from the pre-ranking counts, so an empty screen still read "50 pulled; 8 unseen" —
            // the numbers described a result set the user could not see.
            await emit("done", new
            {
                results = final,
                reason = final.Count == 0
                    ? "no fresh matches — try another angle"
                    : $"{final.Count} picks ranked for you, from {discovered.Count} pulled"
                        + (relaxation > 0 ? " (widened to find enough)" : "") + ".",
            });
            // Logs `final` too: the count the user actually sees is the one worth alerting on, and
            // its absence is what let an empty result set look healthy in the logs.
            logger.LogInformation(
                "Ask Reel '{Query}': {Discovered} discovered at relaxation {Relaxation}, {Eligible} eligible, {Scored} scored, {Reranked} reranked, {Returned} returned.",
                trimmed, discovered.Count, relaxation, eligible.Count, sliceIds.Count, fitById.Count, final.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ask Reel expansion failed for '{Query}'.", trimmed);
            await emit("done", new { results = Array.Empty<object>(), reason = "search hit a snag — please try again" });
        }
    }

    /// <summary>Re-ranks every shown card with the agent (batched, cached per (account,title,query)); streams candidate-reranked.</summary>
    private async Task<Dictionary<Guid, RerankResult>> RerankStreamAsync(
        Guid accountId, string query, string tasteSummary,
        IReadOnlyList<(Title Title, double Sim)> ranked,
        IReadOnlyDictionary<Guid, decimal> scores, Emit emit, CancellationToken ct)
    {
        var qnorm = NormalizeQuery(query);
        var fitById = new Dictionary<Guid, RerankResult>();

        for (var i = 0; i < ranked.Count; i += RerankBatch)
        {
            var batch = ranked.Skip(i).Take(RerankBatch).ToList();
            var uncached = new List<RerankInput>();
            foreach (var (title, _) in batch)
            {
                if (cache.TryGetValue<RerankResult>(FitKey(accountId, title.Id, qnorm), out var cached) && cached is not null)
                {
                    fitById[title.Id] = cached;
                }
                else
                {
                    uncached.Add(new RerankInput(
                        title.Id, title.Name, title.Year, title.MediaType.ToString(), title.Genres, title.Overview,
                        scores.TryGetValue(title.Id, out var p) ? p : null));
                }
            }

            if (uncached.Count > 0)
            {
                var results = await agent.RerankBatchAsync(uncached, query, tasteSummary, ct);
                foreach (var res in results)
                {
                    fitById[res.TitleId] = res;
                    cache.Set(FitKey(accountId, res.TitleId, qnorm), res, FitTtl);
                }
            }

            foreach (var (title, _) in batch)
            {
                if (fitById.TryGetValue(title.Id, out var fit))
                {
                    await emit("candidate-reranked", new { titleId = title.Id, fit = fit.Fit, why = fit.Why });
                }
            }
        }

        return fitById;
    }

    private async Task<List<Guid>> DiscoverCandidateIdsAsync(SearchIntent intent, string? region, CancellationToken ct)
    {
        var key = IntentKey(intent);
        if (cache.TryGetValue<List<Guid>>(CacheKey(key), out var cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        // The TMDB firehose for this intent runs once even under concurrent identical searches;
        // CancellationToken.None so a disconnecting waiter can't starve a sharer (dev StrictMode
        // remount), and it warms the cache regardless.
        var items = await coordinator.RunOnceAsync(
            $"askreel:fetch:{key}",
            () => FetchFromTmdbAsync(intent, region, CancellationToken.None));
        if (items.Count == 0)
        {
            return [];
        }

        var ids = await UpsertAndCollectIdsAsync(items, ct);
        cache.Set(CacheKey(key), ids, DiscoveryTtl);
        return ids;
    }

    /// <summary>
    /// The semantic discovery arm — pgvector cosine kNN over the account's own embedded UNSEEN
    /// catalogue against a concept embedding of the turn. The recall fix for vibe queries: TMDB
    /// genre/keyword/title search whiffs on "fantastically futuristic, tense", and a heavy watcher
    /// has already seen most of the little it does return, so discovery starves. Embedding the
    /// agent's concept text and pulling the nearest-by-meaning UNSEEN titles surfaces matches we
    /// already hold that no keyword names. Restricts to eligible ids BEFORE ordering by cosine
    /// (mirrors <see cref="CandidateRetriever"/>'s AnnPullAsync) — the catalogue is dominated by the
    /// user's seen library, so a global-nearest-then-filter query returns almost nothing. Returns
    /// empty on any failure ⇒ the TMDB arm carries the turn alone (no regression).
    /// </summary>
    private async Task<List<Guid>> SemanticNeighbourIdsAsync(Guid accountId, AgentReply agentReply, CancellationToken ct)
    {
        var concept = ConceptText(agentReply);
        if (concept.Length == 0)
        {
            return [];
        }

        float[] queryVec;
        try
        {
            var embedded = await embeddings.EmbedAsync([concept], ct);
            if (embedded.Length == 0 || embedded[0].Length == 0)
            {
                return [];
            }

            queryVec = embedded[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel semantic arm: concept embedding failed; falling back to TMDB-only discovery.");
            return [];
        }

        var vector = new Vector(queryVec);
        var eligibleIds = eligibility.EligibleTitles(accountId).Select(t => t.Id);
        return await db.TitleEmbeddings
            .Where(e => eligibleIds.Contains(e.TitleId))
            .OrderBy(e => e.Embedding.CosineDistance(vector))
            .Take(SemanticNeighbourCount)
            .Join(db.Titles, e => e.TitleId, t => t.Id, (e, t) => t.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The richest concept text available for the semantic seed — the agent's reply plus the
    /// interpreted free-text, keywords, and genres. This is the "fantastically futuristic, tense,
    /// high-energy" phrasing the query embedding is built from; it embeds in the SAME vector space
    /// as the title embeddings (one <see cref="IEmbeddingProvider"/> by construction).
    /// </summary>
    /// <summary>
    /// One rung of the widening ladder. Level 0 is the intent as asked; each level after drops the
    /// narrowest surviving constraint, because an over-specified turn ("dark thriller serial killer
    /// that gets caught at the end") pulls almost nothing through TMDB's keyword AND-ing.
    /// Media type is never relaxed — it is the one constraint the user set explicitly.
    /// </summary>
    private static SearchIntent Relax(SearchIntent intent, int level) => level switch
    {
        0 => intent,
        // Keywords are AND-ed by TMDB discovery, so they collapse recall fastest — halve them.
        1 => intent with { Keywords = intent.Keywords.Take(Math.Max(1, intent.Keywords.Count / 2)).ToList() },
        // Genre-only: the broadest pull that still honours the vibe.
        _ => intent with { Keywords = [], FreeText = string.Empty },
    };

    /// <summary>True when the title matches the requested media types (empty list = no constraint).</summary>
    private static bool MatchesMedia(Title title, IReadOnlyList<MediaType> mediaTypes) =>
        mediaTypes.Count == 0 || mediaTypes.Contains(title.MediaType);

    /// <summary>
    /// How much a title contradicts what the user ruled out. Each matched negated genre or keyword
    /// costs one <see cref="NegationPenalty"/>; capped so even a title that trips every negation
    /// stays rankable rather than being effectively removed.
    /// </summary>
    private static double NegationDemerit(Title title, SearchIntent intent)
    {
        var hits = 0;
        if (intent.ExcludeGenres is { Count: > 0 } badGenres && title.Genres is { Length: > 0 })
        {
            hits += title.Genres.Count(g => badGenres.Contains(g, StringComparer.OrdinalIgnoreCase));
        }

        if (intent.ExcludeKeywords is { Count: > 0 } badWords)
        {
            var haystack = $"{title.Name} {title.Overview}";
            hits += badWords.Count(w => w.Length > 2 && haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        return Math.Min(hits * NegationPenalty, 0.66);
    }

    private static string ConceptText(AgentReply agentReply)
    {
        var intent = agentReply.Intent;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(agentReply.Reply))
        {
            parts.Add(agentReply.Reply.Trim());
        }

        if (!string.IsNullOrWhiteSpace(intent.FreeText))
        {
            parts.Add(intent.FreeText.Trim());
        }

        if (intent.Keywords.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Keywords));
        }

        if (intent.Genres.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Genres));
        }

        return string.Join(". ", parts).Trim();
    }

    /// <summary>
    /// Streams a person's filmography: chronological (newest first), already-watched entries kept
    /// and badged, the top slice scored for the predicted-rating badge — but never re-ordered or
    /// culled by LLM fit, because the user asked for the WHOLE list, not a recommendation.
    /// </summary>
    private async Task StreamFilmographyAsync(
        Guid accountId, SearchIntent intent, List<Title> eligible,
        FeatureVectorBuilder.TasteState taste, DateTime now, Emit emit, CancellationToken ct)
    {
        var ordered = eligible
            .OrderByDescending(t => t.ReleasedAt ?? t.FirstAiredAt ?? DateTime.MinValue)
            .Take(PersonReturnCount)
            .ToList();

        var seenIds = await SeenTitleIdsAsync(accountId, ordered.Select(t => t.Id).ToList(), ct);
        foreach (var t in ordered)
        {
            await emit("candidate", Card(t, similarity: null, predicted: null, seen: seenIds.Contains(t.Id)));
        }

        // Score the top slice for the badge (no re-rank — the order is the filmography).
        var sliceIds = ordered.Take(HydrateScoreSlice).Select(t => t.Id).ToList();
        await HydrateAsync(sliceIds, ct);
        var scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);
        foreach (var id in sliceIds)
        {
            await emit("candidate-scored", new { titleId = id, predictedRating = scores.TryGetValue(id, out var p) ? (decimal?)p : null });
        }

        var results = ordered
            .Select(t => Card(t, similarity: null,
                predicted: scores.TryGetValue(t.Id, out var p) ? (decimal?)p : null,
                seen: seenIds.Contains(t.Id)))
            .ToList();

        var who = intent.People is { Count: > 0 } ? intent.People[0] : null;
        var verb = who?.Role == PersonRole.Director ? "directed by" : who?.Role == PersonRole.Cast ? "starring" : "from";
        var reason = who is null
            ? $"{ordered.Count} titles in this filmography."
            : $"{ordered.Count} titles {verb} {who.Name}.";
        await emit("done", new { results, reason });
        logger.LogInformation("Ask Reel filmography: {Count} titles for {Person}.", ordered.Count, who?.Name ?? "?");
    }

    /// <summary>The subset of the given title ids the account has watched or rated (for "seen" badges).</summary>
    private async Task<HashSet<Guid>> SeenTitleIdsAsync(Guid accountId, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        var watched = await db.WatchedTitles
            .Where(w => w.AccountId == accountId && titleIds.Contains(w.TitleId))
            .Select(w => w.TitleId).ToListAsync(ct);
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId && titleIds.Contains(r.TitleId))
            .Select(r => r.TitleId).ToListAsync(ct);
        return watched.Concat(rated).ToHashSet();
    }

    private async Task<IReadOnlyList<TmdbListItem>> FetchFromTmdbAsync(SearchIntent intent, string? region, CancellationToken ct)
    {
        // A named-person query resolves to that person's TMDB credits — a name has no business in
        // keyword/title search. Falls through to the normal path only if no person resolves.
        if (intent.People is { Count: > 0 } people)
        {
            var credits = await FetchPeopleCreditsAsync(people, intent, ct);
            if (credits.Count > 0)
            {
                return credits;
            }
        }

        var movieGenreIds = intent.Genres.Where(TmdbGenres.SlugToIds.ContainsKey)
            .Select(g => TmdbGenres.SlugToIds[g].Movie).OfType<int>().ToList();
        var tvGenreIds = intent.Genres.Where(TmdbGenres.SlugToIds.ContainsKey)
            .Select(g => TmdbGenres.SlugToIds[g].Tv).OfType<int>().ToList();

        var keywordIds = new List<int>();
        foreach (var phrase in intent.Keywords.Take(4))
        {
            var matches = await tmdb.SearchKeywordsAsync(phrase, ct);
            if (matches.Count > 0)
            {
                keywordIds.Add(matches[0].Id);
            }
        }

        var wantMovies = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Movie);
        var wantTv = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Show);
        DateTime? after = intent.MinYear is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;

        var tasks = new List<Task<IReadOnlyList<TmdbListItem>>>();
        if (wantMovies)
        {
            tasks.Add(tmdb.DiscoverByConceptAsync(true, movieGenreIds, keywordIds, region, after, SearchPages, ct));
            tasks.Add(tmdb.SearchTitlesAsync(true, intent.FreeText, SearchPages, ct));
        }

        if (wantTv)
        {
            tasks.Add(tmdb.DiscoverByConceptAsync(false, tvGenreIds, keywordIds, null, after, SearchPages, ct));
            tasks.Add(tmdb.SearchTitlesAsync(false, intent.FreeText, SearchPages, ct));
        }

        var batches = await Task.WhenAll(tasks);
        return batches
            .SelectMany(b => b)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .OrderByDescending(i => i.Popularity)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>Resolve each named person → their TMDB credits (role-sliced), unioned + deduped, newest first.</summary>
    private async Task<IReadOnlyList<TmdbListItem>> FetchPeopleCreditsAsync(IReadOnlyList<PersonQuery> people, SearchIntent intent, CancellationToken ct)
    {
        var wantMovies = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Movie);
        var wantTv = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Show);
        DateTime? after = intent.MinYear is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;

        var all = new List<TmdbListItem>();
        foreach (var person in people.Take(2))
        {
            var resolved = await tmdb.SearchPersonAsync(person.Name, ct);
            if (resolved is null)
            {
                continue;
            }

            var role = person.Role switch
            {
                PersonRole.Director => PersonCreditRole.Director,
                PersonRole.Cast => PersonCreditRole.Cast,
                _ => PersonCreditRole.Any,
            };
            all.AddRange(await tmdb.GetPersonCreditsAsync(resolved.Id, role, ct));
        }

        return all
            .Where(i => i.IsMovie ? wantMovies : wantTv)
            .Where(i => after is null || i.ReleasedAt is null || i.ReleasedAt >= after)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .OrderByDescending(i => i.ReleasedAt ?? DateTime.MinValue)
            .Take(MaxCandidates)
            .ToList();
    }

    private async Task<List<Guid>> UpsertAndCollectIdsAsync(IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        await candidateGenerator.UpsertCandidatesAsync(items, ct);
        var movieIds = items.Where(i => i.IsMovie).Select(i => i.Id).ToList();
        var tvIds = items.Where(i => !i.IsMovie).Select(i => i.Id).ToList();
        return await db.Titles
            .Where(t => t.TmdbId != null
                && ((t.MediaType == MediaType.Movie && movieIds.Contains(t.TmdbId.Value))
                    || (t.MediaType == MediaType.Show && tvIds.Contains(t.TmdbId.Value))))
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task EnsureEmbeddedAsync(IReadOnlyList<Title> eligible, CancellationToken ct)
    {
        var ids = eligible.Select(t => t.Id).ToList();
        var already = await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).Select(e => e.TitleId).ToListAsync(ct);
        var missing = eligible.Where(t => !already.Contains(t.Id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var texts = missing.Select(EmbeddingText.Build).ToList();
        var vectors = await embeddings.EmbedAsync(texts, ct);
        for (var i = 0; i < missing.Count; i++)
        {
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = missing[i].Id,
                Embedding = new Vector(vectors[i]),
                EmbeddingModel = EmbeddingText.Model,
                SourceTextHash = EmbeddingText.Hash(texts[i]),
                CreatedAt = DateTime.UtcNow,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent search embedded some of these first (TitleEmbedding PK = TitleId).
            if (db is DbContext ctx)
            {
                foreach (var entry in ctx.ChangeTracker.Entries<TitleEmbedding>()
                             .Where(e => e.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private async Task HydrateAsync(IReadOnlyList<Guid> sliceIds, CancellationToken ct)
    {
        var needs = await db.Titles
            .Where(t => sliceIds.Contains(t.Id) && t.LastMetadataRefreshAt == null && t.TmdbId != null)
            .ToListAsync(ct);
        if (needs.Count == 0)
        {
            return;
        }

        await hydrator.HydrateBatchAsync(needs, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Concurrent hydration conflict on {Count} live-search titles; continuing.", needs.Count);
        }
    }

    private async Task<Dictionary<Guid, float[]>> LoadVectorsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).ToListAsync(ct);
        return rows.ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());
    }

    /// <summary>The account's embedded preference-tag vectors — for the positive ranking boost.</summary>
    private async Task<List<float[]>> LoadPreferenceTagVectorsAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.UserPreferenceTags
            .Where(t => t.AccountId == accountId && t.Embedding != null)
            .Select(t => t.Embedding!)
            .ToListAsync(ct);
        return rows.Select(v => v.ToArray()).ToList();
    }

    /// <summary>Max cosine of a title to any of the user's preference tags (0 when there are none).</summary>
    private static double TagAffinity(float[] titleVec, IReadOnlyList<float[]> tagVecs)
    {
        var best = 0d;
        foreach (var tag in tagVecs)
        {
            best = Math.Max(best, Cosine(titleVec, tag));
        }

        return best;
    }

    /// <summary>A compact taste line for the agent prompts — top genres + a few loved titles.</summary>
    private async Task<string> BuildTasteSummaryAsync(FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var topGenres = taste.GenreRatings
            .Select(kv => new { kv.Key, Score = kv.Value.Count })
            .OrderByDescending(g => g.Score)
            .Take(4)
            .Select(g => g.Key)
            .ToList();
        var loved = await db.Titles
            .Where(t => taste.TopLovedTitleIds.Contains(t.Id))
            .Select(t => t.Name)
            .Take(4)
            .ToListAsync(ct);

        var parts = new List<string>();
        if (topGenres.Count > 0)
        {
            parts.Add("favours " + string.Join(", ", topGenres));
        }

        if (loved.Count > 0)
        {
            parts.Add("loves " + string.Join(", ", loved));
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "no strong signal yet";
    }

    private async Task<IReadOnlyList<string>> ShownTitleNamesAsync(IReadOnlyList<long> shownTmdbIds, CancellationToken ct)
    {
        if (shownTmdbIds.Count == 0)
        {
            return [];
        }

        return await db.Titles
            .Where(t => t.TmdbId != null && shownTmdbIds.Contains(t.TmdbId.Value))
            .Select(t => t.Name)
            .Take(20)
            .ToListAsync(ct);
    }

    private async Task StreamLexicalFallbackAsync(Guid accountId, string query, Emit emit, CancellationToken ct)
    {
        var hits = await lexical.SearchAsync(accountId, query, ReturnCount, ct);
        var cards = new List<object>();
        foreach (var hit in hits)
        {
            var card = Card(hit.Title, similarity: Math.Round(hit.MatchScore, 3), predicted: null);
            cards.Add(card);
            await emit("candidate", card);
        }

        await emit("done", new { results = cards, reason = cards.Count == 0 ? "no matches" : "matched on concepts & keywords" });
    }

    private static object Card(Title t, double? similarity, decimal? predicted, double? fit = null, string? why = null, bool seen = false) => new
    {
        titleId = t.Id,
        mediaType = t.MediaType.ToString(),
        tmdbId = t.TmdbId,
        name = t.Name,
        year = t.Year,
        posterPath = t.PosterPath,
        genres = t.Genres,
        similarity,
        predictedRating = predicted,
        fit,
        why,
        seen,
    };

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

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.ToLowerInvariant().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string IntentKey(SearchIntent intent) =>
        $"{string.Join(',', intent.Genres.OrderBy(g => g))}|{string.Join(',', intent.Keywords.OrderBy(k => k))}|" +
        $"{string.Join(',', intent.MediaTypes.Select(m => m.ToString()).OrderBy(m => m))}|{intent.MinYear}|{NormalizeQuery(intent.FreeText)}|" +
        $"{string.Join(',', (intent.People ?? []).Select(p => $"{p.Name}:{p.Role}").OrderBy(p => p))}";

    private static string CacheKey(string intentKey) => $"askreel:ids:{intentKey}";

    private static string FitKey(Guid accountId, Guid titleId, string qnorm) => $"rerank:{accountId}:{titleId}:{qnorm}";
}
