using System.Globalization;
using FrostAura.Reel.Application.Abstractions;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
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
    PersonalisationContext personalisation,
    OnDemandScorer scorer,
    ExposureLog exposures,
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

    /// <summary>
    /// How many cards must clear the account's rating floor for the floor to BIND at all. Fewer
    /// than this and the floor is dropped for the turn, because a bar honoured strictly enough to
    /// leave three cards on screen has stopped being a preference and become an empty result.
    ///
    /// <para>It is a threshold, never a take. It used to be both — the relaxed branch also
    /// truncated to it — so "your rating floor was relaxed" silently meant "and most of your
    /// results were discarded".</para>
    /// </summary>
    private const int MinFloorRelaxedCount = 6;

    /// <summary>
    /// Per-match weight inside <see cref="NegationDemerit"/> ("less horror", "no ghosts"). Since
    /// the hard partition landed, the demerit's only live role is the contradicts-vs-clean test —
    /// it no longer subtracts from the final blend, because a penalty that merely demotes can
    /// still interleave rejected titles among clean ones.
    /// </summary>
    private const double NegationPenalty = 0.22;
    /// <summary>
    /// The calendar window a release year is allowed to name. The lower bound is the year of the
    /// first surviving film; the upper is the largest year <see cref="DateTime"/> can hold.
    /// </summary>
    /// <remarks>
    /// This is a CRASH guard, not a taste guard. <see cref="SearchIntent.MinYear"/> and
    /// <see cref="SearchIntent.MaxYear"/> arrive from a language model reading free text, so
    /// "movies from the year 99999" reaches four <c>new DateTime(year, …)</c> sites — two in the
    /// concept fetch, two in the person-credit fetch — every one of which throws
    /// <see cref="ArgumentOutOfRangeException"/> and takes the whole turn down as a "snag". A
    /// clamped bound answers a nonsense year with an ordinary, if unhelpful, result set; the
    /// parser narrows the range further for sense, which is a different job.
    /// </remarks>
    private const int MinCalendarYear = 1870;

    /// <inheritdoc cref="MinCalendarYear"/>
    private const int MaxCalendarYear = 9999;

    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);

    /// <summary>An SSE event emitter — (eventName, payload). The endpoint writes it to the stream.</summary>
    public delegate Task Emit(string eventName, object payload);

    /// <summary>
    /// Single-turn convenience overload (no conversation history / shown set) — a query EXPANDED
    /// rather than a conversation continued, and billed as <c>rerank-expand</c> for that reason.
    /// The two shapes cost differently and are wanted separately on the ledger: one is a person
    /// waiting mid-conversation, the other is a one-shot lookup.
    /// </summary>
    public Task StreamAsync(string message, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(message, [], [], region, [], ApiUsagePurpose.RerankExpand, emit, ct);

    /// <summary>Overload without an explicit media filter — the intent's own inference applies.</summary>
    public Task StreamAsync(
        string message, IReadOnlyList<ChatTurn> history, IReadOnlyList<long> shownTmdbIds, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(message, history, shownTmdbIds, region, [], ApiUsagePurpose.RerankAsk, emit, ct);

    /// <summary>
    /// An Ask Reel conversational turn — the shape <c>/api/search/ask</c> uses.
    ///
    /// <para><paramref name="debug"/> attaches the turn's own diagnostics to the terminal frame. The
    /// ENTITLEMENT decision is not made here: the endpoint resolves it from the tier and the query
    /// string, because that is where the account row and the request live.</para>
    /// </summary>
    public Task StreamAsync(
        string message,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<long> shownTmdbIds,
        string? region,
        IReadOnlyList<MediaType> mediaFilter,
        Emit emit,
        CancellationToken ct,
        bool debug = false) =>
        StreamAsync(message, history, shownTmdbIds, region, mediaFilter, ApiUsagePurpose.RerankAsk, emit, ct, debug);

    /// <summary>
    /// Runs a conversational turn end-to-end, emitting the SSE event stream. Never throws for an
    /// ordinary failure — emits a graceful done with a reason instead.
    ///
    /// <para><paramref name="rerankPurpose"/> decides which member of the re-rank family this
    /// turn's candidate judging bills to. Both members it can take are INTERACTIVE and therefore
    /// get the family's full ceiling — a person mid-conversation being told the model is out of
    /// budget because a 3 a.m. feed build spent it is precisely what the ceiling exists to
    /// prevent.</para>
    /// </summary>
    public async Task StreamAsync(
        string message,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<long> shownTmdbIds,
        string? region,
        IReadOnlyList<MediaType> mediaFilter,
        string rerankPurpose,
        Emit emit,
        CancellationToken ct,
        bool debug = false)
    {
        var accountId = accountContext.AccountId
            ?? throw new InvalidOperationException("Live search requires an account.");
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            await emit("done", new { results = Array.Empty<object>(), reason = "empty query" });
            return;
        }

        // Where this turn's seconds went. Stages accumulate, so discovery running three times at
        // three relaxation levels reports one total rather than three entries, and a stage that
        // never ran is absent rather than zero.
        var timings = new AskStageTimings();

        try
        {
            await emit("phase", new { stage = "searching", found = 0, scored = 0 });

            // No embeddings provider → can't rank semantically. Fall back to the local lexical
            // engine so Ask Reel still answers (keyless installs); stream those, unscored-by-rank.
            //
            // This branch precedes the agent call, so it records `emit` alone: there is nothing to
            // interpret, discover, embed, score, rerank or blend. A stage list is a record of work
            // done, not a checklist to fill in.
            if (!embeddings.IsAvailable)
            {
                using (timings.Stage(AskStageTimings.Emit))
                {
                    await StreamLexicalFallbackAsync(accountId, trimmed, emit, ct);
                }

                logger.LogInformation(
                    "Ask Reel '{Query}': lexical fallback (no embeddings provider). {StageTimings}", trimmed, timings);
                return;
            }

            var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
            // The account's effective taste narrative — its own words where it has edited them —
            // falling back to the compact line for an account not yet written one. Ask Reel used to
            // build that line here by hand; PersonalisationContext is now the single definition, so
            // the feed, the paginator and this surface cannot describe the same person differently.
            //
            // Recently-declined titles ride BOTH agent prompts (interpret + rerank), so
            // conversational picks verbally respect dislikes too. The scored component already
            // respects them numerically — OnDemandScorer subtracts the DislikeSignal demotion, and
            // its output feeds this surface's final blend below.
            var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
            var tasteSummary = persona.Declined is { Length: > 0 } declined
                ? $"{persona.Taste}. {declined}"
                : persona.Taste;

            // ── Interpret the turn → conversational reply + discovery intent ─────────────────
            var shownNames = await ShownTitleNamesAsync(shownTmdbIds, ct);
            AgentReply agentReply;
            using (timings.Stage(AskStageTimings.Interpret))
            {
                agentReply = await agent.InterpretAsync(history, trimmed, tasteSummary, shownNames, ct);
            }

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

            // ── Resolve reference titles ("like Blade Runner but more emotional") ────────────
            // Each named comparison resolves to a catalogue title with an embedding; those vectors
            // are blended into the query vector so the semantic neighbourhood of the reference —
            // not just the abstract concept words — drives what surfaces. The references
            // themselves are excluded from the results (the user just named them; recommending
            // one back is junk), but NO footprint record is written: naming a film as a
            // comparison is not evidence of having watched it.
            var (referenceTitles, referenceVectors) = isPersonQuery
                ? (new List<Title>(), new List<float[]>())
                : await ResolveReferenceTitlesAsync(intent, ct);
            var referenceIds = referenceTitles.Select(t => t.Id).ToHashSet();

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
                using (timings.Stage(AskStageTimings.Discover))
                {
                    var candidateIds = await DiscoverCandidateIdsAsync(pass, region, ct);
                    var semanticIds = isPersonQuery
                        ? new List<Guid>()
                        : await SemanticNeighbourIdsAsync(accountId, agentReply with { Intent = pass }, referenceVectors, ct);
                    discovered = discovered.Concat(candidateIds).Concat(semanticIds).Distinct().ToList();
                }

                await emit("phase", new { stage = "discovered", found = discovered.Count, scored = 0 });

                // OrderBy(t.Id) is not cosmetic: without it Postgres may return these rows in any
                // order, and every downstream sort is stable-on-an-unstable-input, so an identical
                // re-ask could present a different list.
                using var eligibleStage = timings.Stage(AskStageTimings.Eligible);
                eligible = (await baseQuery
                        .Where(t => discovered.Contains(t.Id))
                        .OrderBy(t => t.Id)
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    .Where(t => t.TmdbId == null || !shownSet.Contains(t.TmdbId.Value))
                    // Never recommend a reference back: "like Blade Runner" must not answer with
                    // Blade Runner. Excluded from the pool, not recorded as any reaction.
                    .Where(t => !IsReference(t, referenceIds))
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .Where(t => MatchesYear(t, pass))
                    .ToList();

                // The runtime ceiling ("under 90 minutes") applies to the POOL, not just to TMDB
                // discovery — the semantic arm knows nothing about runtimes. Unknown-runtime
                // titles survive only as a floor top-up: absence of a runtime is not evidence it
                // fits, but an empty screen is worse than a candidate of unknown length.
                if (!isPersonQuery)
                {
                    eligible = ApplyRuntimeCeiling(eligible, intent.MaxRuntimeMinutes, MinResults);
                }

                eligibleStage.Dispose();
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
                using var lastResortStage = timings.Stage(AskStageTimings.Eligible);
                eligible = (await baseQuery
                        .Where(t => t.TmdbPopularity != null)
                        .OrderByDescending(t => t.TmdbPopularity)
                        .Take(MaxCandidates * 2)
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    .Where(t => t.TmdbId == null || !shownSet.Contains(t.TmdbId.Value))
                    .Where(t => !IsReference(t, referenceIds))
                    // Deliberately NOT year- or runtime-filtered: this is the last resort that
                    // exists so the screen is never empty, and every narrower constraint has
                    // already failed to fill it. Media type stays because the user set it
                    // explicitly in the UI; the reference exclusion stays because recommending
                    // the comparison title back is wrong at any level of desperation.
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .ToList();
                relaxation = MaxRelaxations;
            }

            if (eligible.Count == 0)
            {
                await emit("done", new { results = Array.Empty<object>(), reason = AskReelReason.Empty });
                return;
            }

            // Person queries are a filmography, not a vibe match: order chronologically, badge what
            // you've seen, score for the predicted-rating badge — but never re-order or cull by fit.
            if (isPersonQuery)
            {
                await StreamFilmographyAsync(accountId, agentReply.Intent, eligible, taste, now, timings, emit, ct);
                return;
            }

            // ── Embed eligible candidates that lack a vector, then cosine pre-rank ───────────
            await emit("phase", new { stage = "embedding", found = eligible.Count, scored = 0 });
            using var embedStage = timings.Stage(AskStageTimings.Embed);
            await EnsureEmbeddedAsync(eligible, ct);
            var vectorsByTitle = await LoadVectorsAsync(eligible.Select(t => t.Id).ToList(), ct);
            var tagVectors = await LoadPreferenceTagVectorsAsync(accountId, ct);

            // Embed the POSITIVE reading of the turn, not the raw sentence. Embedding "horror but
            // no ghosts" verbatim pulls the query vector TOWARDS ghosts — the negation words are
            // just more tokens about ghosts to an embedding model — so the rejected concept scored
            // highest on the very similarity used to pick the top ReturnCount. Falls back to the
            // raw text only when the intent carries no positive signal at all.
            var seedText = RankingSeedText(agentReply, trimmed);
            var queryVec = await RankingSeedVectorAsync(seedText, referenceVectors, ct);
            embedStage.Dispose();

            // Negation is applied BEFORE the cut, not after it — and it is HARD. Titles that
            // contradict what the user ruled out never appear as ordinary picks: with enough clean
            // candidates they are dropped outright, and only when the clean set cannot fill the
            // MinResults floor do the closest contradicting ones appear — explicitly flagged as
            // outside the request, ordered after every clean pick, captioned with WHICH exclusion
            // they trip. (The old top-up refilled to ReturnCount unflagged, which is how "no
            // horror please" returned seven horror titles with why-lines admitting the violation.)
            // With no usable seed there is no cosine to rank by, so the vector requirement is
            // dropped too: every candidate carries a zero similarity and the personal score plus
            // the LLM fit decide the order. Thinner than a seeded turn, never empty.
            var scoredPool = eligible
                .Where(t => queryVec is null || vectorsByTitle.ContainsKey(t.Id))
                .Select(t => (
                    Title: t,
                    Sim: queryVec is not null && vectorsByTitle.TryGetValue(t.Id, out var vec) ? Cosine(queryVec, vec) : 0d))
                // A stable tiebreak: the base query has no ORDER BY, so equal similarities would
                // otherwise resolve in whatever order Postgres happened to return rows, and an
                // identical re-ask could come back in a different order.
                .OrderByDescending(x => x.Sim)
                .ThenBy(x => x.Title.Id)
                .ToList();

            var (ranked, outside) = PartitionByNegation(scoredPool, intent);
            if (outside.Count > 0)
            {
                logger.LogInformation(
                    "Ask Reel '{Query}': only {Clean} candidates cleared the exclusions; showing {Outside} contradicting ones flagged as outside the request.",
                    trimmed, ranked.Count, outside.Count);
            }

            foreach (var (title, sim) in ranked)
            {
                await emit("candidate", Card(title, sim, predicted: null));
            }

            foreach (var (title, sim) in outside)
            {
                await emit("candidate", Card(title, sim, predicted: null, outsideRequest: true));
            }

            // ── Hydrate + personally score EVERY returned card ───────────────────────────────
            // Score all returned cards (outside ones included — they still carry the badges), not
            // just the head slice — otherwise every card past the first slice renders a default
            // ~6.0 predicted-rating badge instead of a real per-user score. OnDemandScorer
            // batches, so one call covers the whole set.
            var returned = ranked.Concat(outside).ToList();
            await emit("phase", new { stage = "scoring", found = returned.Count, scored = 0 });
            var sliceIds = returned.Select(x => x.Title.Id).ToList();
            IReadOnlyDictionary<Guid, decimal> scores;
            using (timings.Stage(AskStageTimings.Score))
            {
                await HydrateAsync(sliceIds, ct);
                scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);
            }

            foreach (var id in sliceIds)
            {
                await emit("candidate-scored", new { titleId = id, predictedRating = scores.TryGetValue(id, out var p) ? (decimal?)p : null });
            }

            // ── Live per-movie LLM re-rank (the hyper-personal layer) — CLEAN cards only ─────
            // Outside cards never reach the LLM why generation: asked to pitch a title the user
            // ruled out, it cheerfully admitted the violation ("exactly what they want to avoid").
            // Their caption is the deterministic statement of which exclusion they trip, streamed
            // as a candidate-reranked frame so live cards pick it up the same way clean cards pick
            // up their LLM why.
            await emit("phase", new { stage = "ranking", found = returned.Count, scored = sliceIds.Count });

            // When the turn named comparison titles, tell the rerank model so the why-lines can
            // name the concrete connection ("shares Blade Runner's neon melancholy") instead of
            // re-deriving the request from keywords. Folded into the query string, so the per-
            // (account,title,query) fit cache keys stay reference-aware for free.
            var referenceNames = referenceTitles.Count > 0
                ? referenceTitles.Select(t => t.Name).ToList()
                : (intent.ReferenceTitles ?? []).ToList();
            var rerankQuery = referenceNames.Count > 0
                ? $"{trimmed}\nThe user wants something like: {string.Join(", ", referenceNames)}"
                : trimmed;
            Dictionary<Guid, RerankResult> fitById;
            using (timings.Stage(AskStageTimings.Rerank))
            {
                fitById = await RerankStreamAsync(
                    accountId, rerankQuery, persona, ranked, scores, rerankPurpose, emit, ct);
            }

            foreach (var (title, _) in outside)
            {
                await emit("candidate-reranked", new
                {
                    titleId = title.Id,
                    fit = (double?)null,
                    why = OutsideWhy(NegationMatches(title, intent)),
                });
            }

            // ── Final blend: relevance × personal score × hyper-personal fit ─────────────────
            // The old `- negationPenalty` term is gone: the partition supersedes it. A demoting
            // penalty could still interleave contradicting titles among clean ones; the partition
            // keeps them in a separate block that always trails every clean pick.
            List<(Title Title, double Sim, decimal? Predicted, RerankResult? Fit, double Blend)> BlendPool(
                IReadOnlyList<(Title Title, double Sim)> pool) => pool
                .Select(x =>
                {
                    var predicted = scores.TryGetValue(x.Title.Id, out var p) ? (decimal?)p : null;
                    var fit = fitById.TryGetValue(x.Title.Id, out var fr) ? fr : null;
                    var tagBoost = vectorsByTitle.TryGetValue(x.Title.Id, out var vec) ? TagAffinity(vec, tagVectors) : 0d;
                    var blend = (x.Sim * 0.32) + ((double)(predicted ?? 6m) / 10d * 0.23) + ((fit?.Fit ?? 0.5d) * 0.35) + (tagBoost * 0.10);
                    return (x.Title, x.Sim, Predicted: predicted, Fit: fit, Blend: blend);
                })
                .OrderByDescending(x => x.Blend)
                .ThenBy(x => x.Title.Id)
                .ToList();

            using var blendStage = timings.Stage(AskStageTimings.Blend);

            // The account's minimum-predicted-rating filter is an explicit instruction and has to
            // reach THIS surface too. It only ever lived in the (now removed) /semantic endpoint,
            // so a user who set a rating floor had it silently ignored by every Ask Reel turn.
            var floor = await MinPredictedFloorAsync(accountId, ct);

            // A card with no prediction CLEARS the floor, so an unscored card is an unannounced
            // exemption from the user's own bar. Before judging anything, make one more attempt on
            // exactly the cards that came back without a number — the first pass can miss a title
            // whose feature vector was not buildable at the time. Only what is still unscored after
            // this is genuinely unscorable, and only then does the exemption apply.
            if (floor > 0m)
            {
                var unscored = returned.Select(x => x.Title.Id).Where(id => !scores.ContainsKey(id)).ToList();
                if (unscored.Count > 0)
                {
                    var late = await scorer.ScoreAsync(accountId, unscored, now, ct, taste);
                    if (late.Count > 0)
                    {
                        var merged = scores.ToDictionary(kv => kv.Key, kv => kv.Value);
                        foreach (var (id, predicted) in late)
                        {
                            merged[id] = predicted;
                        }

                        scores = merged;
                        logger.LogInformation(
                            "Ask Reel: {Late} of {Unscored} unscored cards were scored on the second pass, before the {Floor} rating floor.",
                            late.Count, unscored.Count, floor);
                    }
                }
            }

            var blended = BlendPool(ranked);
            var blendedOutside = BlendPool(outside);

            // Captured before the floor prunes the channel: a below-floor card still has to render
            // with its outside-request flag, and the flag is a property of WHICH channel it came
            // from, not of whether it survived.
            var blendedOutsideIds = blendedOutside.Select(x => x.Title.Id).ToHashSet();

            // MinFloorRelaxedCount is a THRESHOLD here, not a take: fewer than that many cards
            // clearing the bar relaxes the floor for this turn and truncates nothing, and otherwise
            // the cards that miss it are handed back to be shown in a tray and counted out loud.
            var verdict = AskReelFloor.Apply(
                blended, blendedOutside, x => x.Predicted, floor, MinFloorRelaxedCount);
            var floorRelaxed = verdict.Relaxed;
            blended = [.. verdict.Kept];
            blendedOutside = [.. verdict.KeptOutside];

            // Outside cards ALWAYS trail every clean pick, each block ordered by its own blend,
            // and each outside card carries the flag plus its honest deterministic caption.
            var final = blended
                .Select(x => Card(x.Title, x.Sim, x.Predicted, x.Fit?.Fit, x.Fit?.Why, blend: x.Blend))
                .Concat(blendedOutside.Select(x => Card(
                    x.Title, x.Sim, x.Predicted, fit: null,
                    why: OutsideWhy(NegationMatches(x.Title, intent)),
                    outsideRequest: true, blend: x.Blend)))
                .ToList();

            // The tray. A card the floor removed is still a card the pipeline chose, and the user is
            // owed the chance to look at it — a silent drop and a bug are indistinguishable from the
            // outside. Rendered under a fence, never mixed into the picks.
            var belowFloor = verdict.BelowFloor
                .Select(x => Card(
                    x.Title, x.Sim, x.Predicted, x.Fit?.Fit, x.Fit?.Why,
                    outsideRequest: blendedOutsideIds.Contains(x.Title.Id), blend: x.Blend))
                .ToList();
            blendStage.Dispose();

            using var emitStage = timings.Stage(AskStageTimings.Emit);

            // Report what was RETURNED, not what was pulled. The old wording was derived purely
            // from the pre-ranking counts, so an empty screen still read "50 pulled; 8 unseen" —
            // the numbers described a result set the user could not see. When contradicting titles
            // were shown, say so — the split is the honesty the old top-up lacked.
            await emit("done", new
            {
                results = final,
                // The turn's own accounting, so no card can vanish unexplained: everything streamed
                // is either kept or in the tray, and streamed == kept + droppedByFloor always holds.
                streamed = ranked.Count + outside.Count,
                kept = final.Count,
                droppedByFloor = belowFloor.Count,
                belowFloor,
                reason = AskReelReason.Build(
                    blended.Count, blendedOutside.Count, belowFloor.Count, discovered.Count,
                    widened: relaxation > 0, floorRelaxed, floor),
                // Founder-only, and only when asked for: the per-stage millisecond breakdown of this
                // turn. Always PRESENT as a key so the client has one shape to render, null when the
                // caller is not entitled to it. `emit` is absent here and only here — a payload
                // cannot carry the duration of its own transmission, and a zero would be a claim
                // rather than a rounding. The end-of-turn log line carries all eight.
                timingsMs = debug ? timings.Snapshot() : null,
            });
            // The turn's answer, recorded. Ranked by the SAME blend the user is looking at, clean
            // picks first and outside ones trailing, so Rank is the position on screen. Written
            // after `done` is emitted: the stream is what the user came for, and a logging failure
            // must not sit between them and it.
            //
            // A card whose OnDemandScorer had nothing to say carries no prediction, and a card with
            // no prediction is not an exposure worth scoring the model against — those are skipped
            // rather than logged as a fabricated 6.0.
            var artifactId = await db.ModelArtifacts
                .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
                .OrderByDescending(a => a.Version)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(ct);
            if (artifactId is { } activeArtifactId)
            {
                var loggable = blended
                    .Concat(blendedOutside)
                    .Select((x, index) => (x, index))
                    .Where(t => t.x.Predicted is not null)
                    .Select(t => new ExposureCard(
                        t.x.Title.Id,
                        t.index,
                        t.x.Predicted!.Value,
                        LlmFit: t.x.Fit is { } f ? (decimal)f.Fit : null,
                        WhySentence: t.x.Fit?.Why,
                        WhySource: t.x.Fit?.Why is { Length: > 0 } ? ExposureWhySource.Llm : ExposureWhySource.Templater))
                    .ToList();

                await exposures.RecordAsync(
                    accountId, ExposureSurface.AskReel, feedSnapshotId: null, activeArtifactId,
                    DateTime.UtcNow, loggable, ct);
            }

            // Logs `final` too: the count the user actually sees is the one worth alerting on, and
            // its absence is what let an empty result set look healthy in the logs.
            emitStage.Dispose();

            logger.LogInformation(
                "Ask Reel '{Query}': {Discovered} discovered at relaxation {Relaxation}, {Eligible} eligible, "
                + "{Scored} scored, {Reranked} reranked, {Returned} returned. {StageTimings}",
                trimmed, discovered.Count, relaxation, eligible.Count, sliceIds.Count, fitById.Count, final.Count, timings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ask Reel expansion failed for '{Query}'.", trimmed);
            // `failed` is what lets the client tell a crash from an honest empty answer. Without it
            // the only signal was the wording of `reason`, so the UI string-matched "snag" — and a
            // genuine no-match and a server exception rendered two contradictory messages at once.
            await emit("done", new { results = Array.Empty<object>(), reason = "search hit a snag — please try again", failed = true });
        }
    }

    /// <summary>
    /// Re-ranks every shown card with the agent (batched, cached per (account, title, query)) and
    /// streams <c>candidate-reranked</c> per batch, so the grid sharpens as the answers land rather
    /// than all at once at the end.
    ///
    /// <para>Time-boxed only by the caller's own request: unlike the feed build and the paginator,
    /// this surface is a stream, and a partial answer is already what it delivers — every batch
    /// that completes is emitted before the next begins. The batching, failure handling and
    /// per-batch caching are the shared <see cref="LlmBudgetLoop"/> all the same, so there is one
    /// definition of "a batch that was cancelled part-way caches nothing" rather than three.</para>
    /// </summary>
    private async Task<Dictionary<Guid, RerankResult>> RerankStreamAsync(
        Guid accountId, string query, LensPersona persona,
        IReadOnlyList<(Title Title, double Sim)> ranked,
        IReadOnlyDictionary<Guid, decimal> scores, string rerankPurpose, Emit emit, CancellationToken ct)
    {
        var qnorm = NormalizeQuery(query);
        var fitById = new Dictionary<Guid, RerankResult>();

        // The evidence the feed build has always given the judge and this surface never did: tone,
        // era, themes, the seven attribute scalars and the people. Two indexed reads for the whole
        // turn, taken ONCE before the batching loop rather than per batch. Contributions are null
        // because OnDemandScorer computes none — an empty list is the honest answer, and a title the
        // catalogue has not enriched carries nulls rather than zeros.
        var contexts = await RerankContextBuilder.BuildAsync(
            db, ranked.Select(r => r.Title.Id).ToList(), topContributions: null, ct);

        // A title can be judged more than once across a long conversation; averaging the verdicts
        // is the caller's half of the LLM position-bias mitigation (see PositionDebiasedFit).
        var debiased = new PositionDebiasedFit();

        using var loop = new LlmBudgetLoop(LlmBudgetOptions.Unbounded(RerankBatch), ct);
        await loop.RunAsync(ranked, async (batch, batchCt) =>
        {
            var uncached = new List<RerankInput>();
            foreach (var (title, _) in batch)
            {
                if (cache.TryGetValue<RerankResult>(FitKey(accountId, title.Id, qnorm, agent.ModelId), out var cached) && cached is not null)
                {
                    fitById[title.Id] = cached;
                }
                else
                {
                    uncached.Add(new RerankInput(
                        title.Id, title.Name, title.Year, title.MediaType.ToString(), title.Genres, title.Overview,
                        scores.TryGetValue(title.Id, out var p) ? p : null,
                        contexts.GetValueOrDefault(title.Id)));
                }
            }

            if (uncached.Count > 0)
            {
                // No anchor title here: Ask Reel's "anchor" is the user's request itself, which
                // already rides the query — behaviour deliberately unchanged.
                var results = await agent.RerankBatchAsync(
                    uncached, query, persona, anchorName: null, rerankPurpose, batchCt);
                foreach (var raw in results)
                {
                    debiased.Observe(raw);
                    var res = debiased.For(raw.TitleId)!;
                    fitById[res.TitleId] = res;
                    cache.Set(FitKey(accountId, res.TitleId, qnorm, agent.ModelId), res, FitTtl);
                }
            }

            foreach (var (title, _) in batch)
            {
                if (fitById.TryGetValue(title.Id, out var fit))
                {
                    await emit("candidate-reranked", new { titleId = title.Id, fit = fit.Fit, why = fit.Why });
                }
            }
        });

        return fitById;
    }

    /// <summary>
    /// The TMDB discovery arm. Returns empty on any failure ⇒ the semantic arm carries the turn
    /// alone — the mirror of what <see cref="SemanticNeighbourIdsAsync"/> already does when the
    /// concept embedding fails, and for the same reason: a blip on ONE of two arms must not
    /// discard the other. Before this guard a TMDB timeout, a 429 or a keyword-search failure
    /// propagated to the turn's catch-all and returned "search hit a snag" with zero results,
    /// even though every semantically-nearest unseen title was already in hand and needed no
    /// network at all.
    /// </summary>
    private async Task<List<Guid>> DiscoverCandidateIdsAsync(SearchIntent intent, string? region, CancellationToken ct)
    {
        var key = IntentKey(intent, region);
        if (cache.TryGetValue<List<Guid>>(CacheKey(key, agent.ModelId), out var cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        try
        {
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
            cache.Set(CacheKey(key, agent.ModelId), ids, DiscoveryTtl);
            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel TMDB arm: discovery failed; falling back to semantic-only discovery.");
            return [];
        }
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
    private async Task<List<Guid>> SemanticNeighbourIdsAsync(
        Guid accountId, AgentReply agentReply, IReadOnlyList<float[]> referenceVectors, CancellationToken ct)
    {
        var concept = ConceptText(agentReply);
        if (concept.Length == 0 && referenceVectors.Count == 0)
        {
            return [];
        }

        float[] queryVec;
        try
        {
            if (concept.Length > 0)
            {
                var embedded = await embeddings.EmbedAsync([concept], ct);
                if (embedded.Length == 0 || embedded[0].Length == 0)
                {
                    return [];
                }

                queryVec = embedded[0];
            }
            else
            {
                // No concept text at all but the user named references — anchor purely on them.
                queryVec = new float[referenceVectors[0].Length];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel semantic arm: concept embedding failed; falling back to TMDB-only discovery.");
            return [];
        }

        // Blend the reference-title embeddings in, so the kNN pull happens in the reference's
        // neighbourhood — this is what makes "like Blade Runner" DISCOVER Blade Runner's semantic
        // neighbours instead of merely re-ranking whatever the keywords happened to fetch.
        queryVec = BlendQueryVector(queryVec, referenceVectors);

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
    /// Resolves the intent's reference titles ("like Blade Runner") to catalogue rows with
    /// embeddings. Local exact-name match first (cheap, and the reference is usually famous enough
    /// to be in the catalogue already); TMDB title search + the standard upsert path otherwise.
    /// Every step degrades to skipping the reference — anchoring is an enrichment, and a turn must
    /// never fail because a comparison could not be resolved.
    /// </summary>
    private async Task<(List<Title> Titles, List<float[]> Vectors)> ResolveReferenceTitlesAsync(
        SearchIntent intent, CancellationToken ct)
    {
        var names = intent.ReferenceTitles ?? [];
        if (names.Count == 0)
        {
            return ([], []);
        }

        var resolved = new List<Title>();
        foreach (var name in names.Take(3))
        {
            try
            {
                var title = await FindReferenceLocallyAsync(name, ct) ?? await ResolveReferenceViaTmdbAsync(name, ct);
                if (title is not null && resolved.All(t => t.Id != title.Id))
                {
                    resolved.Add(title);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ask Reel: resolving reference title '{Name}' failed; anchoring without it.", name);
            }
        }

        if (resolved.Count == 0)
        {
            return ([], []);
        }

        await EnsureEmbeddedAsync(resolved, ct);
        var vectors = await LoadVectorsAsync(resolved.Select(t => t.Id).ToList(), ct);
        return (resolved, resolved.Where(t => vectors.ContainsKey(t.Id)).Select(t => vectors[t.Id]).ToList());
    }

    /// <summary>
    /// Exact-name catalogue lookup (case-insensitive, most popular first) — the typeahead-style
    /// resolution for a title the user names verbatim. Deliberately NOT fuzzy: a wrong anchor is
    /// worse than a missing one, and the TMDB fallback handles near-miss spellings.
    /// </summary>
    private async Task<Title?> FindReferenceLocallyAsync(string name, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return await db.Titles
            .Where(t => t.Name.ToLower() == lower)
            .OrderByDescending(t => t.TmdbPopularity)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// TMDB best-hit resolution for a reference the catalogue doesn't hold, through the SAME
    /// upsert path discovery uses so dedup and field semantics hold. Searches both media types:
    /// the reference is an anchor, not a result, so a UI "movies only" filter must not stop
    /// "movies like Severance" from anchoring on the show.
    /// </summary>
    private async Task<Title?> ResolveReferenceViaTmdbAsync(string name, CancellationToken ct)
    {
        var hits = (await tmdb.SearchTitlesAsync(true, name, 1, ct))
            .Concat(await tmdb.SearchTitlesAsync(false, name, 1, ct))
            .OrderByDescending(i => i.Popularity)
            .ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        var best = hits[0];
        var ids = await UpsertAndCollectIdsAsync([best], ct);
        return ids.Count == 0
            ? null
            : await db.Titles.FirstOrDefaultAsync(t => t.Id == ids[0], ct);
    }

    /// <summary>
    /// The reference-anchored ranking seed, or <c>null</c> when the provider gave nothing usable to
    /// anchor on.
    ///
    /// <para>The seed used to be a bare <c>[0]</c> index into the provider's answer, so an empty or
    /// truncated response threw and the turn reported a crash. A missing seed costs the turn its
    /// cosine ordering — real, and worth degrading for — but the pool has already been discovered
    /// and is still worth showing. Where the turn named comparison titles, their mean carries the
    /// seed on its own, exactly as the semantic arm already does.</para>
    /// </summary>
    private async Task<float[]?> RankingSeedVectorAsync(
        string seedText, IReadOnlyList<float[]> referenceVectors, CancellationToken ct)
    {
        var embedded = await embeddings.EmbedAsync([seedText], ct);
        if (embedded is [{ Length: > 0 }, ..])
        {
            return BlendQueryVector(embedded[0], referenceVectors);
        }

        logger.LogWarning(
            "Ask Reel: the ranking seed came back empty; ranking this turn without a similarity signal.");
        return referenceVectors.Count > 0
            ? BlendQueryVector(new float[referenceVectors[0].Length], referenceVectors)
            : null;
    }

    /// <summary>
    /// The reference-anchored query vector: <c>normalize(concept + 0.5 × mean(references))</c>.
    /// The concept keeps the majority weight — "like Blade Runner but MORE EMOTIONAL" must still
    /// honour the modifier — while the reference mean drags the query into the named titles'
    /// actual neighbourhood. Pure and internal so the math is pinned by unit tests. Returns the
    /// concept unchanged when no usable reference vector matches its dimensionality.
    /// </summary>
    internal static float[] BlendQueryVector(float[] concept, IReadOnlyList<float[]> referenceVectors)
    {
        var usable = referenceVectors.Where(v => v.Length == concept.Length).ToList();
        if (usable.Count == 0)
        {
            return concept;
        }

        var blended = new double[concept.Length];
        for (var i = 0; i < concept.Length; i++)
        {
            double mean = 0;
            foreach (var reference in usable)
            {
                mean += reference[i];
            }

            blended[i] = concept[i] + (0.5d * (mean / usable.Count));
        }

        var norm = Math.Sqrt(blended.Sum(x => x * x));
        return norm <= 0
            ? concept
            : blended.Select(x => (float)(x / norm)).ToArray();
    }

    /// <summary>
    /// True when the title IS one of the turn's resolved reference anchors. A reference is a
    /// place to search NEAR, never an answer: "like Blade Runner" returning Blade Runner is
    /// junk, and the exclusion is a pool filter — never a footprint record, because naming a
    /// film as a comparison is not evidence of having watched it.
    /// </summary>
    internal static bool IsReference(Title title, IReadOnlySet<Guid> referenceIds) =>
        referenceIds.Contains(title.Id);

    /// <summary>
    /// The runtime post-filter for "under 90 minutes": titles with a KNOWN runtime over the
    /// ceiling are always dropped; unknown-runtime titles are kept only as a top-up to
    /// <paramref name="floor"/> when the known-and-fitting set alone cannot fill it — absence of
    /// a runtime is not evidence of fit, but an empty screen is the worse failure. No ceiling ⇒
    /// the pool passes through untouched. Internal so the tolerance rule is pinned by unit tests.
    /// </summary>
    internal static List<Title> ApplyRuntimeCeiling(List<Title> pool, int? maxRuntimeMinutes, int floor)
    {
        if (maxRuntimeMinutes is not { } ceiling)
        {
            return pool;
        }

        var fits = pool.Where(t => t.RuntimeMinutes is { } m && m <= ceiling).ToList();
        if (fits.Count >= floor)
        {
            return fits;
        }

        var unknown = pool.Where(t => t.RuntimeMinutes is null).Take(floor - fits.Count);
        return fits.Concat(unknown).ToList();
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
        // With one keyword or none there is nothing to halve, so this rung would re-issue the
        // identical query and burn a TMDB round-trip widening nothing; drop the upper year bound
        // instead, which is the next-narrowest constraint after keywords.
        1 => intent.Keywords.Count > 1
            ? intent with { Keywords = intent.Keywords.Take(intent.Keywords.Count / 2).ToList() }
            : intent with { MaxYear = null },
        // Genre-only: the broadest pull that still honours the vibe.
        _ => intent with { Keywords = [], FreeText = string.Empty, MinYear = null, MaxYear = null },
    };

    /// <summary>
    /// The account's minimum-predicted-rating content filter, clamped to 0-10. Zero means "no
    /// floor set", which is also what a malformed stored value degrades to.
    /// </summary>
    private async Task<decimal> MinPredictedFloorAsync(Guid accountId, CancellationToken ct)
    {
        var raw = await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        return raw is not null && decimal.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 10m)
            : 0m;
    }

    /// <summary>
    /// The first instant of a release-year window, or null when unbounded — the single definition
    /// the concept fetch and the person-credit fetch both use. See <see cref="MinCalendarYear"/>
    /// for why the year is clamped rather than trusted.
    /// </summary>
    private static DateTime? YearStart(int? year) =>
        year is { } y ? new DateTime(ClampYear(y), 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;

    /// <summary>
    /// The last instant of a release-year window, or null when unbounded. End-of-day, because
    /// <see cref="SearchIntent.MaxYear"/> is documented as inclusive of the whole year — the two
    /// call sites used to disagree about that by 24 hours.
    /// </summary>
    private static DateTime? YearEnd(int? year) =>
        year is { } y ? new DateTime(ClampYear(y), 12, 31, 23, 59, 59, DateTimeKind.Utc) : null;

    /// <inheritdoc cref="MinCalendarYear"/>
    private static int ClampYear(int year) => Math.Clamp(year, MinCalendarYear, MaxCalendarYear);

    /// <summary>True when the title matches the requested media types (empty list = no constraint).</summary>
    private static bool MatchesMedia(Title title, IReadOnlyList<MediaType> mediaTypes) =>
        mediaTypes.Count == 0 || mediaTypes.Contains(title.MediaType);

    /// <summary>
    /// True when the title falls inside the requested release-year window (null bounds = no
    /// constraint). Applied to the ELIGIBLE pool, not just to TMDB discovery, because discovery is
    /// only one of two arms: the semantic kNN arm ranks the account's own embedded catalogue by
    /// cosine and knows nothing about years, so "80s sci-fi" still surfaced 2026 titles underneath
    /// five genuinely-80s ones. A title with no known date is kept rather than dropped — absence of
    /// a date is not evidence of the wrong date, and the relaxation ladder drops these bounds
    /// anyway if the window turns out to be too narrow to fill a screen.
    /// </summary>
    private static bool MatchesYear(Title title, SearchIntent intent)
    {
        if (intent.MinYear is null && intent.MaxYear is null)
        {
            return true;
        }

        var year = title.Year ?? (title.ReleasedAt ?? title.FirstAiredAt)?.Year;
        if (year is null)
        {
            return true;
        }

        return (intent.MinYear is not { } lo || year >= lo)
            && (intent.MaxYear is not { } hi || year <= hi);
    }

    /// <summary>
    /// How much a title contradicts what the user ruled out. Each matched negated genre or keyword
    /// costs one <see cref="NegationPenalty"/>. Any non-zero value routes the title into the
    /// flagged outside-request partition (<see cref="PartitionByNegation"/>) — it never reaches
    /// the screen as an ordinary pick.
    /// </summary>
    private static double NegationDemerit(Title title, SearchIntent intent)
    {
        var hits = 0;
        if (intent.ExcludeGenres is { Count: > 0 } badGenres)
        {
            hits += badGenres.Count(bad => TripsExcludedGenre(title, bad));
        }

        if (intent.ExcludeKeywords is { Count: > 0 } badWords)
        {
            var haystack = KeywordHaystack(title);
            hits += badWords.Count(w => w.Length > 2 && haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        return Math.Min(hits * NegationPenalty, 0.66);
    }

    /// <summary>
    /// Whether a title trips one excluded genre term. TMDB's TV taxonomy has no "Horror" genre at
    /// all (American Horror Story arrives as drama/mystery/sci-fi), so an exclusion matched only
    /// against <see cref="Title.Genres"/> is blind to TV horror — the exact gap the 2026-08-10
    /// live replay caught. Subgenres and Keywords carry the truth ("anthology horror",
    /// "supernatural horror", "horror"), so the excluded term also matches those by containment.
    /// </summary>
    private static bool TripsExcludedGenre(Title title, string excluded) =>
        (title.Genres is { Length: > 0 } && title.Genres.Any(g => string.Equals(g, excluded, StringComparison.OrdinalIgnoreCase)))
        || (title.Subgenres is { Length: > 0 } && title.Subgenres.Any(s => s.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
        || (title.Keywords is { Length: > 0 } && title.Keywords.Any(k => k.Contains(excluded, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Everything a negated keyword can legitimately match: display text plus the
    /// TMDB keyword/subgenre labels, which name themes the overview often talks around.</summary>
    private static string KeywordHaystack(Title title) =>
        $"{title.Name} {title.Overview} {string.Join(' ', title.Keywords ?? [])} {string.Join(' ', title.Subgenres ?? [])}";

    /// <summary>
    /// Splits the similarity-ordered pool into the picks that honour every negation and the
    /// flagged "outside your request" reserve. Clean titles fill up to <see cref="ReturnCount"/>;
    /// contradicting ones are DROPPED whenever the clean set reaches <see cref="MinResults"/>, and
    /// otherwise top up only to that floor — flagged, never silently mixed in. This is the hard
    /// guarantee behind "no horror please": a contradicting title can reach the screen only as an
    /// explicitly-labelled closest-but-outside card.
    /// </summary>
    internal static (List<(Title Title, double Sim)> Ranked, List<(Title Title, double Sim)> Outside) PartitionByNegation(
        IReadOnlyList<(Title Title, double Sim)> scoredPool, SearchIntent intent)
    {
        var contradicts = scoredPool.Where(x => NegationDemerit(x.Title, intent) > 0).ToList();
        var clean = scoredPool.Where(x => NegationDemerit(x.Title, intent) == 0).ToList();
        var ranked = clean.Take(ReturnCount).ToList();
        var outside = ranked.Count < MinResults && contradicts.Count > 0
            ? contradicts.Take(MinResults - ranked.Count).ToList()
            : [];
        return (ranked, outside);
    }

    /// <summary>
    /// The excluded genres/keywords a title actually trips, phrased for the outside-request
    /// caption ("a horror title", "one that matches ‘ghosts’"). Mirrors
    /// <see cref="NegationDemerit"/> exactly, so a title partitioned as contradicting always has
    /// at least one match to name.
    /// </summary>
    internal static IReadOnlyList<string> NegationMatches(Title title, SearchIntent intent)
    {
        var matches = new List<string>();
        if (intent.ExcludeGenres is { Count: > 0 } badGenres)
        {
            matches.AddRange(badGenres
                .Where(bad => TripsExcludedGenre(title, bad))
                .Select(bad => $"a {bad.ToLowerInvariant()} title"));
        }

        if (intent.ExcludeKeywords is { Count: > 0 } badWords)
        {
            var haystack = KeywordHaystack(title);
            matches.AddRange(badWords
                .Where(w => w.Length > 2 && haystack.Contains(w, StringComparison.OrdinalIgnoreCase))
                .Select(w => $"one that matches ‘{w}’"));
        }

        return matches;
    }

    /// <summary>
    /// The honest deterministic caption for an outside-request card. Replaces the LLM why — which
    /// cheerfully admitted violations ("exactly what they want to avoid") — with a plain statement
    /// of the first exclusion the title trips.
    /// </summary>
    internal static string OutsideWhy(IReadOnlyList<string> matches) =>
        $"Closest match we could find, but it's {(matches.Count > 0 ? matches[0] : "outside what you asked for")} — which you asked to skip.";

    /// <summary>
    /// The text the ranking query vector is built from — the interpreted POSITIVE signal only
    /// (free text, keywords, genres), never the raw sentence and never the agent's prose reply.
    /// An embedding model has no notion of "not": "a thriller, no ghosts" and "a thriller with
    /// ghosts" land close together in vector space, so ranking against the raw turn scores the
    /// rejected concept HIGHEST on the similarity used to pick what to show. Falls back to the raw
    /// text only when the intent carries no positive signal at all, because a bad seed still beats
    /// an empty one.
    /// </summary>
    private static string RankingSeedText(AgentReply agentReply, string rawQuery)
    {
        var intent = agentReply.Intent;

        // Keywords and genres are the only fields guaranteed to hold the positive reading: the
        // interpreter strips rejected terms out of them by contract. FreeText is deliberately NOT
        // used when they are available — it is spec'd as "a concise literal-title search string,
        // usually echoing the request", and the heuristic path sets it to the raw query verbatim,
        // so folding it in here would smuggle "no ghosts" straight back into the vector.
        var parts = new List<string>();
        if (intent.Keywords.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Keywords));
        }

        if (intent.Genres.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Genres));
        }

        if (parts.Count > 0)
        {
            return string.Join(". ", parts);
        }

        // Nothing structured survived. A turn that is purely a rejection ("no ghosts") has no
        // positive signal to rank by at all, so fall back to free text / the raw turn — the
        // exclusion still applies through discovery and the pre-cut partition.
        return !string.IsNullOrWhiteSpace(intent.FreeText) ? intent.FreeText.Trim() : rawQuery;
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
        FeatureVectorBuilder.TasteState taste, DateTime now, AskStageTimings timings, Emit emit, CancellationToken ct)
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
        IReadOnlyDictionary<Guid, decimal> scores;
        using (timings.Stage(AskStageTimings.Score))
        {
            await HydrateAsync(sliceIds, ct);
            scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);
        }

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
        using (timings.Stage(AskStageTimings.Emit))
        {
            await emit("done", new { results, reason });
        }

        logger.LogInformation(
            "Ask Reel filmography: {Count} titles for {Person}. {StageTimings}",
            ordered.Count, who?.Name ?? "?", timings);
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

        // Rejected genres/keywords are resolved the same way the positive ones are and handed to
        // TMDB as without_genres / without_keywords. This is the half of negation that was missing:
        // exclusions used to exist only as a ranking demerit applied AFTER the pool had been cut to
        // the top ReturnCount by cosine similarity, so "horror but no ghosts" filled the pool with
        // ghost stories (they are the closest match to "horror") and the demerit could only shuffle
        // them, never replace them with something the user would accept.
        var excludeMovieGenreIds = (intent.ExcludeGenres ?? []).Where(TmdbGenres.SlugToIds.ContainsKey)
            .Select(g => TmdbGenres.SlugToIds[g].Movie).OfType<int>().ToList();
        var excludeTvGenreIds = (intent.ExcludeGenres ?? []).Where(TmdbGenres.SlugToIds.ContainsKey)
            .Select(g => TmdbGenres.SlugToIds[g].Tv).OfType<int>().ToList();

        var keywordIds = await ResolveKeywordIdsAsync(intent.Keywords, ct);
        var excludeKeywordIds = await ResolveKeywordIdsAsync(intent.ExcludeKeywords ?? [], ct);

        var wantMovies = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Movie);
        var wantTv = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Show);
        var after = YearStart(intent.MinYear);
        var before = YearEnd(intent.MaxYear);

        var tasks = new List<Task<IReadOnlyList<TmdbListItem>>>();
        if (wantMovies)
        {
            tasks.Add(tmdb.DiscoverByConceptAsync(true, movieGenreIds, keywordIds, region, after, SearchPages, excludeMovieGenreIds, excludeKeywordIds, before, intent.MaxRuntimeMinutes, ct: ct));
            tasks.Add(tmdb.SearchTitlesAsync(true, intent.FreeText, SearchPages, ct));
        }

        if (wantTv)
        {
            tasks.Add(tmdb.DiscoverByConceptAsync(false, tvGenreIds, keywordIds, null, after, SearchPages, excludeTvGenreIds, excludeKeywordIds, before, intent.MaxRuntimeMinutes, ct: ct));
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

    /// <summary>Resolves up to four free-text phrases to TMDB keyword ids, dropping anything unmatched.</summary>
    private async Task<List<int>> ResolveKeywordIdsAsync(IReadOnlyList<string> phrases, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var phrase in phrases.Take(4))
        {
            var matches = await tmdb.SearchKeywordsAsync(phrase, ct);
            if (matches.Count > 0)
            {
                ids.Add(matches[0].Id);
            }
        }

        return ids;
    }

    /// <summary>Resolve each named person → their TMDB credits (role-sliced), unioned + deduped, newest first.</summary>
    private async Task<IReadOnlyList<TmdbListItem>> FetchPeopleCreditsAsync(IReadOnlyList<PersonQuery> people, SearchIntent intent, CancellationToken ct)
    {
        var wantMovies = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Movie);
        var wantTv = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Show);
        var after = YearStart(intent.MinYear);
        var before = YearEnd(intent.MaxYear);

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
            .Where(i => before is null || i.ReleasedAt is null || i.ReleasedAt <= before)
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

        // Embedding newly-discovered titles is an ENRICHMENT step, not the answer. It used to be
        // unguarded, so a single transient provider failure propagated to the turn's catch-all and
        // returned "search hit a snag" with zero results — discarding everything already
        // discovered, hydrated and scored. Degrade to whatever is already embedded instead: those
        // titles still rank, the un-embedded ones simply sit this turn out and get picked up next
        // time. Worst case is a thinner result set, never an empty one.
        float[][] vectors;
        try
        {
            vectors = await embeddings.EmbedAsync(texts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: embedding {Count} new titles failed; ranking on the existing vectors only.", missing.Count);
            return;
        }

        // The provider's answer is assumed row-aligned to the texts, and it is not contractually
        // guaranteed to be: a truncated or partially rate-limited response comes back SHORT, and
        // this loop indexed straight off the end of it — an IndexOutOfRangeException that the
        // turn's catch-all then reported as "search hit a snag" with zero results, having already
        // discovered and hydrated a full pool. Store what arrived; the rest sit this turn out.
        if (vectors.Length != missing.Count)
        {
            logger.LogWarning(
                "Ask Reel: the embedding provider answered with {Returned} vectors for {Requested} titles; "
                + "storing the ones that arrived and ranking on those.",
                vectors.Length, missing.Count);
        }

        var usable = Math.Min(missing.Count, vectors.Length);
        for (var i = 0; i < usable; i++)
        {
            if (vectors[i] is not { Length: > 0 })
            {
                continue;
            }

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

    /// <summary>
    /// One card on the wire.
    ///
    /// <para><paramref name="similarity"/> and <paramref name="fit"/> are rounded to 2 dp because
    /// they are RENDERED, and a cosine printed to seventeen significant figures reads as precision
    /// the number does not have. <paramref name="blend"/> is the server's own ranking value, carried
    /// so the client can show why one card sits above another — but the ARRAY ORDER is authoritative
    /// and a client must never re-sort by it: blend is rounded for display and the server's ordering
    /// has a stable tiebreak this number cannot reproduce.</para>
    /// </summary>
    internal static object Card(
        Title t, double? similarity, decimal? predicted, double? fit = null, string? why = null,
        bool seen = false, bool outsideRequest = false, double? blend = null) => new
        {
            titleId = t.Id,
            mediaType = t.MediaType.ToString(),
            tmdbId = t.TmdbId,
            name = t.Name,
            year = t.Year,
            posterPath = t.PosterPath,
            genres = t.Genres,
            similarity = similarity is { } s ? Math.Round(s, 2) : (double?)null,
            predictedRating = predicted,
            // The community number the personal prediction is read against — same resolver as the feed
            // cards, so a title's badge cannot say 7.4 here and 6.9 on the home page.
            crowdRating = CrowdRating.For(t),
            crowdVotes = CrowdRating.VotesFor(t),
            fit = fit is { } f ? Math.Round(f, 2) : (double?)null,
            why,
            seen,
            // The server's ranking value for this card. Display only — the array order is the
            // contract, and it carries a tiebreak this rounded number cannot express.
            blend = blend is { } b ? Math.Round(b, 4) : (double?)null,
            // True only for the closest-but-outside reserve — a card that trips one of the user's
            // exclusions and is shown, flagged, solely because the clean picks could not fill the
            // floor. Additive: old clients simply ignore it.
            outsideRequest,
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

    /// <summary>
    /// The identity of a TMDB discovery pull: every field that changes the query TMDB is asked, and
    /// nothing else.
    ///
    /// <para><b>Every argument of the discover call has to appear here</b>, positive and negative
    /// alike. The exclusions do not merely re-order a pull — they ride as <c>without_genres</c> /
    /// <c>without_keywords</c> and change which rows come back, so a key that omitted them served
    /// "a tense thriller, no horror" the cached, unexcluded answer to "a tense thriller". Region is
    /// here for a second reason on top of that: the single-flight key is derived from this string,
    /// so two accounts in different regions asking the same thing at the same moment would share one
    /// fetch and one of them would get the other's catalogue.</para>
    ///
    /// <para><see cref="ReferenceTitles"/> is deliberately absent, and it is the only field that is:
    /// references are resolved separately and blended into the RANKING vector, never handed to
    /// discovery, so two turns differing only in what they name as a comparison genuinely do share a
    /// pull. <c>AskReelCacheKeyTests</c> holds that reasoning to account by reflection.</para>
    /// </summary>
    internal static string IntentKey(SearchIntent intent, string? region) => string.Create(
        CultureInfo.InvariantCulture,
        $"{string.Join(',', intent.Genres.OrderBy(g => g, StringComparer.Ordinal))}|" +
        $"{string.Join(',', intent.Keywords.OrderBy(k => k, StringComparer.Ordinal))}|" +
        $"{string.Join(',', intent.MediaTypes.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal))}|" +
        $"{intent.MinYear}|{intent.MaxYear}|{intent.MaxRuntimeMinutes}|{NormalizeQuery(intent.FreeText)}|" +
        $"{string.Join(',', (intent.People ?? []).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name}:{p.Role}")).OrderBy(p => p, StringComparer.Ordinal))}|" +
        $"-{string.Join(',', (intent.ExcludeGenres ?? []).OrderBy(g => g, StringComparer.Ordinal))}|" +
        $"-{string.Join(',', (intent.ExcludeKeywords ?? []).OrderBy(k => k, StringComparer.Ordinal))}|" +
        $"@{region}");

    /// <summary>
    /// The discovered-id cache key. Carries the prompt generation and the model id because the ids
    /// cached under it are the ids a PARTICULAR interpretation of the turn produced — edit the
    /// prompt or change the model and the same sentence resolves to a different intent, which is
    /// precisely the case the author will re-test with and the one a version-less key would answer
    /// from cache for the next six hours. Same lever as <c>TitleLensCopy</c>'s prompt version.
    /// </summary>
    internal static string CacheKey(string intentKey, string modelId) => string.Create(
        CultureInfo.InvariantCulture, $"askreel:ids:{AskReelPromptVersion.Current}:{modelId}:{intentKey}");

    /// <summary>
    /// The per-(account, title, query) re-rank verdict cache key, versioned for the same reason
    /// <see cref="CacheKey"/> is: a fit and a why-line are the output of one prompt against one
    /// model, and serving a six-hour-old verdict from the previous prompt is how a prompt fix looks
    /// like it did nothing.
    /// </summary>
    internal static string FitKey(Guid accountId, Guid titleId, string qnorm, string modelId) => string.Create(
        CultureInfo.InvariantCulture, $"rerank:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{titleId}:{qnorm}");
}
