using FrostAura.Reel.Application.Abstractions;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// Live "Ask Reel": pull titles matching a natural-language turn straight from TMDB on demand,
/// score them for the user, then re-rank every one with the conversational agent — streamed as
/// it resolves, never bounded by what's already in the catalogue. The flow, emitted as SSE events:
/// assistant-message (the agent's reply) → phase → candidate → candidate-scored → candidate-reranked
/// → done. Catalog rows are global, so every search warms the catalogue for all tenants; the
/// per-query TMDB fetch is shared via <see cref="ICatalogWorkCoordinator"/>, and per-title LLM
/// fit is cached so re-asks never re-spend.
///
/// <para><b>Split by what survives the cutover (MOD-1).</b> This file is the v1 turn: the entry points
/// and the pipeline. <c>.Contracts</c> holds the turn contract the endpoint and both engines share and
/// stays when v1 goes; every other partial (<c>.Cards</c>, <c>.NoneFound</c>, <c>.Discovery</c>,
/// <c>.Filters</c>, <c>.Judging</c>, <c>.Filmography</c>, <c>.Keyless</c>, <c>.Keys</c>) is v1 only and is
/// deleted whole with this file at cutover (§4.13), except <c>SupplyWire</c>, which v2 also uses.</para>
/// </summary>
public sealed partial class LiveSearchExpansionService(
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
    ILogger<LiveSearchExpansionService> logger,
    Retrieval.IAskReelRetriever? retriever = null,
    Retrieval.ISupplyReporter? supplyReporter = null,
    AskReelJudge? askJudge = null,
    Retrieval.TmdbDiscoveryArm? tmdbDiscovery = null,
    ILlmPurposeCatalog? purposes = null)
{
    /// <summary>
    /// The request judge. Optional on the constructor only so the many hand-built test fixtures stay
    /// valid; production resolves the registered one, and a fixture that does not pass one gets the
    /// same judge over its own agent and cache.
    /// </summary>
    private AskReelJudge Judge => askJudge
        ?? (fallbackJudge ??= new AskReelJudge(
            agent, cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<AskReelJudge>.Instance));

    private AskReelJudge? fallbackJudge;

    /// <summary>
    /// WS-3's TMDB discovery arm (R1-52): standing options sent with the fetch, up to three pages, a
    /// 1.5 s deadline, title search only for a title-shaped message. Optional on the constructor for
    /// the same reason <see cref="Judge"/> is; a fixture that passes none gets the same arm over its
    /// own TMDB client, catalogue and cache.
    /// </summary>
    private Retrieval.TmdbDiscoveryArm TmdbArm => tmdbDiscovery
        ?? (fallbackTmdbArm ??= new Retrieval.TmdbDiscoveryArm(tmdb, db, eligibility, cache));

    private Retrieval.TmdbDiscoveryArm? fallbackTmdbArm;

    /// <summary>
    /// The semantic arm's wait for an UNCACHED concept embedding (§4.7): a new text takes 1.2–2.7 s to
    /// embed, so the kNN pull gives up on it after this long — measured from when the embedding was
    /// started, which is before the TMDB arm, so the arm's own 1.5 s usually covers it — and fusion
    /// proceeds without the arm. The ranking seed still waits for the same call; it is never repeated.
    /// </summary>
    internal static readonly TimeSpan SemanticEmbedBudget = TimeSpan.FromSeconds(1.0);

    private const int MaxCandidates = 40;
    private const int ReturnCount = 24;
    private const int PersonReturnCount = 40; // a filmography can run long — show more than a vibe match
    private const int HydrateScoreSlice = 12;
    private const int SemanticNeighbourCount = 40; // semantic kNN pull size — mirrors the TMDB arm's ceiling

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

    /// <summary>
    /// How long one question's INTERPRETATION is reused. Short on purpose, and much shorter than
    /// <see cref="DiscoveryTtl"/>: this caches a conversational reply as well as an intent, so its
    /// job is to make a re-ask, a double-tap or a reload consistent — not to answer tomorrow's
    /// question with today's sentence. Ten minutes covers the whole window in which a person is
    /// still looking at the same screen.
    /// </summary>
    private static readonly TimeSpan InterpretTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// camelCase, because the stored notice is handed straight back on the read routes and has to
    /// arrive spelled exactly as the live <c>conflict</c> frame spells it. The default serializer
    /// writes the C# property names, and a row that said <c>ExcludedGenres</c> while the wire said
    /// <c>excludedGenres</c> would give the client two shapes for one fact — which is the whole
    /// class of defect this notice is being persisted to close.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions ConflictJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Single-turn convenience overload (no conversation history / shown set) — a query EXPANDED
    /// rather than a conversation continued, and billed as <c>rerank-expand</c> for that reason.
    /// </summary>
    public Task StreamAsync(string message, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(new AskTurnInput(message, [], [], [], region, [], ApiUsagePurpose.RerankExpand), emit, ct);

    /// <summary>Overload without an explicit media filter — the intent's own inference applies.</summary>
    public Task StreamAsync(
        string message, IReadOnlyList<ChatTurn> history, IReadOnlyCollection<Guid> shownTitleIds, string? region, Emit emit, CancellationToken ct) =>
        StreamAsync(new AskTurnInput(message, history, shownTitleIds, [], region, [], ApiUsagePurpose.RerankAsk), emit, ct);

    /// <summary>An Ask Reel conversational turn with an explicit media filter.</summary>
    public Task<TurnOutcome> StreamAsync(
        string message,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyCollection<Guid> shownTitleIds,
        string? region,
        IReadOnlyList<MediaType> mediaFilter,
        Emit emit,
        CancellationToken ct,
        bool debug = false,
        Guid? sourceTurnId = null) =>
        StreamAsync(
            new AskTurnInput(message, history, shownTitleIds, [], region, mediaFilter, ApiUsagePurpose.RerankAsk, debug, sourceTurnId),
            emit, ct);

    /// <summary>An Ask Reel turn billed to an explicit re-rank purpose.</summary>
    public Task<TurnOutcome> StreamAsync(
        string message,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyCollection<Guid> shownTitleIds,
        string? region,
        IReadOnlyList<MediaType> mediaFilter,
        string rerankPurpose,
        Emit emit,
        CancellationToken ct,
        bool debug = false,
        Guid? sourceTurnId = null) =>
        StreamAsync(
            new AskTurnInput(message, history, shownTitleIds, [], region, mediaFilter, rerankPurpose, debug, sourceTurnId),
            emit, ct);

    /// <summary>
    /// Runs a conversational turn end-to-end, emitting the SSE event stream. Never throws for an
    /// ordinary failure — emits a graceful done with a reason instead. Cancellation propagates; what
    /// had been emitted by then is on <see cref="AskTurnInput.Progress"/>.
    ///
    /// <para><b>The shape of a turn, since the interim gate (WS-2 task 1).</b> Interpret → discover
    /// (TMDB, semantic, and the facet arm when the request names a concept) → score → judge the top
    /// sixteen against the REQUEST, concurrently → place every card by its verdict and the account's
    /// own bar → stream the picks as each judge batch returns → done. The reply is withheld until the
    /// first pick is about to go out, because a reply written before any candidate exists promised
    /// "ninja films" above 24 cards with none in them (B-1); a turn that finds nothing answers with
    /// a deterministic account of the supply instead (<see cref="NoneFoundCopy"/>).</para>
    /// </summary>
    public async Task<TurnOutcome> StreamAsync(AskTurnInput input, Emit emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var accountId = accountContext.AccountId
            ?? throw new InvalidOperationException("Live search requires an account.");
        var progress = input.Progress ?? new AskTurnProgress();
        var trimmed = (input.Message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            await emit("done", new { results = Array.Empty<object>(), reason = "empty query" });
            return TurnOutcome.Answered(assistantText: null, "empty query");
        }

        Task Phase(object payload)
        {
            progress.Phase(payload);
            return emit("phase", payload);
        }

        // Where this turn's seconds went. Stages accumulate, so discovery running three times at
        // three relaxation levels reports one total rather than three entries, and a stage that
        // never ran is absent rather than zero.
        var timings = new AskStageTimings();

        // Held outside the try so a crash after the reply still records what the user was told.
        string? assistantText = null;
        string? conflictJson = null;
        var degraded = new List<string>();

        // The reply of a turn answered before the model is consulted (a pointer with no target, a
        // keyless install): recorded on the progress exactly as the main path records its reply.
        async Task ReplyOnce(string text, bool degradedReply)
        {
            assistantText = text;
            progress.Replied(text, degradedReply);
            await emit("assistant-message", new { text, conflict = false, degraded = degradedReply });
        }

        // The effort the turn's calls are actually sent at, read from the same purpose catalog the
        // chat client sends from (R1-09) — never a hard-coded "provider-default".
        var effort = EffortSent(input.RerankPurpose);
        var turnClock = System.Diagnostics.Stopwatch.StartNew();

        // Time to the first card on the wire — the number §4.11's first-card gate is about, recorded
        // on the turn so it is a query rather than a stopwatch someone has to hold (R1-07).
        var firstCardMarked = false;
        void MarkFirstCard()
        {
            if (!firstCardMarked)
            {
                firstCardMarked = true;
                timings.Mark(AskStageTimings.FirstCard, turnClock.Elapsed.TotalMilliseconds);
            }
        }
        progress.Attach(timings, ModelIdsJson(), effort);
        try
        {
            await Phase(new { stage = "searching", found = 0, scored = 0 });

            // "The second one" with nothing on screen for it to point at is a question to ask back,
            // not a search (R1-04): answered deterministically, before any model call.
            if (AskReelPointer.Ordinal(trimmed) is { } pointer && !AskReelPointer.CanResolve(pointer, input.Presented))
            {
                return await NothingToPointAtAsync(pointer, input, timings, ReplyOnce, emit);
            }

            // No embeddings provider → no model to rank or judge with (a keyless install). The local
            // lexical engine still finds keyword matches, but nothing checks them against the request,
            // so they are answered honestly as unchecked — never as picks (R1-10).
            if (!embeddings.IsAvailable)
            {
                var keyless = await StreamKeylessAsync(accountId, trimmed, input, timings, ReplyOnce, emit, ct);
                logger.LogInformation(
                    "Ask Reel '{Query}': keyless lexical answer (no embeddings provider). {StageTimings}", trimmed, timings);
                return keyless;
            }

            var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
            var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
            var tasteSummary = persona.Declined is { Length: > 0 } declined
                ? $"{persona.Taste}. {declined}"
                : persona.Taste;

            // The account's standing content rules, read ONCE per turn.
            var filterRows = await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync(ct);
            var standingRules = FilterRuleSet.FromRows(filterRows);
            var standingSummary = AskReelStandingFilters.Describe(standingRules);

            // ── Interpret the turn → reply + discovery intent ────────────────────────────────
            // Cached on the QUESTION IN ITS CONTEXT — see InterpretKey. A DEGRADED reading is never
            // cached (A-14, B-5): a retry inside the window used to replay the failure for ten minutes.
            var interpretKey = InterpretKey(
                accountId, trimmed, standingSummary, input.MediaFilter, agent.ModelId, input.History,
                input.Presented, input.ShownTitleIds);
            AgentReply agentReply;
            using (timings.Stage(AskStageTimings.Interpret))
            {
                if (cache.TryGetValue<AgentReply>(interpretKey, out var remembered) && remembered is { Degraded: false })
                {
                    agentReply = remembered;
                    timings.MarkCached(AskStageTimings.Interpret);
                }
                else
                {
                    agentReply = await agent.InterpretAsync(
                        input.History, trimmed, tasteSummary, AskReelHandles.PromptLines(input.Presented), ct,
                        standingSummary);
                    if (!agentReply.Degraded)
                    {
                        cache.Set(interpretKey, agentReply, InterpretTtl);
                    }
                }
            }

            if (agentReply.Degraded)
            {
                degraded.Add(SearchTurnDegraded.Plan);
                progress.Degrade(SearchTurnDegraded.Plan);

                // The cause rides beside the plan flag, never inside it: PlanFailed and the frontend
                // match the exact "plan" token, so "plan:budget_spent" would let the turn into history.
                if (agentReply.FailureCode is { Length: > 0 } cause)
                {
                    degraded.Add(SearchTurnDegraded.PlanCausePrefix + cause);
                    progress.Degrade(SearchTurnDegraded.PlanCausePrefix + cause);
                }
            }

            // The server's correction of the model's reading: no negation phrase or ordinal as a
            // positive keyword, no standing condition restated as a turn exclusion (A-9, B-12).
            agentReply = agentReply with
            {
                Intent = AskReelIntentSanitizer.Sanitize(agentReply.Intent, standingRules, trimmed),
            };

            // ── The collision, computed from the words the user typed ────────────────────────
            var askedFor = AskReelStandingFilters.AskedFor(trimmed, agentReply.Intent, agentReply.Requested);
            var collidingGenres = AskReelStandingFilters.Colliding(standingRules.ExcludeGenres, askedFor);
            var collidingKeywords = AskReelStandingFilters.Colliding(standingRules.ExcludeKeywords, askedFor);
            var yearCollides = AskReelStandingFilters.YearWindowCollides(
                standingRules, agentReply.Intent, agentReply.Requested, trimmed);
            var hasConflict = collidingGenres.Count > 0 || collidingKeywords.Count > 0 || yearCollides;

            // The turn's negation model: the account's permanent rules (airtight) plus whatever the
            // user's own words ruled out (soft). The model's inferred exclusions are absent.
            var negations = AskReelNegations.For(standingRules, agentReply.Requested);

            AskReelConflict? conflict = hasConflict
                ? new AskReelConflict(
                    collidingGenres,
                    collidingKeywords,
                    yearCollides ? standingRules.MinYear : null,
                    yearCollides ? standingRules.MaxYear : null,
                    AskReelStandingFilters.Message(
                        collidingGenres, collidingKeywords, yearCollides, standingRules))
                : null;
            conflictJson = conflict is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(conflict, ConflictJsonOptions);

            // The model's sentence, plain text, qualified by the conditions it is about to meet. A
            // degraded reading carries the degraded copy for ITS cause (IREV2-SYS-02), which claims
            // nothing and quotes nobody. Re-derived from the failure rather than read from Reply, so a
            // degraded reply built anywhere else still can never echo the request. A spent PLAN
            // allowance names only what it cost — the judge has its own (IREV3-SYS-03).
            var modelSentence = agentReply.Degraded
                ? AskReelDegradedCopy.SearchLead(agentReply.Failure, agentReply.FailureCode)
                : AskReelText.StripMarkdown(agentReply.Reply) ?? string.Empty;
            var pickReply = hasConflict && !agentReply.Degraded
                ? AskReelStandingFilters.Qualify(
                    modelSentence, collidingGenres, collidingKeywords, yearCollides, standingRules)
                : modelSentence;

            // The reply goes out ONCE, at the moment the turn knows what it is answering with — just
            // before the first pick, or as the none-found account. Recorded on the progress so an
            // aborted turn still persists exactly what was on screen.
            var replied = false;
            async Task ReplyAsync(string text)
            {
                if (replied)
                {
                    return;
                }

                replied = true;
                assistantText = text;
                progress.Replied(text, agentReply.Degraded);
                await emit("assistant-message", new { text, conflict = hasConflict, degraded = agentReply.Degraded });
            }

            var isPersonQuery = agentReply.Intent.People is { Count: > 0 };

            // An explicit media filter from the UI is a direct instruction and outranks the model.
            // A follow-up silent about media keeps the conversation's media (AskReelCarry).
            var intent = input.MediaFilter.Count > 0
                ? agentReply.Intent with { MediaTypes = input.MediaFilter.Distinct().ToList() }
                : isPersonQuery
                    ? agentReply.Intent
                    : agentReply.Intent with
                    {
                        MediaTypes = AskReelCarry.Media(agentReply.Intent.MediaTypes, input.PriorIntent?.MediaTypes, trimmed),
                    };
            agentReply = agentReply with { Intent = intent };

            var shownSet = input.ShownTitleIds.ToHashSet();
            var baseQuery = isPersonQuery
                ? eligibility.ContentFilteredTitles(accountId)
                : eligibility.EligibleTitles(accountId);

            var (referenceTitles, referenceVectors) = isPersonQuery
                ? (new List<Title>(), new List<float[]>())
                : await ResolveReferenceTitlesAsync(intent, ct);
            var referenceIds = referenceTitles.Select(t => t.Id).ToHashSet();

            // The request's hard constraints as WS-3's retrieval contract reads them, built from ONE
            // resolved topic: the keywords the supply counts and the words the reply names them by
            // are the same list (R1-02). The facet arm runs only when the request names a concrete
            // concept (a keyword): a genre-only mood request would put the most-voted titles of that
            // genre ahead of the semantic matches.
            var topic = AskReelTopic.Resolve(intent.Keywords, trimmed, input.History);
            var hard = Retrieval.IntentConstraints.FromIntent(
                intent with { Keywords = [.. topic.Keywords] }, input.MediaFilter, topic.Label);
            Retrieval.RetrievalResult? facet = null;
            var evidence = new Dictionary<Guid, IReadOnlyList<string>>();

            // The concept embedding is a network call that needs no database, so it starts NOW and
            // runs underneath the TMDB arm instead of after it (R1-07: discovery ran its arms one
            // after another, 2.8–4.9 s a turn). The kNN arm and the ranking seed both read this one
            // call; it is never made twice.
            var conceptVector = isPersonQuery
                ? null
                : StartConceptVector(accountId, ConceptText(agentReply, trimmed, negations), ct);

            // ── Discover, widening until there is enough to rank ─────────────────────────────
            var discovered = new List<Guid>();
            var eligible = new List<Title>();
            var relaxation = 0;
            // A request with a topic the user named ("ninja") never widens past it (§4.8): the
            // ladder's lower rungs drop the keywords, so everything they add is off-topic filler the
            // judge then rejects — paid for in TMDB round trips, embeddings and judge calls (the
            // "ninja movies" turn ran all three rungs and took 25 s). Mood and genre requests keep
            // their genres on every rung, so they still widen.
            var namedTopic = topic.Keywords.Count > 0;
            var widest = namedTopic ? 1 : MaxRelaxations;
            for (; relaxation < widest; relaxation++)
            {
                var pass = Relax(intent, relaxation);
                using (timings.Stage(AskStageTimings.Discover))
                {
                    List<Guid> candidateIds;
                    using (timings.Stage(AskStageTimings.DiscoverTmdb))
                    {
                        candidateIds = await DiscoverCandidateIdsAsync(
                            accountId, pass, negations, trimmed, input.Region, ct);
                    }

                    if (relaxation == 0 && retriever is not null && !isPersonQuery && topic.Keywords.Count > 0)
                    {
                        using (timings.Stage(AskStageTimings.DiscoverFacet))
                        {
                            facet = await FacetArmAsync(accountId, hard, shownSet, referenceIds, ct);
                        }

                        foreach (var c in facet?.Candidates ?? [])
                        {
                            evidence[c.TitleId] = c.Evidence;
                        }
                    }

                    List<Guid> semanticIds;
                    using (timings.Stage(AskStageTimings.DiscoverKnn))
                    {
                        semanticIds = isPersonQuery
                            ? []
                            : await SemanticNeighbourIdsAsync(
                                accountId, agentReply with { Intent = pass }, referenceVectors, negations, trimmed,
                                relaxation == 0 ? conceptVector : null, ct);
                    }

                    discovered = discovered
                        .Concat(facet?.Candidates.Select(c => c.TitleId) ?? [])
                        .Concat(candidateIds)
                        .Concat(semanticIds)
                        .Distinct()
                        .ToList();
                }

                await Phase(new { stage = "discovered", found = discovered.Count, scored = 0 });

                using var eligibleStage = timings.Stage(AskStageTimings.Eligible);
                eligible = (await baseQuery
                        .Where(t => discovered.Contains(t.Id))
                        .OrderBy(t => t.Id)
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    // The no-repeat set, keyed on the TITLE. TMDB's movie and TV id spaces overlap,
                    // so the old TmdbId key hid unrelated shows that shared a number with a movie.
                    .Where(t => !shownSet.Contains(t.Id))
                    .Where(t => !IsReference(t, referenceIds))
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .Where(t => MatchesYear(t, pass))
                    .ToList();

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

            // The level discovery actually ran at: a concept request that stopped at its only pass
            // did not widen, whatever the loop counter says.
            relaxation = Math.Min(relaxation, widest - 1);

            // Last resort: the user's own eligible catalogue, nearest-by-taste. Every one of these
            // is still JUDGED against the request before it can reach the screen, so a last resort
            // that matches nothing ends as an honest none-found rather than as filler. Never for a
            // request with a named topic: popular titles are not ninja films, and judging them only
            // spends a judge call and the user's time to say so (§4.8, R1-07).
            if (eligible.Count == 0 && !namedTopic)
            {
                using var lastResortStage = timings.Stage(AskStageTimings.Eligible);
                eligible = (await baseQuery
                        .Where(t => t.TmdbPopularity != null)
                        .OrderByDescending(t => t.TmdbPopularity)
                        .Take(MaxCandidates * 2)
                        .ToListAsync(ct))
                    .Where(t => EligibilityQueryBuilder.PassesMaturity(t, account.Settings.MaturityCeiling))
                    .Where(t => !shownSet.Contains(t.Id))
                    .Where(t => !IsReference(t, referenceIds))
                    .Where(t => MatchesMedia(t, intent.MediaTypes))
                    .ToList();
                relaxation = MaxRelaxations;
            }

            var eligiblePoolSize = await baseQuery.CountAsync(ct);

            if (hasConflict)
            {
                await emit("conflict", new
                {
                    excludedGenres = conflict!.ExcludedGenres,
                    excludedKeywords = conflict.ExcludedKeywords,
                    minYear = conflict.MinYear,
                    maxYear = conflict.MaxYear,
                    poolSize = eligiblePoolSize,
                    catalogueSize = await db.Titles.CountAsync(ct),
                    message = conflict.Message,
                });
            }

            if (eligible.Count == 0)
            {
                logger.LogInformation(
                    "Ask Reel '{Query}': nothing survived discovery. {EligiblePool} titles are eligible for this account overall.",
                    trimmed, eligiblePoolSize);
                return await NoneFoundAsync(
                    accountId, hard, facet, intent, relaxation, eligibleCount: 0, eligiblePoolSize, discovered.Count,
                    underBar: [], nearMiss: [], belowFloor: [], floor: MinPredictedFloor(filterRows),
                    agentReply.Degraded ? modelSentence : null, ReplyAsync, degraded, timings, input, emit, conflictJson, conflict?.Message,
                    judged: 0, judge: null, effort, ct);
            }

            // Person queries are a filmography, not a vibe match: order chronologically, badge what
            // you've seen, score for the predicted-rating badge — but never re-order or cull by fit.
            if (isPersonQuery)
            {
                await ReplyAsync(pickReply);
                return await StreamFilmographyAsync(
                    accountId, assistantText, agentReply.Intent, eligible, taste, now, timings, emit, ct);
            }

            // ── Embed eligible candidates that lack a vector, then cosine pre-rank ───────────
            await Phase(new { stage = "embedding", found = eligible.Count, scored = 0 });
            using var embedStage = timings.Stage(AskStageTimings.Embed);
            await EnsureEmbeddedAsync(eligible, ct, timings);
            var vectorsByTitle = await LoadVectorsAsync(eligible.Select(t => t.Id).ToList(), ct);
            var tagVectors = await LoadPreferenceTagVectorsAsync(accountId, ct);

            // The POSITIVE reading of the turn, never the raw sentence: an embedding has no "not".
            var seedText = RankingSeedText(agentReply, trimmed, negations);
            // Timed on its own: the concept embedding started before discovery, so this is only the
            // part of it the arms did not hide — the number that says whether an uncached embedding
            // is what the first card is waiting on (R1-07).
            float[]? queryVec;
            using (timings.Stage(AskStageTimings.EmbedSeed))
            {
                queryVec = await RankingSeedVectorAsync(accountId, seedText, referenceVectors, conceptVector, ct);
            }

            embedStage.Dispose();

            var scoredPool = eligible
                .Where(t => queryVec is null || vectorsByTitle.ContainsKey(t.Id))
                .Select(t => (
                    Title: t,
                    Sim: queryVec is not null && vectorsByTitle.TryGetValue(t.Id, out var vec) ? Cosine(queryVec, vec) : 0d))
                .OrderByDescending(x => x.Sim)
                .ThenBy(x => x.Title.Id)
                .ToList();

            // Evidence first BEFORE any cut (IREV1-SYS-01): the seed is the very vector the kNN arm
            // pulled its neighbours with, so a cosine cut fills with kNN titles by construction and a
            // facet title the catalogue tags with the concept survived only if it was ALSO among the
            // nearest. The reply then counted "5 unseen heist movies tagged that way" that no judge
            // ever saw.
            var (ranked, outside) = PartitionByNegation(scoredPool, negations, evidence.ContainsKey);

            // The judge's order is the partition's order: titles carrying catalogue evidence for the
            // concept first (facet arm), then by similarity — and, when there are more tagged titles
            // than the judge rules on, the tagged ones the account's bar lets through ahead of the
            // rest (IREV2-SYS-01).
            var floor = MinPredictedFloor(filterRows);
            var judgeOrder = (await JudgeOrderAsync(accountId, ranked, evidence.ContainsKey, floor, now, taste, timings, ct))
                .Take(AskReelJudge.MaxJudged)
                .ToList();

            // ── Score what can reach the screen ──────────────────────────────────────────────
            var sliceIds = judgeOrder.Concat(outside).Select(x => x.Title.Id).ToList();
            await Phase(new { stage = "scoring", found = sliceIds.Count, scored = 0 });
            IReadOnlyDictionary<Guid, decimal> scores;
            using (timings.Stage(AskStageTimings.Score))
            {
                // A title TMDB discovery has just added arrives with no keywords and no certification,
                // so it passed eligibility on what its list row carried. Once hydrated it is judged
                // again — the arm's own contract for a provisional title — and one that now trips a
                // standing condition or the maturity ceiling never reaches the judge or the screen.
                if (await HydrateAsync(sliceIds, ct, timings) > 0)
                {
                    var stillEligible = (await baseQuery
                            .Where(t => sliceIds.Contains(t.Id))
                            .Select(t => t.Id)
                            .ToListAsync(ct))
                        .ToHashSet();
                    bool Keeps((Title Title, double Sim) x) =>
                        stillEligible.Contains(x.Title.Id)
                        && EligibilityQueryBuilder.PassesMaturity(x.Title, account.Settings.MaturityCeiling)
                        && !TripsStandingExclusion(x.Title, negations);
                    judgeOrder = judgeOrder.Where(Keeps).ToList();
                    outside = outside.Where(Keeps).ToList();
                    sliceIds = judgeOrder.Concat(outside).Select(x => x.Title.Id).ToList();
                }

                scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);

                // A card with no prediction never clears a set bar (AskReelGate.Clears), so a card the
                // scorer missed would be held back for want of a number rather than on its merits:
                // one more attempt on exactly those first.
                if (floor > 0m)
                {
                    var unscored = sliceIds.Where(id => !scores.ContainsKey(id)).ToList();
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
                        }
                    }
                }
            }

            decimal? PredictedOf(Guid id) => scores.TryGetValue(id, out var p) ? p : null;
            double TagBoost(Guid id) => vectorsByTitle.TryGetValue(id, out var vec) ? TagAffinity(vec, tagVectors) : 0d;

            // ── Judge the top of the pool against the REQUEST, concurrently ──────────────────
            await Phase(new { stage = "ranking", found = judgeOrder.Count, scored = sliceIds.Count });
            var referenceNames = referenceTitles.Count > 0
                ? referenceTitles.Select(t => t.Name).ToList()
                : (intent.ReferenceTitles ?? []).ToList();
            // The request in its conversation, never the bare follow-up (D2): the judge's verdict is
            // the gate, and its cache key is this text.
            var rerankQuery = AskReelJudgeRequest.Compose(trimmed, input.History, intent, referenceNames);
            var contexts = await RerankContextBuilder.BuildAsync(
                db, judgeOrder.Select(r => r.Title.Id).ToList(), topContributions: null, ct);
            var inputs = judgeOrder
                .Select(x => new RerankInput(
                    x.Title.Id, x.Title.Name, x.Title.Year, x.Title.MediaType.ToString(), x.Title.Genres, x.Title.Overview,
                    PredictedOf(x.Title.Id), contexts.GetValueOrDefault(x.Title.Id)))
                .ToList();
            var simById = judgeOrder.ToDictionary(x => x.Title.Id, x => x.Sim);
            var titleById = judgeOrder.ToDictionary(x => x.Title.Id, x => x.Title);

            var placed = new Dictionary<Guid, (AskReelGate.Placement Placement, RerankResult? Verdict)>();
            var picks = new List<GatedCard>();
            AskReelJudge.Summary judgeSummary;
            // A failed batch — and every title a batch skipped — is recorded on the progress AS IT
            // RETURNS, so a turn aborted before the judge finishes still persists the judge flags (D9).
            var judgeBatches = AskReelJudge.BatchCount(inputs.Count);
            var judgedCount = Math.Min(inputs.Count, AskReelJudge.MaxJudged);
            var failedSoFar = 0;
            var missingSoFar = 0;
            var judgeClock = System.Diagnostics.Stopwatch.StartNew();
            var firstBatchTimed = false;
            using (timings.Stage(AskStageTimings.Rerank))
            {
                judgeSummary = await Judge.JudgeAsync(
                    accountId, rerankQuery, persona, inputs, input.RerankPurpose,
                    async batch =>
                    {
                        if (!firstBatchTimed)
                        {
                            firstBatchTimed = true;
                            timings.Mark(AskStageTimings.RerankFirstBatch, judgeClock.Elapsed.TotalMilliseconds);
                        }

                        if (batch.Failed)
                        {
                            failedSoFar++;
                            progress.Degrade(FormattableString.Invariant(
                                $"{SearchTurnDegraded.JudgePrefix}{failedSoFar}/{judgeBatches}"));
                        }

                        if (batch.Missing > 0)
                        {
                            missingSoFar += batch.Missing;
                            progress.Degrade(FormattableString.Invariant(
                                $"{SearchTurnDegraded.JudgeMissingPrefix}{missingSoFar}/{judgedCount}"));
                        }

                        var accepted = new List<GatedCard>();
                        foreach (var item in batch.Inputs)
                        {
                            var verdict = batch.Results.GetValueOrDefault(item.TitleId);
                            var satisfies = AskReelJudge.Ruled(verdict) ? verdict!.Satisfies : null;
                            var placement = AskReelGate.Place(satisfies, PredictedOf(item.TitleId), floor);
                            placed[item.TitleId] = (placement, verdict);
                            if (placement == AskReelGate.Placement.Pick)
                            {
                                accepted.Add(Gate(titleById[item.TitleId], simById[item.TitleId], PredictedOf(item.TitleId),
                                    verdict, TagBoost(item.TitleId), placement));
                            }
                        }

                        // Within one batch, the strongest first; across batches, the order they
                        // returned in — a card never moves once it is on screen.
                        foreach (var card in accepted.OrderByDescending(c => c.Blend).ThenBy(c => c.Title.Id))
                        {
                            await ReplyAsync(pickReply);
                            picks.Add(card);
                            await EmitCardAsync(card, progress, emit);
                            MarkFirstCard();
                        }
                    },
                    ct);
            }

            if (judgeSummary.FailedBatches > 0)
            {
                var judgeFlag = FormattableString.Invariant(
                    $"{SearchTurnDegraded.JudgePrefix}{judgeSummary.FailedBatches}/{judgeSummary.Batches}");
                degraded.Add(judgeFlag);
            }

            // Any title the judge did not rule on is a degraded judge, not only a batch that ruled on
            // none (R1-01): one skipped entry is a card on screen that nobody checked.
            if (judgeSummary.Missing > 0)
            {
                degraded.Add(FormattableString.Invariant(
                    $"{SearchTurnDegraded.JudgeMissingPrefix}{judgeSummary.Missing}/{judgeSummary.Judged}"));
            }

            // ── Place everything the judge did not accept outright ───────────────────────────
            using var blendStage = timings.Stage(AskStageTimings.Blend);
            GatedCard Of((Title Title, double Sim) x, AskReelGate.Placement placement) =>
                Gate(x.Title, x.Sim, PredictedOf(x.Title.Id), placed[x.Title.Id].Verdict, TagBoost(x.Title.Id), placement);

            // Unjudged cards, in retrieval order, with no why (§4.10): below every judged pick when
            // there is one and the card clears the bar, in the "not checked" tray otherwise (§4.8, D1,
            // R1-01). Never in the floor tray, which holds judged matches only.
            var unjudged = judgeOrder
                .Where(x => placed.TryGetValue(x.Title.Id, out var p) && p.Placement == AskReelGate.Placement.Unjudged)
                .Select(x => Of(x, AskReelGate.Placement.Unjudged))
                .ToList();
            var nearMiss = judgeOrder
                .Where(x => placed.TryGetValue(x.Title.Id, out var p) && p.Placement == AskReelGate.Placement.NearMiss)
                .Select(x => Of(x, AskReelGate.Placement.NearMiss))
                .ToList();
            var belowFloor = judgeOrder
                .Where(x => placed.TryGetValue(x.Title.Id, out var p) && p.Placement == AskReelGate.Placement.BelowFloor)
                .Select(x => Of(x, AskReelGate.Placement.BelowFloor))
                .ToList();
            blendStage.Dispose();

            // NoneFound exactly when no card was judged a match — unjudged cards never make an
            // answer on their own (§4.8; D1).
            if (AskReelGate.ModeFor(picks.Count) == SearchTurnMode.NoneFound)
            {
                // Judged matches the bar held back: named as under it only when they have a score.
                var heldMatches = belowFloor
                    .Where(c => c.Verdict is { FitSource: ResultSource.Llm, Satisfies: Satisfies.Yes })
                    .ToList();
                var underBar = heldMatches.Where(c => c.Predicted is not null).Select(c => c.Title.Name).ToList();
                var unscoredHeld = heldMatches.Where(c => c.Predicted is null).Select(c => c.Title.Name).ToList();
                // What the judge actually ruled on — a skipped or failed entry is not a check.
                var judgedIds = placed
                    .Where(p => p.Value.Placement != AskReelGate.Placement.Unjudged)
                    .Select(p => p.Key)
                    .ToList();
                // The bar binds every card (XM-06): an unjudged card it holds back joins the floor
                // tray with no verdict — never the unchecked tray, which reads as cards that cleared it.
                // It is not a held MATCH, so it is never named as one (R1-01: heldMatches above).
                var (heldUnjudged, uncheckedNone) = SplitByBar(unjudged, floor);
                belowFloor.AddRange(heldUnjudged);
                // The scoring stage above ran (and stored what it scored), so the supply the facet arm
                // counted at discovery is re-counted where it still had unscored titles.
                return await NoneFoundAsync(
                    accountId, hard, facet, intent, relaxation, eligible.Count, eligiblePoolSize, discovered.Count,
                    underBar, nearMiss, belowFloor, floor, agentReply.Degraded ? modelSentence : null, ReplyAsync, degraded, timings, input,
                    emit, conflictJson, conflict?.Message, judgeOrder.Count, judgeSummary, effort, ct, uncheckedNone, unscoredHeld,
                    judgedIds, scoredThisTurn: true, heldUnchecked: heldUnjudged.Count);
            }

            await ReplyAsync(pickReply);
            var judgedPicks = picks.Count;
            var uncheckedTray = new List<GatedCard>();
            foreach (var card in unjudged)
            {
                if (AskReelGate.UnjudgedTier(judgedPicks, AskReelGate.Clears(card.Predicted, floor)) == SearchTurnCardTier.Pick)
                {
                    picks.Add(card);
                    await EmitCardAsync(card, progress, emit);
                }
                else if (AskReelGate.UncheckedTier(card.Predicted, floor) == SearchTurnCardTier.BelowFloor)
                {
                    // The bar binds first (XM-06): under it is the floor tray, verdict or not.
                    belowFloor.Add(card);
                }
                else
                {
                    uncheckedTray.Add(card);
                }
            }

            // Outside cards trail every pick, each captioned with the exclusion it trips. They are
            // shown only beside real picks — never as an answer of their own — and the bar binds
            // them like any other card: one under the bar joins the floor tray rather than vanishing
            // (1e), carrying no verdict (XM-06; R1-01 is about naming such a card as a match).
            var outsideAll = outside
                .Select(x => Gate(x.Title, x.Sim, PredictedOf(x.Title.Id), verdict: null, TagBoost(x.Title.Id),
                    AskReelGate.Placement.Unjudged) with
                {
                    Outside = true,
                    Why = OutsideWhy(NegationMatches(x.Title, negations)),
                })
                .ToList();
            var outsideCards = outsideAll.Where(c => AskReelGate.Clears(c.Predicted, floor)).ToList();
            belowFloor.AddRange(outsideAll.Where(c => !AskReelGate.Clears(c.Predicted, floor)));
            foreach (var card in outsideCards)
            {
                await EmitCardAsync(card, progress, emit);
            }

            using var emitStage = timings.Stage(AskStageTimings.Emit);
            var final = picks.Concat(outsideCards).ToList();
            var storedCards = Stored(final, nearMiss, belowFloor, uncheckedTray);
            var reason = AskReelReason.Build(
                new TurnCounts(
                    Picks: judgedPicks,
                    Outside: outsideCards.Count,
                    Tray: belowFloor.Count,
                    NearMiss: nearMiss.Count,
                    Unchecked: picks.Count - judgedPicks,
                    UncheckedTray: uncheckedTray.Count,
                    HeldUnchecked: belowFloor.Count(c => c.Placement != AskReelGate.Placement.BelowFloor)),
                floor);

            await emit("done", new
            {
                results = final.Select(CardOf).ToList(),
                // The turn's own accounting: everything on the glass is either kept or in the floor
                // tray, and streamed == kept + droppedByFloor always holds. The near-miss tray is
                // counted on its own, because it is not a cut of the user's bar.
                streamed = final.Count + belowFloor.Count,
                kept = final.Count,
                droppedByFloor = belowFloor.Count,
                belowFloor = belowFloor.Select(CardOf).ToList(),
                nearMiss = nearMiss.Select(CardOf).ToList(),
                @unchecked = uncheckedTray.Select(CardOf).ToList(),
                mode = SearchTurnMode.Picks,
                // Present and null on a turn with picks: one key set for one event name.
                supply = (object?)null,
                degraded = degraded.ToArray(),
                reason,
                timingsMs = input.Debug ? timings.Snapshot() : null,
                debug = input.Debug
                    ? (object?)new
                    {
                        intent,
                        relaxation,
                        eligibleCount = eligible.Count,
                        eligiblePoolSize,
                        judged = judgeOrder.Count,
                        judgeBatches = judgeSummary.Batches,
                        judgeFailedBatches = judgeSummary.FailedBatches,
                        judgeMissing = judgeSummary.Missing,
                        topic = topic.Label,
                        streamed = final.Count + belowFloor.Count,
                        kept = final.Count,
                        droppedByFloor = belowFloor.Count,
                        nearMiss = nearMiss.Count,
                        timingsMs = timings.Snapshot(),
                    }
                    : null,
            });

            // ── Exposures: Pick-tier cards only, with typed sources ─────────────────────────
            // An exposure means the product RECOMMENDED this title at this rank. Neither tray is a
            // recommendation, so neither is logged; outside cards are logged as before, captioned by
            // the deterministic template they carried.
            var artifactId = await ActiveArtifactAsync(accountId, ct);
            if (artifactId is { } activeArtifactId)
            {
                var loggable = final
                    .Select((c, index) => (c, index))
                    .Where(t => t.c.Predicted is not null)
                    .Select(t => new ExposureCard(
                        t.c.Title.Id,
                        t.index,
                        t.c.Predicted!.Value,
                        LlmFit: t.c.ModelFit is { } f ? (decimal)f : null,
                        WhySentence: t.c.Why,
                        WhySource: ExposureWhySourceOf(t.c)))
                    .ToList();

                await exposures.RecordAsync(
                    accountId, ExposureSurface.AskReel, feedSnapshotId: null, activeArtifactId,
                    DateTime.UtcNow, loggable, ct, input.SourceTurnId);
            }

            emitStage.Dispose();

            logger.LogInformation(
                "Ask Reel '{Query}': {Discovered} discovered at relaxation {Relaxation}, {Eligible} eligible "
                + "of {EligiblePool} in the account's pool, {Judged} judged in {Batches} batch(es) ({Failed} failed), "
                + "{Picks} picks, {NearMiss} near misses, {Tray} under the bar returned. {StageTimings}",
                trimmed, discovered.Count, relaxation, eligible.Count, eligiblePoolSize, judgeOrder.Count,
                judgeSummary.Batches, judgeSummary.FailedBatches, picks.Count, nearMiss.Count, belowFloor.Count, timings);

            return new TurnOutcome(
                assistantText,
                reason,
                Failed: false,
                FailureCode: null,
                relaxation,
                FloorRelaxed: false,
                discovered.Count,
                System.Text.Json.JsonSerializer.Serialize(intent),
                artifactId,
                agent.ModelId,
                storedCards,
                conflictJson,
                SearchTurnMode.Picks,
                DegradedFlagsOf(degraded),
                TimingsJson(timings),
                ModelIdsJson(),
                effort);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ask Reel expansion failed for '{Query}'.", trimmed);
            const string reason = "search hit a snag — please try again";
            await emit("done", new
            {
                results = Array.Empty<object>(),
                streamed = 0,
                kept = 0,
                droppedByFloor = 0,
                belowFloor = Array.Empty<object>(),
                nearMiss = Array.Empty<object>(),
                @unchecked = Array.Empty<object>(),
                mode = (string?)null,
                supply = (object?)null,
                degraded = degraded.ToArray(),
                reason,
                failed = true,
                timingsMs = input.Debug ? timings.Snapshot() : null,
                debug = (object?)null,
            });
            return TurnOutcome.Crashed(assistantText, reason, ex.GetType().Name, conflictJson, degradedFlags: DegradedFlagsOf(degraded))
                with
            {
                StageTimingsJson = TimingsJson(timings),
                ModelIdsJson = ModelIdsJson(),
                ReasoningEffort = effort,
            };
        }
    }

    /// <summary>The account's active model artifact, or null.</summary>
    private Task<Guid?> ActiveArtifactAsync(Guid accountId, CancellationToken ct) =>
        db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The stored degraded flags, or null when nothing degraded.</summary>
    private static string? DegradedFlagsOf(IReadOnlyList<string> flags) =>
        flags.Count == 0 ? null : string.Join(',', flags);

    /// <summary>The stage timings as stored JSON (J-13). Serialised invariantly by System.Text.Json.</summary>
    private static string TimingsJson(AskStageTimings timings) =>
        System.Text.Json.JsonSerializer.Serialize(timings.Snapshot());

    /// <summary>The models behind the turn, by role, as stored JSON.</summary>
    private string ModelIdsJson() =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["plan"] = agent.ModelId,
            ["judge"] = agent.ModelId,
            ["embedding"] = EmbeddingText.Model,
        });

    /// <summary>
    /// The reasoning effort this turn's model calls are sent at (R1-09), read from the purpose
    /// catalog the chat client sends from: the plan's and the judge's, written once when they agree
    /// and as <c>plan/judge</c> when they do not. Null when no model is available — nothing was sent.
    /// A fixture built without the catalog records the provider default, which is what an absent
    /// effort parameter means.
    /// </summary>
    private string? EffortSent(string rerankPurpose)
    {
        if (!agent.IsAvailable)
        {
            return null;
        }

        if (purposes is null)
        {
            return SearchTurnDegraded.ProviderDefaultEffort;
        }

        string Of(string purpose) => purposes.For(purpose).ReasoningEffort ?? SearchTurnDegraded.ProviderDefaultEffort;
        var plan = Of(ApiUsagePurpose.SearchAgent);
        var judge = Of(rerankPurpose);
        return string.Equals(plan, judge, StringComparison.Ordinal) ? plan : $"{plan}/{judge}";
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
        bool seen = false, bool outsideRequest = false, double? blend = null, string? satisfies = null) => new
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
            // The judge's request verdict ("yes" / "partial"), or null where the card was not judged.
            // Additive: old clients ignore it.
            satisfies,
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

}
