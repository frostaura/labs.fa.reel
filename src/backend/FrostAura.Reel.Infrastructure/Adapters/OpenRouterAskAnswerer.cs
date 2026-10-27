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

    /// <summary>
    /// Plain text, at most two sentences and <see cref="MaxAnswerChars"/>; null when nothing usable is
    /// left — including an answer that talks about Reel's inputs instead of the title ("The runtime
    /// fact says it is 197 minutes long.", "the keywords and overview identify it as…", HREV1-ASK-07).
    /// The engine then answers from the fact sheet itself, which names the title.
    /// </summary>
    internal static string? Clean(string? raw)
    {
        var text = AskReelText.StripMarkdown(raw)?.Trim();
        if (string.IsNullOrEmpty(text) || PipelineTalk.IsMatch(text))
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
                // "title", not "facts": the key names what the answer is about, and an answer primed
                // with "facts" cited them ("The runtime fact says…", HREV1-ASK-07).
                title = new
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

    /// <summary>
    /// Words that cite the answer's INPUT as a source: "the facts", "the runtime fact", "its
    /// keywords", "the overview says", "the data". The viewer never sees a fact sheet, so an answer
    /// that quotes one answers nobody.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PipelineTalk = new(
        @"\b(?:facts?|keywords?|overview|fact\s+sheet|the\s+data|details\s+(?:given|listed|provided))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    internal const string SystemPrompt =
        "You answer the viewer's question about one film or show for Reel, using ONLY what you are told about it. " +
        "Reply in at most two short sentences, plain text, addressed to the viewer as \"you\". Name the title and " +
        "state the answer directly as a statement about it: \"On the Beach runs 134 minutes.\", \"Blade of the 47 " +
        "Ronin is a samurai film; nothing I know about it makes it a ninja film.\". Never refer to where you got it: " +
        "never write \"fact\", \"facts\", \"keyword\", \"keywords\", \"overview\" or \"data\". If what you are told " +
        "does not answer the question, say plainly that you don't know that about the title, and say what you do know. " +
        "Never guess, never recommend, never use markdown. Return ONLY {\"answer\": \"...\"}.";

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
