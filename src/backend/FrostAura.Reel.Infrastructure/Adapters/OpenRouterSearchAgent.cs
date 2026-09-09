using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
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
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default)
    {
        var intentFallback = await interpreter.InterpretAsync(message, ct);
        if (StubMode)
        {
            return new AgentReply(StubReply(message, intentFallback), intentFallback);
        }

        try
        {
            var user = BuildInterpretPrompt(history, message, tasteSummary, shownTitles);
            var (content, usage) = await ChatAsync(InterpretSystemPrompt, user, ct);
            RecordCall(ApiUsagePurpose.SearchAgent, usage);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new AgentReply(StubReply(message, intentFallback), intentFallback);
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var reply = root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s
                ? s : StubReply(message, intentFallback);
            var intent = SearchIntentParser.FromJson(root, message, intentFallback);
            return new AgentReply(reply, intent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent interpret failed for '{Message}'; using heuristic intent.", message);
            return new AgentReply(StubReply(message, intentFallback), intentFallback);
        }
    }

    public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
        IReadOnlyList<RerankInput> titles, string query, string tasteSummary, string? anchorName = null, CancellationToken ct = default)
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
            var user = BuildRerankPrompt(titles, query, tasteSummary, anchorName);
            var (content, usage) = await ChatAsync(RerankSystemPrompt, user, ct);
            RecordCall(ApiUsagePurpose.Rerank, usage);
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

    private async Task<(string? Content, OpenRouterUsage? Usage)> ChatAsync(string system, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = ModelId,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct);
        return (dto?.Choices?.FirstOrDefault()?.Message?.Content, dto?.Usage);
    }

    private static string StubReply(string message, SearchIntent intent)
    {
        var lens = intent.Genres.Count > 0 ? intent.Genres[0] : intent.Keywords.Count > 0 ? intent.Keywords[0] : "your taste";
        return $"Here are some {lens} picks for “{message}” — leaning into what you tend to love.";
    }

    private static RerankResult StubRerank(RerankInput t)
    {
        // Deterministic: anchor fit on the personal prediction when present, else a stable hash.
        var fit = t.Predicted is { } p
            ? Math.Clamp((double)p / 10d, 0d, 1d)
            : Math.Abs(t.Name.GetHashCode()) % 1000 / 1000d;
        var genre = t.Genres.Count > 0 ? t.Genres[0] : "this";
        return new RerankResult(t.TitleId, fit, $"A {genre} pick aligned with your taste.");
    }

    private static string BuildInterpretPrompt(IReadOnlyList<ChatTurn> history, string message, string taste, IReadOnlyList<string> shown)
    {
        var sb = new StringBuilder();
        sb.Append("Your taste: ").Append(taste).Append('\n');
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
    internal static string BuildRerankPrompt(IReadOnlyList<RerankInput> titles, string query, string taste, string? anchorName = null)
    {
        var sb = new StringBuilder();
        sb.Append("Your taste: ").Append(taste).Append('\n');
        sb.Append("Your request: ").Append(query).Append('\n');
        if (!string.IsNullOrWhiteSpace(anchorName))
        {
            sb.Append("Anchor title: ").Append(anchorName)
                .Append(" — each candidate sits on a rail anchored to this title you loved. In each why, name the CONCRETE link to ")
                .Append(anchorName)
                .Append(" (shared tone, premise, craft, or people), never just that you loved it.\n");
        }

        sb.Append("Score each title 0.0-1.0 for how well it fits YOU and your request, with a one-line reason addressed to you:\n");
        foreach (var t in titles)
        {
            sb.Append("- id=").Append(t.TitleId).Append(" | ").Append(t.Name);
            if (t.Year is { } y)
            {
                sb.Append(" (").Append(y).Append(')');
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
        "Be discerning — spread the scores. Output strictly valid JSON.";

    private sealed record ChatResponse(ChatChoice[]? Choices, OpenRouterUsage? Usage);

    private sealed record ChatChoice(ChatMessage? Message);

    private sealed record ChatMessage(string? Content);
}
