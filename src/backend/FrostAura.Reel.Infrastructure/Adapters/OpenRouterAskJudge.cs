using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Ask Reel v2's judge, one batch per call (§4.5): an Ask-only prompt — the feed's shared rerank
/// prompt stays untouched — that rules on whether each title IS what the request asks for, with a
/// fit, a why-line that names its evidence, and the evidence itself.
///
/// <para><b>What the judge sees per title</b>: name, year, media type, genres, the top 12 TMDB
/// keywords (never passed before), a 200-character overview, trusted themes and tone. And one
/// persona line for the viewer.</para>
///
/// <para><b>What it may not say.</b> A why-line that talks about the viewer in the third person,
/// opens with the banned template, argues against its own pick or hedges (F*1), or names no
/// evidence, is discarded — the verdict and fit stand, the line goes, and nothing is written in its
/// place (<see cref="ResultSource.Fallback"/> on <c>WhySource</c>).</para>
/// </summary>
public sealed class OpenRouterAskJudge(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterAskJudge> logger,
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null) : IRequestJudgeModel
{
    /// <summary>The why-line ceiling (§4.5).</summary>
    public const int MaxWhyChars = 140;

    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => configuration["OPENROUTER_API_KEY"] is { Length: > 0 };

    public string ModelId => _chat.Options(ApiUsagePurpose.AskJudge).Model;

    public string CacheSalt => string.Concat(_chat.CacheSalt(ApiUsagePurpose.AskJudge), ":", PromptFingerprint);

    /// <summary>A fingerprint of the prompt and schema: an edit re-keys every cached verdict.</summary>
    public static string PromptFingerprint { get; } =
        "p" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SystemPrompt + JsonSerializer.Serialize(Schema()))).AsSpan(0, 4));

    public async Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Titles.Count == 0)
        {
            return new JudgeBatchResult([], LlmCallMeta.None, Failed: false);
        }

        var completion = await _chat.CompleteAsync(
            new LlmChatRequest(
                ApiUsagePurpose.AskJudge,
                [LlmMessage.System(SystemPrompt), LlmMessage.User(UserPrompt(input))],
                Schema(),
                Items: input.Titles.Count),
            ct);
        var meta = new LlmCallMeta(
            completion.Model, completion.Effort, completion.Failure == LlmCallFailure.Truncated,
            completion.HttpAttempts, (long)completion.Elapsed.TotalMilliseconds, completion.ReasonCode);
        if (!completion.Succeeded)
        {
            return new JudgeBatchResult(input.Titles.Select(t => JudgeVerdict.Unjudged(t.TitleId)).ToList(), meta, Failed: true);
        }

        using var doc = LlmJson.FirstObject(completion.Content, out _);
        var read = doc is null ? new Dictionary<Guid, JudgeVerdict>() : Parse(doc.RootElement, input);
        if (doc is null)
        {
            logger.LogWarning("Ask judge answer held no readable JSON object; the batch is unjudged.");
        }

        var verdicts = input.Titles.Select(t => read.GetValueOrDefault(t.TitleId) ?? JudgeVerdict.Unjudged(t.TitleId)).ToList();
        return new JudgeBatchResult(verdicts, meta, Failed: verdicts.All(v => !v.Judged));
    }

    /// <summary>The model's entries as verdicts, one bad entry costing only itself. Internal so the reading is pinned by tests.</summary>
    internal static Dictionary<Guid, JudgeVerdict> Parse(JsonElement root, JudgeInput input)
    {
        var asked = input.Titles.Select(t => t.TitleId).ToHashSet();
        var verdicts = new Dictionary<Guid, JudgeVerdict>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return verdicts;
        }

        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || IdOf(idEl.GetString(), input) is not { } id || !asked.Contains(id)
                || !entry.TryGetProperty("satisfies", out var satEl) || satEl.ValueKind != JsonValueKind.String
                || LlmResultText.ParseSatisfies(satEl.GetString()) is not { } satisfies
                || !entry.TryGetProperty("fit", out var fitEl) || fitEl.ValueKind != JsonValueKind.Number
                || !fitEl.TryGetDouble(out var fit) || double.IsNaN(fit) || fit < -0.01d || fit > 1.01d)
            {
                continue;
            }

            var evidence = entry.TryGetProperty("evidence", out var evEl) && evEl.ValueKind == JsonValueKind.String
                ? evEl.GetString()?.Trim()
                : null;
            var why = entry.TryGetProperty("why", out var whyEl) && whyEl.ValueKind == JsonValueKind.String
                ? whyEl.GetString()?.Trim()
                : null;
            var keepWhy = !RejectsWhy(why, evidence, satisfies);
            verdicts[id] = new JudgeVerdict(
                id,
                satisfies,
                Math.Clamp(fit, 0d, 1d),
                keepWhy ? why : null,
                string.IsNullOrWhiteSpace(evidence) ? null : evidence,
                ResultSource.Llm,
                keepWhy ? ResultSource.Llm : ResultSource.Fallback);
        }

        return verdicts;
    }

    /// <summary>
    /// Whether a why-line is discarded: empty, too long, no evidence behind it, the voice guard, and —
    /// on a title the model said satisfies the request — any hedge against its own pick (F*1).
    /// </summary>
    internal static bool RejectsWhy(string? why, string? evidence, Satisfies satisfies) =>
        string.IsNullOrWhiteSpace(why)
        || why.Length > MaxWhyChars
        || string.IsNullOrWhiteSpace(evidence)
        || SecondPersonVoice.Rejects(why)
        || (satisfies == Satisfies.Yes && (SecondPersonVoice.SoundsHedged(why) || SecondPersonVoice.SoundsAntiRecommendation(why)));

    /// <summary>
    /// The title an answer's <c>id</c> names: the 1-based position the prompt numbered it with. A
    /// position, not the title's UUID, because the id is echoed in every entry and a UUID is ~20
    /// output tokens a title — a fifth of a batch's answer, and output tokens are what a batch waits
    /// on. A full UUID is still accepted, for a model that copies one from elsewhere.
    /// </summary>
    internal static Guid? IdOf(string? value, JudgeInput input)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)
            && position >= 1 && position <= input.Titles.Count)
        {
            return input.Titles[position - 1].TitleId;
        }

        return Guid.TryParse(value, out var id) ? id : null;
    }

    internal static string UserPrompt(JudgeInput input)
    {
        var payload = new
        {
            request = input.Request,
            viewer = input.Persona,
            titles = input.Titles.Select((t, index) => new
            {
                id = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                name = t.Name,
                year = t.Year,
                media = t.Media == Domain.Catalog.MediaType.Show ? "tv" : "movie",
                genres = t.Genres,
                keywords = t.Keywords,
                overview = t.Overview,
                themes = t.Themes,
                tone = t.Tone,
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    internal const string SystemPrompt =
        "You are the judge for Reel, a film and TV recommender. For EVERY title given, decide whether it IS what the " +
        "request asks for, judged only from what is given about the title (genres, keywords, overview, themes, tone). " +
        "Return ONLY {\"results\":[{\"id\",\"satisfies\",\"fit\",\"why\",\"evidence\"}]} with one entry per title, " +
        "id being the title's id exactly as given (\"1\", \"2\", …). " +
        "satisfies: \"yes\" = plainly the thing asked for, every concept the request requires; \"partial\" = matches some " +
        "of it, or only loosely; \"no\" = not what was asked for. A title missing a required concept is \"no\", however " +
        "good it is. Never \"yes\" because a title is merely good, popular or similar in mood. A title carrying a thing " +
        "the request rules out is \"no\". fit: 0.0-1.0, how well it fits this viewer AND the request. evidence: the " +
        "specific given fact your verdict rests on, at most 40 characters (\"keyword: ninja\", \"overview: a shinobi clan\", " +
        "\"genre: comedy\"). why: at most 100 characters, addressed to the viewer as \"you\", naming that evidence — " +
        "never \"they\" or \"the user\", never generic (\"fits your taste\"), never hedging or arguing against the " +
        "title when satisfies is \"yes\". When satisfies is \"no\", why is an empty string (it is never shown). " +
        "Output strictly valid JSON.";

    internal static object Schema()
    {
        var entry = new Dictionary<string, object>
        {
            ["id"] = new { type = "string" },
            ["satisfies"] = new { type = "string", @enum = new[] { "yes", "partial", "no" } },
            ["fit"] = new { type = "number" },
            ["why"] = new { type = "string" },
            ["evidence"] = new { type = "string" },
        };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "reel_ask_judge",
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
                                required = entry.Keys.ToArray(),
                                properties = entry,
                            },
                        },
                    },
                },
            },
        };
    }
}
