using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Enrichment;

/// <summary>
/// The LIVE parse path, driven by a canned HTTP handler — no network, no spend, no key.
///
/// This is the coverage whose absence let the headline defect survive: every existing test
/// exercised only the deterministic stub, so the adapter's real behaviour on a malformed model
/// response was never asserted. It used to substitute a plausible neutral 0.5 for any scalar the
/// model omitted, returned null for, or returned as a string — and the enrich handler then marked
/// that row Done, freezing a fabricated judgement into the recommender's feature vector for every
/// tenant, for ever, with nothing in the logs.
///
/// The contract pinned here: a required scalar that was not genuinely answered makes the whole
/// extraction FAIL (null), so the caller leaves the row un-Done and retryable. There is no
/// neutral default anywhere on this path.
/// </summary>
public class OpenRouterAttributeExtractorLiveModeTests
{
    private const string GoodPayload =
        """
        {"darkness":0.8,"pacing":0.3,"complexity":0.6,"emotionalIntensity":0.7,"humor":0.1,
         "optimism":0.2,"ensembleVsSolo":0.4,"tone":"brooding","era":"near-future",
         "themes":["identity","memory"]}
        """;

    [Fact]
    public async Task An_omitted_scalar_fails_the_extraction_instead_of_becoming_a_neutral_guess()
    {
        // "humor" simply isn't there. The old code produced 0.5 and the row went Done.
        var payload = Without(GoodPayload, "humor");
        var handler = new CannedHandler(Ok(payload), Ok(payload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        Assert.Null(result);
    }

    [Fact]
    public async Task A_null_scalar_fails_the_extraction()
    {
        var payload = GoodPayload.Replace("\"humor\":0.1", "\"humor\":null", StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload), Ok(payload));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
    }

    [Fact]
    public async Task A_string_typed_number_fails_the_extraction()
    {
        // json_object mode guarantees only that the bytes parse — "0.8" is perfectly legal JSON
        // and perfectly useless as a scalar.
        var payload = GoodPayload.Replace("\"humor\":0.1", "\"humor\":\"0.1\"", StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload), Ok(payload));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
    }

