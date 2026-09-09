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
    public async Task A_third_person_why_is_replaced_with_the_stub_sentence_keeping_the_fit()
    {
        // Two leaking sentences ("their", "the user's") and one clean second-person line.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":0.91,"why":"Matches their taste for slow-burn thrillers"},
              {"titleId":"{{TitleB}}","fit":0.42,"why":"The user's favourite kind of drama"},
              {"titleId":"{{TitleC}}","fit":0.77,"why":"A tense pick that fits your taste"}
            ]}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var results = await agent.RerankBatchAsync(Titles(), "something tense", "likes thrillers");

        // The model's fit survives; only the leaking sentence is swapped for the stub's line.
        Assert.Equal(0.91d, results[0].Fit, 3);
        Assert.Equal("A thriller pick aligned with your taste.", results[0].Why);
        Assert.Equal(0.42d, results[1].Fit, 3);
        Assert.Equal("A drama pick aligned with your taste.", results[1].Why);
        Assert.Equal("A tense pick that fits your taste", results[2].Why);
    }

    [Fact]
    public void The_rerank_prompt_carries_the_anchor_name_and_the_concrete_link_instruction()
    {
        var prompt = OpenRouterSearchAgent.BuildRerankPrompt(
            Titles(), "titles in the same vein as ones you loved", "likes thrillers", anchorName: "Hokum");

        Assert.Contains("Anchor title: Hokum", prompt, StringComparison.Ordinal);
        Assert.Contains("CONCRETE link to Hokum", prompt, StringComparison.Ordinal);
        // No anchor → no anchor block; Ask Reel's behaviour is unchanged.
        Assert.DoesNotContain("Anchor title:",
            OpenRouterSearchAgent.BuildRerankPrompt(Titles(), "q", "taste"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_rerank_prompt_carries_the_overview_truncated_at_three_hundred_chars()
    {
        var longOverview = new string('x', 320);
        var prompt = OpenRouterSearchAgent.BuildRerankPrompt(
            [new(TitleA, "Alpha", 1984, "movie", ["thriller"], longOverview, 9.0m)], "q", "taste");

        Assert.Contains(new string('x', 300), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 301), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rerank_system_prompt_demands_specific_second_person_whys()
    {
        var handler = new CannedHandler(Ok("""{"results":[]}"""));

        await Agent(handler).RerankBatchAsync(Titles(), "q", "taste", anchorName: "Hokum");

        var system = SystemMessageOf(handler.Bodies[0]);
        Assert.Contains("at most 160 characters", system, StringComparison.Ordinal);
        Assert.Contains("NEVER start a why with \"Because you loved\"", system, StringComparison.Ordinal);
        Assert.Contains("cite something SPECIFIC", system, StringComparison.Ordinal);
        Assert.Contains("Vary the sentence openers", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_because_you_loved_opener_is_replaced_with_the_stub_sentence_keeping_the_fit()
    {
        // The exact vague template the founder rejected — the model must not smuggle it back in.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":0.91,"why":"Because you loved Hokum, and peak 2000s — your era"},
              {"titleId":"{{TitleC}}","fit":0.77,"why":"Shares Hokum's slow-burn small-town dread"}
            ]}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var results = await agent.RerankBatchAsync(Titles(), "q", "taste", anchorName: "Hokum");

        Assert.Equal(0.91d, results[0].Fit, 3); // fit survives; only the sentence is discarded
        Assert.Equal("A thriller pick aligned with your taste.", results[0].Why);
        Assert.Equal("Shares Hokum's slow-burn small-town dread", results[2].Why);
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
        // The Phase-8 fields: comparison anchors and the runtime ceiling. A field the parser reads
        // but the prompt never asks for is simply always absent — the exact drift class this test exists for.
        Assert.Contains("referenceTitles", system, StringComparison.Ordinal);
        Assert.Contains("maxRuntimeMinutes", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_titles_and_runtime_bound_map_into_the_intent()
    {
        var payload = """
            {"reply":"Emotional sci-fi coming up.","genres":["science-fiction"],"keywords":["melancholy"],
             "referenceTitles":["Blade Runner"],"maxRuntimeMinutes":90,"freeText":"like blade runner"}
            """;
        var agent = Agent(new CannedHandler(Ok(payload)));

        var reply = await agent.InterpretAsync([], "like Blade Runner but more emotional, under 90 minutes", "likes sci-fi", []);

        Assert.Equal(["Blade Runner"], reply.Intent.ReferenceTitles);
        Assert.Equal(90, reply.Intent.MaxRuntimeMinutes);
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

    [Fact]
    public async Task A_successful_turn_makes_exactly_one_chat_call_and_never_pays_the_interpreter()
    {
        // The waste this pins: the agent used to await interpreter.InterpretAsync — a SECOND
        // billable OpenRouter completion — on every turn, purely to hold a fallback that the
        // parser discards whenever the primary call carries usable signal.
        var handler = new CannedHandler(Ok("""{"reply":"ok","genres":["horror"],"keywords":["shark"]}"""));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "shark horrors", "likes horror", []);

        Assert.Single(handler.Bodies); // one chat completion for the turn, not two
        Assert.Equal(0, interpreter.InterpretCalls);
        Assert.Equal(1, interpreter.HeuristicCalls); // the free reading, once
        Assert.Contains("horror", reply.Intent.Genres);
    }

    [Fact]
    public async Task The_exception_path_still_yields_a_heuristic_intent_and_asks_the_interpreter()
    {
        // A turn whose own model call is unusable is exactly where a second call earns its cost.
        var handler = new CannedHandler(new CannedResponse(HttpStatusCode.InternalServerError, "{}"));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "shark horrors", "likes horror", []);

        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal(["shark horrors"], reply.Intent.Keywords); // the heuristic reading, not an empty intent
        Assert.False(string.IsNullOrWhiteSpace(reply.Reply));
    }

    [Fact]
    public async Task An_empty_model_answer_falls_back_to_the_interpreter_rather_than_shipping_nothing()
    {
        var handler = new CannedHandler(Ok(string.Empty));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "quiet space films", "likes sci-fi", []);

        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal(["quiet space films"], reply.Intent.Keywords);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterSearchAgent Agent(CannedHandler handler) => Agent(handler, new HeuristicInterpreter());

    private static OpenRouterSearchAgent Agent(CannedHandler handler, HeuristicInterpreter interpreter)
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
            client, interpreter, recorder, config, NullLogger<OpenRouterSearchAgent>.Instance);
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

    /// <summary>
    /// Stands in for the deterministic interpreter. It counts BOTH members, because the whole point
    /// of the split is that <see cref="ISearchQueryInterpreter.Heuristic"/> is free and
    /// <see cref="ISearchQueryInterpreter.InterpretAsync"/> costs a chat completion — a regression
    /// that reinstates the paid call on the happy path is invisible to an assertion on the intent.
    /// </summary>
    private sealed class HeuristicInterpreter : ISearchQueryInterpreter
    {
        public int InterpretCalls { get; private set; }

        public int HeuristicCalls { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "fake/heuristic";

        public Task<SearchIntent> InterpretAsync(string query, CancellationToken ct = default)
        {
            InterpretCalls++;
            return Task.FromResult(Intent(query));
        }

        public SearchIntent Heuristic(string query)
        {
            HeuristicCalls++;
            return Intent(query);
        }

        private static SearchIntent Intent(string query) =>
            new([], [query], Array.Empty<MediaType>(), query, null, null);
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
