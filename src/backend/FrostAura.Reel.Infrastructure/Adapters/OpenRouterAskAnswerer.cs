using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Search.AskReel;
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
/// Ask Reel v2's ItemQuestion answerer (§4.5): the fact sheet plus the question, at most two
/// sentences that cite the facts and claim nothing beyond them. The only v2 call whose text reaches
/// the user, so it is the only one held to a sentence ceiling here as well as in the prompt.
/// </summary>
public sealed class OpenRouterAskAnswerer(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterAskAnswerer> logger,
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null) : IAskReelAnswerer
{
    /// <summary>The answer's outer bound in characters: two short sentences.</summary>
    public const int MaxAnswerChars = 360;

    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => configuration["OPENROUTER_API_KEY"] is { Length: > 0 };

    public string ModelId => _chat.Options(ApiUsagePurpose.AskAnswer).Model;

    public async Task<AnswerResult> AnswerAsync(AnswerInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var completion = await _chat.CompleteAsync(
            new LlmChatRequest(
                ApiUsagePurpose.AskAnswer,
                [LlmMessage.System(SystemPrompt), LlmMessage.User(UserPrompt(input))],
                Schema()),
            ct);
        var meta = new LlmCallMeta(
            completion.Model, completion.Effort, completion.Failure == LlmCallFailure.Truncated,
            completion.HttpAttempts, (long)completion.Elapsed.TotalMilliseconds, completion.ReasonCode);
        if (!completion.Succeeded)
        {
            return new AnswerResult(null, meta, completion.AgentFailure);
        }

        using var doc = LlmJson.FirstObject(completion.Content, out _);
        var text = doc is not null
            && doc.RootElement.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                ? Clean(a.GetString())
                : null;
        if (text is null)
        {
            logger.LogWarning("Ask answer was unreadable or empty; the engine answers from the facts alone.");
            return new AnswerResult(null, meta, AgentFailureKind.MalformedAnswer);
        }

        return new AnswerResult(text, meta, null);
    }

    /// <summary>Plain text, at most two sentences and <see cref="MaxAnswerChars"/>; null when nothing usable is left.</summary>
    internal static string? Clean(string? raw)
    {
        var text = AskReelText.StripMarkdown(raw)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var sentences = new List<string>(2);
        var start = 0;
        for (var i = 0; i < text.Length && sentences.Count < 2; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                sentences.Add(text[start..(i + 1)].Trim());
                start = i + 1;
            }
        }

        if (sentences.Count < 2 && start < text.Length)
        {
            sentences.Add(text[start..].Trim());
        }

        var joined = string.Join(' ', sentences.Where(s => s.Length > 0));
        return joined.Length == 0 ? null : joined.Length <= MaxAnswerChars ? joined : joined[..MaxAnswerChars].TrimEnd() + "…";
    }

    internal static string UserPrompt(AnswerInput input)
    {
        var f = input.Facts;
        return JsonSerializer.Serialize(
            new
            {
                question = input.Question,
                facts = new
                {
                    name = f.Name,
                    year = f.Year,
                    media = f.Media == Domain.Catalog.MediaType.Show ? "tv" : "movie",
                    genres = f.Genres,
                    keywords = f.Keywords,
                    overview = f.Overview,
                    runtimeMinutes = f.RuntimeMinutes,
                    certification = f.Certification,
                    directors = f.Directors,
                    cast = f.Cast,
                    themes = f.Themes,
                    tone = f.Tone,
                    youHaveSeenIt = f.Seen,
                },
            },
            Json);
    }

    internal const string SystemPrompt =
        "You answer one question about one film or show for Reel, using ONLY the facts given. Reply in at most two " +
        "short sentences, plain text, addressed to the viewer as \"you\". Every claim must come from a fact given — " +
        "name the fact (its certification, a keyword, the overview, a credit). If the facts do not answer the " +
        "question, say plainly that you don't know from what you have, and say what you do know. Never guess, never " +
        "recommend, never use markdown. Return ONLY {\"answer\": \"...\"}.";

    internal static object Schema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "reel_ask_answer",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "answer" },
                properties = new Dictionary<string, object> { ["answer"] = new { type = "string" } },
            },
        },
    };
}
