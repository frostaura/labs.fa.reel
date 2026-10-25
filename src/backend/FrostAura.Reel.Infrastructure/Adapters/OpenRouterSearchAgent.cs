using System.Globalization;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// The conversational Ask Reel agent over OpenRouter (REEL_SEARCH_MODEL, default
/// <c>openai/gpt-6-luna</c> — the "small model crunching more data" lever). Two jobs: turn a
/// chat turn into a reply + discovery intent, and judge each candidate's hyper-personal fit for a
/// live re-rank. Stub mode (no key / TestHelpers:ModelStubMode) is deterministic: it reuses the
/// query interpreter's free reading for intent and derives fit from the predicted rating, labelled
/// <see cref="ResultSource.Fallback"/>. The test flag answers with a fixed reply that claims
/// nothing; a keyless install answers DEGRADED, because it has no model and must not sound as if
/// it did.
/// </summary>
public class OpenRouterSearchAgent(
    HttpClient httpClient,
    ISearchQueryInterpreter interpreter,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterSearchAgent> logger,
    // Optional by design: the typed HttpClient is transient and is constructed inside a scope in
    // every real code path, but the adapter must still be `new`-able in tests without a scope.
    IAccountContext? accountContext = null,
    // The process-wide LLM platform pieces, DI singletons in production; null in a hand-built
    // adapter, which then gets private ones (see OpenRouterChatClient).
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null)
    : ISearchAgent
{
    /// <summary>
    /// The shared transport. The interpret ceiling (purpose <c>search-agent</c>) and the re-rank
    /// ceiling (each re-rank purpose) are per-purpose options now, not constants here: the old
    /// 400 and 900 were sized for gpt-5.4-mini and a reasoning model spent the 400 thinking.
    /// </summary>
    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate);

    /// <summary>
    /// A conversational reply is one short sentence by contract. 400 chars is the outer bound of
    /// "short"; past that the model has stopped answering and started writing, and the surface
    /// renders it above the grid where length costs the cards their space.
    /// </summary>
    private const int MaxReplyChars = 400;

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool TestStubMode => configuration.GetValue("TestHelpers:ModelStubMode", false);

    private bool StubMode => ApiKey is null || TestStubMode;

    // Honest availability: a real key, or an explicit test stub. A keyless production install used to
    // report available and present the deterministic stub's order as "LLM curation" — now the
    // curation pass is correctly skipped and callers fall back to pure model order. Mirrors
    // OpenRouterAttributeExtractor so every OpenRouter-backed capability gates identically.
    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    // REEL_SEARCH_MODEL, then OPENROUTER_MODEL, then luna — the rule moved into the purpose
    // catalog unchanged, where a per-purpose override can now also pin it (FD-1).
    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        : _chat.Options(ApiUsagePurpose.SearchAgent).Model;

    /// <summary>
    /// The result-format version, plus the model AND the reasoning effort this purpose is sent at:
    /// a verdict written at one effort is a different instrument from one written at another.
    /// </summary>
    public string CacheSalt(string usagePurpose) => StubMode
        ? string.Concat(RerankCacheVersion.Current, ":", ModelId)
        : string.Concat(RerankCacheVersion.Current, ":", _chat.CacheSalt(usagePurpose));

    public async Task<AgentReply> InterpretAsync(
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
        CancellationToken ct = default, string? standingFilters = null)
    {
        // The interpreter's FREE deterministic reading, not a billable round-trip. It rides every
        // reply as the UNLAUNDERED reading of the turn (see AgentReply.Requested), and it is the
        // intent of last resort when nothing better can be had.
        var intentFallback = interpreter.Heuristic(message);
        if (StubMode)
        {
            // Two different situations share StubMode. The explicit test flag stands in for a
            // WORKING model, so its reply is a deterministic answer that claims nothing; a keyless
            // install has no model at all, and saying otherwise is the exact dishonesty this type
            // now refuses — its reply is degraded and says so.
            return TestStubMode
                ? new AgentReply(StubModeReply, intentFallback, intentFallback)
                : new AgentReply(AgentReply.DegradedReply, intentFallback, intentFallback, Degraded: true, AgentFailureKind.NoModel);
        }

        var user = BuildInterpretPrompt(history, message, tasteSummary, shownTitles, standingFilters);
        var first = await TryInterpretAsync(user, message, intentFallback, ct);
        if (first.Reply is { } answered)
        {
            return answered;
        }

        // ONE retry of the agent itself, replacing the old switch to a second, history-blind prompt
        // that turned ordinals and negations into positive keywords. Only a CONTENT failure earns
        // it: the shared client has already retried a truncation once at twice the ceiling and a
        // transient failure up to the purpose's limit, so a second agent-level try at those would
        // be a third bill for the same answer.
        var last = first;
        if (first.Retryable)
        {
            logger.LogInformation("Agent interpret attempt failed ({Failure}); retrying once.", first.Failure);
            var second = await TryInterpretAsync(user, message, intentFallback, ct);
            if (second.Reply is { } recovered)
            {
                return recovered;
            }

            last = second;
        }

        // Every attempt failed, so the turn is DEGRADED whatever happens next: the reply is the
        // degraded copy, never a sentence built around the user's own words. What is left to
        // decide is only the best intent for the plain search underneath.
        var failure = last.Failure ?? AgentFailureKind.MalformedAnswer;
        SearchIntent intent;
        if ((last.ParsedIntent ?? first.ParsedIntent) is { } parsed)
        {
            // The model resolved the request and only the reply line was missing or unusable.
            intent = parsed;
        }
        else if (last.RecoverWithInterpreter)
        {
            // The interpreter's smaller single-purpose prompt often answers where this one did not.
            // It is handed the conversation, because a recovery path blind to "the second one" or
            // to a negation carried from an earlier turn answers a different question.
            intent = await interpreter.InterpretAsync(message, history, ct, standingFilters);
        }
        else
        {
            // The provider refused the key or timed out: a second prompt on the same provider is
            // another refusal or another wait, so the free reading carries the turn.
            intent = intentFallback;
        }

        logger.LogWarning("Agent interpret degraded ({Failure}) for this turn; the reply says so.", failure);
        return new AgentReply(AgentReply.DegradedReply, intent, intentFallback, Degraded: true, failure);
    }

    /// <summary>
    /// One interpret attempt. Never throws except for cancellation: every other failure comes back
    /// classified, because what the caller does next — retry, recover, or stop — depends on WHICH
    /// failure it was, and an exception erases that.
    /// </summary>
    private async Task<InterpretAttempt> TryInterpretAsync(
        string user, string message, SearchIntent intentFallback, CancellationToken ct)
    {
        // ResponseFormat null deliberately (json_object). The interpret spec is a dozen fields wide
        // and strict mode requires every one of them declared and required, with nullability
        // expressed as a union type — that schema is the planner's, written against the finished
        // field set.
        var completion = await _chat.CompleteAsync(
            new LlmChatRequest(
                ApiUsagePurpose.SearchAgent,
                [LlmMessage.System(InterpretSystemPrompt), LlmMessage.User(user)]),
            ct);
        if (!completion.Succeeded)
        {
            // Only an empty answer is worth another try; everything else either already had its
            // retry inside the client or cannot succeed on a second identical request.
            return InterpretAttempt.Failed(
                completion.AgentFailure!.Value, retryable: completion.Failure == LlmCallFailure.EmptyAnswer);
        }

        try
        {
            using var doc = JsonDocument.Parse(completion.Content!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InterpretAttempt.Failed(AgentFailureKind.MalformedAnswer);
            }

            var intent = SearchIntentParser.FromJson(root, message, intentFallback);
            if (root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String
                && r.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
            {
                // The heuristic rides along as the UNLAUNDERED reading of the turn. `intent` has
                // been through a prompt that was told what this account permanently excludes, so a
                // well-behaved model has already removed the excluded genre from it — and the
                // collision rung downstream cannot find what the model helpfully took out.
                return new InterpretAttempt(new AgentReply(CapReply(s), intent, intentFallback), null, null);
            }

            return InterpretAttempt.Failed(AgentFailureKind.MalformedAnswer, intent);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Agent interpret returned content that is not JSON.");
            return InterpretAttempt.Failed(AgentFailureKind.MalformedAnswer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Agent interpret returned a spec the parser could not read.");
            return InterpretAttempt.Failed(AgentFailureKind.MalformedAnswer);
        }
    }

    public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
        IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
        string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
    {
        if (titles.Count == 0)
        {
            return [];
        }

        if (StubMode)
        {
            return titles.Select((t, i) => FallbackVerdict(t, i)).ToList();
        }

        // The request check is asked ONLY where the query is the user's own request. The feed's
        // purposes judge fit to a row ("the best titles for you tonight"), where "is it what was
        // asked for" has no meaning — and their shared system prompt and schema stay byte-identical.
        var judgesRequest = JudgesRequest(usagePurpose);
        var user = BuildRerankPrompt(titles, query, persona, anchorName);
        var completion = await _chat.CompleteAsync(
            new LlmChatRequest(
                usagePurpose,
                [LlmMessage.System(judgesRequest ? AskRerankSystemPrompt : RerankSystemPrompt), LlmMessage.User(user)],
                judgesRequest ? AskRerankSchema() : RerankSchema(),
                Items: titles.Count),
            ct);
        if (!completion.Succeeded)
        {
            // Truncated (after the client's one 2x retry), refused, timed out, over budget or behind
            // an open breaker: every title in the batch is a FALLBACK — no model fit, no sentence.
            logger.LogWarning(
                "Agent rerank for {Purpose} produced no verdicts ({Failure}, {Count} titles); the batch is a labelled fallback.",
                usagePurpose, completion.Failure, titles.Count);
            return titles.Select((t, i) => FallbackVerdict(t, i)).ToList();
        }

        try
        {
            using var doc = JsonDocument.Parse(completion.Content!);
            var byId = new Dictionary<Guid, RerankResult>();
            var rejected = 0;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    // Per-ENTRY defence, deliberately. Every accessor below is kind-checked and the
                    // whole entry is wrapped, because the catch further down is batch-wide: one
                    // entry returning titleId as a JSON number used to throw
                    // InvalidOperationException out of GetString(), and every OTHER title in the
                    // batch then silently lost its model fit score. One bad entry costs exactly one
                    // entry.
                    if (!TryReadEntry(el, judgesRequest, out var result))
                    {
                        rejected++;
                        continue;
                    }

                    byId[result.TitleId] = result;
                }
            }

            if (rejected > 0)
            {
                logger.LogWarning(
                    "Agent rerank discarded {Rejected} malformed result entr(ies) of {Count}; those titles are labelled fallbacks.",
                    rejected, titles.Count);
            }

            // Row-aligned: any title the model skipped is a labelled fallback.
            return titles.Select((t, index) =>
            {
                if (!byId.TryGetValue(t.TitleId, out var res))
                {
                    return FallbackVerdict(t, index);
                }

                // Output guard: a "why" that talks ABOUT the viewer ("their tastes", "the user's"),
                // opens with the banned "Because you loved" template, or blows the length contract
                // is discarded. The model's fit score (and its request verdict) are kept — only the
                // offending sentence goes, and NOTHING is written in its place: the consumer shows
                // its own deterministic line, labelled as what it is.
                return SecondPersonVoice.Rejects(res.Why)
                    ? res with { Why = string.Empty, WhySource = ResultSource.Fallback }
                    : res;
            }).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Agent rerank returned content that is not JSON ({Count} titles); the batch is a labelled fallback.", titles.Count);
            return titles.Select((t, i) => FallbackVerdict(t, i)).ToList();
        }
    }

    /// <summary>
    /// True for the re-rank purposes whose query is the user's own request (Ask Reel's turn and the
    /// single-turn expansion) — the only ones that are asked whether each title satisfies it.
    /// </summary>
    internal static bool JudgesRequest(string usagePurpose) =>
        usagePurpose is ApiUsagePurpose.RerankAsk or ApiUsagePurpose.RerankExpand;

    /// <summary>
    /// Reads one re-rank entry, or rejects it. Nothing in here may throw on hostile JSON: the
    /// caller iterates a whole batch and a throw would cost every title, not this one.
    ///
    /// A rejected entry is left OUT of the map on purpose. The caller then falls back to that
    /// title's deterministic stub fit, which is anchored on its personal prediction — strictly
    /// more informative than the flat 0.5 this used to invent for a missing or malformed score.
    /// </summary>
    private static bool TryReadEntry(JsonElement el, bool judgesRequest, out RerankResult result)
    {
        result = default!;
        try
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("titleId", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idEl.GetString(), out var id))
            {
                return false;
            }

            if (!el.TryGetProperty("fit", out var f)
                || f.ValueKind != JsonValueKind.Number
                || !f.TryGetDouble(out var raw)
                || double.IsNaN(raw))
            {
                return false;
            }

            // A fit well outside 0-1 means the model scored on another scale entirely (0-10,
            // 0-100). Clamping would pin every such title to 1.0 and flatten the ranking it was
            // asked to produce, so the entry is rejected instead.
            if (raw < -0.01d || raw > 1.01d)
            {
                return false;
            }

            var why = el.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString() ?? string.Empty
                : string.Empty;

            // A missing or unreadable verdict leaves the entry's fit standing and the title
            // UNJUDGED (null) — never "no", which would drop a title the model did not reject.
            Satisfies? satisfies = judgesRequest
                && el.TryGetProperty("satisfies", out var sat) && sat.ValueKind == JsonValueKind.String
                ? LlmResultText.ParseSatisfies(sat.GetString())
                : null;
            result = new RerankResult(
                id, Math.Clamp(raw, 0d, 1d), ResultSource.Llm, why, ResultSource.Llm, satisfies);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The re-rank response shape, enforced by the provider where it can be. Every constraint here
    /// already exists in <see cref="TryReadEntry"/>; declaring it twice is the point — the parser
    /// discards a bad entry AFTER paying for it, the schema stops it being generated. titleId as a
    /// string rather than a number is the specific defect that cost a whole batch its fits.
    ///
    /// <para>Kept to constructs this codebase has already proven against the provider (the lens
    /// writer's schemas use exactly these); numeric bounds are left to the parser, which rejects an
    /// off-scale fit rather than clamping it.</para>
    /// </summary>
    internal static object RerankSchema() => Schema("reel_rerank", withSatisfies: false);

    /// <summary>
    /// The request-judging purposes' schema: the feed's, plus a required <c>satisfies</c> verdict
    /// per entry. A separate schema rather than an optional field, because strict mode cannot make
    /// a field optional — and the feed's must stay exactly as it was.
    /// </summary>
    internal static object AskRerankSchema() => Schema("reel_rerank_ask", withSatisfies: true);

    private static object Schema(string name, bool withSatisfies)
    {
        var properties = new Dictionary<string, object>
        {
            ["titleId"] = new { type = "string" },
            ["fit"] = new { type = "number" },
            ["why"] = new { type = "string", maxLength = 160 },
        };
        if (withSatisfies)
        {
            properties["satisfies"] = new { type = "string", @enum = SatisfiesVocabulary };
        }

        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name,
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "results" },
                    properties = new Dictionary<string, object>
                    {
                        ["results"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                // Strict mode requires EVERY property to be listed as required.
                                required = properties.Keys.ToArray(),
                                properties,
                            },
                        },
                    },
                },
            },
        };
    }

    private static readonly string[] SatisfiesVocabulary =
        [LlmResultText.Wire(Satisfies.Yes), LlmResultText.Wire(Satisfies.Partial), LlmResultText.Wire(Satisfies.No)];

    /// <summary>
    /// Bounds the conversational reply, cutting at a SENTENCE boundary so the result reads as a
    /// finished thought rather than a severed one. A hard cut is the fallback, and it backs off a
    /// trailing high surrogate — half an emoji is not a character.
    /// </summary>
    internal static string CapReply(string reply)
    {
        if (reply.Length <= MaxReplyChars)
        {
            return reply;
        }

        var window = reply[..MaxReplyChars];
        var boundary = window.LastIndexOfAny(['.', '!', '?', '\u2026']);

        // A boundary in the first fifth of the window is a stray abbreviation, not the end of a
        // sentence — cutting there would throw away most of an answer the user can read.
        if (boundary >= MaxReplyChars / 5)
        {
            return window[..(boundary + 1)];
        }

        return (char.IsHighSurrogate(window[^1]) ? window[..^1] : window).TrimEnd() + '\u2026';
    }

    /// <summary>
    /// The test double's reply (TestHelpers:ModelStubMode). Fixed and deterministic, and it claims
    /// nothing — no echo of the message, no genre reading, no promise about what comes back.
    /// </summary>
    internal const string StubModeReply = "Looking through your catalogue for that.";

    /// <summary>
    /// The fit a fallback carries for a title with no personal prediction: its position in the
    /// batch as the caller ordered it (retrieval order), strictly decreasing and inside [0, 0.5].
    /// It used to be a hash of the title's NAME — and .NET randomises string hashes per process,
    /// so the same fallback batch came back in a different order after every restart while the
    /// comment promised it kept retrieval order. A position is what that promise means.
    /// </summary>
    internal static double PositionFit(int index) => 0.5d / (1d + Math.Max(0, index));

    /// <summary>
    /// The verdict for a title the model did not judge: a failed or skipped batch, a malformed
    /// entry, or stub mode. Labelled <see cref="ResultSource.Fallback"/> on both halves, and the
    /// sentence is EMPTY — the old stub wrote "A {genre} pick aligned with your taste." here, which
    /// was labelled model output, cached for six hours and logged on thousands of append-only
    /// exposure rows. No sentence is invented for a title nobody judged.
    ///
    /// <para>The fit is still a number in this revision, and only as a RANKING value: the personal
    /// prediction scaled to 0–1, else the title's <paramref name="index"/> in the batch (see
    /// <see cref="PositionFit"/>), so a fallback batch keeps a stable, reproducible order instead of
    /// collapsing. The task-4 flip makes it null; until then <see cref="RerankResult.ModelFit"/> is
    /// null for it and <see cref="RerankResult.Cacheable"/> is false — consumers must read those.</para>
    /// </summary>
    internal static RerankResult FallbackVerdict(RerankInput t, int index)
    {
        var fit = t.Predicted is { } p
            ? Math.Clamp((double)p / 10d, 0d, 1d)
            : PositionFit(index);
        return new RerankResult(t.TitleId, fit, ResultSource.Fallback, string.Empty, ResultSource.Fallback);
    }

    /// <summary>
    /// The line that carries the account's standing content rules into a prompt. Single-sourced
    /// and internal because BOTH Ask Reel prompts must carry it identically — this agent's live
    /// prompt and <see cref="OpenRouterSearchInterpreter"/>'s fallback. A session was already lost
    /// to that pair drifting once, when maxYear reached one prompt and not the other.
    /// </summary>
    internal static string StandingFiltersLine(string filters) =>
        "Standing filters (the account's own rules; the answer space is limited to titles that pass them): "
        + filters + '\n';

    internal static string BuildInterpretPrompt(
        IReadOnlyList<ChatTurn> history, string message, string taste, IReadOnlyList<string> shown,
        string? standingFilters = null)
    {
        var sb = new StringBuilder();
        sb.Append("Your taste: ").Append(taste).Append('\n');

        // Beside taste, deliberately: both describe WHO is asking rather than what was asked, and
        // the model reads the pair as one frame. In the USER message rather than the system one
        // because it is per-account data, not policy.
        if (standingFilters is { Length: > 0 } filters)
        {
            sb.Append(StandingFiltersLine(filters));
        }
        if (history.Count > 0)
        {
            sb.Append("Conversation so far:\n");
            foreach (var turn in history.TakeLast(8))
            {
                sb.Append(turn.Role).Append(": ").Append(turn.Text).Append('\n');
            }
        }

        if (shown.Count > 0)
        {
            sb.Append("Already shown this session (do not repeat; resolve references like \"the second one\" against this list): ")
                .Append(string.Join("; ", shown.Take(20))).Append('\n');
        }

        sb.Append("New request: ").Append(message);
        return sb.ToString();
    }

    /// <summary>
    /// Internal (not private) so the payload contract is pinned by tests: the anchor's NAME and
    /// each candidate's overview must actually reach the model, or the "name the concrete
    /// connection" instruction has nothing to work with and the whys collapse back to generic.
    /// </summary>
    internal static string BuildRerankPrompt(
        IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null)
    {
        var sb = new StringBuilder();
        sb.Append("THE VIEWER\n").Append(persona.Taste).Append('\n');

        // The declined line was already riding the Ask Reel prompts and had never reached the feed,
        // so the feed's judge had never been told a single thing this person dislikes.
        if (persona.Declined is { Length: > 0 } declined)
        {
            sb.Append(declined).Append('\n');
        }

        sb.Append("Your request: ").Append(query).Append('\n');
        if (!string.IsNullOrWhiteSpace(anchorName))
        {
            sb.Append("Anchor title: ").Append(anchorName)
                .Append(" — each candidate sits on a rail anchored to this title you loved. In each why, name the CONCRETE link to ")
                .Append(anchorName)
                .Append(" (shared tone, premise, craft, or people), never just that you loved it.\n");
        }

        sb.Append("Score each title 0.0-1.0 for how well it fits YOU and your request, with a one-line reason addressed to you:\n");

        // Candidate order is SHUFFLED deterministically per batch (see RerankOrder). LLM rankers
        // score items later in a prompt lower — a documented position bias — and the caller
        // averages a title's fit across the batches it appears in, so the shuffle is what turns
        // that bias into noise that averaging can cancel instead of a systematic penalty on
        // whatever the model ranked last.
        foreach (var t in RerankOrder.Shuffle(titles, query, anchorName))
        {
            sb.Append("- id=").Append(t.TitleId).Append(" | ").Append(t.Name);
            if (t.Year is { } y)
            {
                sb.Append(" (").Append(y.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            sb.Append(" | ").Append(t.MediaType).Append(" | ").Append(string.Join(", ", t.Genres));
            if (t.Predicted is { } p)
            {
                // InvariantCulture, because this string goes into an LLM PROMPT. The dev machine
                // runs en_ZA, where the default format renders 6.8 as "6,8" — a decimal comma in a
                // list whose separator is already a comma, sent to a model asked to reason about
                // the number. The host's locale has no business changing what the model reads.
                sb.Append(" | predicted ").Append(p.ToString("0.0", CultureInfo.InvariantCulture));
            }

            AppendContext(sb, t.Context);

            if (!string.IsNullOrWhiteSpace(t.Overview))
            {
                // ~300 chars of overview: enough plot premise for the why to cite something
                // specific (a setting, a hook, a character) instead of re-summarising the genres.
                sb.Append(" | ").Append(t.Overview!.Length > 300 ? t.Overview[..300] : t.Overview);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The enrichment the prompt used to leave on the floor: tone, era, themes, the seven scalars,
    /// the people, and the model's own top contributions. Every field is omitted rather than
    /// zero-filled when it is missing — a title with no attributes should read as unenriched, not
    /// as uniformly neutral.
    /// </summary>
    private static void AppendContext(StringBuilder sb, RerankTitleContext? context)
    {
        if (context is null)
        {
            return;
        }

        var c = CultureInfo.InvariantCulture;
        if (context.Tone is { Length: > 0 } tone)
        {
            sb.Append(" | tone ").Append(tone);
        }

        if (context.Era is { Length: > 0 } era)
        {
            sb.Append(" | set ").Append(era);
        }

        if (context.Themes.Count > 0)
        {
            sb.Append(" | themes ").Append(string.Join(", ", context.Themes.Take(4)));
        }

        if (context.Darkness is not null)
        {
            sb.Append(string.Format(
                c,
                " | dark {0:0.00} pace {1:0.00} complex {2:0.00} intense {3:0.00} funny {4:0.00} hopeful {5:0.00} ensemble {6:0.00}",
                context.Darkness, context.Pacing, context.Complexity, context.EmotionalIntensity,
                context.Humor, context.Optimism, context.EnsembleVsSolo));
        }

        var people = new List<string>();
        if (context.Director is { Length: > 0 } director)
        {
            people.Add("dir " + director);
        }

        if (context.Writer is { Length: > 0 } writer)
        {
            people.Add("wr " + writer);
        }

        if (context.TopCast.Count > 0)
        {
            people.Add(string.Join(", ", context.TopCast.Take(3)));
        }

        if (people.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(" · ", people));
        }

        if (context.TopContributions.Count > 0)
        {
            sb.Append(" | picked for ").Append(string.Join(", ", context.TopContributions.Take(3)));
        }
    }

    /// <summary>
    /// Ask Reel routes through THIS prompt, not <c>OpenRouterSearchInterpreter.SystemPrompt</c> —
    /// the two must be kept in step field-for-field, because both are parsed by the same
    /// <see cref="SearchIntentParser"/>. A field the parser reads but this prompt never asks for
    /// is simply always absent: "80s sci-fi movies" returned 2026/1996/2012/1993 titles for
    /// exactly that reason, since only minYear was requested and the upper bound never existed.
    /// </summary>
    internal const string InterpretSystemPrompt =
        "You are Reel, a sharp, warm film & TV recommendation companion. Reply conversationally in ONE short sentence, " +
        "then resolve the request into a discovery spec. Return ONLY a JSON object: reply (string), genres (array of " +
        "slugs from EXACTLY: action, adventure, animation, comedy, crime, documentary, drama, family, fantasy, history, " +
        "horror, music, mystery, romance, science-fiction, thriller, war, western), keywords (array of up to 5 concept " +
        "phrases), people (array of {name, role} where role is \"director\", \"cast\", or \"any\" — extract any named " +
        "filmmaker or actor; e.g. \"movies Christopher Nolan directs\" → people:[{\"name\":\"Christopher Nolan\",\"role\":\"director\"}], " +
        "\"films with Tom Hardy\" → people:[{\"name\":\"Tom Hardy\",\"role\":\"cast\"}]; do NOT put a person's name in keywords " +
        "or freeText), mediaTypes (array of \"movie\"/\"tv\"; empty = both), minYear (integer or null), maxYear (integer or " +
        "null), freeText (literal-title search string), excludeGenres (array of the same genre slugs), excludeKeywords " +
        "(array of concept phrases), referenceTitles (array of movie/show titles the user names as COMPARISONS — " +
        "\"like Blade Runner but more emotional\" → referenceTitles:[\"Blade Runner\"], \"similar to Severance\" → " +
        "referenceTitles:[\"Severance\"]; extract only explicitly named titles, keep them OUT of keywords and freeText) " +
        "and maxRuntimeMinutes (integer or null — an upper runtime bound in minutes: \"under 90 minutes\" → 90, " +
        "\"quick watch\"/\"short film\" → 100; null when the user gives no length constraint). " +
        "NEGATION IS CRITICAL: anything the user rejects or wants less of — \"less horror\", \"no ghosts\", \"not a musical\", " +
        "\"I don't want X\" — belongs ONLY in excludeGenres/excludeKeywords and MUST NOT appear in genres/keywords, because " +
        "those are used to FETCH titles and would hand them exactly what they just rejected. e.g. \"more thriller and less " +
        "horror, I don't want ghosts I want serial killers\" → genres:[\"thriller\",\"crime\"], keywords:[\"serial killer\"], " +
        "excludeGenres:[\"horror\"], excludeKeywords:[\"ghost\",\"supernatural\"]. Rejection lists can be long: put EVERY " +
        "rejected item in the exclude arrays, not just the first few. Carry negations forward across turns unless " +
        "the user retracts them. " +
        "YEAR RANGES: a decade means both bounds — \"80s\" is minYear 1980 and maxYear 1989, \"from the 90s\" is 1990-1999. " +
        "\"before 2000\" sets only maxYear; \"after 2015\" sets only minYear. Do not infer a year from a title that merely " +
        "looks like one (\"1917\", \"300\", \"2001\"). " +
        "STANDING FILTERS: when the request collides with a rule listed under \"Standing filters\" — they ask for " +
        "something those rules exclude — say so plainly in reply and offer the closest thing that DOES pass, " +
        "instead of promising the excluded thing. Never put an excluded genre or term in genres/keywords. " +
        "Use the conversation + shown list to resolve references. Output strictly valid JSON.";

    internal const string RerankSystemPrompt =
        "You are Reel's taste engine, speaking directly to the viewer whose taste and request are given. For each " +
        "title, judge how well it fits YOU — that viewer — factoring your taste, the request's mood/soft-constraints " +
        "(e.g. \"nothing too gory\", \"feel-good\"), and the title itself. Return ONLY a JSON object " +
        "{\"results\":[{\"titleId\":\"<id>\",\"fit\":0.0-1.0,\"why\":\"<one short line>\"}]} " +
        "with one entry per title. The why sentence MUST address the reader as \"you\"/\"your\" (\"you rate…\", " +
        "\"your love of…\") and MUST NEVER say \"they\", \"their\", \"them\" or \"the user\". " +
        "Each why MUST be at most 160 characters and MUST cite something SPECIFIC about THAT title — its plot " +
        "premise, tone, era, craft, or a person by name from its overview. Generic filler (\"matches your taste\", " +
        "\"fits your profile\", \"a great pick for you\") is FORBIDDEN. When an anchor title is given, say what " +
        "actually links the candidate to it — a shared premise, mood, or craft (e.g. \"shares the anchor's " +
        "slow-burn small-town dread\") — NEVER just that you loved the anchor. NEVER start a why with \"Because " +
        "you loved\". Vary the sentence openers across the batch — no two whys may start with the same words. " +
        "titleId MUST be the id string quoted exactly as given, never a number. fit MUST be a " +
        "bare JSON number between 0.0 and 1.0 — never a 0-10 or 0-100 score and never a string. " +
        "Judge fit against WHO THE VIEWER IS as described above — not against how popular, " +
        "well-known or widely-watched a title is. A format that fills schedules (reality, talk, " +
        "game and panel shows) earns a high fit ONLY if the viewer description actually says they " +
        "enjoy that kind of thing; its reach is not evidence about this person. " +
        "Be discerning — spread the scores. Output strictly valid JSON.";

    /// <summary>
    /// The request-judging purposes' system prompt: the feed's prompt, UNTOUCHED, followed by the
    /// request check. Appended rather than edited in, so the feed's prompt stays byte-identical.
    /// </summary>
    internal const string AskRerankSystemPrompt = RerankSystemPrompt + " " +
        "ALSO, for every title, include \"satisfies\": \"yes\", \"partial\" or \"no\" — whether the title IS " +
        "what the request asks for, judged only from what is given about the title (its genres, overview, themes, " +
        "tone and people), and SEPARATELY from whether you would like it. \"yes\": it plainly is the thing asked " +
        "for, every concept the request names. \"partial\": it matches some of the request, or only loosely. " +
        "\"no\": it is not what was asked for — an action film with no ninjas is \"no\" for \"ninja movies\", " +
        "however well it fits your taste. Never answer \"yes\" because a title is merely good or similar in mood.";

    /// <summary>One interpret attempt's outcome: an answer, or a classified failure.</summary>
    private sealed record InterpretAttempt(AgentReply? Reply, AgentFailureKind? Failure, SearchIntent? ParsedIntent)
    {
        /// <summary>Content failures (an empty or unreadable answer) are worth exactly one more try.</summary>
        public bool Retryable { get; private init; }

        /// <summary>
        /// False where a second prompt can only fail the same way or must not be sent at all: the
        /// provider refused the key, the clock ran out, a breaker is open, or the day's allowance is
        /// spent.
        /// </summary>
        public bool RecoverWithInterpreter => Failure is not (AgentFailureKind.ProviderRefused
            or AgentFailureKind.TimedOut or AgentFailureKind.ProviderUnavailable or AgentFailureKind.BudgetSpent);

        public static InterpretAttempt Failed(AgentFailureKind failure, SearchIntent? parsed = null, bool retryable = true) =>
            new(null, failure, parsed)
            {
                Retryable = retryable && failure is AgentFailureKind.EmptyAnswer or AgentFailureKind.MalformedAnswer,
            };
    }
}
