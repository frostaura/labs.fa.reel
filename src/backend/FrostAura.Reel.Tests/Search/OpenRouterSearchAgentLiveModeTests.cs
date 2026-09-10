using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
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

    // ── Standing filters ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Standing_filters_appear_in_the_user_message_of_both_prompts()
    {
        // Ask Reel has TWO prompt/parser pairs: this agent's, which the live turn uses, and
        // OpenRouterSearchInterpreter's, which the RECOVERY path uses when the live call fails.
        // A rule that reaches one and not the other is invisible — the fallback quietly answers
        // outside the account's own filters, and only a user who is offered an excluded title
        // ever finds out. So both are asserted here, in one test, carrying the IDENTICAL line.
        const string filters = "never horror; nothing before 1990";

        var agentHandler = new CannedHandler(Ok("""{"reply":"ok","genres":["science-fiction"]}"""));
        await Agent(agentHandler).InterpretAsync(
            [], "something scary", "likes sci-fi", [], standingFilters: filters);

        var interpreterHandler = new InterpreterHandler(Envelope("""{"genres":["science-fiction"]}"""));
        await Interpreter(interpreterHandler).InterpretAsync("something scary", default, filters);

        var expected = OpenRouterSearchAgent.StandingFiltersLine(filters).TrimEnd('\n');
        Assert.Contains(expected, UserMessageOf(agentHandler.Bodies[0]), StringComparison.Ordinal);
        Assert.Contains(expected, UserMessageOf(interpreterHandler.Bodies[0]), StringComparison.Ordinal);

        // Both SYSTEM prompts state what to do about a collision, or the data above is inert.
        Assert.Contains("STANDING FILTERS", SystemMessageOf(agentHandler.Bodies[0]), StringComparison.Ordinal);
        Assert.Contains("STANDING FILTERS", SystemMessageOf(interpreterHandler.Bodies[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_standing_filters_leaves_both_user_messages_exactly_as_they_were()
    {
        // The probe on the other side of the branch. Most accounts have no content rules at all,
        // and a prompt that grows an empty "Standing filters: " header for them is spend and noise.
        var agentHandler = new CannedHandler(Ok("""{"reply":"ok","genres":["science-fiction"]}"""));
        await Agent(agentHandler).InterpretAsync([], "something scary", "likes sci-fi", []);

        var interpreterHandler = new InterpreterHandler(Envelope("""{"genres":["science-fiction"]}"""));
        await Interpreter(interpreterHandler).InterpretAsync("something scary");

        Assert.DoesNotContain("Standing filters", UserMessageOf(agentHandler.Bodies[0]), StringComparison.Ordinal);
        // The fallback's message is the bare query when there are no rules — byte-identical to
        // every request it made before this change.
        Assert.Equal("something scary", UserMessageOf(interpreterHandler.Bodies[0]));
    }

    [Fact]
    public async Task The_recovery_path_hands_the_standing_filters_down_to_the_fallback()
    {
        // The wiring, not the wording: a live call that fails routes through the interpreter, and
        // that hop is where the summary was most likely to be dropped on the floor.
        var handler = new CannedHandler(new CannedResponse(HttpStatusCode.InternalServerError, "{}"));
        var interpreter = new HeuristicInterpreter();

        await Agent(handler, interpreter).InterpretAsync(
            [], "something scary", "likes sci-fi", [], standingFilters: "never horror");

        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal("never horror", interpreter.LastStandingFilters);
    }

    // ── Ceilings ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Interpret_sends_max_tokens_400()
    {
        var handler = new CannedHandler(Ok("""{"reply":"ok","genres":["science-fiction"]}"""));

        await Agent(handler).InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(400, MaxTokensOf(handler.Bodies[0]));
    }

    [Fact]
    public async Task Rerank_sends_max_tokens_900()
    {
        // Deliberately a DIFFERENT number from interpret's: a single shared constant would have
        // passed a test that asserted only "max_tokens is present", while starving one of the two
        // callers. The re-rank answer is per-title and an order of magnitude wider.
        var handler = new CannedHandler(Ok("""{"results":[]}"""));

        await Agent(handler).RerankBatchAsync(Titles(), "q", "taste");

        Assert.Equal(900, MaxTokensOf(handler.Bodies[0]));
    }

    [Fact]
    public async Task A_length_finish_is_treated_as_a_failure()
    {
        // The payload PARSES — it is a complete JSON object that simply stops early, which is what
        // a truncated discovery spec actually looks like: the trailing fields are absent rather
        // than malformed, so the negation the user asked for silently never existed. Accepting it
        // would look like a working turn.
        var truncated = """{"reply":"Sure, here you go.","genres":["horror"]}""";
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.OK, Envelope(truncated, finishReason: "length")));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync(
            [], "horror but no ghosts", "likes horror", []);

        // Discarded: the interpreter was consulted, and the intent is the recovered one — NOT the
        // half-answer's "horror" genre, which is the thing that would have looked fine.
        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal(["horror but no ghosts"], reply.Intent.Keywords);
        Assert.DoesNotContain("horror", reply.Intent.Genres);

        // And the same finish on the re-rank path costs the batch its model fits, not a partial
        // set of them — every title lands on its deterministic, prediction-anchored stub.
        var rerankHandler = new CannedHandler(new CannedResponse(
            HttpStatusCode.OK,
            Envelope($$"""{"results":[{"titleId":"{{TitleA}}","fit":0.91,"why":"a"}]}""", finishReason: "length")));

        var results = await Agent(rerankHandler).RerankBatchAsync(Titles(), "q", "taste");

        Assert.Equal(0.9d, results[0].Fit, 3); // stub = predicted 9.0 / 10, not the truncated 0.91
    }

    [Fact]
    public async Task A_strict_schema_rejection_downgrades_once()
    {
        // The re-rank request carries a strict json_schema. A provider that cannot honour it
        // answers 400 naming response_format; the adapter retires strict mode for the PROCESS and
        // re-sends as json_object. Per-INSTANCE would be useless — the adapter rides a transient
        // typed HttpClient and is rebuilt every scope, so it would re-pay the rejected request on
        // literally every call.
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok($$"""{"results":[{"titleId":"{{TitleA}}","fit":0.42,"why":"still answered"}]}"""));
        var recorder = Recorder();

        var results = await Agent(handler, recorder).RerankBatchAsync(
            Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("json_schema", ResponseFormatTypeOf(handler.Bodies[0]));
        Assert.Equal("json_object", ResponseFormatTypeOf(handler.Bodies[1]));
        Assert.Equal(0.42d, results[0].Fit, 3); // the retry's answer is the one that lands

        // ONCE: a fresh adapter — a new scope, a new HttpClient — never probes strict mode again.
        // The probe here also varies the PURPOSE, because the retry recurses through the transport
        // while the ledger is written by the caller: a record moved inside ChatAsync would bill the
        // downgrade twice, and a record keyed off the wrong variable would bill it to the wrong
        // surface. Both buckets are asserted, because one absorbing the other is the failure that
        // leaves every per-surface cost figure wrong without looking wrong.
        var feedHandler = new CannedHandler(Ok("""{"results":[]}"""));
        await Agent(feedHandler, recorder).RerankBatchAsync(
            Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankFeed);

        Assert.Single(feedHandler.Bodies);
        Assert.Equal("json_object", ResponseFormatTypeOf(feedHandler.Bodies[0]));
        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.RerankFeed).Calls);
        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.RerankAsk).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_long_reply_is_cut_at_a_sentence_boundary(bool endsOnAnEmoji)
    {
        // Two shapes, because the fallback is where the subtle break lives. With a sentence
        // boundary in range the cut is clean; with none, the hard cut must not sever a surrogate
        // pair and leave half an emoji on the wire.
        var reply = endsOnAnEmoji
            ? new string('a', 399) + "\U0001F600" + new string('b', 200)
            : new string('a', 200) + ". " + new string('b', 400);

        var capped = OpenRouterSearchAgent.CapReply(reply);

        Assert.True(capped.Length <= 401, $"length was {capped.Length}"); // 400 plus the ellipsis
        Assert.DoesNotContain(capped, char.IsSurrogate);
        if (!endsOnAnEmoji)
        {
            Assert.EndsWith(".", capped, StringComparison.Ordinal);
            Assert.Equal(201, capped.Length);
        }
    }

    [Fact]
    public async Task A_model_reply_past_the_bound_is_capped_on_the_way_out_of_the_agent()
    {
        // CapReply is also unit-tested above, and that alone is NOT enough: a version of this
        // adapter that never calls it passes every direct test of the helper. This asserts the
        // WIRING — the caller receives the bounded string, not the model's 600-char essay.
        var essay = new string('a', 220) + ". " + new string('b', 380);
        var handler = new CannedHandler(Ok(
            JsonSerializer.Serialize(new { reply = essay, genres = new[] { "thriller" } })));

        var reply = await Agent(handler).InterpretAsync([], "something tense", "likes thrillers", []);

        Assert.True(reply.Reply.Length <= 401, $"length was {reply.Reply.Length}");
        Assert.EndsWith(".", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_within_the_bound_is_returned_untouched()
    {
        var handler = new CannedHandler(Ok("""{"reply":"Three tense ones coming up.","genres":["thriller"]}"""));

        var reply = await Agent(handler).InterpretAsync([], "something tense", "likes thrillers", []);

        Assert.Equal("Three tense ones coming up.", reply.Reply);
    }

    [Fact]
    public void The_stub_fit_survives_the_one_hash_Math_Abs_cannot_negate()
    {
        // Math.Abs(int.MinValue) throws OverflowException. String hash codes are randomised per
        // process, so the name that lands there is unpredictable rather than impossible — and the
        // throw would come from inside the batch loop that exists to survive a bad response,
        // costing every OTHER title in the batch its fit.
        Assert.InRange(OpenRouterSearchAgent.StableFit(int.MinValue), 0d, 0.999d);
        Assert.InRange(OpenRouterSearchAgent.StableFit(int.MaxValue), 0d, 0.999d);
        Assert.InRange(OpenRouterSearchAgent.StableFit(-1), 0d, 0.999d);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterSearchAgent Agent(CannedHandler handler) => Agent(handler, new HeuristicInterpreter());

    private static OpenRouterSearchAgent Agent(CannedHandler handler, ApiUsageRecorder recorder) =>
        Agent(handler, new HeuristicInterpreter(), recorder);

    private static OpenRouterSearchAgent Agent(CannedHandler handler, HeuristicInterpreter interpreter) =>
        Agent(handler, interpreter, Recorder());

    private static ApiUsageRecorder Recorder()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
    }

    private static OpenRouterSearchAgent Agent(
        CannedHandler handler, HeuristicInterpreter interpreter, ApiUsageRecorder recorder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
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

    private static string Envelope(string content, string finishReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } },
        });

    private static int MaxTokensOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("max_tokens").GetInt32();
    }

    private static string ResponseFormatTypeOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("response_format").GetProperty("type").GetString()!;
    }

    private static OpenRouterSearchInterpreter Interpreter(InterpreterHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterSearchInterpreter(
            client, Recorder(), config, NullLogger<OpenRouterSearchInterpreter>.Instance);
    }

    private static string UserMessageOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!;
    }

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

        /// <summary>What the RECOVERY path handed down — the half the two-prompt gotcha is about.</summary>
        public string? LastStandingFilters { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "fake/heuristic";

        public Task<SearchIntent> InterpretAsync(
            string query, CancellationToken ct = default, string? standingFilters = null)
        {
            InterpretCalls++;
            LastStandingFilters = standingFilters;
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

    /// <summary>The fallback interpreter's transport — same shape, its own capture list.</summary>
    private sealed class InterpreterHandler(string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
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
