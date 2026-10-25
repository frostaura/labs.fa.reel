using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Search;
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
    ILogger<LiveSearchExpansionService> logger,
    Retrieval.IAskReelRetriever? retriever = null,
    Retrieval.ISupplyReporter? supplyReporter = null,
    AskReelJudge? askJudge = null)
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

    private const int MaxCandidates = 40;
    private const int ReturnCount = 24;
    private const int PersonReturnCount = 40; // a filmography can run long — show more than a vibe match
    private const int HydrateScoreSlice = 12;
    private const int SemanticNeighbourCount = 40; // semantic kNN pull size — mirrors the TMDB arm's ceiling
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

    /// <summary>An SSE event emitter — (eventName, payload). The endpoint writes it to the stream.</summary>
    public delegate Task Emit(string eventName, object payload);

    /// <summary>
    /// One card of the answer, as a row rather than as JSON. Built from the SAME projection as the
    /// <c>done.results</c> array and in the same order, so <see cref="Rank"/> is the position on
    /// screen, the stored rank and the exposure rank — one number, three readers, no chance of
    /// them disagreeing.
    /// </summary>
    /// <param name="TitleId">The title on the card.</param>
    /// <param name="TmdbId">Denormalised TMDB id, kept for readers that still key on it.</param>
    /// <param name="Rank">Index in the stored answer, zero-based: picks, then outside cards, then the trays.</param>
    /// <param name="OutsideRequest">The closest-but-outside reserve flag, as it was on the wire.</param>
    /// <param name="Similarity">Cosine to the ranking vector, rounded as rendered.</param>
    /// <param name="PredictedRating">The prediction the card carried, at the column's 2 dp.</param>
    /// <param name="LlmFit">The judge's fit, at the column's 3 dp — only where the MODEL produced it.</param>
    /// <param name="Blend">The server's ranking value.</param>
    /// <param name="Why">The why-line rendered, verbatim; null where none was shown.</param>
    /// <param name="WhySource">How that sentence was produced — an <c>ExposureWhySource</c> value.</param>
    /// <param name="BelowFloor">
    /// True for every card that is not a pick — the floor tray AND the near-miss tray — so an image
    /// that predates <paramref name="Tier"/> can never replay a tray card as a pick.
    /// </param>
    /// <param name="Tier">One of <c>SearchTurnCardTier</c>; null only from a caller that predates it.</param>
    /// <param name="Satisfies">The judge's request verdict (<c>yes</c>/<c>partial</c>/<c>no</c>), or null when unjudged.</param>
    /// <param name="FitSource">Where <paramref name="LlmFit"/> came from, by name; null where there is no fit.</param>
    public readonly record struct TurnCard(
        Guid TitleId,
        long? TmdbId,
        int Rank,
        bool OutsideRequest,
        double? Similarity,
        decimal? PredictedRating,
        decimal? LlmFit,
        decimal Blend,
        string? Why,
        string WhySource,
        bool BelowFloor = false,
        string? Tier = null,
        string? Satisfies = null,
        string? FitSource = null);

    /// <summary>
    /// What the turn was, handed back to whoever has a place to put it.
    ///
    /// <para>The service streams the answer and returns this; the ENDPOINT owns persistence,
    /// because only it knows which row this turn is and it must write on a scope that outlives the
    /// request's own tracked context. Nothing here is read back by the pipeline — a turn that is
    /// never stored behaves identically — so a persistence failure can never cost the user the
    /// answer they are already looking at.</para>
    /// </summary>
    /// <param name="AssistantText">The reply, exactly as emitted.</param>
    /// <param name="Reason">The <c>done.reason</c> clause list, exactly as emitted.</param>
    /// <param name="Failed">Whether the terminal frame admitted a crash.</param>
    /// <param name="FailureCode">The exception type or pipeline code behind <paramref name="Failed"/>.</param>
    /// <param name="Relaxation">How many levels discovery widened through.</param>
    /// <param name="FloorRelaxed">Always false since the floor stopped relaxing; kept for the column.</param>
    /// <param name="DiscoveredCount">How many candidates discovery returned, before eligibility.</param>
    /// <param name="IntentJson">The interpreted intent, serialised.</param>
    /// <param name="ModelArtifactId">The artifact behind the predictions on these cards.</param>
    /// <param name="ModelId">The chat model that produced the reply.</param>
    /// <param name="Cards">The answer, in stored order.</param>
    /// <param name="ConflictJson">The <c>conflict</c> frame this turn emitted, serialised, or null.</param>
    /// <param name="Mode">One of <c>SearchTurnMode</c>; null where the turn never reached a mode.</param>
    /// <param name="DegradedFlags">Comma-separated <c>SearchTurnDegraded</c> tokens, or null.</param>
    /// <param name="StageTimingsJson">The per-stage milliseconds, serialised.</param>
    /// <param name="ModelIdsJson">The models behind the turn by role, serialised.</param>
    /// <param name="ReasoningEffort">The effort the turn's model calls were made at.</param>
    public sealed record TurnOutcome(
        string? AssistantText,
        string? Reason,
        bool Failed,
        string? FailureCode,
        int? Relaxation,
        bool FloorRelaxed,
        int? DiscoveredCount,
        string? IntentJson,
        Guid? ModelArtifactId,
        string? ModelId,
        IReadOnlyList<TurnCard> Cards,
        string? ConflictJson = null,
        string? Mode = null,
        string? DegradedFlags = null,
        string? StageTimingsJson = null,
        string? ModelIdsJson = null,
        string? ReasoningEffort = null)
    {
        /// <summary>A turn that answered without the full pipeline — an empty query, a fallback, a filmography.</summary>
        public static TurnOutcome Answered(string? assistantText, string? reason, IReadOnlyList<TurnCard>? cards = null) =>
            new(assistantText, reason, Failed: false, FailureCode: null, Relaxation: null, FloorRelaxed: false,
                DiscoveredCount: null, IntentJson: null, ModelArtifactId: null, ModelId: null, cards ?? []);

        /// <summary>
        /// A turn that fell over. <paramref name="failureCode"/> is what the row records.
        ///
        /// <para><paramref name="conflictJson"/> survives the crash for the same reason
        /// <paramref name="assistantText"/> does: both went out on the wire before it.
        /// <paramref name="cards"/> is non-empty only for a turn the CLIENT ended: the cards it had
        /// already been shown are recorded, so they enter history and the no-repeat set (B-3).</para>
        /// </summary>
        public static TurnOutcome Crashed(
            string? assistantText, string reason, string failureCode, string? conflictJson = null,
            IReadOnlyList<TurnCard>? cards = null, string? degradedFlags = null) =>
            new(assistantText, reason, Failed: true, failureCode, Relaxation: null, FloorRelaxed: false,
                DiscoveredCount: null, IntentJson: null, ModelArtifactId: null, ModelId: null, cards ?? [], conflictJson,
                DegradedFlags: degradedFlags);
    }

    /// <summary>
    /// Everything one Ask Reel turn is given — all of it derived by the endpoint from rows it wrote,
    /// never from the client.
    /// </summary>
    /// <param name="Message">The question.</param>
    /// <param name="History">The transcript the model reads, assistant lines carrying their shown handles.</param>
    /// <param name="ShownTitleIds">The conversation's no-repeat set, keyed on the title (B-16).</param>
    /// <param name="Presented">The last answers' cards with their handles, for reference resolution.</param>
    /// <param name="Region">The account's watch region.</param>
    /// <param name="MediaFilter">The explicit UI media filter; empty for none.</param>
    /// <param name="RerankPurpose">Which member of the re-rank family the judge bills to.</param>
    /// <param name="Debug">Whether the founder-only diagnostics ride the terminal frame.</param>
    /// <param name="SourceTurnId">The assistant row this turn is, for the exposure key.</param>
    /// <param name="Progress">Where the turn records what it has emitted, so an abort can persist it.</param>
    public sealed record AskTurnInput(
        string Message,
        IReadOnlyList<ChatTurn> History,
        IReadOnlyCollection<Guid> ShownTitleIds,
        IReadOnlyList<PresentedCard> Presented,
        string? Region,
        IReadOnlyList<MediaType> MediaFilter,
        string RerankPurpose,
        bool Debug = false,
        Guid? SourceTurnId = null,
        AskTurnProgress? Progress = null,
        SearchIntent? PriorIntent = null);

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

        progress.Attach(timings, ModelIdsJson(), SearchTurnDegraded.ProviderDefaultEffort);
        try
        {
            await Phase(new { stage = "searching", found = 0, scored = 0 });

            // No embeddings provider → can't rank semantically. Fall back to the local lexical
            // engine so Ask Reel still answers (keyless installs); stream those, unscored-by-rank.
            if (!embeddings.IsAvailable)
            {
                TurnOutcome fallback;
                using (timings.Stage(AskStageTimings.Emit))
                {
                    fallback = await StreamLexicalFallbackAsync(accountId, trimmed, emit, ct);
                }

                logger.LogInformation(
                    "Ask Reel '{Query}': lexical fallback (no embeddings provider). {StageTimings}", trimmed, timings);
                return fallback;
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
            // degraded reading carries the degraded copy, which claims nothing and quotes nobody.
            var modelSentence = agentReply.Degraded
                ? AgentReply.DegradedReply
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

            // The request's hard constraints as WS-3's retrieval contract reads them. The facet arm
            // runs only when the request names a concrete concept (a keyword): a genre-only mood
            // request would put the most-voted titles of that genre ahead of the semantic matches.
            var hard = Retrieval.IntentConstraints.FromIntent(
                intent, input.MediaFilter, ConceptWords(intent));
            Retrieval.RetrievalResult? facet = null;
            var evidence = new Dictionary<Guid, IReadOnlyList<string>>();

            // ── Discover, widening until there is enough to rank ─────────────────────────────
            var discovered = new List<Guid>();
            var eligible = new List<Title>();
            var relaxation = 0;
            for (; relaxation < MaxRelaxations; relaxation++)
            {
                var pass = Relax(intent, relaxation);
                using (timings.Stage(AskStageTimings.Discover))
                {
                    var candidateIds = await DiscoverCandidateIdsAsync(WithStandingGenres(pass, standingRules), input.Region, ct);
                    var semanticIds = isPersonQuery
                        ? new List<Guid>()
                        : await SemanticNeighbourIdsAsync(
                            accountId, agentReply with { Intent = pass }, referenceVectors, negations, trimmed, ct);
                    if (relaxation == 0 && retriever is not null && !isPersonQuery && intent.Keywords.Count > 0)
                    {
                        facet = await FacetArmAsync(accountId, hard, shownSet, referenceIds, ct);
                        foreach (var c in facet?.Candidates ?? [])
                        {
                            evidence[c.TitleId] = c.Evidence;
                        }
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

            // Last resort: the user's own eligible catalogue, nearest-by-taste. Every one of these
            // is still JUDGED against the request before it can reach the screen, so a last resort
            // that matches nothing ends as an honest none-found rather than as filler.
            if (eligible.Count == 0)
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
                    agentReply.Degraded, ReplyAsync, degraded, timings, input, emit, conflictJson, conflict?.Message,
                    judged: 0, judge: null, ct);
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
            await EnsureEmbeddedAsync(eligible, ct);
            var vectorsByTitle = await LoadVectorsAsync(eligible.Select(t => t.Id).ToList(), ct);
            var tagVectors = await LoadPreferenceTagVectorsAsync(accountId, ct);

            // The POSITIVE reading of the turn, never the raw sentence: an embedding has no "not".
            var seedText = RankingSeedText(agentReply, trimmed, negations);
            var queryVec = await RankingSeedVectorAsync(accountId, seedText, referenceVectors, ct);
            embedStage.Dispose();

            var scoredPool = eligible
                .Where(t => queryVec is null || vectorsByTitle.ContainsKey(t.Id))
                .Select(t => (
                    Title: t,
                    Sim: queryVec is not null && vectorsByTitle.TryGetValue(t.Id, out var vec) ? Cosine(queryVec, vec) : 0d))
                .OrderByDescending(x => x.Sim)
                .ThenBy(x => x.Title.Id)
                .ToList();

            var (ranked, outside) = PartitionByNegation(scoredPool, negations);

            // The judge's order: titles carrying catalogue evidence for the concept first (facet
            // arm), then by similarity. Stable, so equal footing keeps the cosine order.
            var judgeOrder = ranked
                .Select((x, index) => (x.Title, x.Sim, Index: index))
                .OrderBy(x => evidence.ContainsKey(x.Title.Id) ? 0 : 1)
                .ThenBy(x => x.Index)
                .Take(AskReelJudge.MaxJudged)
                .Select(x => (x.Title, x.Sim))
                .ToList();

            // ── Score what can reach the screen ──────────────────────────────────────────────
            var sliceIds = judgeOrder.Concat(outside).Select(x => x.Title.Id).ToList();
            await Phase(new { stage = "scoring", found = sliceIds.Count, scored = 0 });
            var floor = MinPredictedFloor(filterRows);
            IReadOnlyDictionary<Guid, decimal> scores;
            using (timings.Stage(AskStageTimings.Score))
            {
                await HydrateAsync(sliceIds, ct);
                scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);

                // A card with no prediction CLEARS the floor, so an unscored card is an unannounced
                // exemption from the user's own bar: one more attempt on exactly those first.
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
            // A failed batch is recorded on the progress AS IT RETURNS, so a turn aborted before the
            // judge finishes still persists the judge flag (D9).
            var judgeBatches = (Math.Min(inputs.Count, AskReelJudge.MaxJudged) + AskReelJudge.BatchSize - 1) / AskReelJudge.BatchSize;
            var failedSoFar = 0;
            using (timings.Stage(AskStageTimings.Rerank))
            {
                judgeSummary = await Judge.JudgeAsync(
                    accountId, rerankQuery, persona, inputs, input.RerankPurpose,
                    async batch =>
                    {
                        if (batch.Failed)
                        {
                            failedSoFar++;
                            progress.Degrade(FormattableString.Invariant(
                                $"{SearchTurnDegraded.JudgePrefix}{failedSoFar}/{judgeBatches}"));
                        }

                        var accepted = new List<GatedCard>();
                        foreach (var item in batch.Inputs)
                        {
                            var verdict = batch.Results.GetValueOrDefault(item.TitleId);
                            var satisfies = verdict is { FitSource: ResultSource.Llm } ? verdict.Satisfies : null;
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

            // ── Place everything the judge did not accept outright ───────────────────────────
            using var blendStage = timings.Stage(AskStageTimings.Blend);
            GatedCard Of((Title Title, double Sim) x, AskReelGate.Placement placement) =>
                Gate(x.Title, x.Sim, PredictedOf(x.Title.Id), placed[x.Title.Id].Verdict, TagBoost(x.Title.Id), placement);

            // Unjudged cards, in retrieval order, with no why (§4.10): below every judged pick when
            // there is one, in the "not checked" tray when there is none (§4.8, D1).
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
                return await NoneFoundAsync(
                    accountId, hard, facet, intent, relaxation, eligible.Count, eligiblePoolSize, discovered.Count,
                    underBar, nearMiss, belowFloor, floor, agentReply.Degraded, ReplyAsync, degraded, timings, input,
                    emit, conflictJson, conflict?.Message, judgeOrder.Count, judgeSummary, ct, unjudged, unscoredHeld);
            }

            await ReplyAsync(pickReply);
            var judgedPicks = picks.Count;
            foreach (var card in unjudged)
            {
                picks.Add(card);
                await EmitCardAsync(card, progress, emit);
            }

            // Outside cards trail every pick, each captioned with the exclusion it trips. They are
            // shown only beside real picks — never as an answer of their own — and the bar binds
            // them like any other card: one under it joins the floor tray rather than vanishing (1e).
            var outsideAll = outside
                .Select(x => Gate(x.Title, x.Sim, PredictedOf(x.Title.Id), verdict: null, TagBoost(x.Title.Id),
                    AskReelGate.Placement.Unjudged) with
                {
                    Outside = true,
                    Why = OutsideWhy(NegationMatches(x.Title, negations)),
                })
                .ToList();
            var outsideCards = outsideAll.Where(c => AskReelGate.Clears(c.Predicted, floor)).ToList();
            belowFloor.AddRange(outsideAll
                .Where(c => !AskReelGate.Clears(c.Predicted, floor))
                .Select(c => c with { Placement = AskReelGate.Placement.BelowFloor }));
            foreach (var card in outsideCards)
            {
                await EmitCardAsync(card, progress, emit);
            }

            using var emitStage = timings.Stage(AskStageTimings.Emit);
            var final = picks.Concat(outsideCards).ToList();
            var storedCards = Stored(final, nearMiss, belowFloor, uncheckedTray: []);
            var reason = AskReelReason.Build(
                new TurnCounts(
                    Picks: judgedPicks,
                    Outside: outsideCards.Count,
                    Tray: belowFloor.Count,
                    NearMiss: nearMiss.Count,
                    Unchecked: picks.Count - judgedPicks),
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
                @unchecked = Array.Empty<object>(),
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
                SearchTurnDegraded.ProviderDefaultEffort);
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
                ReasoningEffort = SearchTurnDegraded.ProviderDefaultEffort,
            };
        }
    }

    /// <summary>
    /// A card on its way to one of the answer's places, with everything every reader of it needs.
    /// </summary>
    private sealed record GatedCard(
        Title Title,
        double Sim,
        decimal? Predicted,
        RerankResult? Verdict,
        double Blend,
        AskReelGate.Placement Placement,
        bool Outside,
        string? Why)
    {
        /// <summary>The fit, only where the MODEL produced it — a fallback number is never shown or stored.</summary>
        public double? ModelFit => Verdict is { FitSource: ResultSource.Llm } v ? v.Fit : null;

        /// <summary>The model's request verdict, persisted form; null when unjudged.</summary>
        public string? SatisfiesWire =>
            Verdict is { FitSource: ResultSource.Llm, Satisfies: { } s } ? LlmResultText.Wire(s) : null;
    }

    /// <summary>
    /// Builds a gated card. The why-line is the model's ONLY when the model wrote it: a fallback
    /// sentence ("A action pick aligned with your taste.") is never shown, never stored, never
    /// logged as model output (A-8, B-6). The blend uses the fit only when it is the model's; an
    /// unjudged card is ordered without one rather than with an invented one.
    /// </summary>
    private static GatedCard Gate(
        Title title, double sim, decimal? predicted, RerankResult? verdict, double tagBoost, AskReelGate.Placement placement)
    {
        var modelFit = verdict is { FitSource: ResultSource.Llm } v ? v.Fit : (double?)null;
        var blend = (sim * 0.32) + ((double)(predicted ?? 6m) / 10d * 0.23) + ((modelFit ?? 0d) * 0.35) + (tagBoost * 0.10);
        var why = verdict is { WhySource: ResultSource.Llm, Why: { Length: > 0 } w } ? AskReelText.StripMarkdown(w) : null;
        return new GatedCard(title, sim, predicted, verdict, blend, placement, Outside: false, why);
    }

    /// <summary>The wire card for a gated card.</summary>
    private static object CardOf(GatedCard c) =>
        Card(c.Title, c.Sim, c.Predicted, c.ModelFit, c.Why, outsideRequest: c.Outside, blend: c.Blend,
            satisfies: c.SatisfiesWire);

    /// <summary>Emits one card and records it as shown, so an abort persists it.</summary>
    private static async Task EmitCardAsync(GatedCard card, AskTurnProgress progress, Emit emit)
    {
        progress.Shown(Row(card, rank: 0, SearchTurnCardTier.Pick));
        await emit("candidate", CardOf(card));
    }

    /// <summary>
    /// The stored rows: picks and outside cards first (ranks 0..n-1), then the near-miss tray, then the
    /// floor tray, then the unchecked tray.
    /// </summary>
    private static List<TurnCard> Stored(
        IReadOnlyList<GatedCard> final, IReadOnlyList<GatedCard> nearMiss, IReadOnlyList<GatedCard> belowFloor,
        IReadOnlyList<GatedCard> uncheckedTray)
    {
        var rows = new List<TurnCard>(final.Count + nearMiss.Count + belowFloor.Count + uncheckedTray.Count);
        rows.AddRange(final.Select((c, i) => Row(c, i, SearchTurnCardTier.Pick)));
        var offset = rows.Count;
        rows.AddRange(nearMiss.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.NearMiss)));
        offset = rows.Count;
        rows.AddRange(belowFloor.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.BelowFloor)));
        offset = rows.Count;
        rows.AddRange(uncheckedTray.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.Unchecked)));
        return rows;
    }

    /// <summary>One stored row for a gated card.</summary>
    private static TurnCard Row(GatedCard c, int rank, string tier) =>
        new(
            c.Title.Id, c.Title.TmdbId, rank, c.Outside,
            Math.Round(c.Sim, 2),
            c.Predicted is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null,
            c.ModelFit is { } fit ? Math.Round((decimal)fit, 3, MidpointRounding.AwayFromZero) : null,
            Math.Round((decimal)c.Blend, 4, MidpointRounding.AwayFromZero),
            c.Why,
            ExposureWhySourceOf(c),
            BelowFloor: tier != SearchTurnCardTier.Pick,
            Tier: tier,
            Satisfies: c.SatisfiesWire,
            FitSource: c.Verdict is { } v ? LlmResultText.Name(v.FitSource) : null);

    /// <summary>
    /// The typed source of a card's why-line, assigned by the code that produced it — never inferred
    /// from "the sentence is non-empty", which is how thousands of stub sentences were logged as model
    /// output (B-6). An outside card's caption is the deterministic template; a model sentence is
    /// <c>Llm</c>; a card with no why-line says so (<c>None</c>).
    /// </summary>
    private static ExposureWhySource ExposureWhySourceOf(GatedCard c) =>
        c.Outside ? ExposureWhySource.Templater
        : c.Why is { Length: > 0 } ? ExposureWhySource.Llm
        : ExposureWhySource.None;

    /// <summary>
    /// The none-found answer: the deterministic account of the supply as the reply, the trays the
    /// judge filled (the under-bar matches, the near misses), and a terminal frame that says
    /// <c>mode: noneFound</c> with the supply report behind it.
    /// </summary>
    private async Task<TurnOutcome> NoneFoundAsync(
        Guid accountId,
        Retrieval.HardConstraints hard,
        Retrieval.RetrievalResult? facet,
        SearchIntent intent,
        int relaxation,
        int eligibleCount,
        int eligiblePoolSize,
        int discoveredCount,
        IReadOnlyList<string> underBar,
        IReadOnlyList<GatedCard> nearMiss,
        IReadOnlyList<GatedCard> belowFloor,
        decimal floor,
        bool planDegraded,
        Func<string, Task> replyAsync,
        IReadOnlyList<string> degraded,
        AskStageTimings timings,
        AskTurnInput input,
        Emit emit,
        string? conflictJson,
        string? conflictMessage,
        int judged,
        AskReelJudge.Summary? judge,
        CancellationToken ct,
        IReadOnlyList<GatedCard>? uncheckedTray = null,
        IReadOnlyList<string>? unscoredHeld = null)
    {
        uncheckedTray ??= [];
        Retrieval.SupplyReport? supply = facet?.Supply;
        if (supply is null && supplyReporter is not null && (hard.HasConcepts || hard.People.Count > 0))
        {
            try
            {
                supply = await supplyReporter.ReportAsync(accountId, hard, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The counts are the explanation, not the answer: without them the reply still says
                // nothing matched, it just cannot say how many were seen.
                logger.LogWarning(ex, "Ask Reel: the supply report failed; the none-found reply carries no counts.");
            }
        }

        var concept = hard.Concepts.Count > 0 ? hard.Concepts[0].UserWords : null;
        var shownHere = supply is { ClearingFloor: > 0 }
            ? await ShownInSupplyAsync(accountId, hard, supply, input.ShownTitleIds, floor, ct)
            : 0;
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, concept, hard.Media, underBar, nearMiss.Count, floor,
            Unchecked: uncheckedTray.Count,
            JudgedAny: judge?.JudgedAny ?? true,
            ShownHere: shownHere,
            UnscoredHeld: unscoredHeld));

        // A follow-up that changes nothing hears that it was heard, not the same paragraph again.
        text = NoneFoundCopy.Again(text, PreviousReply(input.History));

        // A collision with the account's own conditions is the likeliest reason nothing is left,
        // so it leads: "Your conditions permanently exclude comedy and romance…" before the counts.
        if (conflictMessage is { Length: > 0 })
        {
            text = $"{conflictMessage} {text}";
        }

        if (planDegraded)
        {
            text = $"{AgentReply.DegradedReply} {text}";
        }

        await replyAsync(text);
        var reason = AskReelReason.Build(
            new TurnCounts(Picks: 0, Outside: 0, Tray: belowFloor.Count, NearMiss: nearMiss.Count, UncheckedTray: uncheckedTray.Count),
            floor);
        await emit("done", new
        {
            results = Array.Empty<object>(),
            streamed = belowFloor.Count,
            kept = 0,
            droppedByFloor = belowFloor.Count,
            belowFloor = belowFloor.Select(CardOf).ToList(),
            nearMiss = nearMiss.Select(CardOf).ToList(),
            @unchecked = uncheckedTray.Select(CardOf).ToList(),
            mode = SearchTurnMode.NoneFound,
            supply = SupplyWire(supply),
            degraded = degraded.ToArray(),
            reason,
            timingsMs = input.Debug ? timings.Snapshot() : null,
            debug = input.Debug
                ? (object?)new
                {
                    intent,
                    relaxation,
                    eligibleCount,
                    eligiblePoolSize,
                    judged,
                    judgeBatches = judge?.Batches ?? 0,
                    judgeFailedBatches = judge?.FailedBatches ?? 0,
                    streamed = belowFloor.Count,
                    kept = 0,
                    droppedByFloor = belowFloor.Count,
                    nearMiss = nearMiss.Count,
                    timingsMs = timings.Snapshot(),
                }
                : null,
        });

        return new TurnOutcome(
            text,
            reason,
            Failed: false,
            FailureCode: null,
            relaxation,
            FloorRelaxed: false,
            discoveredCount,
            System.Text.Json.JsonSerializer.Serialize(intent),
            await ActiveArtifactAsync(accountId, ct),
            agent.ModelId,
            Stored([], nearMiss, belowFloor, uncheckedTray),
            conflictJson,
            SearchTurnMode.NoneFound,
            DegradedFlagsOf(degraded),
            TimingsJson(timings),
            ModelIdsJson(),
            SearchTurnDegraded.ProviderDefaultEffort);
    }

    /// <summary>
    /// The supply report as the wire carries it: every enum as its NAME. The report's
    /// <c>ConditionKnockout.Kind</c> is a <c>FilterKind</c>, and the stream's serializer writes enums
    /// as numbers, so the client — which reads <c>kind</c> as a string — rejected every <c>done</c>
    /// frame that carried a relaxation offer and left the turn spinning (acceptance D1). Projected
    /// here rather than by a global converter, so no other frame's shape moves.
    /// </summary>
    internal static object? SupplyWire(Retrieval.SupplyReport? supply) =>
        supply is null
            ? null
            : new
            {
                supply.MatchingInCatalogue,
                supply.AlreadySeen,
                supply.SavedOrDismissed,
                supply.ExcludedByConditions,
                supply.Eligible,
                supply.BelowFloor,
                supply.Unscored,
                supply.ClearingFloor,
                supply.LowVoteMatches,
                supply.MinVotes,
                supply.PredictedFloor,
                supply.KnockoutsMeasured,
                Knockouts = supply.Knockouts.Select(k => new
                {
                    Kind = k.Kind.ToString(),
                    k.Value,
                    k.ContentFilterId,
                    k.Label,
                    k.WouldUnlock,
                    Sample = k.Sample.Select(t => new { t.TitleId, t.Name, t.Year, Media = t.Media.ToString() }).ToList(),
                }).ToList(),
            };

    /// <summary>
    /// How many of the titles the supply counts as clearing the bar this conversation has already
    /// shown — counted through the same eligibility and topic predicates, then the same bar. They were
    /// excluded before anything was judged, so the reply names them as shown rather than as rejected
    /// (D4). A failure costs the sentence, never the turn.
    /// </summary>
    private async Task<int> ShownInSupplyAsync(
        Guid accountId, Retrieval.HardConstraints hard, Retrieval.SupplyReport supply,
        IReadOnlyCollection<Guid> shownIds, decimal floor, CancellationToken ct)
    {
        if (shownIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var ids = shownIds.ToList();
            var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);
            var topical = (await Retrieval.HardConstraintFilter.Apply(eligibility.EligibleTitles(accountId), db, hard)
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync(ct))
                .Where(t => Retrieval.CrowdVotes.Of(t) >= supply.MinVotes)
                .Where(t => EligibilityQueryBuilder.PassesMaturity(t, ceiling))
                .Select(t => t.Id)
                .ToList();
            if (topical.Count == 0)
            {
                return 0;
            }

            var artifactId = await ActiveArtifactAsync(accountId, ct);
            if (artifactId is not { } activeId)
            {
                return 0;
            }

            var predicted = await db.TitleScores
                .Where(x => x.AccountId == accountId && x.ModelArtifactId == activeId && topical.Contains(x.TitleId))
                .Select(x => new { x.TitleId, x.PredictedRating })
                .ToListAsync(ct);
            return predicted
                .GroupBy(x => x.TitleId)
                .Count(g => AskReelGate.Clears(g.First().PredictedRating, floor));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: counting the conversation's shown titles failed; the reply omits them.");
            return 0;
        }
    }

    /// <summary>The last thing Reel said in this conversation, without the card handles history appends.</summary>
    private static string? PreviousReply(IReadOnlyList<ChatTurn> history)
    {
        var last = history.LastOrDefault(t => t.Role == "assistant")?.Text;
        if (last is null)
        {
            return null;
        }

        var handles = last.IndexOf(AskReelHandles.HistoryMarker, StringComparison.Ordinal);
        return (handles >= 0 ? last[..handles] : last).TrimEnd();
    }

    /// <summary>
    /// The facet arm: eligible titles carrying catalogue evidence for every concept the request
    /// names, through WS-3's retrieval contract, plus the supply report behind them. A failure costs
    /// the arm, never the turn.
    /// </summary>
    private async Task<Retrieval.RetrievalResult?> FacetArmAsync(
        Guid accountId, Retrieval.HardConstraints hard, IReadOnlySet<Guid> shown, IReadOnlySet<Guid> references,
        CancellationToken ct)
    {
        if (retriever is null || !hard.HasConcepts)
        {
            return null;
        }

        try
        {
            return await retriever.RetrieveAsync(
                new Retrieval.RetrievalQuery(accountId, hard, Retrieval.SoftPreferences.None, shown.Concat(references).ToList(), ReturnCount),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel facet arm failed; discovery continues without it.");
            return null;
        }
    }

    /// <summary>
    /// The user's words for the request's concept: its first positive keyword. Read by the supply
    /// report and the none-found reply ("ninja movies"); every keyword still rides the concept's
    /// catalogue labels.
    /// </summary>
    private static string? ConceptWords(SearchIntent intent) =>
        intent.Keywords.Count > 0 ? intent.Keywords[0] : null;

    /// <summary>
    /// The discovery intent with the account's excluded GENRES added back as TMDB exclusions,
    /// deterministically. The model's own copy of the standing rules was stripped by
    /// <see cref="AskReelIntentSanitizer"/>; the pull still should not spend its forty slots on
    /// titles eligibility is about to remove.
    /// </summary>
    private static SearchIntent WithStandingGenres(SearchIntent intent, FilterRuleSet standing) =>
        standing.ExcludeGenres.Count == 0
            ? intent
            : intent with
            {
                ExcludeGenres = (intent.ExcludeGenres ?? [])
                    .Concat(standing.ExcludeGenres.Select(g => g.Trim().ToLowerInvariant()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

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
    /// already hold that no keyword names. Returns empty on any failure ⇒ the TMDB arm carries the
    /// turn alone (no regression).
    ///
    /// <para><b>The eligibility predicate below is not what makes this correct, and believing it was
    /// cost this surface real recall.</b> Postgres pushes <c>ORDER BY … &lt;=&gt; … LIMIT</c> into the
    /// HNSW index whatever order the LINQ is written in, and an HNSW scan post-filters its bounded
    /// candidate window — so with pgvector's default settings this asked for 40 eligible neighbours
    /// and got back only the eligible members of the 40 nearest titles OVERALL. Measured at 10 of a
    /// 20-row eligible set. What actually makes the pull whole is
    /// <c>hnsw.iterative_scan</c>, set on every connection by
    /// <c>HnswFilteredSearchInterceptor</c>; read that before touching either side of this query.
    /// The predicate stays because it is still the cheapest way to keep ineligible rows out.</para>
    /// </summary>
    private async Task<List<Guid>> SemanticNeighbourIdsAsync(
        Guid accountId, AgentReply agentReply, IReadOnlyList<float[]> referenceVectors, AskReelNegations negations,
        string rawQuery, CancellationToken ct)
    {
        var concept = ConceptText(agentReply, rawQuery, negations);
        if (concept.Length == 0 && referenceVectors.Count == 0)
        {
            return [];
        }

        float[] queryVec;
        try
        {
            if (concept.Length > 0)
            {
                // The OTHER half of "discovery", and the one that was still being bought on every
                // repeat. The TMDB arm has been cached per intent since F2-02 and it hits — a
                // byte-identical question re-pulls nothing from TMDB — yet the `discover` stage
                // still cost 1.5 seconds on a measured repeat. This embedding is why: a paid
                // provider call, on the critical path, for a string the process resolved moments
                // ago.
                //
                // The CONCEPT is cached, not the neighbour list, and the difference matters. The
                // kNN underneath is local, cheap, and reads the account's own catalogue — which the
                // enrichment sweep grows continuously — so running it fresh is what makes the pool
                // current rather than stale. (The 87-then-86 pool sizes the finder measured across
                // two identical turns are that catalogue moving, which is correct.) Caching the
                // resolved ids instead would have frozen a recall decision for the sake of a
                // millisecond query and skipped titles embedded since.
                //
                // The window is InterpretTtl rather than DiscoveryTtl because past ten minutes the
                // interpret cache has expired anyway, the model re-reads the question and the
                // concept can legitimately change — a longer TTL here would rarely be reachable.
                var conceptKey = ConceptVectorKey(accountId, concept, agent.ModelId);
                if (cache.TryGetValue<float[]>(conceptKey, out var remembered) && remembered is { Length: > 0 })
                {
                    queryVec = remembered;
                }
                else
                {
                    var embedded = await embeddings.EmbedAsync([concept], ct);
                    if (embedded.Length == 0 || embedded[0].Length == 0)
                    {
                        return [];
                    }

                    queryVec = embedded[0];
                    cache.Set(conceptKey, queryVec, InterpretTtl);
                }
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
        Guid accountId, string seedText, IReadOnlyList<float[]> referenceVectors, CancellationToken ct)
    {
        float[]? seed = null;
        if (seedText.Length > 0)
        {
            // The same text the semantic arm embedded a moment ago, under the same key — the seed and
            // the concept are one string now (A-9), so a second paid embedding of it is pure waste.
            var key = ConceptVectorKey(accountId, seedText, agent.ModelId);
            if (cache.TryGetValue<float[]>(key, out var remembered) && remembered is { Length: > 0 })
            {
                seed = remembered;
            }
            else
            {
                var embedded = await embeddings.EmbedAsync([seedText], ct);
                if (embedded is [{ Length: > 0 }, ..])
                {
                    seed = embedded[0];
                    cache.Set(key, seed, InterpretTtl);
                }
            }
        }

        if (seed is not null)
        {
            return BlendQueryVector(seed, referenceVectors);
        }

        if (seedText.Length > 0)
        {
            logger.LogWarning(
                "Ask Reel: the ranking seed came back empty; ranking this turn without a similarity signal.");
        }

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
    /// The account's minimum-predicted-rating content filter, clamped to 0-10. Zero means "no floor
    /// set", which is also what a malformed stored value degrades to.
    ///
    /// <para>Reads rows the turn has ALREADY loaded rather than issuing its own query: the standing
    /// rules are needed before the interpret call anyway, and two reads of the same table one turn
    /// apart could disagree if the user edits their conditions mid-turn.</para>
    /// </summary>
    private static decimal MinPredictedFloor(IReadOnlyList<ContentFilter> rows)
    {
        var raw = rows.FirstOrDefault(f => f.Kind == FilterKind.MinPredictedRating)?.Value;
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
    /// How much a title contradicts what the user's own words ruled out. Each matched genre or
    /// keyword costs one <see cref="NegationPenalty"/>. Any non-zero value routes the title into
    /// the flagged outside-request partition (<see cref="PartitionByNegation"/>) — it never reaches
    /// the screen as an ordinary pick.
    ///
    /// <para>It reads ONLY the requested half of <see cref="AskReelNegations"/>. The standing half
    /// is not a demotion at all: a title tripping one is culled before this runs, because an
    /// account-level exclusion is airtight and "shown, but flagged" is still shown.</para>
    /// </summary>
    private static double RequestedDemerit(Title title, AskReelNegations negations)
    {
        var hits = negations.RequestedGenres.Count(bad => TripsExcludedGenre(title, bad));
        hits += negations.RequestedKeywords.Count(w => w.Length > 2 && TripsKeyword(title, w));

        return Math.Min(hits * NegationPenalty, 0.66);
    }

    /// <summary>
    /// Whether a title trips one of the account's PERMANENT exclusions, under exactly the matching
    /// <see cref="TripsExcludedGenre"/> uses.
    ///
    /// <para><b>Why this exists on top of the eligibility query.</b>
    /// <c>EligibilityQueryBuilder</c> excludes a genre with <c>t.Genres.Contains(value)</c> — an
    /// exact match against the genre array, and nothing else. This matcher is deliberately richer:
    /// it also reads <c>Subgenres</c> and <c>Keywords</c> by containment, because TMDB's TV taxonomy
    /// has no Horror genre at all and an exclusion blind to those is blind to TV horror. The two
    /// therefore disagree by construction, and the gap is real on screen: a title whose
    /// <c>Genres</c> says drama and whose keywords say "black comedy" passes the SQL filter for an
    /// account that permanently excludes comedy, and then gets captioned by this pipeline as "a
    /// comedy title". It reached the outside channel under a notice stating that comedy can never
    /// appear.</para>
    ///
    /// <para>Closing it HERE rather than in the SQL is deliberate. The eligibility rule is
    /// founder-locked and its query is read by every surface in the product; widening it to
    /// keyword containment would quietly re-cut the feed, the rails and the digest on the strength
    /// of a defect found in one channel of one surface. The partition is where this turn's screen
    /// is decided, and it is the last place a standing exclusion can still be honoured.</para>
    /// </summary>
    private static bool TripsStandingExclusion(Title title, AskReelNegations negations)
    {
        if (negations.StandingGenres.Any(bad => TripsExcludedGenre(title, bad)))
        {
            return true;
        }

        return negations.StandingKeywords.Any(w => w.Length > 2 && TripsKeyword(title, w));
    }

    /// <summary>
    /// Whether a title trips one excluded genre term. TMDB's TV taxonomy has no "Horror" genre at
    /// all (American Horror Story arrives as drama/mystery/sci-fi), so an exclusion matched only
    /// against <see cref="Title.Genres"/> is blind to TV horror — the exact gap the 2026-08-10
    /// live replay caught. Subgenres and Keywords carry the truth ("anthology horror",
    /// "supernatural horror", "horror"), so the excluded term also matches those labels.
    ///
    /// <para>Matched through <see cref="NegationTerms"/>: any singular or plural form, as a WHOLE
    /// phrase inside a label — "horror" still trips "supernatural horror", and "music" no longer
    /// trips "musical" (M NEW-3).</para>
    /// </summary>
    private static bool TripsExcludedGenre(Title title, string excluded) =>
        (title.Genres is { Length: > 0 } && title.Genres.Any(g => NegationTerms.EqualsAnyForm(g, excluded)))
        || NegationTerms.AnyLabelMatches(title.Subgenres, excluded)
        || NegationTerms.AnyLabelMatches(title.Keywords, excluded);

    /// <summary>
    /// Whether a negated WORD trips a title: any form of it, as a whole phrase, in the display text,
    /// the TMDB keyword and subgenre labels, or as one of its genres — "no superheroes" trips the
    /// singular label <c>superhero</c> and the <c>superhero</c> genre alike (A-9).
    /// </summary>
    private static bool TripsKeyword(Title title, string word) =>
        (title.Genres is { Length: > 0 } && title.Genres.Any(g => NegationTerms.EqualsAnyForm(g, word)))
        || NegationTerms.AnyLabelMatches(title.Keywords, word)
        || NegationTerms.AnyLabelMatches(title.Subgenres, word)
        || NegationTerms.MatchesPhrase(title.Name, word, literalOnly: true)
        || NegationTerms.MatchesPhrase(title.Overview, word, literalOnly: true);

    /// <summary>
    /// Splits the similarity-ordered pool into the picks, the flagged "outside your request"
    /// reserve — and, before either, drops everything the account permanently excludes.
    ///
    /// <para><b>Two rules, not one, because the two sources are not the same kind of fact.</b></para>
    ///
    /// <para><b>A standing exclusion is airtight.</b> A title tripping one is removed from the pool
    /// outright and can appear in neither channel. That is the account's own permanent rule, set
    /// behind a PIN, and the product says so in as many words: "Your conditions permanently exclude
    /// comedy, so nothing you asked for there can appear." It was not true. Standing exclusions
    /// reached this method as ordinary soft negations — the interpret prompt is TOLD about them and
    /// the model echoes them back on <c>intent.ExcludeGenres</c> — so the very same turn that
    /// rendered that notice also rendered three comedy cards in the reserve beneath it, each
    /// captioned by the product as a comedy title. A notice that a filter is absolute, printed above
    /// cards the filter did not stop, is worse than no notice.</para>
    ///
    /// <para><b>A requested negation is soft</b>, and that is the behaviour this method was written
    /// for: "no horror please" drops contradicting titles whenever the clean set reaches
    /// <see cref="MinResults"/>, and otherwise tops up only to that floor — flagged, ordered after
    /// every clean pick, never silently mixed in. An empty screen is not a better answer than a
    /// labelled near-miss when the user's constraint was a preference they expressed in passing.</para>
    ///
    /// <para>The cull can leave a turn with less to show, and that is the correct outcome: it
    /// removes exactly the titles the account has said it never wants to see.</para>
    /// </summary>
    internal static (List<(Title Title, double Sim)> Ranked, List<(Title Title, double Sim)> Outside) PartitionByNegation(
        IReadOnlyList<(Title Title, double Sim)> scoredPool, AskReelNegations negations)
    {
        var permitted = negations.HasStanding
            ? scoredPool.Where(x => !TripsStandingExclusion(x.Title, negations)).ToList()
            : [.. scoredPool];
        var contradicts = permitted.Where(x => RequestedDemerit(x.Title, negations) > 0).ToList();
        var clean = permitted.Where(x => RequestedDemerit(x.Title, negations) == 0).ToList();
        var ranked = clean.Take(ReturnCount).ToList();
        var outside = ranked.Count < MinResults && contradicts.Count > 0
            ? contradicts.Take(MinResults - ranked.Count).ToList()
            : [];
        return (ranked, outside);
    }

    /// <summary>
    /// The exclusions a title actually trips, phrased for the outside-request caption ("a horror
    /// title", "one that matches ‘ghosts’") and tagged with where each rule came from. Mirrors
    /// <see cref="RequestedDemerit"/> and <see cref="TripsStandingExclusion"/> exactly, so a title
    /// in either channel always has at least one match to name.
    ///
    /// <para>Standing matches come first, because they are the stronger claim about why the card is
    /// where it is — and in practice they are unreachable from the outside channel, since
    /// <see cref="PartitionByNegation"/> culls those titles before the partition. They are still
    /// computed, because a caption that could silently fall back to "you asked to skip" for a rule
    /// the user never typed is exactly the defect this distinction closes.</para>
    /// </summary>
    internal static IReadOnlyList<NegationMatch> NegationMatches(Title title, AskReelNegations negations)
    {
        var matches = new List<NegationMatch>();

        void Collect(IReadOnlyList<string> genres, IReadOnlyList<string> keywords, bool standing)
        {
            matches.AddRange(genres
                .Where(bad => TripsExcludedGenre(title, bad))
                .Select(bad => new NegationMatch($"a {bad.ToLowerInvariant()} title", standing)));
            matches.AddRange(keywords
                .Where(w => w.Length > 2 && TripsKeyword(title, w))
                .Select(w => new NegationMatch($"one that matches ‘{w}’", standing)));
        }

        Collect(negations.StandingGenres, negations.StandingKeywords, standing: true);
        Collect(negations.RequestedGenres, negations.RequestedKeywords, standing: false);
        return matches;
    }

    /// <summary>
    /// The honest deterministic caption for an outside-request card. Replaces the LLM why — which
    /// cheerfully admitted violations ("exactly what they want to avoid") — with a plain statement
    /// of the first exclusion the title trips.
    ///
    /// <para><b>The tail names the right authority.</b> "Which you asked to skip" is a quotation of
    /// the user, and it was being printed for exclusions the user never made: on one measured
    /// screen, seven of eleven outside cards told a user who had asked for a 90s romantic comedy
    /// that they had asked to skip horror — an inference of the model's, phrased as their own
    /// words. A standing rule gets "which your conditions exclude", which is the other true
    /// sentence and is also actionable, because Conditions is a screen the user can open.</para>
    /// </summary>
    internal static string OutsideWhy(IReadOnlyList<NegationMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "Closest match we could find, but it's outside what you asked for — which you asked to skip.";
        }

        var first = matches[0];
        var tail = first.Standing ? "which your conditions exclude" : "which you asked to skip";
        return $"Closest match we could find, but it's {first.Phrase} — {tail}.";
    }

    /// <summary>
    /// The text the ranking query vector is built from — the interpreted POSITIVE signal only
    /// (free text, keywords, genres), never the raw sentence and never the agent's prose reply.
    /// An embedding model has no notion of "not": "a thriller, no ghosts" and "a thriller with
    /// ghosts" land close together in vector space, so ranking against the raw turn scores the
    /// rejected concept HIGHEST on the similarity used to pick what to show. Falls back to the raw
    /// text only when the intent carries no positive signal at all, because a bad seed still beats
    /// an empty one.
    /// </summary>
    internal static string RankingSeedText(AgentReply agentReply, string rawQuery, AskReelNegations negations)
    {
        var intent = agentReply.Intent;

        // Keywords and genres are the only fields guaranteed to hold the positive reading: the
        // interpreter strips rejected terms out of them by contract, and AskReelIntentSanitizer
        // removes whatever slipped through. FreeText is deliberately NOT used when they are
        // available — the heuristic path sets it to the raw query verbatim, negations and all.
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

        // Nothing structured survived. The fallback is the POSITIVE half of the free text or the raw
        // sentence — every negation cue, every word it governs and every word the user ruled out
        // removed — so "no superheroes please" seeds nothing rather than seeding superheroes.
        var negated = negations.RequestedGenres.Concat(negations.RequestedKeywords)
            .Concat(intent.ExcludeGenres ?? []).Concat(intent.ExcludeKeywords ?? []);
        var source = !string.IsNullOrWhiteSpace(intent.FreeText) ? intent.FreeText.Trim() : rawQuery;
        return AskReelIntentSanitizer.PositiveText(source, negated);
    }

    /// <summary>
    /// The text the semantic arm embeds — the SAME positive reading as the ranking seed (A-9, C-6).
    /// It used to prepend the model's reply and the raw free text, so the retrieval vector carried
    /// "…while avoiding comedy and romance" and, on a failed read, the canned sentence itself.
    /// </summary>
    internal static string ConceptText(AgentReply agentReply, string rawQuery, AskReelNegations negations) =>
        RankingSeedText(agentReply, rawQuery, negations);

    /// <summary>
    /// Streams a person's filmography: chronological (newest first), already-watched entries kept
    /// and badged, the top slice scored for the predicted-rating badge — but never re-ordered or
    /// culled by LLM fit, because the user asked for the WHOLE list, not a recommendation.
    /// </summary>
    private async Task<TurnOutcome> StreamFilmographyAsync(
        Guid accountId, string? assistantText, SearchIntent intent, List<Title> eligible,
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

        // Chronological, not ranked — so the stored rank is the position in the filmography and
        // there is no blend to record. Replaying it re-establishes the same list in the same order.
        var stored = ordered
            .Select((t, index) => new TurnCard(
                t.Id, t.TmdbId, index, OutsideRequest: false,
                Similarity: null,
                scores.TryGetValue(t.Id, out var predicted) ? Math.Round(predicted, 2, MidpointRounding.AwayFromZero) : null,
                LlmFit: null, Blend: 0m, Why: null, ExposureWhySource.Generic))
            .ToList();
        return TurnOutcome.Answered(assistantText, reason, stored);
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

    private async Task<TurnOutcome> StreamLexicalFallbackAsync(Guid accountId, string query, Emit emit, CancellationToken ct)
    {
        var hits = await lexical.SearchAsync(accountId, query, ReturnCount, ct);
        var cards = new List<object>();
        var stored = new List<TurnCard>();
        foreach (var hit in hits)
        {
            var similarity = Math.Round(hit.MatchScore, 3);
            var card = Card(hit.Title, similarity: similarity, predicted: null);
            cards.Add(card);
            // Stored in the SAME order as the wire, so a lexical turn replays as itself rather
            // than as an empty transcript. It has no blend — nothing blended it — and zero is the
            // honest value for a rank that came from the match score alone.
            stored.Add(new TurnCard(
                hit.Title.Id, hit.Title.TmdbId, stored.Count, OutsideRequest: false,
                similarity, PredictedRating: null, LlmFit: null, Blend: 0m,
                Why: null, ExposureWhySource.Generic));
            await emit("candidate", card);
        }

        var reason = cards.Count == 0 ? "no matches" : "matched on concepts & keywords";
        await emit("done", new { results = cards, reason });
        return TurnOutcome.Answered(assistantText: null, reason, stored);
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
    /// The identity of one INTERPRETATION: everything that changes what the interpret call is asked,
    /// and nothing that does not.
    ///
    /// <para>The account is on the key and is not optional. This is a process-wide
    /// <see cref="IMemoryCache"/> in a multi-tenant app, and the cached value is a REPLY written
    /// against one person's taste summary — handing it to somebody else asking the same thing would
    /// be a cross-tenant leak in a product whose entire premise is per-user. The standing-filter
    /// summary rides in whole rather than hashed: it is already short, sorted and deterministic
    /// (<see cref="AskReelStandingFilters.Describe"/>), and a hash would trade readability for a
    /// collision class nobody could debug.</para>
    ///
    /// <para><b>The CONVERSATION is on the key, and leaving it off was a defect that shipped.</b>
    /// The interpret prompt is handed the last turns of the transcript, so the interpretation is a
    /// function of the question AND the thread it was asked in — not of the question alone. An
    /// earlier version of this key omitted the history on the reasoning that it "moves on every
    /// turn", which is true and beside the point: a key that omits an input does not merely miss
    /// less often, it answers a DIFFERENT question confidently for the whole TTL. The observed
    /// failure was exactly that. A thread where the user had said "only shows" resolved the next
    /// sentence, "thrillers, no horror", to <c>MediaTypes: [Show]</c> — correct, history shaped it.
    /// The same user then pressed + New, typed the identical sentence into a thread with no history
    /// at all, and was served the first thread's interpretation: same <c>IntentJson</c>, a
    /// byte-identical reply promising "shows", and an all-TV answer to a question that had asked for
    /// nothing of the kind. <c>AskReelConversationIsolationTests</c> is that turn, pinned.</para>
    ///
    /// <para>The <b>presented handles</b> are on the key for the same reason: they reach the prompt
    /// as the numbered list "the second one" resolves against, so they change what a pronoun in the
    /// question means. They ride as <see cref="AskReelHandles.Digest"/> — every handle and its title
    /// id, in prompt order — and the conversation's whole no-repeat set rides beside them as a digest
    /// of title ids, because it changes what the model is told not to return.</para>
    ///
    /// <para>Both ride as a SHA-256 digest rather than in full, because a transcript is unbounded
    /// and a cache key is not; the standing-filter summary still rides whole because it is short,
    /// sorted and deterministic (<see cref="AskReelStandingFilters.Describe"/>) and a hash would
    /// trade readability for a collision class nobody could debug. "No history" and "nothing shown"
    /// are their own literal tokens rather than the digest of an empty string, so the commonest
    /// context in the product — a brand-new thread — is a value a person can read in a dump.</para>
    ///
    /// <para><b>What this deliberately does NOT cost.</b> The reason the cache exists is a
    /// byte-identical repeat: one question asked twice inside the window used to re-buy the
    /// interpret call and then re-pull TMDB behind a drifted intent, ~21 s against ~2.5 s. A repeat
    /// in the SAME context still hits, because the same transcript digests to the same value. What
    /// no longer hits is a repeat in a different context, which was never the same question.
    /// Note also what is NOT used here: the conversation id. It would close the leak and throw the
    /// win away, because two threads with identical (empty) transcripts would stop sharing an
    /// entry. The key is the context, not the container.</para>
    ///
    /// <para>The remaining prompt input that is not on the key is the taste summary, and that is a
    /// judgement rather than an oversight: it is one person's, the account is already on the key,
    /// and it can only move inside a ten-minute <see cref="InterpretTtl"/> if that person rates
    /// something mid-conversation. The cost of being wrong is one slightly stale reply for one
    /// account for under ten minutes; the cost of keying on it is a miss on every turn after any
    /// rating. Revisit it if the TTL grows.</para>
    /// </summary>
    internal static string InterpretKey(
        Guid accountId,
        string query,
        string? standingSummary,
        IReadOnlyList<MediaType> mediaFilter,
        string modelId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<PresentedCard> presented,
        IReadOnlyCollection<Guid> shownTitleIds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"askreel:intent:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{NormalizeQuery(query)}|" +
            $"{string.Join(',', mediaFilter.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal))}|" +
            $"{standingSummary}|h={HistoryDigest(history)}|p={AskReelHandles.Digest(presented)}|s={ShownDigest(shownTitleIds)}");

    /// <summary>
    /// A stable digest of the transcript the interpreter is handed — every turn, in order, role and
    /// text.
    ///
    /// <para><b>Order is preserved and never sorted.</b> "only shows" then "thrillers, no horror" is
    /// a different conversation from the reverse, and the model reads them as a sequence.</para>
    ///
    /// <para><b>Every field is length-prefixed</b> before it is hashed, which is the whole
    /// difference between a digest and a concatenation. Without it <c>["ab", ""]</c> and
    /// <c>["a", "b"]</c> flatten to the same bytes, and two different transcripts share a cache
    /// entry again — the identical defect, one layer down and far harder to see. The unit separators
    /// are control characters no transcript contains, and the lengths are formatted invariantly like
    /// every other number this backend writes.</para>
    ///
    /// <para>The WHOLE history is digested, not the slice the prompt keeps (the adapter takes the
    /// last 8, and the endpoint reads at most 8 rows, so today they are the same list). Digesting
    /// the superset can only ever cost an extra miss where two transcripts differ solely in turns
    /// the prompt would have dropped; digesting a guess at the adapter's window would cost a leak
    /// the day that window changes, and this file cannot see that constant.</para>
    /// </summary>
    internal static string HistoryDigest(IReadOnlyList<ChatTurn> history)
    {
        if (history.Count == 0)
        {
            // Its own token, not the digest of an empty string: "a thread with no history" is the
            // most common context in the product and is worth being able to read in a cache dump.
            return "none";
        }

        var sb = new StringBuilder();
        foreach (var turn in history)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{turn.Role.Length}\u001f{turn.Role}\u001f{turn.Text.Length}\u001f{turn.Text}\u001e"));
        }

        return Digest(sb.ToString());
    }

    /// <summary>
    /// A stable digest of the conversation's no-repeat set.
    ///
    /// <para>Sorted and de-duplicated first, because the set is what the prompt is a function of and
    /// the endpoint's <c>Distinct()</c> read carries no ordering guarantee from Postgres — keying on
    /// the raw row order would turn one context into arbitrarily many keys and quietly cost the
    /// repeat hit. Keyed on the TITLE id, like the set itself (B-16).</para>
    /// </summary>
    internal static string ShownDigest(IReadOnlyCollection<Guid> shownTitleIds)
    {
        if (shownTitleIds.Count == 0)
        {
            return "none";
        }

        return Digest(string.Join(
            ',',
            shownTitleIds.Distinct().OrderBy(id => id).Select(id => id.ToString("N"))));
    }

    /// <summary>
    /// SHA-256, lower-case hex. Not a security boundary — it is a fixed-width, collision-resistant
    /// stand-in for an unbounded string in a cache key, chosen over <c>string.GetHashCode</c>
    /// (randomised per process, so a key would not survive a restart and could not be reasoned about
    /// across a fleet) and over the raw text (a transcript has no length bound).
    /// </summary>
    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
    internal static string FitKey(Guid accountId, Guid titleId, string qnorm, string modelId) =>
        AskReelJudge.FitKey(accountId, titleId, qnorm, modelId);

    /// <summary>
    /// The semantic arm's concept-embedding key: the account and the exact text the provider would
    /// be paid to embed.
    ///
    /// <para>The CONCEPT TEXT rather than the intent, because it is literally the provider's
    /// argument: two intents that render the same concept are the same call, and one that renders a
    /// different concept is a different vector however similar the intents look.</para>
    ///
    /// <para>The ACCOUNT is on it for the same reason it is on <see cref="InterpretKey"/>, even
    /// though an embedding is a pure function of its text and could in principle be shared: this is
    /// a process-wide <see cref="IMemoryCache"/> in a multi-tenant app, the concept is assembled
    /// from a reply written against one person's taste, and the cost of keeping the boundary is one
    /// duplicate embedding in the rare case two tenants type a byte-identical concept. Cheap
    /// insurance beats a tenancy argument that has to be re-made every time someone reads this.</para>
    /// </summary>
    internal static string ConceptVectorKey(Guid accountId, string concept, string modelId) => string.Create(
        CultureInfo.InvariantCulture,
        $"askreel:concept:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{NormalizeQuery(concept)}");
}