    [Fact]
    public async Task A_scalar_on_a_zero_to_ten_scale_is_rejected_rather_than_clamped_to_one()
    {
        // The silent catastrophe: clamping turns every off-scale answer into a confident 1.0 that
        // is indistinguishable downstream from a real "maximally dark" judgement.
        var payload = GoodPayload.Replace("\"darkness\":0.8", "\"darkness\":8", StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload), Ok(payload));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
    }

    [Fact]
    public async Task All_three_malformed_shapes_at_once_still_fail_closed()
    {
        var payload = Without(GoodPayload, "humor")
            .Replace("\"optimism\":0.2", "\"optimism\":null", StringComparison.Ordinal)
            .Replace("\"pacing\":0.3", "\"pacing\":\"0.3\"", StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload), Ok(payload));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
    }

    [Fact]
    public async Task A_well_formed_response_parses_every_field()
    {
        var handler = new CannedHandler(Ok(GoodPayload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        Assert.NotNull(result);
        Assert.Equal(0.8m, result!.Darkness);
        Assert.Equal(0.1m, result.Humor);
        Assert.Equal(0.4m, result.EnsembleVsSolo);
        Assert.Equal("brooding", result.Tone);
        Assert.Equal("near-future", result.Era);
        Assert.Equal(new[] { "identity", "memory" }, result.Themes);
        Assert.Single(handler.Bodies); // a good answer costs exactly one request
    }

    [Fact]
    public async Task The_request_asks_the_provider_to_enforce_the_schema_not_the_prose()
    {
        var handler = new CannedHandler(Ok(GoodPayload));

        await Extractor(handler).ExtractAsync([Input()]);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());

        var required = schema.GetProperty("schema").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("darkness", required);
        Assert.Contains("ensembleVsSolo", required);
        Assert.Contains("tone", required);

        // Closed vocabulary, enforced by the provider rather than requested in prose.
        var tone = schema.GetProperty("schema").GetProperty("properties").GetProperty("tone");
        Assert.Contains("brooding", tone.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_provider_that_rejects_strict_schema_mode_falls_back_to_json_object()
    {
        // Graceful degradation: not every model/route implements structured outputs, and a 400
        // there must not become a permanently failed title.
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok(GoodPayload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        Assert.NotNull(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("json_schema", ResponseFormatType(handler.Bodies[0]));
        Assert.Equal("json_object", ResponseFormatType(handler.Bodies[1]));
    }

    [Fact]
    public async Task A_malformed_response_is_re_asked_once_with_a_materially_different_prompt()
    {
        // Defect 3's answer: retries only happen when something meaningful changes. The repair
        // request names the field that was wrong — the one edit that can change the outcome at
        // temperature 0.
        var handler = new CannedHandler(Ok(Without(GoodPayload, "humor")), Ok(GoodPayload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        Assert.NotNull(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.NotEqual(handler.Bodies[0], handler.Bodies[1]);
        Assert.Contains("humor was absent", MessagesOf(handler.Bodies[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("rejected because of these fields", MessagesOf(handler.Bodies[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_completion_is_re_asked_with_a_compacted_prompt_and_a_wider_budget()
    {
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.OK, Envelope(GoodPayload, finishReason: "length")),
            Ok(GoodPayload));

        var result = (await Extractor(handler).ExtractAsync([Input(overview: new string('x', 4000))]))[0];

        Assert.NotNull(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(MaxTokensOf(handler.Bodies[1]) > MaxTokensOf(handler.Bodies[0]));
        Assert.True(handler.Bodies[1].Length < handler.Bodies[0].Length); // input actually shrank
    }

    [Fact]
    public async Task A_transport_failure_does_not_buy_a_second_identical_request()
    {
        // Nothing about re-sending the same bytes to a 500 immediately helps; the caller's
        // attempt budget retries on a later run, where elapsed time is the useful variable.
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.InternalServerError, "upstream exploded"),
            Ok(GoodPayload));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task An_invented_tone_or_era_is_dropped_rather_than_widening_the_taxonomy()
    {
        var payload = GoodPayload
            .Replace("\"tone\":\"brooding\"", "\"tone\":\"hauntingly-elegiac\"", StringComparison.Ordinal)
            .Replace("\"era\":\"near-future\"", "\"era\":\"post-post-apocalyptic\"", StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        // The scalars are real judgements, so the row is still usable — but no invented label is
        // ever stored, and the numbers are untouched.
        Assert.NotNull(result);
        Assert.Null(result!.Tone);
        Assert.Null(result.Era);
        Assert.Equal(0.8m, result.Darkness);
    }

    [Fact]
    public async Task Themes_are_normalized_deduped_and_capped_before_they_reach_the_hash_buckets()
    {
        var noisyThemes = "\"themes\":[\"  Identity \",\"identity\",\"MEMORY\",\"a\",\"b\",\"c\",\"d\",\"e\",\""
            + new string('z', 200) + "\"]";
        var payload = GoodPayload.Replace(
            "\"themes\":[\"identity\",\"memory\"]", noisyThemes, StringComparison.Ordinal);
        var handler = new CannedHandler(Ok(payload));

        var result = (await Extractor(handler).ExtractAsync([Input()]))[0];

        Assert.NotNull(result);
        Assert.True(result!.Themes.Count <= 6);
        Assert.Contains("identity", result.Themes);
        Assert.Equal(result.Themes.Count, result.Themes.Distinct().Count());
        Assert.All(result.Themes, t => Assert.True(t.Length <= 60));
    }

    [Fact]
    public async Task A_response_that_is_not_a_json_object_fails_closed()
    {
        var handler = new CannedHandler(Ok("[1,2,3]"), Ok("[1,2,3]"));

        Assert.Null((await Extractor(handler).ExtractAsync([Input()]))[0]);
    }

    [Fact]
    public async Task One_bad_title_in_a_batch_does_not_take_the_good_ones_with_it()
    {
        // The port promises row-aligned results; a per-title failure must stay per-title.
        var bad = Without(GoodPayload, "humor");
        var handler = new CannedHandler(Ok(bad), Ok(bad), Ok(GoodPayload));

        var results = await Extractor(handler).ExtractAsync([Input("Bad One"), Input("Good One")]);

        Assert.Equal(2, results.Count);
        Assert.Null(results[0]);
        Assert.NotNull(results[1]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterAttributeExtractor Extractor(CannedHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
                ["OPENROUTER_MAX_CONCURRENCY"] = "1", // serial, so canned responses stay deterministic
            })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterAttributeExtractor(
            client, recorder, config, NullLogger<OpenRouterAttributeExtractor>.Instance);
    }

    private static TitleAttributeInput Input(string name = "Blade Runner 2049", string? overview = "An overview.") =>
        new(Guid.NewGuid(), "Movie", name, 2017, overview, ["science-fiction"], ["replicant", "memory"]);

    private static CannedResponse Ok(string content) => new(HttpStatusCode.OK, Envelope(content));

    private static string Envelope(string content, string finishReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } },
        });

    /// <summary>Removes a key from a JSON payload the way a model omitting a field would.</summary>
    private static string Without(string payload, string key)
    {
        using var doc = JsonDocument.Parse(payload);
        var kept = doc.RootElement.EnumerateObject().Where(p => p.Name != key);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in kept)
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ResponseFormatType(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("response_format").GetProperty("type").GetString()!;
    }

    private static int MaxTokensOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("max_tokens").GetInt32();
    }

    private static string MessagesOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return string.Join("\n", doc.RootElement.GetProperty("messages")
            .EnumerateArray().Select(m => m.GetProperty("content").GetString()));
    }

    private sealed record CannedResponse(HttpStatusCode Status, string Body);

    /// <summary>Replays a fixed script of responses and keeps every request body for assertion.</summary>
    private sealed class CannedHandler(params CannedResponse[] script) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var canned = script[Math.Min(_index++, script.Length - 1)];
            return new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
