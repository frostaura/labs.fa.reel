using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// The conversational Ask Reel agent over OpenRouter (REEL_SEARCH_MODEL, default
/// <c>openai/gpt-5.4-mini</c> — the "small model crunching more data" lever). Two jobs: turn a
/// chat turn into a reply + discovery intent, and judge each candidate's hyper-personal fit for a
/// live re-rank. Stub mode (no key / TestHelpers:ModelStubMode) is deterministic: it reuses the
/// query interpreter for intent, returns a canned reply, and derives fit from the predicted rating
/// — so keyless dev and tests get the whole experience without spend.
/// </summary>
public class OpenRouterSearchAgent(
    HttpClient httpClient,
    ISearchQueryInterpreter interpreter,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterSearchAgent> logger,
    // Optional by design: the typed HttpClient is transient and is constructed inside a scope in
    // every real code path, but the adapter must still be `new`-able in tests without a scope.
    IAccountContext? accountContext = null)
    : ISearchAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The interpret completion's ceiling. The answer is one short reply sentence plus a small
    /// discovery spec — roughly 250 tokens at its widest, so 400 is headroom rather than a target.
    /// An UNBOUNDED completion here is the expensive shape: nothing about the response schema stops
    /// a model from monologuing, and the caller pays for every token of it.
    /// </summary>
    private const int InterpretTokenBudget = 400;

    /// <summary>
    /// The re-rank ceiling. The widest live batch is 9 titles (<c>BuildFeedJobHandler</c> and
    /// <c>FeedPaginator</c>; Ask Reel uses 6), each entry being a 36-char id, a number and a why
    /// capped at 160 chars by the prompt — about 75 tokens, so ~700 for a full batch. 900 leaves
    /// room for a model that pads its JSON without leaving room for one that rambles.
    /// </summary>
    private const int RerankTokenBudget = 900;

    /// <summary>
    /// A conversational reply is one short sentence by contract. 400 chars is the outer bound of
    /// "short"; past that the model has stopped answering and started writing, and the surface
    /// renders it above the grid where length costs the cards their space.
    /// </summary>
    private const int MaxReplyChars = 400;

    /// <summary>
    /// PROCESS-wide, not per-instance, and that is the whole point: this adapter is registered on a
    /// transient typed HttpClient and is rebuilt for every scope, so an instance flag would
    /// re-probe strict schema support on literally every call and re-pay the rejected request each
    /// time. One rejection retires strict mode for the process; the parser was always the real
    /// backstop and stays exactly as strict as it was.
    /// </summary>
    private static volatile bool _strictSchemaSupported = true;

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    // Honest availability: a real key, or an explicit test stub. A keyless production install used to
    // report available and present the deterministic stub's order as "LLM curation" — now the
    // curation pass is correctly skipped and callers fall back to pure model order. Mirrors
    // OpenRouterAttributeExtractor so every OpenRouter-backed capability gates identically.
    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        // Empty env vars are "present but blank" to .NET config, which defeats ?? — treat blank as absent.
        : configuration["REEL_SEARCH_MODEL"] is { Length: > 0 } m ? m
        : configuration["OPENROUTER_MODEL"] is { Length: > 0 } o ? o
        : "openai/gpt-5.4-mini";

    public async Task<AgentReply> InterpretAsync(
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
        CancellationToken ct = default, string? standingFilters = null)
    {
        // The interpreter's FREE deterministic reading, not a second billable round-trip. This
        // value is only ever a fallback — SearchIntentParser consumes it solely when the model's
        // own answer carries no usable signal, and StubReply only when the reply line is missing —
        // yet the agent used to buy a full chat completion for it on EVERY turn, doubling the
        // per-turn interpreter spend for output that is byte-identical whenever the primary call
        // succeeds. The paid interpreter is now consulted only where this turn has already failed.
        var intentFallback = interpreter.Heuristic(message);
        if (StubMode)
        {
            return new AgentReply(StubReply(message, intentFallback), intentFallback);
        }

        try
        {
            var user = BuildInterpretPrompt(history, message, tasteSummary, shownTitles, standingFilters);
            // schema: null deliberately. The interpret spec is a dozen fields wide and strict mode
            // requires every one of them declared and required, with nullability expressed as a
            // union type — that schema is Phase 3's, written against the finished field set rather
            // than guessed at here. The ceiling and the length guard below are the parts that stop
            // spend today, and they do not depend on it.
            var (content, usage) = await ChatAsync(InterpretSystemPrompt, user, InterpretTokenBudget, schema: null, ct);
            RecordCall(ApiUsagePurpose.SearchAgent, usage);
            if (!string.IsNullOrWhiteSpace(content))
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                var reply = root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s
                    ? CapReply(s) : StubReply(message, intentFallback);
                var intent = SearchIntentParser.FromJson(root, message, intentFallback);
                return new AgentReply(reply, intent);
            }

            logger.LogWarning("Agent interpret returned no content for '{Message}'; asking the interpreter.", message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent interpret failed for '{Message}'; asking the interpreter.", message);
        }

        // Reached only when this turn's own model call produced nothing usable. HERE a second call
        // earns its cost: the interpreter's prompt is smaller and single-purpose, so it often
        // answers where the agent's did not — and it degrades to the same free heuristic if it
        // fails too, which is exactly what the old code fell back to.
        // The standing filters ride the fallback too. This is THE drift the two-prompt gotcha is
        // about: the recovery path is the one a failing turn actually takes, so a rule the live
        // prompt honours and this one does not is invisible until a user is told about a title
        // their own filters exclude.
        var recovered = await interpreter.InterpretAsync(message, ct, standingFilters);
        return new AgentReply(StubReply(message, recovered), recovered);
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
            return titles.Select(StubRerank).ToList();
        }

        try
        {
            var user = BuildRerankPrompt(titles, query, persona, anchorName);
            var (content, usage) = await ChatAsync(
                RerankSystemPrompt, user, RerankTokenBudget, RerankSchema(), ct);
            RecordCall(usagePurpose, usage);
            if (string.IsNullOrWhiteSpace(content))
            {
                return titles.Select(StubRerank).ToList();
            }

            using var doc = JsonDocument.Parse(content);
            var byId = new Dictionary<Guid, RerankResult>();
            var rejected = 0;
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    // Per-ENTRY defence, deliberately. Every accessor below is kind-checked and the
                    // whole entry is wrapped, because the catch further down is batch-wide: one
                    // entry returning titleId as a JSON number used to throw
                    // InvalidOperationException out of GetString(), and every OTHER title in the
                    // batch then silently lost its model fit score to the deterministic stub. One
                    // bad entry costs exactly one entry.
                    if (!TryReadEntry(el, out var result))
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
                    "Agent rerank discarded {Rejected} malformed result entr(ies) of {Count}; those titles fall back to the deterministic fit.",
                    rejected, titles.Count);
            }

            // Row-aligned: any title the model skipped falls back to its deterministic stub fit.
            return titles.Select(t =>
            {
                if (!byId.TryGetValue(t.TitleId, out var res))
                {
                    return StubRerank(t);
                }

                // Output guard: a "why" that talks ABOUT the viewer ("their tastes", "the user's"),
                // opens with the banned "Because you loved" template, or blows the length contract
                // is discarded for the deterministic second-person sentence. The model's fit score
                // is kept — only the offending sentence is replaced.
                return SecondPersonVoice.Rejects(res.Why) ? res with { Why = StubRerank(t).Why } : res;
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent rerank failed ({Count} titles); using fallback fit.", titles.Count);
            return titles.Select(StubRerank).ToList();
        }
    }

    /// <summary>
    /// Reads one re-rank entry, or rejects it. Nothing in here may throw on hostile JSON: the
    /// caller iterates a whole batch and a throw would cost every title, not this one.
    ///
    /// A rejected entry is left OUT of the map on purpose. The caller then falls back to that
    /// title's deterministic stub fit, which is anchored on its personal prediction — strictly
    /// more informative than the flat 0.5 this used to invent for a missing or malformed score.
    /// </summary>
    private static bool TryReadEntry(JsonElement el, out RerankResult result)
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
            result = new RerankResult(id, Math.Clamp(raw, 0d, 1d), why);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records one billable call. Called AFTER the response so <c>usage.prompt_tokens</c> /
    /// <c>usage.completion_tokens</c> are in hand — the old ordering recorded before the request and
    /// could never see them. A call that throws before a response is not recorded here; the
    /// exception path is already logged and its cost is unknowable from this side.
    /// </summary>
    private void RecordCall(string purpose, OpenRouterUsage? usage) =>
        usageRecorder.Record(
            ApiProvider.OpenRouter,
            purpose,
            ModelId,
            accountContext?.AccountId,
            usage?.PromptTokens,
            usage?.CompletionTokens);

    /// <summary>
    /// One chat completion, with a token ceiling and — where a <paramref name="schema"/> is given
    /// and the provider still accepts one — strict <c>json_schema</c> enforcement. Lifted from
    /// <c>OpenRouterLensWriter.ChatAsync</c>, which had all three guards while the search agent,
    /// the one path a user waits on, had none of them.
    ///
    /// <para>A <c>length</c> finish is returned as a FAILURE rather than as content. This is the
    /// non-obvious half: a truncated discovery spec usually still parses, because the fields that
    /// were cut off are simply absent — so "80s sci-fi, no ghosts" silently became "80s sci-fi"
    /// with the negation missing, and nothing anywhere looked wrong. The caller's existing
    /// fallback (interpreter for intent, deterministic fit for re-rank) is a better answer than a
    /// half-answer that reads as a whole one.</para>
    /// </summary>
    private async Task<(string? Content, OpenRouterUsage? Usage)> ChatAsync(
        string system, string user, int maxTokens, object? schema, CancellationToken ct)
    {
        var strict = schema is not null && _strictSchemaSupported;
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = ModelId,
                temperature = 0,
                max_tokens = maxTokens,
                response_format = strict ? schema! : new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (strict && IsStrictSchemaRejection(response.StatusCode, await response.Content.ReadAsStringAsync(ct)))
            {
                _strictSchemaSupported = false;
                logger.LogWarning(
                    "OpenRouter rejected strict json_schema for {Model} (HTTP {Status}); falling back to "
                    + "json_object for the remainder of this process. Field validation now rests on the parser.",
                    ModelId, (int)response.StatusCode);
                return await ChatAsync(system, user, maxTokens, schema, ct);
            }

            // Anything else is the caller's existing failure path — it logs, and falls back.
            response.EnsureSuccessStatusCode();
        }

        var dto = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct);
        var choice = dto?.Choices?.FirstOrDefault();
        if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Search agent completion hit its {MaxTokens}-token ceiling for {Model}; discarding the truncated "
                + "object rather than parsing a partial answer as a whole one.",
                maxTokens, ModelId);
            return (null, dto?.Usage);
        }

        return (choice?.Message?.Content, dto?.Usage);
    }

    /// <summary>
    /// Whether a failure is the provider saying it cannot do strict schemas, as opposed to any
    /// other 4xx. Deliberately the same predicate as the lens writer's: a broader reading would
    /// retire strict mode on an unrelated bad request and never re-probe it.
    /// </summary>
    private static bool IsStrictSchemaRejection(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.NotImplemented or HttpStatusCode.NotFound))
        {
            return false;
        }

        return body.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || body.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || body.Contains("structured output", StringComparison.OrdinalIgnoreCase);
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
    internal static object RerankSchema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "reel_rerank",
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
                            required = new[] { "titleId", "fit", "why" },
                            properties = new Dictionary<string, object>
                            {
                                ["titleId"] = new { type = "string" },
                                ["fit"] = new { type = "number" },
                                ["why"] = new { type = "string", maxLength = 160 },
                            },
                        },
                    },
                },
            },
        },
    };

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

    private static string StubReply(string message, SearchIntent intent)
    {
        var lens = intent.Genres.Count > 0 ? intent.Genres[0] : intent.Keywords.Count > 0 ? intent.Keywords[0] : "your taste";
        // Capped like the model's own reply: this one interpolates the user's message, which the
        // endpoint bounds at 1,000 chars — comfortably longer than a "short sentence".
        return CapReply($"Here are some {lens} picks for “{message}” — leaning into what you tend to love.");
    }

    /// <summary>
    /// The hash-derived fit, masked rather than <c>Math.Abs</c>-ed. <c>Math.Abs(int.MinValue)</c>
    /// THROWS OverflowException — and this is the fallback path, reached from inside a batch loop
    /// whose whole purpose is to survive a bad response, so one unluckily-hashed title name would
    /// have taken down every other title's fit with it. Masking the sign bit is total.
    /// </summary>
    internal static double StableFit(int hash) => (hash & int.MaxValue) % 1000 / 1000d;

    private static RerankResult StubRerank(RerankInput t)
    {
        // Deterministic: anchor fit on the personal prediction when present, else a stable hash.
        var fit = t.Predicted is { } p
            ? Math.Clamp((double)p / 10d, 0d, 1d)
            : StableFit(t.Name.GetHashCode());
        var genre = t.Genres.Count > 0 ? t.Genres[0] : "this";
        return new RerankResult(t.TitleId, fit, $"A {genre} pick aligned with your taste.");
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

    private static string BuildInterpretPrompt(
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
    private const string InterpretSystemPrompt =
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
        "STANDING FILTERS: when the user's request collides with a standing filter given above — they ask for " +
        "something those rules exclude — say so plainly in reply and offer the closest thing that DOES pass, " +
        "instead of promising the excluded thing. Never put an excluded genre or term in genres/keywords. " +
        "Use the conversation + shown list to resolve references. Output strictly valid JSON.";

    private const string RerankSystemPrompt =
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

    private sealed record ChatResponse(ChatChoice[]? Choices, OpenRouterUsage? Usage);

    private sealed record ChatChoice(
        ChatMessage? Message, [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatMessage(string? Content);
}
