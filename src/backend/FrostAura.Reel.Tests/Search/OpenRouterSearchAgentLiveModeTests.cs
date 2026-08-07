using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The LIVE agent path against a canned HTTP handler — no network, no key, no spend.
///
/// Two regressions are pinned here. First, batch-wide fragility: the re-rank used to call
/// GetString() on titleId without checking its kind, so ONE entry coming back as a JSON number
/// threw out of the loop and every OTHER title in the batch silently lost its model fit score to
/// the deterministic fallback. Second, prompt/parser drift: Ask Reel routes through this agent's
/// own InterpretSystemPrompt, so a field the parser reads but the prompt never requests (maxYear)
/// is always absent — which is why "80s sci-fi" came back full of modern titles.
/// </summary>
public class OpenRouterSearchAgentLiveModeTests
{
    private static readonly Guid TitleA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TitleB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TitleC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task One_numeric_titleId_costs_one_entry_not_the_whole_batch()
    {
        // The middle entry returns titleId as a bare number, exactly as a model occasionally does.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":0.91,"why":"a"},
              {"titleId":22222222,"fit":0.50,"why":"b"},
              {"titleId":"{{TitleC}}","fit":0.77,"why":"c"}
            ]}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var results = await agent.RerankBatchAsync(Titles(), "something tense", "likes thrillers");

        Assert.Equal(3, results.Count);
        Assert.Equal(0.91d, results[0].Fit, 3);
        Assert.Equal(0.77d, results[2].Fit, 3); // survived the bad sibling
        Assert.Equal(0.3d, results[1].Fit, 3); // the bad one alone falls back to its predicted-anchored stub
        Assert.Equal("a", results[0].Why);
        Assert.Equal("c", results[2].Why);
    }

    [Fact]
    public async Task A_malformed_fit_falls_back_to_the_deterministic_stub_not_a_flat_half()
    {
        // A string-typed fit and an off-scale (0-10) fit are both rejected. The old code answered
        // 0.5 for the first and clamped the second to 1.0 — a fabricated top ranking.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":"0.91","why":"a"},
              {"titleId":"{{TitleB}}","fit":8,"why":"b"},
              {"titleId":"{{TitleC}}","fit":0.77,"why":"c"}
            ]}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var results = await agent.RerankBatchAsync(Titles(), "something tense", "likes thrillers");

        Assert.Equal(0.9d, results[0].Fit, 3); // stub = predicted 9.0 / 10
        Assert.Equal(0.3d, results[1].Fit, 3); // stub = predicted 3.0 / 10, NOT a clamped 1.0
        Assert.Equal(0.77d, results[2].Fit, 3);
    }

    [Fact]
    public async Task A_results_entry_that_is_not_an_object_is_skipped_without_taking_the_batch()
    {
        var payload = $$"""
            {"results":["nonsense",null,{"titleId":"{{TitleC}}","fit":0.62,"why":"c"}]}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var results = await agent.RerankBatchAsync(Titles(), "q", "taste");

        Assert.Equal(0.62d, results[2].Fit, 3);
    }

    [Fact]
    public async Task The_interpret_prompt_requests_every_field_the_parser_reads()
    {
        var handler = new CannedHandler(Ok("""{"reply":"ok","genres":["science-fiction"]}"""));

        await Agent(handler).InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        var system = SystemMessageOf(handler.Bodies[0]);
        Assert.Contains("maxYear", system, StringComparison.Ordinal);
        Assert.Contains("minYear", system, StringComparison.Ordinal);
        Assert.Contains("excludeGenres", system, StringComparison.Ordinal);
        Assert.Contains("excludeKeywords", system, StringComparison.Ordinal);
        // The specific instruction whose absence produced 2026/1996/2012/1993 for "80s sci-fi".
        Assert.Contains("a decade means both bounds", system, StringComparison.Ordinal);
        Assert.Contains("EVERY rejected item", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decade_query_carries_BOTH_year_bounds_into_the_intent()
    {
        var payload = """
            {"reply":"Here's some 80s sci-fi.","genres":["science-fiction"],"keywords":["space"],
             "mediaTypes":["movie"],"minYear":1980,"maxYear":1989,"freeText":"80s sci-fi"}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var reply = await agent.InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(1980, reply.Intent.MinYear);
        Assert.Equal(1989, reply.Intent.MaxYear);
        Assert.Contains("science-fiction", reply.Intent.Genres);
    }

    [Fact]
    public async Task Inverted_year_bounds_are_swapped_rather_than_dropped()
    {
        var payload = """
            {"reply":"ok","genres":["science-fiction"],"minYear":1989,"maxYear":1980,"freeText":"80s sci-fi"}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var reply = await agent.InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(1980, reply.Intent.MinYear);
        Assert.Equal(1989, reply.Intent.MaxYear);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterSearchAgent Agent(CannedHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterSearchAgent(
            client, new HeuristicInterpreter(), recorder, config, NullLogger<OpenRouterSearchAgent>.Instance);
    }

    private static List<RerankInput> Titles() =>
    [
        new(TitleA, "Alpha", 1984, "movie", ["thriller"], "A tense one.", 9.0m),
        new(TitleB, "Bravo", 1986, "movie", ["drama"], "A slow one.", 3.0m),
        new(TitleC, "Charlie", 1988, "movie", ["horror"], "A scary one.", 6.0m),
    ];

    private static CannedResponse Ok(string content) => new(HttpStatusCode.OK, Envelope(content));

    private static string Envelope(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        });

    private static string SystemMessageOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
    }

    /// <summary>Stands in for the deterministic interpreter — the agent only needs a fallback intent.</summary>
    private sealed class HeuristicInterpreter : ISearchQueryInterpreter
    {
        public bool IsAvailable => true;

        public string ModelId => "fake/heuristic";

        public Task<SearchIntent> InterpretAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(new SearchIntent([], [query], Array.Empty<MediaType>(), query, null, null));
    }

    private sealed record CannedResponse(HttpStatusCode Status, string Body);

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
