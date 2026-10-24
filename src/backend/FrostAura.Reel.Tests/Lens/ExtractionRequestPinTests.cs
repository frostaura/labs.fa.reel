using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// WS-1 task 2's extraction pin: moving the attribute extractor onto the shared chat client is
/// TRANSPORT-ONLY. Every request body it sends — the first strict request, the malformed-answer
/// repair, the truncation's compact repair, and the json_object fallback after a strict-schema
/// rejection — must be byte-identical to what the pre-migration adapter sent, and its cache key
/// (model id + extractor version) must not move. Either change would re-key the catalogue and
/// trigger a paid re-extraction of every title, which FD-1 has not decided.
///
/// <para>The golden files under <c>Golden/</c> were captured from the pre-migration adapter
/// (commit before the migration) by running this class with <c>REEL_WRITE_GOLDEN=1</c>.</para>
/// </summary>
public class ExtractionRequestPinTests
{
    private const string GoodPayload =
        """
        {"darkness":0.8,"pacing":0.3,"complexity":0.6,"emotionalIntensity":0.7,"humor":0.1,
         "optimism":0.2,"ensembleVsSolo":0.4,"tone":"brooding","era":"near-future",
         "themes":["identity","memory"]}
        """;

    private static readonly TitleAttributeInput Input = new(
        Guid.Parse("8d3f0a52-5f0e-4c7b-9a51-0f8b2c6f3a11"), "Movie", "Blade Runner 2049", 2017,
        "Officer K unearths a long-buried secret — and goes looking for Rick Deckard.",
        ["science-fiction", "drama"], ["replicant", "memory", "dystopia"]);

    [Fact]
    public async Task The_first_strict_request_is_byte_identical()
    {
        var handler = new CapturingHandler(Ok(GoodPayload));
        Assert.NotNull((await Extractor(handler).ExtractAsync([Input]))[0]);
        AssertGolden("extraction-strict.json", handler.Bodies[0]);
        Assert.Equal("application/json; charset=utf-8", handler.ContentTypes[0]);
    }

    [Fact]
    public async Task The_malformed_answer_repair_request_is_byte_identical()
    {
        var handler = new CapturingHandler(Ok("""{"darkness":0.5}"""), Ok(GoodPayload));
        Assert.NotNull((await Extractor(handler).ExtractAsync([Input]))[0]);
        Assert.Equal(2, handler.Bodies.Count);
        AssertGolden("extraction-strict.json", handler.Bodies[0]);
        AssertGolden("extraction-repair-malformed.json", handler.Bodies[1]);
    }

    [Fact]
    public async Task The_truncation_compact_repair_request_is_byte_identical()
    {
        var handler = new CapturingHandler(Ok(GoodPayload, finishReason: "length"), Ok(GoodPayload));
        Assert.NotNull((await Extractor(handler).ExtractAsync([Input]))[0]);
        Assert.Equal(2, handler.Bodies.Count);
        AssertGolden("extraction-repair-compact.json", handler.Bodies[1]);
    }

    [Fact]
    public async Task The_json_object_fallback_request_is_byte_identical()
    {
        var handler = new CapturingHandler(
            new Canned(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok(GoodPayload));
        Assert.NotNull((await Extractor(handler).ExtractAsync([Input]))[0]);
        Assert.Equal(2, handler.Bodies.Count);
        AssertGolden("extraction-strict.json", handler.Bodies[0]);
        AssertGolden("extraction-json-object.json", handler.Bodies[1]);
    }

    [Theory]
    [InlineData(null, "openai/gpt-6-luna")]
    [InlineData("openai/gpt-5.4-mini", "openai/gpt-5.4-mini")]
    // The quirk is part of the key: a BLANK OPENROUTER_MODEL is used as-is by the extractor (the
    // search family treats blank as absent). Normalising it would re-key every stored row.
    [InlineData("", "")]
    public void The_cache_key_model_and_version_do_not_move(string? configured, string expected)
    {
        var extractor = Extractor(new CapturingHandler(Ok(GoodPayload)), configured, setModel: true);
        Assert.Equal(expected, extractor.ModelId);
        Assert.Equal(2, extractor.Version);
    }

    [Fact]
    public void Extraction_ignores_the_per_purpose_effort_and_model_overrides()
    {
        // Pinning the search model or turning effort on elsewhere must not leak into extraction.
        var extractor = Extractor(
            new CapturingHandler(Ok(GoodPayload)),
            extra: new()
            {
                ["LLM_EXTRACT_ATTRIBUTES_MODEL"] = "openai/other",
                ["LLM_EXTRACT_ATTRIBUTES_EFFORT"] = "low",
                ["REEL_SEARCH_MODEL"] = "openai/search-only",
            });
        Assert.Equal("openai/gpt-6-luna", extractor.ModelId);
    }

    [Fact]
    public async Task An_effort_override_never_reaches_the_extraction_request()
    {
        var handler = new CapturingHandler(Ok(GoodPayload));
        var extractor = Extractor(handler, extra: new() { ["LLM_EXTRACT_ATTRIBUTES_EFFORT"] = "low" });
        Assert.NotNull((await extractor.ExtractAsync([Input]))[0]);
        AssertGolden("extraction-strict.json", handler.Bodies[0]);
    }

    private static void AssertGolden(string name, string actual, [CallerFilePath] string sourceFile = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Golden", name);
        if (Environment.GetEnvironmentVariable("REEL_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Equal(File.ReadAllText(path, Encoding.UTF8), actual);
    }

    private static OpenRouterAttributeExtractor Extractor(
        HttpMessageHandler handler, string? model = null, bool setModel = false, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
            ["TestHelpers:ModelStubMode"] = "false",
            ["OPENROUTER_MAX_CONCURRENCY"] = "1",
        };
        if (setModel)
        {
            values["OPENROUTER_MODEL"] = model;
        }

        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterAttributeExtractor(client, recorder, config, NullLogger<OpenRouterAttributeExtractor>.Instance);
    }

    private static Canned Ok(string content, string finishReason = "stop") =>
        new(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } },
            usage = new { prompt_tokens = 300, completion_tokens = 80, total_tokens = 380 },
        }));

    private sealed record Canned(HttpStatusCode Status, string Body);

    private sealed class CapturingHandler(params Canned[] script) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        public List<string?> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            ContentTypes.Add(request.Content.Headers.ContentType?.ToString());
            var canned = script[Math.Min(_index++, script.Length - 1)];
            return new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
