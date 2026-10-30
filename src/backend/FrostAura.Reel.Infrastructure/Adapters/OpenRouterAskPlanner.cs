using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Ask Reel v2's planner (§4.3, §4.5): one <c>ask-plan</c> call with a strict JSON schema, at the
/// purpose's effort (none). It reads the message in its conversation and returns an act, a state
/// delta, the handles it refers to and the tools it wants — never prose for a search act.
///
/// <para><b>What this adapter does NOT decide.</b> Whether a negation is really the user's, whether
/// a handle was really shown, whether a concept is really a standing condition copied back: those
/// are checked server-side by <see cref="PlanSanitizer"/>, because a prompt rule is a request and a
/// sanitiser is a guarantee.</para>
///
/// <para><b>Retry.</b> A truncated or timed-out answer is asked once more at twice the token budget
/// (§4.10); a second failure comes back as a failed plan and the engine carries the state forward. A
/// spent allowance, an open breaker or a refused key is not retried — those end the turn.</para>
/// </summary>
public sealed class OpenRouterAskPlanner(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterAskPlanner> logger,
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null) : IAskReelPlanner
{
    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => configuration["OPENROUTER_API_KEY"] is { Length: > 0 };

    public string ModelId => _chat.Options(ApiUsagePurpose.AskPlan).Model;

    /// <summary>A fingerprint of the prompt and schema, for the turn record.</summary>
    public static string PromptFingerprint { get; } = Fingerprint(SystemPrompt + JsonSerializer.Serialize(Schema()));

    public async Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var messages = Messages(input);
        var options = _chat.Options(ApiUsagePurpose.AskPlan);
        var first = await _chat.CompleteAsync(new LlmChatRequest(ApiUsagePurpose.AskPlan, messages, Schema()), ct);
        var attempt = Read(first, input, PlanSource.Llm);
        if (attempt.Failure is { Refusal: false } failure
            && (failure.Kind is AgentFailureKind.Truncated or AgentFailureKind.TimedOut or AgentFailureKind.MalformedAnswer or AgentFailureKind.EmptyAnswer))
        {
            logger.LogWarning("Ask plan failed ({Failure}); retrying once at twice the budget.", failure.Kind);
            var retry = await _chat.CompleteAsync(
                new LlmChatRequest(ApiUsagePurpose.AskPlan, messages, Schema(), MaxTokens: options.MaxTokensFor(0) * 2), ct);
            var second = Read(retry, input, PlanSource.LlmRetry);
            return second with
            {
                Meta = second.Meta with
                {
                    Truncated = first.Failure == LlmCallFailure.Truncated || second.Meta.Truncated,
                    Attempts = first.HttpAttempts + retry.HttpAttempts,
                    ElapsedMs = (long)(first.Elapsed + retry.Elapsed).TotalMilliseconds,
                },
            };
        }

        return attempt;
    }

    private PlanResult Read(LlmChatResult completion, PlanInput input, PlanSource source)
    {
        var meta = new LlmCallMeta(
            completion.Model, completion.Effort, completion.Failure == LlmCallFailure.Truncated,
            completion.HttpAttempts, (long)completion.Elapsed.TotalMilliseconds, completion.ReasonCode);
        if (!completion.Succeeded)
        {
            var kind = completion.AgentFailure ?? AgentFailureKind.Unknown;
            var refusal = kind is AgentFailureKind.BudgetSpent or AgentFailureKind.ProviderUnavailable or AgentFailureKind.ProviderRefused;
            return PlanResult.Failed(new PlanFailure(kind, completion.ReasonCode, refusal), meta);
        }

        using var doc = LlmJson.FirstObject(completion.Content, out _);
        if (doc is null)
        {
            logger.LogWarning("Ask plan answer held no readable JSON object ({Chars} chars).", completion.Content?.Length ?? 0);
            return PlanResult.Failed(new PlanFailure(AgentFailureKind.MalformedAnswer, null, false), meta);
        }

        try
        {
            return Parse(doc.RootElement, source, meta);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Ask plan answer did not match the schema.");
            return PlanResult.Failed(new PlanFailure(AgentFailureKind.MalformedAnswer, null, false), meta);
        }
    }

    /// <summary>The model's JSON as a plan. Internal so the reading is pinned by unit tests.</summary>
    internal static PlanResult Parse(JsonElement root, PlanSource source, LlmCallMeta meta)
    {
        var act = Enum.TryParse<TurnAct>(Str(root, "act"), ignoreCase: true, out var a) && Enum.IsDefined(a) ? a : TurnAct.NewRequest;
        var people = Arr(root, "people")
            .Where(p => p.ValueKind == JsonValueKind.Object)
            .Select(p => new PersonConstraint(
                Str(p, "name") ?? string.Empty,
                null,
                Str(p, "role") switch
                {
                    "director" => PersonRole.Director,
                    "cast" => PersonRole.Cast,
                    _ => PersonRole.Any,
                }))
            .Where(p => p.Name.Length > 0)
            .ToList();
        int? min = Int(root, "minYear");
        int? max = Int(root, "maxYear");
        MediaScope? media = Str(root, "media") switch
        {
            "movie" => MediaScope.Movies,
            "tv" => MediaScope.Shows,
            "any" => MediaScope.Any,
            _ => null,
        };
        var directions = Arr(root, "directions")
            .Where(d => d.ValueKind == JsonValueKind.Object && Str(d, "attribute") is { Length: > 0 })
            .Select(d => new AttributeDirection(Str(d, "attribute")!, Str(d, "trend") == "less" ? AttributeTrend.Less : AttributeTrend.More))
            .ToList();
        var delta = new ConversationStateDelta(
            Strings(root, "concepts"),
            // The schema is no longer strict, so its genre enum is enforced here.
            [.. Strings(root, "genres").Where(g => Genres.Contains(g, StringComparer.Ordinal))],
            people,
            min is null && max is null ? null : new YearRange(min, max),
            Int(root, "maxRuntimeMinutes"),
            media,
            Strings(root, "moods"),
            directions,
            Strings(root, "negations"),
            Strings(root, "referenceNames"));
        var references = Strings(root, "references").Select(TurnHandle.Parse).OfType<TurnHandle>().Distinct().ToList();
        var tools = Strings(root, "tools")
            .Select(t => t switch
            {
                "search_catalog" => AskTool.SearchCatalog,
                "similar_to" => AskTool.SimilarTo,
                "title_facts" => AskTool.TitleFacts,
                "filmography" => AskTool.Filmography,
                "supply" => AskTool.Supply,
                "conditions" => AskTool.Conditions,
                _ => (AskTool?)null,
            })
            .OfType<AskTool>()
            .Distinct()
            .Select(t => new ToolRequest(t))
            .ToList();
        var clarify = Str(root, "clarifyQuestion");
        return new PlanResult(
            act, delta, references, tools,
            string.IsNullOrWhiteSpace(clarify) ? null : clarify.Trim(),
            Strings(root, "clarifyOptions"),
            source, meta,
            Str(root, "question"));
    }

    /// <summary>
    /// The messages (§4.5 planner input): the system prompt; the last three turns as real chat
    /// messages, each assistant line a one-line summary plus its handles; then one user message
    /// carrying the compact state, the handles on screen, the standing conditions (read-only), the
    /// taste summary, the media filter and the message itself.
    /// </summary>
    internal static IReadOnlyList<LlmMessage> Messages(PlanInput input)
    {
        var messages = new List<LlmMessage> { LlmMessage.System(SystemPrompt) };
        foreach (var turn in input.RecentTurns.TakeLast(6))
        {
            messages.Add(new LlmMessage(turn.Role == "assistant" ? "assistant" : "user", Cap(turn.Text, 600)));
        }

        var state = input.State;
        var context = new
        {
            state = new
            {
                concepts = state.Hard.Concepts.Select(c => c.UserWords).ToList(),
                people = state.Hard.People.Select(p => $"{p.Name} ({p.Role.ToString().ToLowerInvariant()})").ToList(),
                years = state.Hard.Years is { } y ? $"{y.From?.ToString(CultureInfo.InvariantCulture) ?? ""}-{y.To?.ToString(CultureInfo.InvariantCulture) ?? ""}" : null,
                maxRuntimeMinutes = state.Hard.MaxRuntimeMinutes,
                media = MediaWord(state.Hard.Media),
                moods = state.Soft.Moods,
                negations = state.UserNegations,
            },
            onScreen = state.LastPresented
                .Take(20)
                .Select(p => p.Year is { } year
                    ? string.Create(CultureInfo.InvariantCulture, $"{p.Handle} {p.Name} ({year})")
                    : $"{p.Handle} {p.Name}")
                .ToList(),
            standingConditions = input.Conditions.Summary ?? "none",
            taste = Cap(input.TasteSummary, TasteChars),
            // The app's own filter, applied by the server. "none" rather than "any" when unset: the
            // planner copied "any" into media and "samurai films" searched shows (HREV1-ASK-02).
            mediaFilter = input.MediaFilter == MediaScope.Any ? "none" : MediaWord(input.MediaFilter),
            clarifiedAlready = state.ClarifiedOnce,
            message = input.Message,
        };
        messages.Add(LlmMessage.User(JsonSerializer.Serialize(context, Json)));
        return messages;
    }

    /// <summary>
    /// How much of the taste summary the planner reads. It steers how an open request is read, never
    /// what is eligible; 600 characters keep its lead lines and cut ~150 input tokens a call against
    /// §4.11's ~1.2k plan budget (the live plan input measured ~1.87k tokens, HREV1-ASK-05).
    /// </summary>
    internal const int TasteChars = 600;

    internal const string SystemPrompt =
        "You are the planner for Reel, a film and TV recommender. You never write prose for the user. You read the " +
        "user's newest message in its conversation and return ONE JSON object that classifies it and says what to " +
        "search. The server runs the search, judges the results and writes every reply itself.\n" +
        "ACTS: NewRequest = a fresh request unrelated to what is on screen. Refine = narrows or adjusts the current " +
        "request (\"only movies\", \"no superheroes please\", \"older ones\", \"funnier\"). CritiqueSet = rejects what is " +
        "on screen as a whole (\"none of these are ninja movies\"). CritiqueItem = asks for a change relative to " +
        "specific cards (\"more heart than T2.1\", \"less violent than the first one\"), or rejects one (\"not the first one\", " +
        "\"I didn't like the second one\"). ItemQuestion = a question " +
        "about one title or person, or about the supply or the conditions, that wants an answer, not new cards " +
        "(\"is the first one violent?\", \"who directed T1.3?\", \"how many ninja movies are left?\"). Chitchat = " +
        "greetings, thanks, anything with no request.\n" +
        "FIELDS: concepts = topics the user REQUIRES, in their own words, singular and lower-case (\"ninja movies\" -> " +
        "[\"ninja\"]); never a mood, never a person, never a title, never a word the user negated. genres = genre slugs " +
        "the user names as a requirement (\"comedies\" -> [\"comedy\"]). people = named directors or actors. " +
        "minYear/maxYear: a decade means both bounds (\"80s\" -> 1980, 1989). media = movie when the message says " +
        "film(s)/movie(s), tv when it says show(s)/series/tv as the KIND of title (\"zombie shows\", \"a show\") — " +
        "never the verb in \"show me\"/\"show us\", which names no medium — any ONLY when it asks for both, " +
        "unchanged when it names neither; mediaFilter is the app's own filter, applied by the server — never copy it into media. " +
        "moods = soft feelings (\"feel-good\", \"cosy\", \"tense\"). directions = attribute steers: attribute is one of " +
        "darkness, pacing, complexity, intensity, humor, optimism; trend more|less (\"funnier\" -> humor more). " +
        "negations = ONLY words the user says they do not want, copied from their message (\"no superheroes please\" " +
        "-> [\"superheroes\"]). references = handles from onScreen the message points at (\"the second one\" -> the " +
        "handle at rank 2 of the newest answer); ONLY handles listed in onScreen. referenceNames = every title the " +
        "message NAMES, exactly as written: a comparison (\"like John Wick\" -> [\"John Wick\"]) or the title a question " +
        "is about (\"is Heat violent?\" -> [\"Heat\"]). tools = what the server should run: search_catalog, " +
        "similar_to, title_facts, filmography, supply, conditions. question = for ItemQuestion, the question in the " +
        "user's words, else null.\n" +
        "RULES: Never copy a standing condition into any field — they are applied by the server already. \"None of " +
        "these/the above are X\" is CritiqueSet with X as a concept, NOT a negation. A word the user asks for again " +
        "after rejecting it is a concept. Ask a clarifying question (clarifyQuestion + 2-3 clarifyOptions) only when " +
        "two readings differ in what they REQUIRE, and never when clarifiedAlready is true; otherwise clarifyQuestion " +
        "is null and clarifyOptions empty.\n" +
        "EXAMPLES: \"ninja movies\" -> {act:NewRequest, concepts:[ninja], media:movie, tools:[search_catalog]}. " +
        "\"samurai films\" -> {act:NewRequest, concepts:[samurai], media:movie}. \"Liam Neeson movies\" -> " +
        "{act:NewRequest, people:[{name:Liam Neeson, role:cast}], media:movie, tools:[filmography]}. \"zombie shows\" -> " +
        "{act:NewRequest, concepts:[zombie], media:tv}. \"show me samurai films\" -> {act:NewRequest, concepts:[samurai], " +
        "media:movie}. \"show me something feel-good\" -> {act:NewRequest, moods:[feel-good], media:unchanged}. " +
        "\"feel-good movie\" -> {act:NewRequest, moods:[feel-good], media:movie}. \"no superheroes please\" -> " +
        "{act:Refine, negations:[superheroes], media:unchanged}. \"like John Wick but funnier\" -> {act:NewRequest, " +
        "referenceNames:[John Wick], directions:[{attribute:humor, trend:more}], tools:[similar_to]}. \"none of the " +
        "above are ninja movies. I want ninja movies\" -> {act:CritiqueSet, concepts:[ninja], media:unchanged}. " +
        "\"more like the second one\" -> {act:Refine, references:[<rank-2 handle of the newest answer>], " +
        "tools:[similar_to]}. \"less dark than T2.1\" -> {act:CritiqueItem, references:[T2.1], " +
        "directions:[{attribute:darkness, trend:less}]}. \"not the first one\" -> {act:CritiqueItem, references:[<rank-1 handle>]} " +
        "(rejects that card only: no directions, never similar_to). \"is the first one violent?\" -> {act:ItemQuestion, " +
        "references:[<rank-1 handle>], tools:[title_facts], question:\"is it violent?\"}. \"is Heat violent?\" -> " +
        "{act:ItemQuestion, referenceNames:[Heat], tools:[title_facts], question:\"is it violent?\"}. \"is Blade of the 47 " +
        "Ronin a ninja film or a samurai film?\" -> {act:ItemQuestion, referenceNames:[Blade of the 47 Ronin], " +
        "tools:[title_facts], question:\"is it a ninja film or a samurai film?\"}. \"what has Christopher Nolan " +
        "directed?\" -> {act:NewRequest, people:[{name:Christopher Nolan, role:director}], tools:[filmography]}. " +
        "\"how many ninja movies are left?\" -> {act:ItemQuestion, concepts:[ninja], tools:[supply]}. \"thanks!\" -> " +
        "{act:Chitchat}.\n" +
        "OUTPUT: act is always present. Omit EVERY other field that would be empty, null or unchanged — return only " +
        "the fields that carry something, exactly as the examples show.";

    private static readonly string[] Genres = OpenRouterSearchAgent.InterpretGenreSlugs;

    /// <summary>
    /// The response schema: every field the planner may write, none extra — but only <c>act</c>
    /// required, and not strict, so the model writes only the fields that carry something (HREV1-ASK-05).
    /// The plan's output was ~94 tokens a call, ~70 of them empty fields a strict schema forced it to
    /// write ("minYear":null, "clarifyOptions":[] …), and output tokens are what the plan stage waits
    /// on. <see cref="Parse"/> reads a missing field as empty and drops a value outside its enum, so
    /// nothing the strict schema guaranteed is lost.
    /// </summary>
    internal static object Schema()
    {
        static object Strings() => new { type = "array", items = new { type = "string" } };
        static object NullableInt() => new { type = new[] { "integer", "null" } };
        var properties = new Dictionary<string, object>
        {
            ["act"] = new { type = "string", @enum = Enum.GetNames<TurnAct>() },
            ["concepts"] = Strings(),
            ["genres"] = new { type = "array", items = new { type = "string", @enum = Genres } },
            ["people"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "name", "role" },
                    properties = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string" },
                        ["role"] = new { type = "string", @enum = new[] { "director", "cast", "any" } },
                    },
                },
            },
            ["minYear"] = NullableInt(),
            ["maxYear"] = NullableInt(),
            ["maxRuntimeMinutes"] = NullableInt(),
            ["media"] = new { type = "string", @enum = new[] { "unchanged", "any", "movie", "tv" } },
            ["moods"] = Strings(),
            ["directions"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "attribute", "trend" },
                    properties = new Dictionary<string, object>
                    {
                        ["attribute"] = new { type = "string", @enum = new[] { "darkness", "pacing", "complexity", "intensity", "humor", "optimism" } },
                        ["trend"] = new { type = "string", @enum = new[] { "more", "less" } },
                    },
                },
            },
            ["negations"] = Strings(),
            ["references"] = Strings(),
            ["referenceNames"] = Strings(),
            ["tools"] = new
            {
                type = "array",
                items = new { type = "string", @enum = new[] { "search_catalog", "similar_to", "title_facts", "filmography", "supply", "conditions" } },
            },
            ["question"] = new { type = new[] { "string", "null" } },
            ["clarifyQuestion"] = new { type = new[] { "string", "null" } },
            ["clarifyOptions"] = Strings(),
        };

        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "reel_ask_plan",
                strict = false,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "act" },
                    properties,
                },
            },
        };
    }

    private static string MediaWord(MediaScope media) => media switch
    {
        MediaScope.Movies => "movie",
        MediaScope.Shows => "tv",
        _ => "any",
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static List<string> Strings(JsonElement e, string name) =>
        Arr(e, name)
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Cap(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];

    private static string Fingerprint(string text) =>
        "p" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 4));
}
