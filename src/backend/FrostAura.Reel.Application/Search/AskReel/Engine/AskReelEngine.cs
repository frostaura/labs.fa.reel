using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TurnCard = FrostAura.Reel.Application.Search.LiveSearchExpansionService.TurnCard;
using TurnOutcome = FrostAura.Reel.Application.Search.LiveSearchExpansionService.TurnOutcome;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// Ask Reel v2 (§4): plan → execute → judge → rank → compose → persist, behind <c>ASK_ENGINE=v2</c>.
/// The endpoint's lifecycle — validation, the rows written before the stream, idempotency, the
/// keepalive, abort handling and the final write — is shared with v1; only the turn itself is new.
///
/// <para><b>At most three model calls on the critical path</b>: the plan, then either the judge
/// batches (concurrent, one round trip deep) or the ItemQuestion answer. Nothing a model returns is
/// fed back to another model except the facts an answer is written from.</para>
///
/// <para><b>Grounded by construction.</b> Every card comes from this turn's retrieval (eligible at
/// the moment it was retrieved, re-checked by the retriever's own eligibility), and a pick is a card
/// the judge said IS what was asked for and that clears the account's bar. Every sentence of a
/// search turn's reply is a template filled from counts the server holds.</para>
/// </summary>
public sealed class AskReelEngine(
    IReelDbContext db,
    IAskReelPlanner planner,
    IRequestJudge judge,
    IRequestJudgeModel judgeModel,
    IAskReelAnswerer answerer,
    TitleFacts facts,
    IAskReelRetriever retriever,
    ISupplyReporter supplyReporter,
    EligibilityQueryBuilder eligibility,
    OnDemandScorer scorer,
    ExposureLog exposures,
    FeatureVectorBuilder featureBuilder,
    PersonalisationContext personalisation,
    ITurnComposer composer,
    IConceptResolver resolver,
    FilterCostMeter costMeter,
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    ConceptEmbeddings conceptEmbeddings,
    ILogger<AskReelEngine> logger)
{
    /// <summary>
    /// How long a mood request waits for its text's embedding before retrieval (§4.7: an uncached text
    /// takes 1.2–2.7 s, while the semantic arm itself waits only 1.0 s). See <see cref="WarmMoodAsync"/>.
    /// </summary>
    public static readonly TimeSpan MoodEmbedBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// How long one account's persona is reused across turns. Building it reads the whole taste state
    /// (0.4–1.8 s measured on the founder snapshot, all of it ahead of the plan call on the critical
    /// path); it steers the planner's reading and the judge's fit, never eligibility, so a rating made
    /// mid-conversation reaching it ten minutes late changes no card's right to be shown.
    /// </summary>
    public static readonly TimeSpan PersonaTtl = TimeSpan.FromMinutes(10);

    /// <summary>How many candidates the executor asks retrieval for.</summary>
    public const int RetrievalWant = 40;

    /// <summary>The most TMDB-only titles one turn hydrates and admits (each is a TMDB round trip).</summary>
    public const int MaxProvisional = 6;

    /// <summary>
    /// How long the provisional titles' hydration may take. It runs beside the first judge pass, so
    /// it costs the turn nothing up to about that pass's length; a title still hydrating at the
    /// deadline sits this turn out (its summary row is kept, and later turns find it in the catalogue).
    /// </summary>
    public static readonly TimeSpan ProvisionalBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>The heartbeat cadence the endpoint keeps (§4.9: at least every 5 s).</summary>
    public static readonly TimeSpan PhaseHeartbeat = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>One card as placed: where it goes, and everything the wire and the row need.</summary>
    private sealed record Placed(
        Title Title,
        Candidate? Candidate,
        JudgeVerdict Verdict,
        decimal? Predicted,
        double Blend,
        string Tier,
        int Rank = -1);

    /// <summary>Per-turn working state, threaded through the stages.</summary>
    private sealed class Turn(LiveSearchExpansionService.AskTurnInput input, LiveSearchExpansionService.Emit emit)
    {
        public LiveSearchExpansionService.AskTurnInput Input { get; } = input;

        public LiveSearchExpansionService.Emit Emit { get; } = emit;

        public AskStageTimings Timings { get; } = new();

        public List<string> Degraded { get; } = [];

        public List<LlmCallMeta> Calls { get; } = [];

        public Dictionary<string, string> Models { get; } = new(StringComparer.Ordinal);

        public List<Placed> Shown { get; } = [];

        public Guid AccountId { get; init; }

        public decimal Floor { get; set; }

        public FilterRuleSet Standing { get; set; } = FilterRuleSet.FromRows([]);

        public MediaScope MediaFilter { get; set; }

        public string? ConflictJson { get; set; }

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public bool FirstCardMarked { get; set; }

        /// <summary>The viewer's persona line, built once for the planner and reused by the judge.</summary>
        public string? Persona { get; set; }

        /// <summary>
        /// Stored-only flags (<see cref="SearchTurnDegraded.ArmPrefix"/>): written to the turn's
        /// <c>DegradedFlags</c> so a thin retrieval is a query, never sent to the client — the reply
        /// and the cards are what they are, and a skipped arm is not a degraded answer (§4.10).
        /// </summary>
        public List<string> Notes { get; } = [];

        public void Degrade(string flag)
        {
            if (!Degraded.Contains(flag, StringComparer.Ordinal))
            {
                Degraded.Add(flag);
            }

            Input.Progress?.Degrade(flag);
        }

        public void Note(string flag)
        {
            if (!Notes.Contains(flag, StringComparer.Ordinal))
            {
                Notes.Add(flag);
            }

            Input.Progress?.Degrade(flag);
        }

        /// <summary>Everything the turn record stores: the client-facing flags, then the stored-only notes.</summary>
        public string? StoredFlags => Degraded.Count + Notes.Count == 0 ? null : string.Join(',', Degraded.Concat(Notes));
    }

    public async Task<TurnOutcome> StreamAsync(
        LiveSearchExpansionService.AskTurnInput input, Guid accountId, LiveSearchExpansionService.Emit emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(emit);
        var turn = new Turn(input, emit) { AccountId = accountId };
        input.Progress?.Attach(turn.Timings, "{}", null);

        var filterRows = await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync(ct);
        turn.Standing = FilterRuleSet.FromRows(filterRows);
        turn.Floor = Floor(filterRows);
        var mediaFilter = MediaScopes.From(input.MediaFilter);
        turn.MediaFilter = mediaFilter;
        var state = await LoadStateAsync(input, ct);

        await PhaseAsync(turn, "planning", "Reading your request");
        PlanResult plan;
        using (turn.Timings.Stage(AskStageTimings.Plan))
        {
            plan = await PlanAsync(turn, state, mediaFilter, ct);
        }

        if (plan.Failure is { Refusal: true } refusal)
        {
            return await RefusedAsync(turn, plan, refusal, state, ct);
        }

        plan = plan.Failure is null ? PlanSanitizer.Clean(plan, input.Message, state, turn.Standing) : plan;
        var next = ConversationStateReducer.Apply(state, plan, mediaFilter);

        await turn.Emit("plan", new
        {
            act = plan.Act.ToString(),
            concepts = next.Hard.Concepts.Select(c => c.UserWords).ToList(),
            people = next.Hard.People.Select(p => new { name = p.Name, role = p.Role.ToString() }).ToList(),
            references = plan.References.Select(r => r.ToString()).ToList(),
            media = next.Hard.Media.ToString(),
            source = plan.Source.ToString(),
        });

        if (plan.ClarifyQuestion is { Length: > 0 } question)
        {
            return await ClarifyAsync(turn, plan, next with { ClarifiedOnce = true }, question, plan.ClarifyOptions, ct);
        }

        return plan.Act switch
        {
            TurnAct.Chitchat => await AnswerAsync(turn, plan, next, TurnComposer.ChitchatReply, null, ct),
            TurnAct.ItemQuestion => await ItemQuestionAsync(turn, plan, state, next, ct),
            _ => await SearchAsync(turn, plan, next, ct),
        };
    }

    // ── Plan ────────────────────────────────────────────────────────────────────────────────

    private async Task<PlanResult> PlanAsync(Turn turn, ConversationState state, MediaScope mediaFilter, CancellationToken ct)
    {
        if (!planner.IsAvailable)
        {
            turn.Degrade(SearchTurnDegraded.NoModel);
            return FallbackPlans.Keyless(turn.Input.Message, state);
        }

        var accountId = turn.AccountId;
        var contextClock = Stopwatch.StartNew();
        var persona = await PersonaAsync(accountId, ct);
        turn.Timings.Mark(AskStageTimings.PlanContext, contextClock.Elapsed.TotalMilliseconds);
        var tasteSummary = persona.Declined is { Length: > 0 } declined ? $"{persona.Taste}. {declined}" : persona.Taste;
        turn.Persona = persona.Taste.Length <= 300 ? persona.Taste : persona.Taste[..300];

        var plan = await planner.PlanAsync(
            new PlanInput(
                turn.Input.Message,
                state,
                turn.Input.History.TakeLast(6).ToList(),
                new StandingConditionsView(AskReelStandingFilters.Describe(turn.Standing)),
                tasteSummary,
                mediaFilter),
            ct);
        turn.Calls.Add(plan.Meta);
        turn.Models["plan"] = plan.Meta.Model ?? planner.ModelId;
        if (plan.Failure is { Refusal: false } failure)
        {
            turn.Degrade(SearchTurnDegraded.Plan);
            if (failure.Code is { Length: > 0 } code)
            {
                turn.Degrade(SearchTurnDegraded.PlanCausePrefix + code);
            }

            return FallbackPlans.CarriedForward(plan, turn.Input.Message, state);
        }

        return plan;
    }

    private async Task<TurnOutcome> RefusedAsync(Turn turn, PlanResult plan, PlanFailure refusal, ConversationState state, CancellationToken ct)
    {
        // §4.10: a spent allowance or an open breaker is a coded refusal before any work. The reply
        // is the cause's own copy (it quotes nobody and promises nothing), and the turn is Failed with
        // the code a client and the status page can act on.
        var code = refusal.Kind switch
        {
            AgentFailureKind.BudgetSpent => "ask_budget_spent",
            AgentFailureKind.ProviderUnavailable => "provider_unavailable",
            _ => refusal.Code ?? "provider_refused",
        };
        turn.Degrade(SearchTurnDegraded.Plan);
        if (refusal.Code is { Length: > 0 } cause)
        {
            turn.Degrade(SearchTurnDegraded.PlanCausePrefix + cause);
        }

        var reply = Application.Llm.LlmFailureCodes.DegradedReply(refusal.Kind, refusal.Code);
        await turn.Emit("degraded", new { what = turn.Degraded.ToArray() });
        await ReplyAsync(turn, reply, degraded: true);
        await DoneAsync(turn, plan, mode: null, results: [], trays: [], supply: null, reason: null, answer: null, clarify: null, failed: true);
        return Outcome(turn, plan, state with { Version = turn.Input.AnswerNumber }, reply, null, mode: null) with
        {
            Failed = true,
            FailureCode = code,
        };
    }

    // ── Clarify, chitchat and item questions ────────────────────────────────────────────────

    private async Task<TurnOutcome> ClarifyAsync(
        Turn turn, PlanResult plan, ConversationState next, string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        _ = ct;
        var composed = composer.Compose(Context(turn, plan, FinalMode.Clarify) with { ClarifyQuestion = question });
        await DegradedFrameAsync(turn);
        // The `clarify` frame IS the reply on this turn: no `assistant-message` follows it, or a
        // client that renders both shows the question twice (docs/ask-reel-turns.md, "Frames").
        turn.Input.Progress?.Replied(composed.Reply, degraded: false);
        await turn.Emit("clarify", new { question = composed.Reply, options });
        var clarify = new { question = composed.Reply, options };
        await DoneAsync(turn, plan, SearchTurnMode.Clarify, [], [], null, null, null, clarify);
        return Outcome(turn, plan, next with { Version = turn.Input.AnswerNumber }, composed.Reply, null, SearchTurnMode.Clarify);
    }

    private async Task<TurnOutcome> AnswerAsync(
        Turn turn, PlanResult plan, ConversationState next, string text, TitleFactSheet? about, CancellationToken ct)
    {
        _ = ct;
        var handle = about is null
            ? null
            : next.LastPresented.FirstOrDefault(p => p.TitleId == about.TitleId)?.Handle.ToString();
        var composed = composer.Compose(Context(turn, plan, FinalMode.Answer) with { Answer = text, AnswerTitle = about?.Name });
        // Flags first, so the reply they describe is marked as it lands; then the `answer` frame,
        // which IS the reply on this turn — no `assistant-message` follows it (see ClarifyAsync).
        await DegradedFrameAsync(turn);
        turn.Input.Progress?.Replied(composed.Reply, SearchTurnDegraded.PlanFailed(string.Join(',', turn.Degraded)));
        await turn.Emit("answer", new { text = composed.Reply, handle, titleId = about?.TitleId });
        await DoneAsync(turn, plan, SearchTurnMode.Answer, [], [], null, null, composed.Reply, null);
        return Outcome(turn, plan, next with { Version = turn.Input.AnswerNumber }, composed.Reply, null, SearchTurnMode.Answer);
    }

    private async Task<TurnOutcome> ItemQuestionAsync(
        Turn turn, PlanResult plan, ConversationState state, ConversationState next, CancellationToken ct)
    {
        var tools = plan.Tools.Select(t => t.Tool).ToHashSet();

        // "How many ninja movies are left?" — the supply tool, counts only.
        if (tools.Contains(AskTool.Supply) && plan.References.Count == 0)
        {
            // The shelf the question names ("how many ninja movies…"), read as a request of its own;
            // a question that names none ("how many are left?") counts the conversation's current one.
            var delta = plan.Delta;
            var hard = delta.Concepts.Count > 0 || delta.Genres.Count > 0 || delta.People.Count > 0
                ? ConversationStateReducer.Apply(state, plan with { Act = TurnAct.NewRequest }, turn.MediaFilter).Hard
                : state.Hard;
            await PhaseAsync(turn, "searching", "Counting what's left");
            var report = await supplyReporter.ReportAsync(turn.AccountId, hard, turn.Input.ShownTitleIds, ct);
            return await AnswerAsync(turn, plan, next, SupplyAnswer(report, hard), null, ct);
        }

        // "What are my conditions?" — read-only, with what each one is costing (§4.4 conditions(),
        // WS-3 CR-2b: the single-pass FilterCostMeter, the same numbers the Conditions editor shows).
        if (tools.Contains(AskTool.Conditions) && plan.References.Count == 0)
        {
            await PhaseAsync(turn, "searching", "Counting what your conditions leave");
            FilterCostReport? cost = null;
            if (turn.Standing.Rules().Count > 0)
            {
                try
                {
                    cost = await costMeter.MeasureAsync(turn.AccountId, turn.Standing, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Ask Reel v2: the conditions cost could not be measured; answering with the rules alone.");
                }
            }

            return await AnswerAsync(turn, plan, next, ConditionsAnswer(turn.Standing, cost), null, ct);
        }

        var titleId = ConversationStateReducer.ResolveHandles(plan.References, state.LastPresented).FirstOrDefault();
        if (titleId == Guid.Empty && plan.Delta.ReferenceNames.Count > 0)
        {
            titleId = await FindByNameAsync(plan.Delta.ReferenceNames[0], ct) ?? Guid.Empty;
        }

        if (titleId == Guid.Empty)
        {
            // Nothing to answer about: asked back, deterministically, without a search.
            var onScreen = state.LastPresented.Count;
            var ask = onScreen == 0
                ? "There's nothing on screen yet for me to answer about — which title do you mean?"
                : "Which title do you mean? Name it, or point at a card by its number.";
            return await ClarifyAsync(turn, plan, next, ask, [], ct);
        }

        await PhaseAsync(turn, "answering", "Looking it up");
        var sheet = await facts.GetAsync(turn.AccountId, titleId, ct);
        if (sheet is null)
        {
            return await AnswerAsync(turn, plan, next, "I don't have that title's details to answer from.", null, ct);
        }

        string text;
        if (answerer.IsAvailable)
        {
            AnswerResult answer;
            using (turn.Timings.Stage(AskStageTimings.Answer))
            {
                answer = await answerer.AnswerAsync(new AnswerInput(plan.Question ?? turn.Input.Message, sheet), ct);
            }

            turn.Calls.Add(answer.Meta);
            turn.Models["answer"] = answer.Meta.Model ?? answerer.ModelId;
            if (answer.Text is { Length: > 0 } written)
            {
                text = written;
            }
            else
            {
                turn.Degrade("answer");
                text = FactLine(sheet);
            }
        }
        else
        {
            turn.Degrade(SearchTurnDegraded.NoModel);
            text = FactLine(sheet);
        }

        return await AnswerAsync(turn, plan, next, text, sheet, ct);
    }

    /// <summary>The facts alone, as a sentence — what an answer says when no model wrote one.</summary>
    internal static string FactLine(TitleFactSheet f)
    {
        var parts = new List<string>(4);
        var noun = f.Media == MediaType.Show ? "show" : "film";
        var genres = f.Genres.Count > 0 ? string.Join(", ", f.Genres.Take(3)) + " " : string.Empty;
        parts.Add(f.Year is { } y
            ? FormattableString.Invariant($"{f.Name} ({y}) is a {genres}{noun}")
            : $"{f.Name} is a {genres}{noun}");
        if (f.Directors.Count > 0)
        {
            parts.Add($"directed by {string.Join(" and ", f.Directors.Take(2))}");
        }

        if (f.Certification is { Length: > 0 } cert)
        {
            parts.Add($"rated {cert}");
        }

        if (f.RuntimeMinutes is { } minutes)
        {
            parts.Add(FormattableString.Invariant($"{minutes} minutes long"));
        }

        return string.Join(", ", parts) + ". That's what I know about it — I can't say more than that.";
    }

    /// <summary>
    /// "What are my conditions?" (§4.4 <c>conditions()</c>): the rules, the pool they leave, and the
    /// rules costing the most, each with how many titles relaxing it ALONE would open — marginals,
    /// which do not sum, so they are never added up. With no measurement, the rules alone.
    /// </summary>
    internal static string ConditionsAnswer(FilterRuleSet standing, FilterCostReport? cost)
    {
        var described = AskReelStandingFilters.Describe(standing);
        if (described is not { Length: > 0 })
        {
            return "You have no standing conditions set, so every unseen title in the catalogue is open to you.";
        }

        var text = $"Your standing conditions: {described}.";
        if (cost is not { } report)
        {
            return text;
        }

        text += FormattableString.Invariant(
            $" They leave {report.Eligible:N0} unseen {(report.Eligible == 1 ? "title" : "titles")} open to you, out of {report.Total:N0} in the catalogue.");
        var costly = report.ByRule
            .Where(r => r.Marginal > 0)
            .OrderByDescending(r => r.Marginal)
            .Take(FilterCostMeter.SuggestionCount)
            .Select(r => FormattableString.Invariant(
                $"{Retrieval.SupplyReporter.LabelFor(new FilterRule(Enum.TryParse<FilterKind>(r.Kind, out var kind) ? kind : default, r.Value))} (+{r.Marginal:N0})"))
            .ToList();
        if (costly.Count > 0)
        {
            text += $" Relaxing one alone would open the most for: {string.Join(", ", costly)}.";
        }

        return text;
    }

    /// <summary>
    /// "How many X are left?" from the supply alone (§4.4 <c>supply</c>). With a bar set, "left" is
    /// what clears it and has not been shown here; titles with no score yet are counted separately,
    /// because a set bar holds them back. With no bar, every eligible unshown title is left.
    /// </summary>
    internal static string SupplyAnswer(SupplyReport report, HardConstraints hard)
    {
        var topic = hard.Concepts.Count > 0 ? hard.Concepts[0].UserWords + " " : string.Empty;
        var noun = hard.Media switch { MediaScope.Movies => "movies", MediaScope.Shows => "shows", _ => "titles" };
        var barred = report.PredictedFloor is > 0m;
        var left = Math.Max(0, barred ? report.Offerable : report.Eligible - report.AlreadyShown);
        var lead = barred
            ? FormattableString.Invariant($"You have {left} unseen {topic}{noun} left that clear your conditions and your bar")
            : FormattableString.Invariant($"You have {left} unseen {topic}{noun} left inside your conditions");
        var parts = new List<string> { lead };
        if (report.AlreadySeen > 0)
        {
            parts.Add(FormattableString.Invariant($"you've seen {report.AlreadySeen}"));
        }

        if (barred && report.Unscored > 0)
        {
            parts.Add(FormattableString.Invariant($"{report.Unscored} more have no score from your model yet"));
        }

        if (report.ExcludedByConditions > 0)
        {
            parts.Add(FormattableString.Invariant($"your conditions hold back {report.ExcludedByConditions}"));
        }

        return string.Join("; ", parts) + ".";
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────

    private async Task<TurnOutcome> SearchAsync(Turn turn, PlanResult plan, ConversationState next, CancellationToken ct)
    {
        var accountId = turn.AccountId;
        var hard = next.Hard;
        var soft = next.Soft;

        // Titles named as comparisons are searched near and never returned.
        var referenceIds = soft.ReferenceTitleIds.ToList();
        foreach (var name in plan.Delta.ReferenceNames)
        {
            if (await FindByNameAsync(name, ct) is { } id && !referenceIds.Contains(id))
            {
                referenceIds.Add(id);
            }
        }

        soft = soft with { ReferenceTitleIds = referenceIds };
        next = next with { Soft = soft };

        await ConflictAsync(turn, hard, ct);

        var exclude = turn.Input.ShownTitleIds
            .Concat(next.Rejected)
            .Concat(next.LegacyShown ?? [])
            .Concat(referenceIds)
            .ToHashSet();
        var topic = hard.Concepts.Count > 0 ? hard.Concepts[0].UserWords : null;
        await PhaseAsync(turn, "searching", topic is null ? "Searching the catalogue" : $"Searching the catalogue for {topic}");

        // The user's own rejections ride INTO retrieval as this turn's extra exclusions (WS-3 CR-2b), so
        // every arm — TMDB's included — fills its depth with titles that can survive the filter below,
        // and the message and region reach the TMDB arm's title search and release window.
        var (negatedGenres, negatedKeywords) = NegationRules(next.UserNegations);
        var query = new RetrievalQuery(accountId, hard, soft, exclude, RetrievalWant)
        {
            ExcludeGenres = negatedGenres,
            ExcludeKeywords = negatedKeywords,
            Message = turn.Input.Message,
            Region = turn.Input.Region,
        };
        await WarmMoodAsync(turn, hard, soft, ct);
        RetrievalResult retrieval;
        using (turn.Timings.Stage(AskStageTimings.Retrieve))
        {
            retrieval = await retriever.RetrieveAsync(query, ct);
        }

        // An arm that contributed nothing is a thin retrieval, recorded on the row (WS-3 CR-2c), not a
        // degraded answer: the cards were fused without it and are still exactly what they claim (§4.10).
        foreach (var arm in retrieval.SkippedArms)
        {
            turn.Note(SearchTurnDegraded.ArmPrefix + arm);
        }

        var titles = await db.Titles.AsNoTracking()
            .Where(t => retrieval.Candidates.Select(c => c.TitleId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        var legacy = (next.LegacyTmdbExclude ?? []).ToHashSet();
        var candidates = retrieval.Candidates
            .Where(c => titles.ContainsKey(c.TitleId) && !exclude.Contains(c.TitleId))
            .Where(c => titles[c.TitleId].TmdbId is not { } tmdb || !legacy.Contains(tmdb))
            // The user's own negations are airtight on the picks: a title carrying a ruled-out word as
            // a keyword or a genre is not judged at all.
            .Where(c => !next.UserNegations.Any(n => Trips(titles[c.TitleId], n)))
            .ToList();

        var personOnly = hard.People.Count > 0 && !hard.HasConcepts;
        var judgedSlice = candidates.Take(RequestJudge.MaxJudged).ToList();

        // TMDB found titles the catalogue does not hold yet (WS-3 CR-2b). They are admitted only where
        // the catalogue left the judged slice short, and only after they are hydrated and pass the
        // account's eligibility and the request's hard constraints exactly as a catalogue title would.
        // The hydration runs on scopes of its own WHILE the catalogue slice is judged, so the first
        // card never waits for it; whatever it admits is judged after, as one more round trip.
        var room = Math.Min(MaxProvisional, RequestJudge.MaxJudged - judgedSlice.Count);
        var provisional = !personOnly && judgeModel.IsAvailable && room > 0 && retrieval.Provisional.Count > 0
            ? HydrateProvisionalAsync(turn, retrieval.Provisional.Take(room).ToList(), ct)
            : null;
        var judgedCount = personOnly ? 0 : judgedSlice.Count;
        int? personSeen = null;
        if (personOnly)
        {
            var predictions = await ScoreAsync(turn, judgedSlice, ct);
            personSeen = await SeenCreditsAsync(accountId, hard.People, hard.Media, ct);
            await PlacePersonCreditsAsync(turn, plan, judgedSlice, titles, predictions, hard.People[0]);
        }
        else if (!judgeModel.IsAvailable)
        {
            var predictions = await ScoreAsync(turn, judgedSlice, ct);
            // No model to judge with: what was found is shown as found, unchecked, never as a pick.
            if (!turn.Degraded.Contains(SearchTurnDegraded.NoModel))
            {
                turn.Degrade(SearchTurnDegraded.NoModel);
            }

            foreach (var c in judgedSlice)
            {
                decimal? predicted = predictions.TryGetValue(c.TitleId, out var p) && p > 0m ? p : null;
                turn.Shown.Add(new Placed(
                    titles[c.TitleId], c, JudgeVerdict.Unjudged(c.TitleId), predicted,
                    0d, AskReelGate.UncheckedTier(predicted, turn.Floor)));
            }
        }
        else
        {
            await JudgeAsync(turn, next, judgedSlice, titles, ct);
            if (provisional is not null)
            {
                var admitted = await AdmitProvisionalAsync(turn, hard, query, await provisional, legacy, next, ct);
                foreach (var (_, title) in admitted)
                {
                    titles[title.Id] = title;
                }

                if (admitted.Count > 0)
                {
                    var extra = admitted.Select(a => a.Candidate).ToList();
                    await JudgeAsync(turn, next, extra, titles, ct);
                    judgedCount += extra.Count;
                }
            }
        }

        // Trays, in the order the screen renders them: the floor tray, the near misses, the unchecked.
        var picks = turn.Shown.Where(p => p.Tier == SearchTurnCardTier.Pick).ToList();
        var trays = new List<(string Kind, List<Placed> Cards)>
        {
            ("belowFloor", turn.Shown.Where(p => p.Tier == SearchTurnCardTier.BelowFloor).ToList()),
            ("nearMiss", turn.Shown.Where(p => p.Tier == SearchTurnCardTier.NearMiss).ToList()),
            ("unchecked", turn.Shown.Where(p => p.Tier == SearchTurnCardTier.Unchecked).ToList()),
        };
        var rank = picks.Count;
        var placedTrays = new List<(string Kind, List<Placed> Cards)>();
        foreach (var (kind, cards) in trays.Where(t => t.Cards.Count > 0))
        {
            var ranked = cards.Select(c => c with { Rank = rank++ }).ToList();
            placedTrays.Add((kind, ranked));
            await turn.Emit("tray", new { kind, cards = ranked.Select(c => Wire(turn, c)).ToList() });
            await PersistAsync(turn, ranked);
        }

        var mode = picks.Count > 0 ? SearchTurnMode.Picks : SearchTurnMode.NoneFound;
        SupplyReport? supply = null;
        if (mode == SearchTurnMode.NoneFound)
        {
            // Counted after this turn's scoring, so the split by score is the one the trays show
            // (IREV3-SYS-01). Only a request with something to count — a topic or a person — has a
            // supply: a mood request's "supply" would be the whole catalogue, and a reply built on it
            // said "270 unseen titles tagged that way" about a request that named no tag.
            var countable = hard.HasConcepts || hard.People.Count > 0;
            if (countable && !(personOnly && retrieval.Supply.MatchingInCatalogue == 0))
            {
                supply = await RecountAsync(turn, hard, retrieval.Supply, ct);
            }

            await turn.Emit("none-found", new { supply = LiveSearchExpansionService.SupplyWire(supply) });
        }

        var judgeFailed = SearchTurnDegraded.JudgeFailed(string.Join(',', turn.Degraded));
        var finalMode = mode == SearchTurnMode.Picks ? FinalMode.Picks : FinalMode.NoneFound;
        var underBar = placedTrays.FirstOrDefault(t => t.Kind == "belowFloor").Cards ?? [];
        var nearMiss = placedTrays.FirstOrDefault(t => t.Kind == "nearMiss").Cards ?? [];
        var uncheckedTray = placedTrays.FirstOrDefault(t => t.Kind == "unchecked").Cards ?? [];
        ComposedTurn composed;
        using (turn.Timings.Stage(AskStageTimings.Compose))
        {
            composed = composer.Compose(Context(turn, plan, finalMode) with
            {
                Topic = topic,
                Media = hard.Media,
                Picks = picks.Count(p => p.Verdict.Judged || p.Verdict.FitSource == ResultSource.Facet),
                UnjudgedPicks = picks.Count(p => !p.Verdict.Judged && p.Verdict.FitSource != ResultSource.Facet),
                NearMissNames = nearMiss.Select(p => p.Title.Name).ToList(),
                UnderBar = underBar.Count,
                // Named under the bar only where something said it matches (R1-01): the judge, or the credits.
                UnderBarNames = underBar.Where(p => Matched(p.Verdict)).Select(p => p.Title.Name).ToList(),
                HeldUnchecked = underBar.Count(p => !Matched(p.Verdict)),
                Unchecked = uncheckedTray.Count,
                Judged = judgedCount,
                Supply = supply,
                JudgeFailed = judgeFailed,
                Person = personOnly ? hard.People[0] : null,
                PersonSeen = personSeen,
            });
        }

        if (mode == SearchTurnMode.NoneFound)
        {
            // A follow-up that gets the same answer hears that it was heard (the v1 rule, kept).
            composed = composed with { Reply = NoneFoundCopy.Again(composed.Reply, PreviousReply(turn.Input.History)) };
        }

        await DegradedFrameAsync(turn);
        await ReplyAsync(turn, composed.Reply, degraded: SearchTurnDegraded.PlanFailed(string.Join(',', turn.Degraded)));
        await DoneAsync(
            turn, plan, mode, picks, placedTrays, supply, composed.Reason, null, null);

        await RecordExposuresAsync(turn, picks, ct);

        // This answer's handles, for the next turn's references: its picks, else its trays in screen order.
        var presented = (picks.Count > 0 ? picks : placedTrays.SelectMany(t => t.Cards).Take(AskReelHandles.MaxHistoryHandles).ToList())
            .Select((p, index) => new PresentedRef(
                p.Title.Id,
                new TurnHandle(turn.Input.AnswerNumber, picks.Count > 0 ? p.Rank + 1 : index + 1),
                p.Title.Name, p.Title.Year, p.Title.MediaType))
            .ToList();
        var saved = ConversationStateReducer.Presented(next, presented) with { Version = turn.Input.AnswerNumber };
        var all = picks.Concat(placedTrays.SelectMany(t => t.Cards)).ToList();
        return Outcome(turn, plan, saved, composed.Reply, composed.Reason, mode, all, IntentJsonOf(hard, next));
    }

    /// <summary>
    /// A request with no concept and no person — a mood ("feel-good", "something quiet") — has the
    /// semantic arm as its ONLY topical arm, and that arm waits 1.0 s for an embedding a new text takes
    /// 1.2–2.7 s to make: on the founder slot it was skipped on nearly every first ask, so the judge was
    /// handed the catalogue's best-regarded titles with no reading of the mood at all. The text the arm
    /// will embed is embedded here first, waiting up to <see cref="MoodEmbedBudget"/>, into the shared
    /// cache the arm reads. A request with a concept is left alone: its semantic arm only re-ranks titles
    /// the catalogue evidence already admitted, and is not worth the wait.
    /// </summary>
    private async Task WarmMoodAsync(Turn turn, HardConstraints hard, SoftPreferences soft, CancellationToken ct)
    {
        if (hard.HasConcepts || hard.People.Count > 0 || !conceptEmbeddings.IsAvailable)
        {
            return;
        }

        var text = HybridRetriever.SemanticText(hard, soft);
        if (text.Length == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var vector = await conceptEmbeddings.EmbedAsync(text, MoodEmbedBudget, ct);
        turn.Timings.Mark(AskStageTimings.RetrieveEmbed, clock.Elapsed.TotalMilliseconds);
        if (vector is null)
        {
            turn.Note(SearchTurnDegraded.ArmPrefix + "mood-embed-late");
        }
    }

    /// <summary>
    /// The user's negations as retrieval rules: a word that names a genre ("no horror") excludes the
    /// genre; any other word ("no superheroes") excludes the keyword in every spelling
    /// <see cref="NegationTerms.Forms"/> gives it, because the catalogue's labels are singular as
    /// often as plural. Never a standing rule — these ride on one query only.
    /// </summary>
    internal static (IReadOnlyList<string> Genres, IReadOnlyList<string> Keywords) NegationRules(IReadOnlyList<string> negations)
    {
        var genres = new List<string>();
        var keywords = new List<string>();
        foreach (var negation in negations)
        {
            var asGenres = SearchVocabulary.ResolveGenres(negation);
            if (asGenres.Count > 0)
            {
                genres.AddRange(asGenres);
            }
            else
            {
                keywords.AddRange(NegationTerms.Forms(negation));
            }
        }

        return (
            genres.Distinct(StringComparer.Ordinal).ToList(),
            keywords.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Upserts and hydrates the TMDB-only titles, each on a scope of its own (pinned to the account,
    /// so nothing here reads unscoped) and all at once, within <see cref="ProvisionalBudget"/>.
    /// Returns the catalogue ids of the ones that are now hydrated; never throws — a provisional
    /// title is a bonus, and a failure to fetch one costs only that title.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> HydrateProvisionalAsync(
        Turn turn, IReadOnlyList<ProvisionalTitle> provisional, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ProvisionalBudget);
        var clock = Stopwatch.StartNew();
        var accountId = turn.AccountId;
        var region = turn.Input.Region;
        var results = await Task.WhenAll(provisional.Select(OneAsync));
        var hydrated = results.OfType<Guid>().ToList();
        if (hydrated.Count < provisional.Count)
        {
            turn.Note(FormattableString.Invariant($"{SearchTurnDegraded.ArmPrefix}provisional-{provisional.Count - hydrated.Count}/{provisional.Count}"));
        }

        logger.LogDebug(
            "Ask Reel v2: hydrated {Hydrated} of {Provisional} TMDB-only titles in {Elapsed} ms.",
            hydrated.Count, provisional.Count, clock.ElapsedMilliseconds);
        return hydrated;

        async Task<Guid?> OneAsync(ProvisionalTitle p)
        {
            try
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<Tenancy.IAccountContext>().SetAccount(accountId);
                var scoped = scope.ServiceProvider.GetRequiredService<IReelDbContext>();
                var media = p.Media;
                var item = new Domain.Ports.Tmdb.TmdbListItem(
                    p.TmdbId, media == MediaType.Movie, p.Name, null, null, null, 0m, 0m, 0,
                    p.Year is { } year ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null, []);
                await scope.ServiceProvider.GetRequiredService<Ranking.CandidateGenerator>().UpsertCandidatesAsync([item], bounded.Token);
                var title = await scoped.Titles.FirstOrDefaultAsync(t => t.TmdbId == p.TmdbId && t.MediaType == media, bounded.Token);
                if (title is null)
                {
                    return null;
                }

                if (title.LastMetadataRefreshAt is null)
                {
                    await scope.ServiceProvider.GetRequiredService<Ingestion.TitleHydrator>().HydrateBatchAsync([title], bounded.Token, region);
                    await scoped.SaveChangesAsync(bounded.Token);
                }

                return title.LastMetadataRefreshAt is null ? null : title.Id;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Ask Reel v2: TMDB title {TmdbId} could not be hydrated this turn.", p.TmdbId);
                return null;
            }
        }
    }

    /// <summary>
    /// The hydrated TMDB-only titles that may be judged: the ones inside the account's eligibility,
    /// this turn's rejections and the request's resolved hard constraints — the same gates a
    /// catalogue candidate passed in retrieval — with the evidence those constraints give them.
    /// </summary>
    private async Task<List<(Candidate Candidate, Title Title)>> AdmitProvisionalAsync(
        Turn turn,
        HardConstraints hard,
        RetrievalQuery query,
        IReadOnlyList<Guid> hydrated,
        IReadOnlySet<long> legacy,
        ConversationState next,
        CancellationToken ct)
    {
        if (hydrated.Count == 0)
        {
            return [];
        }

        var resolved = await resolver.ResolveAsync(hard, ct);
        var rules = turn.Standing.WithExclusions(query.ExcludeGenres, query.ExcludeKeywords);
        var exclude = query.Exclude.ToHashSet();
        var ids = hydrated.Where(id => !exclude.Contains(id)).ToList();
        var rows = await HardConstraintFilter.Apply(eligibility.EligibleTitles(turn.AccountId, rules), db, resolved)
            .Where(t => ids.Contains(t.Id))
            .AsNoTracking()
            .ToListAsync(ct);
        var ceiling = await eligibility.MaturityCeilingAsync(turn.AccountId, ct);
        return EligibilityQueryBuilder.WithinMaturity(rows, ceiling)
            .Where(t => t.TmdbId is not { } tmdb || !legacy.Contains(tmdb))
            .Where(t => !next.UserNegations.Any(n => Trips(t, n)))
            .OrderBy(t => ids.IndexOf(t.Id))
            .Select(t => (new Candidate(t.Id, 0d, null, HardConstraintFilter.EvidenceFor(t, resolved).Append("tmdb:discover").ToList()), t))
            .ToList();
    }

    /// <summary>The account's predictions for <paramref name="slice"/>, scored on demand where missing (§4.7).</summary>
    private async Task<IReadOnlyDictionary<Guid, decimal>> ScoreAsync(Turn turn, IReadOnlyList<Candidate> slice, CancellationToken ct)
    {
        using (turn.Timings.Stage(AskStageTimings.Score))
        {
            return slice.Count == 0
                ? new Dictionary<Guid, decimal>()
                : await scorer.ScoreAsync(turn.AccountId, slice.Select(c => c.TitleId).ToList(), DateTime.UtcNow, ct);
        }
    }

    private async Task JudgeAsync(
        Turn turn,
        ConversationState next,
        IReadOnlyList<Candidate> slice,
        IReadOnlyDictionary<Guid, Title> titles,
        CancellationToken ct)
    {
        if (slice.Count == 0)
        {
            return;
        }

        var judgeTitles = await facts.JudgeTitlesAsync(slice.Select(c => c.TitleId).ToList(), ct);
        var batches = RequestJudge.Batches(judgeTitles.Select(t => t.TitleId).ToList());
        var batchOf = batches.SelectMany((ids, index) => ids.Select(id => (id, index))).ToDictionary(x => x.id, x => x.index);
        var pending = batches.Select(b => b.Count).ToArray();
        var verdicts = new Dictionary<Guid, JudgeVerdict>();
        var candidates = slice.ToDictionary(c => c.TitleId);
        var persona = turn.Persona ?? await PersonaLineAsync(turn.AccountId, ct);
        turn.Models["judge"] = judgeModel.ModelId;

        // Scored WHILE the judge runs: the predictions are needed only to place a returned batch, and
        // the judge's calls never touch the database, so the scorer has the context to itself. Every
        // database read the judging needs (the titles, the persona) is done above, before it starts.
        // The timings are recorded from this flow only (AskStageTimings is not thread-safe), so the
        // scorer's elapsed time is captured and marked once it has been awaited.
        var scoreMs = 0d;
        var scoring = TimedScoreAsync();
        async Task<IReadOnlyDictionary<Guid, decimal>> TimedScoreAsync()
        {
            var clock = Stopwatch.StartNew();
            var scored = await scorer.ScoreAsync(turn.AccountId, slice.Select(c => c.TitleId).ToList(), DateTime.UtcNow, ct);
            scoreMs = clock.Elapsed.TotalMilliseconds;
            return scored;
        }

        IReadOnlyDictionary<Guid, decimal>? predictions = null;

        await PhaseAsync(turn, "judging", FormattableString.Invariant(
            $"Checking {judgeTitles.Count} {(judgeTitles.Count == 1 ? "title" : "titles")} against your request"));

        var failedBatches = 0;
        var unjudgedYes = new List<Placed>();
        var judgeClock = Stopwatch.StartNew();
        var firstBatchMarked = false;
        try
        {
            await foreach (var verdict in judge.JudgeAsync(
                               new JudgeInput(turn.AccountId, JudgeRequest(turn.Input.Message, next), persona, judgeTitles), ct))
            {
                verdicts[verdict.TitleId] = verdict;
                var index = batchOf[verdict.TitleId];
                if (--pending[index] > 0)
                {
                    continue;
                }

                if (!firstBatchMarked)
                {
                    firstBatchMarked = true;
                    turn.Timings.Mark(AskStageTimings.JudgeFirstBatch, judgeClock.Elapsed.TotalMilliseconds);
                }

                // The whole batch is in: place it, rank its picks by the blend, and emit them appended.
                predictions ??= await scoring;
                var batch = batches[index].Select(id => verdicts[id]).ToList();
                if (batch.All(v => !v.Judged))
                {
                    failedBatches++;
                }

                var placed = batch
                    .Select(v => Place(titles[v.TitleId], candidates[v.TitleId], v, predictions.GetValueOrDefault(v.TitleId), turn.Floor))
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .ToList();
                unjudgedYes.AddRange(placed.Where(p => p.Tier == "unjudged"));
                var batchPicks = placed
                    .Where(p => p.Tier == SearchTurnCardTier.Pick)
                    .OrderByDescending(p => p.Blend)
                    .ThenBy(p => p.Title.Id)
                    .Select(p => p with { Rank = turn.Shown.Count(s => s.Tier == SearchTurnCardTier.Pick) })
                    .ToList();
                var ranked = new List<Placed>(batchPicks.Count);
                foreach (var pick in batchPicks)
                {
                    var withRank = pick with { Rank = turn.Shown.Count(s => s.Tier == SearchTurnCardTier.Pick) };
                    turn.Shown.Add(withRank);
                    ranked.Add(withRank);
                }

                turn.Shown.AddRange(placed.Where(p => p.Tier is SearchTurnCardTier.NearMiss or SearchTurnCardTier.BelowFloor));
                if (ranked.Count > 0)
                {
                    await EmitPicksAsync(turn, index, ranked);
                }
            }
        }
        finally
        {
            // Never leave the scorer running on this turn's context: a judge that threw or a turn
            // that was cancelled still waits for it, and its outcome is then irrelevant.
            if (predictions is null)
            {
                try
                {
                    await scoring;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogDebug(ex, "Ask Reel v2: scoring abandoned with the judge.");
                }
            }
        }

        predictions ??= await scoring;
        turn.Timings.Mark(AskStageTimings.Score, scoreMs);
        turn.Timings.Mark(AskStageTimings.Judge, judgeClock.Elapsed.TotalMilliseconds);
        turn.Calls.Add(new LlmCallMeta(judgeModel.ModelId, null, false, batches.Count, (long)judgeClock.Elapsed.TotalMilliseconds));

        var missing = verdicts.Values.Count(v => !v.Judged);
        if (failedBatches > 0)
        {
            turn.Degrade(FormattableString.Invariant($"{SearchTurnDegraded.JudgePrefix}{failedBatches}/{batches.Count}"));
        }

        if (missing > 0)
        {
            turn.Degrade(FormattableString.Invariant($"{SearchTurnDegraded.JudgeMissingPrefix}{missing}/{verdicts.Count}"));
        }

        // §4.10: an unjudged card ranks below every judged pick, in retrieval order, with no
        // why-line — on a turn that has judged picks. With none, it is shown as unchecked.
        var judgedPicks = turn.Shown.Count(s => s.Tier == SearchTurnCardTier.Pick);
        var tail = new List<Placed>();
        foreach (var card in unjudgedYes.OrderByDescending(c => c.Candidate?.FusedScore ?? 0d))
        {
            if (judgedPicks > 0 && AskReelGate.Clears(card.Predicted, turn.Floor))
            {
                var withRank = card with { Tier = SearchTurnCardTier.Pick, Rank = turn.Shown.Count(s => s.Tier == SearchTurnCardTier.Pick) };
                turn.Shown.Add(withRank);
                tail.Add(withRank);
            }
            else
            {
                // The bar binds first (XM-06): unchecked AND under it is the floor tray's, never a
                // tray the matrix — or the user — reads as cards that cleared their bar.
                turn.Shown.Add(card with { Tier = AskReelGate.UncheckedTier(card.Predicted, turn.Floor) });
            }
        }

        if (tail.Count > 0)
        {
            await EmitPicksAsync(turn, batches.Count, tail);
        }
    }

    /// <summary>
    /// A person request with no topic: the credits are the evidence (FD-3 default — only unseen
    /// titles, the seen ones counted in the reply and never named), so nothing is sent to the judge.
    /// </summary>
    private async Task PlacePersonCreditsAsync(
        Turn turn,
        PlanResult plan,
        IReadOnlyList<Candidate> slice,
        IReadOnlyDictionary<Guid, Title> titles,
        IReadOnlyDictionary<Guid, decimal> predictions,
        PersonConstraint person)
    {
        _ = plan;
        var why = person.Role switch
        {
            PersonRole.Director => $"Directed by {person.Name}.",
            PersonRole.Cast => $"{person.Name} is in the cast.",
            _ => $"{person.Name} is credited.",
        };
        var placed = slice
            .Select(c =>
            {
                var predicted = predictions.GetValueOrDefault(c.TitleId);
                decimal? prediction = predictions.ContainsKey(c.TitleId) ? predicted : null;
                var verdict = new JudgeVerdict(
                    c.TitleId, Satisfies.Yes, null, why, c.Evidence.FirstOrDefault(e => e.StartsWith("person:", StringComparison.Ordinal)),
                    ResultSource.Facet, ResultSource.Facet);
                var tier = AskReelGate.Clears(prediction, turn.Floor) ? SearchTurnCardTier.Pick : SearchTurnCardTier.BelowFloor;
                return new Placed(titles[c.TitleId], c, verdict, prediction, Blend(titles[c.TitleId], null, null, c.FusedScore), tier);
            })
            .ToList();
        var picks = new List<Placed>();
        foreach (var p in placed.Where(p => p.Tier == SearchTurnCardTier.Pick).OrderByDescending(p => p.Blend).ThenBy(p => p.Title.Id))
        {
            var withRank = p with { Rank = picks.Count };
            picks.Add(withRank);
            turn.Shown.Add(withRank);
        }

        turn.Shown.AddRange(placed.Where(p => p.Tier == SearchTurnCardTier.BelowFloor));
        if (picks.Count > 0)
        {
            await EmitPicksAsync(turn, 0, picks);
        }
    }

    /// <summary>Where one judged card goes (§4.8); null for a card the judge said is not what was asked for.</summary>
    private static Placed? Place(Title title, Candidate candidate, JudgeVerdict verdict, decimal? predicted, decimal floor)
    {
        decimal? prediction = predicted is { } p && p > 0m ? p : null;
        var blend = Blend(title, verdict.Fit, candidate.Similarity, candidate.FusedScore);
        if (!verdict.Judged)
        {
            return new Placed(title, candidate, verdict, prediction, blend, "unjudged");
        }

        return AskReelGate.Place(verdict.Satisfies, prediction, floor) switch
        {
            AskReelGate.Placement.Pick => new Placed(title, candidate, verdict, prediction, blend, SearchTurnCardTier.Pick),
            AskReelGate.Placement.BelowFloor => new Placed(title, candidate, verdict, prediction, blend, SearchTurnCardTier.BelowFloor),
            AskReelGate.Placement.NearMiss => new Placed(title, candidate, verdict, prediction, blend, SearchTurnCardTier.NearMiss),
            _ => null,
        };
    }

    /// <summary>
    /// The within-batch order (§4.8): the judge's fit, the vote-shrunk crowd score and the similarity.
    /// Display only beyond the batch — a card never moves once it is shown.
    /// </summary>
    internal static double Blend(Title title, double? fit, double? similarity, double fused)
    {
        var (rating, votes) = CrowdRating.Resolve(title);
        var crowd = rating is { } r ? (double)CrowdRating.Shrink(r, votes ?? 0) / 10d : 0.5d;
        return (0.6d * (fit ?? 0d)) + (0.3d * crowd) + (0.1d * (similarity ?? 0d)) + (fused * 1e-3);
    }

    private async Task EmitPicksAsync(Turn turn, int batch, IReadOnlyList<Placed> cards)
    {
        if (!turn.FirstCardMarked)
        {
            turn.FirstCardMarked = true;
            turn.Timings.Mark(AskStageTimings.FirstCard, turn.Clock.Elapsed.TotalMilliseconds);
        }

        await turn.Emit("picks", new { batch, cards = cards.Select(c => Wire(turn, c)).ToList() });
        await PersistAsync(turn, cards);
    }

    private static async Task PersistAsync(Turn turn, IReadOnlyList<Placed> cards)
    {
        var rows = cards.Select(Row).ToList();
        foreach (var row in rows)
        {
            turn.Input.Progress?.Shown(row);
        }

        if (turn.Input.PersistCards is { } persist)
        {
            await persist(rows);
        }
    }

    private static TurnCard Row(Placed c) => new(
        c.Title.Id,
        c.Title.TmdbId,
        c.Rank,
        OutsideRequest: false,
        c.Candidate?.Similarity is { } sim ? Math.Round(sim, 2) : null,
        c.Predicted is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null,
        c.Verdict.Judged && c.Verdict.Fit is { } fit ? Math.Round((decimal)fit, 3, MidpointRounding.AwayFromZero) : null,
        Math.Round((decimal)c.Blend, 4, MidpointRounding.AwayFromZero),
        ShowsWhy(c.Verdict) ? c.Verdict.Why : null,
        WhySourceOf(c.Verdict),
        BelowFloor: c.Tier != SearchTurnCardTier.Pick,
        Tier: c.Tier,
        Satisfies: c.Verdict.Judged || c.Verdict.FitSource == ResultSource.Facet ? LlmResultText.Wire(c.Verdict.Satisfies) : null,
        FitSource: c.Verdict.Fit is null ? null : c.Verdict.FitSource.ToString(),
        SatisfiesEvidence: c.Verdict.Evidence is { Length: > 0 } e ? (e.Length > 280 ? e[..280] : e) : null);

    /// <summary>Whether something established the card matches: a judge verdict, or a person's credits.</summary>
    private static bool Matched(JudgeVerdict v) => v.Judged || v.FitSource == ResultSource.Facet;

    /// <summary>A why-line is shown only where a model wrote it or the credits state it — never a fallback.</summary>
    private static bool ShowsWhy(JudgeVerdict v) =>
        v.WhySource is ResultSource.Llm or ResultSource.Facet && v.Why is { Length: > 0 };

    /// <summary>The typed source of a card's why-line, assigned by the code that produced it (B-6).</summary>
    private static ExposureWhySource WhySourceOf(JudgeVerdict v) =>
        !ShowsWhy(v) ? ExposureWhySource.None
        : v.WhySource == ResultSource.Facet ? ExposureWhySource.Facet
        : ExposureWhySource.Llm;

    private object Wire(Turn turn, Placed c)
    {
        var t = c.Title;
        var shown = ShowsWhy(c.Verdict);
        return new
        {
            titleId = t.Id,
            mediaType = t.MediaType.ToString(),
            tmdbId = t.TmdbId,
            name = t.Name,
            year = t.Year,
            posterPath = t.PosterPath,
            genres = t.Genres,
            similarity = c.Candidate?.Similarity is { } s ? Math.Round(s, 2) : (double?)null,
            predictedRating = c.Predicted,
            crowdRating = CrowdRating.For(t),
            crowdVotes = CrowdRating.VotesFor(t),
            fit = c.Verdict.Judged && c.Verdict.Fit is { } f ? Math.Round(f, 2) : (double?)null,
            why = shown ? c.Verdict.Why : null,
            seen = false,
            blend = Math.Round(c.Blend, 4),
            outsideRequest = false,
            satisfies = c.Verdict.Judged || c.Verdict.FitSource == ResultSource.Facet ? LlmResultText.Wire(c.Verdict.Satisfies) : null,
            handle = new TurnHandle(turn.Input.AnswerNumber, c.Rank + 1).ToString(),
            tier = c.Tier,
            evidence = c.Verdict.Evidence,
        };
    }

    private async Task RecordExposuresAsync(Turn turn, IReadOnlyList<Placed> picks, CancellationToken ct)
    {
        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == turn.AccountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } activeArtifactId)
        {
            return;
        }

        var cards = picks
            .Where(p => p.Predicted is not null)
            .Select(p => new ExposureCard(
                p.Title.Id,
                p.Rank,
                p.Predicted!.Value,
                LlmFit: p.Verdict.Judged && p.Verdict.Fit is { } fit ? (decimal)fit : null,
                WhySentence: ShowsWhy(p.Verdict) ? p.Verdict.Why : null,
                WhySource: WhySourceOf(p.Verdict)))
            .ToList();
        await exposures.RecordAsync(
            turn.AccountId, ExposureSurface.AskReel, feedSnapshotId: null, activeArtifactId, DateTime.UtcNow, cards, ct,
            turn.Input.SourceTurnId);
    }

    private async Task<SupplyReport> RecountAsync(Turn turn, HardConstraints hard, SupplyReport discovered, CancellationToken ct)
    {
        if (!hard.HasConcepts && hard.People.Count == 0)
        {
            return discovered;
        }

        try
        {
            return await supplyReporter.ReportAsync(turn.AccountId, hard, turn.Input.ShownTitleIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel v2: the supply re-count failed; the reply uses the count taken at retrieval.");
            return discovered;
        }
    }

    private async Task ConflictAsync(Turn turn, HardConstraints hard, CancellationToken ct)
    {
        var intent = IntentOf(hard);
        var genres = AskReelStandingFilters.Colliding(turn.Standing.ExcludeGenres, AskReelStandingFilters.AskedFor(turn.Input.Message, intent, null));
        var keywords = AskReelStandingFilters.Colliding(turn.Standing.ExcludeKeywords, AskReelStandingFilters.AskedFor(turn.Input.Message, intent, null));
        var years = AskReelStandingFilters.YearWindowCollides(turn.Standing, intent, null, turn.Input.Message);
        if (genres.Count == 0 && keywords.Count == 0 && !years)
        {
            return;
        }

        var message = AskReelStandingFilters.Message(genres, keywords, years, turn.Standing);
        var conflict = new AskReelConflict(genres, keywords, turn.Standing.MinYear, turn.Standing.MaxYear, message);
        turn.ConflictJson = JsonSerializer.Serialize(conflict, WireJson);
        await turn.Emit("conflict", new
        {
            excludedGenres = conflict.ExcludedGenres,
            excludedKeywords = conflict.ExcludedKeywords,
            minYear = conflict.MinYear,
            maxYear = conflict.MaxYear,
            poolSize = await eligibility.EligibleTitles(turn.AccountId).CountAsync(ct),
            catalogueSize = await db.Titles.CountAsync(ct),
            message = conflict.Message,
        });
    }

    // ── Frames and the outcome ──────────────────────────────────────────────────────────────

    private static async Task PhaseAsync(Turn turn, string stage, string label)
    {
        var payload = new { stage, label };
        turn.Input.Progress?.Phase(payload);
        await turn.Emit("phase", payload);
    }

    /// <summary>The <c>degraded</c> frame, when anything the client is told about did not run as designed.</summary>
    private static async Task DegradedFrameAsync(Turn turn)
    {
        if (turn.Degraded.Count > 0)
        {
            await turn.Emit("degraded", new { what = turn.Degraded.ToArray() });
        }
    }

    private static async Task ReplyAsync(Turn turn, string text, bool degraded)
    {
        turn.Input.Progress?.Replied(text, degraded);
        await turn.Emit("assistant-message", new { text, degraded });
    }

    private async Task DoneAsync(
        Turn turn,
        PlanResult plan,
        string? mode,
        IReadOnlyList<Placed> results,
        IReadOnlyList<(string Kind, List<Placed> Cards)> trays,
        SupplyReport? supply,
        string? reason,
        string? answer,
        object? clarify,
        bool failed = false)
    {
        List<object> Tray(string kind) =>
            trays.FirstOrDefault(t => t.Kind == kind).Cards?.Select(c => Wire(turn, c)).ToList() ?? [];
        var belowFloor = Tray("belowFloor");
        await turn.Emit("done", new
        {
            results = results.Select(c => Wire(turn, c)).ToList(),
            streamed = results.Count + belowFloor.Count,
            kept = results.Count,
            droppedByFloor = belowFloor.Count,
            belowFloor,
            nearMiss = Tray("nearMiss"),
            @unchecked = Tray("unchecked"),
            mode,
            supply = LiveSearchExpansionService.SupplyWire(supply),
            degraded = turn.Degraded.ToArray(),
            reason,
            failed,
            engine = "v2",
            act = plan.Act.ToString(),
            planSource = plan.Source.ToString(),
            answer,
            clarify,
            timingsMs = turn.Input.Debug ? turn.Timings.Snapshot() : null,
            debug = turn.Input.Debug ? (object?)new { plan = new { plan.Act, plan.References, plan.Tools, plan.Delta } } : null,
        });
    }

    private TurnOutcome Outcome(
        Turn turn,
        PlanResult plan,
        ConversationState state,
        string? reply,
        string? reason,
        string? mode,
        IReadOnlyList<Placed>? cards = null,
        string? intentJson = null)
    {
        var rows = (cards ?? []).OrderBy(c => c.Rank).Select(Row).ToList();
        var effort = turn.Calls.Select(c => c.Effort).FirstOrDefault(e => e is { Length: > 0 }) ?? SearchTurnDegraded.ProviderDefaultEffort;
        return new TurnOutcome(
            reply,
            reason,
            Failed: false,
            FailureCode: null,
            Relaxation: null,
            FloorRelaxed: false,
            DiscoveredCount: null,
            intentJson,
            ModelArtifactId: null,
            ModelId: turn.Models.GetValueOrDefault("plan") ?? planner.ModelId,
            rows,
            turn.ConflictJson,
            mode,
            turn.StoredFlags,
            JsonSerializer.Serialize(turn.Timings.Snapshot()),
            JsonSerializer.Serialize(turn.Models),
            effort,
            plan.Act.ToString(),
            plan.Source.ToString(),
            turn.Calls.Count(c => c.Truncated),
            ConversationStateJson.Write(state));
    }

    private TurnContext Context(Turn turn, PlanResult plan, FinalMode mode) =>
        new(
            plan.Act, mode, null, MediaScope.Any, 0, 0, [], 0, [], 0, 0, turn.Floor, null,
            CarriedForward: plan.Source == PlanSource.CarriedForward && plan.Failure is not null,
            JudgeFailed: false);

    // ── State ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conversation's state before this turn: the stored one when it is current (written by the
    /// previous turn), otherwise rebuilt from the turn record (§4.6) — which is also what a pre-v2
    /// conversation gets on its first v2 turn. The handles on screen always come from the record.
    /// </summary>
    private async Task<ConversationState> LoadStateAsync(LiveSearchExpansionService.AskTurnInput input, CancellationToken ct)
    {
        var presented = input.Presented
            .Select(p => new PresentedRef(p.TitleId, new TurnHandle(p.Turn, p.Rank), p.Name, p.Year, p.Media))
            .ToList();
        if (input.ConversationId is not { } conversationId)
        {
            return ConversationState.Empty with { LastPresented = presented };
        }

        var stored = ConversationStateJson.Read(await db.SearchConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.StateJson)
            .FirstOrDefaultAsync(ct));
        if (stored is not null && stored.Version == input.AnswerNumber - 1)
        {
            return stored with { LastPresented = presented };
        }

        var rebuilt = await ConversationStateRebuild.RebuildAsync(db, conversationId, input.AnswerNumber, presented, ct);
        return stored is null
            ? rebuilt
            : rebuilt with
            {
                // What only the v2 record knows survives a rebuild forced by a turn v2 did not see.
                Rejected = stored.Rejected.Union(rebuilt.Rejected).ToList(),
                Liked = stored.Liked,
                ClarifiedOnce = stored.ClarifiedOnce,
            };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid?> FindByNameAsync(string name, CancellationToken ct)
    {
        var lowered = name.Trim().ToLowerInvariant();
        if (lowered.Length == 0)
        {
            return null;
        }

        return await db.Titles.AsNoTracking()
            .Where(t => t.Name.ToLower() == lowered)
            .OrderByDescending(t => t.TmdbVoteCount)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int> SeenCreditsAsync(Guid accountId, IReadOnlyList<PersonConstraint> people, MediaScope media, CancellationToken ct)
    {
        var seen = db.Titles.Where(t =>
            db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id)
            || db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id));
        var hard = new HardConstraints([], people, null, null, media);
        return await HardConstraintFilter.Apply(seen, db, hard).CountAsync(ct);
    }

    private async Task<string> PersonaLineAsync(Guid accountId, CancellationToken ct)
    {
        var line = (await PersonaAsync(accountId, ct)).Taste;
        return line.Length <= 300 ? line : line[..300];
    }

    /// <summary>The account's persona, built once per <see cref="PersonaTtl"/> (see there for why that is safe).</summary>
    private async Task<Domain.Lens.LensPersona> PersonaAsync(Guid accountId, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"askreel:persona:{accountId:N}");
        if (cache.TryGetValue<Domain.Lens.LensPersona>(key, out var hit) && hit is not null)
        {
            return hit;
        }

        var taste = await featureBuilder.BuildTasteStateAsync(accountId, DateTime.UtcNow, ct);
        var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
        cache.Set(key, persona, PersonaTtl);
        return persona;
    }

    /// <summary>The request the judge rules on: the message, in its conversation's requirements.</summary>
    internal static string JudgeRequest(string message, ConversationState state)
    {
        var parts = new List<string> { $"Request: {message.Trim()}" };
        if (state.Hard.Concepts.Count > 0)
        {
            parts.Add($"Requires: {string.Join(", ", state.Hard.Concepts.Select(c => c.UserWords))}");
        }

        if (state.Hard.People.Count > 0)
        {
            parts.Add($"People: {string.Join(", ", state.Hard.People.Select(p => $"{p.Name} ({p.Role.ToString().ToLowerInvariant()})"))}");
        }

        if (state.Hard.Years is { } years)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Years: {years.From?.ToString(CultureInfo.InvariantCulture) ?? "any"}-{years.To?.ToString(CultureInfo.InvariantCulture) ?? "any"}"));
        }

        if (state.Soft.Moods.Count > 0)
        {
            parts.Add($"Mood: {string.Join(", ", state.Soft.Moods)}");
        }

        if (state.Soft.Directions.Count > 0)
        {
            parts.Add($"Steer: {string.Join(", ", state.Soft.Directions.Select(d => $"{d.Trend.ToString().ToLowerInvariant()} {d.Attribute}"))}");
        }

        if (state.UserNegations.Count > 0)
        {
            parts.Add($"Rules out: {string.Join(", ", state.UserNegations)}");
        }

        if (state.Hard.Media != MediaScope.Any)
        {
            parts.Add($"Media: {state.Hard.Media.ToString().ToLowerInvariant()}");
        }

        return string.Join(". ", parts);
    }

    /// <summary>The previous answer's reply, without the handle suffix history lines carry.</summary>
    private static string? PreviousReply(IReadOnlyList<ChatTurn> history)
    {
        var text = history.LastOrDefault(t => t.Role == "assistant")?.Text;
        if (text is null)
        {
            return null;
        }

        var marker = text.IndexOf(AskReelHandles.HistoryMarker, StringComparison.Ordinal);
        return marker >= 0 ? text[..marker] : text;
    }

    private static bool Trips(Title title, string negation) =>
        NegationTerms.AnyLabelMatches(title.Keywords, negation) || NegationTerms.AnyLabelMatches(title.Genres, negation);

    private static decimal Floor(IReadOnlyList<ContentFilter> rows)
    {
        var raw = rows.FirstOrDefault(f => f.Kind == FilterKind.MinPredictedRating)?.Value;
        return raw is not null && decimal.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 10m)
            : 0m;
    }

    /// <summary>The state's requirements as the v1 intent shape, for conflict detection and for <c>IntentJson</c>.</summary>
    internal static SearchIntent IntentOf(HardConstraints hard) =>
        new(
            hard.Concepts.SelectMany(c => c.Genres).Distinct().ToList(),
            hard.Concepts.Where(c => c.Genres.Count == 0).Select(c => c.UserWords).ToList(),
            hard.Media switch
            {
                MediaScope.Movies => [MediaType.Movie],
                MediaScope.Shows => [MediaType.Show],
                _ => [],
            },
            string.Empty,
            hard.Years?.From,
            hard.Years?.To,
            hard.People.Select(p => new PersonQuery(p.Name, p.Role)).ToList(),
            MaxRuntimeMinutes: hard.MaxRuntimeMinutes);

    private static string IntentJsonOf(HardConstraints hard, ConversationState state) =>
        JsonSerializer.Serialize(IntentOf(hard) with { ExcludeKeywords = state.UserNegations });
}
