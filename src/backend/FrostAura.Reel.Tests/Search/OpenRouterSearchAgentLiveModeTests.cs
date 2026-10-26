using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
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
    public async Task A_third_person_why_is_dropped_keeping_the_model_fit()
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

        // The model's fit survives; the leaking sentence is DROPPED, and nothing is invented in its
        // place — the consumer shows its own deterministic line, labelled as what it is.
        Assert.Equal(0.91d, results[0].Fit, 3);
        Assert.Equal(ResultSource.Llm, results[0].FitSource);
        Assert.Equal(string.Empty, results[0].Why);
        Assert.Equal(ResultSource.Fallback, results[0].WhySource);
        Assert.Null(results[0].ModelWhy);
        Assert.Equal(0.91d, results[0].ModelFit);
        Assert.Equal(0.42d, results[1].Fit, 3);
        Assert.Equal(string.Empty, results[1].Why);
        Assert.Equal("A tense pick that fits your taste", results[2].Why);
        Assert.Equal(ResultSource.Llm, results[2].WhySource);
        Assert.DoesNotContain(results, r => r.Why.Contains("aligned with your taste", StringComparison.Ordinal));
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
    public async Task A_because_you_loved_opener_is_dropped_keeping_the_model_fit()
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
        Assert.Equal(string.Empty, results[0].Why);
        Assert.Equal(ResultSource.Fallback, results[0].WhySource);
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

        Assert.Equal(2, handler.Bodies.Count); // the agent retried once before anything else
        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal(["shark horrors"], reply.Intent.Keywords); // the heuristic reading, not an empty intent
        Assert.Equal(AgentReply.DegradedReply, reply.Reply);
        Assert.True(reply.Degraded);
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
    public async Task Interpret_sends_its_measured_ceiling()
    {
        // WS-1 task 3: p99 completion at effort none (125), plus ~60 tokens for the fields the
        // strict schema now makes the model write out, x 1.5, rounded up — 300. The mini-era 400
        // was spent on hidden reasoning by a reasoning model; 200 had no room for the schema.
        var handler = new CannedHandler(Ok("""{"reply":"ok","genres":["science-fiction"]}"""));

        await Agent(handler).InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(300, MaxTokensOf(handler.Bodies[0]));
        Assert.Equal("none", EffortOf(handler.Bodies[0]));
    }

    [Theory]
    [InlineData(ApiUsagePurpose.RerankFeed, 100 + (356 * 3))]
    [InlineData(ApiUsagePurpose.RerankPage, 100 + (356 * 3))]
    [InlineData(ApiUsagePurpose.RerankAsk, 100 + (370 * 3))]
    [InlineData(ApiUsagePurpose.RerankExpand, 100 + (370 * 3))]
    public async Task Rerank_sends_an_envelope_plus_a_measured_per_title_share(string purpose, int expected)
    {
        // Deliberately per title: the re-rank answer is one entry per title, and a flat ceiling
        // sized for 9 either starves a 9-title batch at p99 (the old 900 against a measured 1,194)
        // or over-provisions a 3-title one.
        var handler = new CannedHandler(Ok("""{"results":[]}"""));

        await Agent(handler).RerankBatchAsync(Titles(), "q", "taste", anchorName: null, purpose);

        Assert.Equal(expected, MaxTokensOf(handler.Bodies[0]));
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

        // Discarded twice — the first answer and the one retry — and only then is the interpreter
        // consulted; the intent is the recovered one, NOT the half-answer's "horror" genre, which
        // is the thing that would have looked fine. The turn says it is degraded.
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Equal(["horror but no ghosts"], reply.Intent.Keywords);
        Assert.DoesNotContain("horror", reply.Intent.Genres);
        Assert.True(reply.Degraded);
        Assert.Equal(AgentFailureKind.Truncated, reply.Failure);

        // And the same finish on the re-rank path costs the batch its model fits, not a partial
        // set of them — every title lands on its deterministic, prediction-anchored stub.
        var rerankHandler = new CannedHandler(new CannedResponse(
            HttpStatusCode.OK,
            Envelope($$"""{"results":[{"titleId":"{{TitleA}}","fit":0.91,"why":"a"}]}""", finishReason: "length")));

        var results = await Agent(rerankHandler).RerankBatchAsync(Titles(), "q", "taste");

        Assert.Equal(0.9d, results[0].Fit, 3); // stub = predicted 9.0 / 10, not the truncated 0.91
        Assert.All(results, r => Assert.Equal(ResultSource.Fallback, r.FitSource));
    }

    [Fact]
    public async Task A_strict_schema_rejection_downgrades_once()
    {
        // The re-rank request carries a strict json_schema. A provider that cannot honour it
        // answers 400 naming response_format; the adapter retires strict mode for the PROCESS and
        // re-sends as json_object. Per-INSTANCE would be useless — the adapter rides a transient
        // typed HttpClient and is rebuilt every scope, so it would re-pay the rejected request on
        // literally every call. The memory is the DI-singleton capability table, keyed by MODEL.
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok($$"""{"results":[{"titleId":"{{TitleA}}","fit":0.42,"why":"still answered"}]}"""));
        var recorder = Recorder();
        var capabilities = new FrostAura.Reel.Infrastructure.Llm.ModelCapabilities();

        var results = await Agent(handler, recorder, capabilities).RerankBatchAsync(
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
        // leaves every per-surface cost figure wrong without looking wrong. The rejected request
        // IS on the ledger: it was an HTTP attempt, and every attempt is a call.
        var feedHandler = new CannedHandler(Ok("""{"results":[]}"""));
        await Agent(feedHandler, recorder, capabilities).RerankBatchAsync(
            Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankFeed);

        Assert.Single(feedHandler.Bodies);
        Assert.Equal("json_object", ResponseFormatTypeOf(feedHandler.Bodies[0]));
        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.RerankFeed).Calls);
        Assert.Equal(2, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.RerankAsk).Calls);

        // And a hand-built adapter with NO shared table (a test, a fresh process) probes again —
        // one rejection never leaks into an unrelated instance's state.
        var isolated = new CannedHandler(Ok("""{"results":[]}"""));
        await Agent(isolated, Recorder()).RerankBatchAsync(Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankFeed);
        Assert.Equal("json_schema", ResponseFormatTypeOf(isolated.Bodies[0]));
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
    public async Task A_failed_batch_without_predictions_keeps_its_input_order_and_the_same_order_every_time()
    {
        // The fallback fit used to hash the title NAME — and string hashes are randomised per
        // process, so "keeps retrieval order" was false and the order changed on every restart.
        // Names are chosen so alphabetical, reverse and input order all differ.
        List<RerankInput> unpredicted =
        [
            new(TitleB, "Zulu", 1990, "movie", ["drama"], "One.", null),
            new(TitleA, "Mike", 1991, "movie", ["drama"], "Two.", null),
            new(TitleC, "Alpha", 1992, "movie", ["drama"], "Three.", null),
        ];

        var first = await Agent(new CannedHandler(new CannedResponse(HttpStatusCode.BadRequest, "{}")))
            .RerankBatchAsync(unpredicted, "q", "taste");
        var second = await Agent(new CannedHandler(new CannedResponse(HttpStatusCode.BadRequest, "{}")))
            .RerankBatchAsync(unpredicted, "q", "taste");

        Assert.All(first, r => Assert.Equal(ResultSource.Fallback, r.FitSource));
        Assert.Equal(unpredicted.Select(t => t.TitleId), first.OrderByDescending(r => r.Fit).Select(r => r.TitleId));
        Assert.Equal(first.Select(r => r.Fit), second.Select(r => r.Fit));
        Assert.All(first, r => Assert.InRange(r.Fit, 0d, 0.5d));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void The_position_fit_is_total_and_bounded(int index) =>
        Assert.InRange(OpenRouterSearchAgent.PositionFit(index), 0d, 0.5d);

    // ── Degraded turns: retry once, never echo, say so (WS-1 task 1a) ─────────────────────

    public static TheoryData<string, int, string, AgentFailureKind> FailedAnswers => new()
    {
        { "http 500", 500, "{}", AgentFailureKind.ProviderError },
        { "empty content", 200, Envelope(string.Empty), AgentFailureKind.EmptyAnswer },
        { "length finish", 200, Envelope("""{"reply":"Here you go.","genres":["action"]}""", finishReason: "length"), AgentFailureKind.Truncated },
        { "not json", 200, Envelope("Sure! Ninja films are great."), AgentFailureKind.MalformedAnswer },
        { "no reply line", 200, Envelope("""{"genres":["action"],"keywords":["ninja"]}"""), AgentFailureKind.MalformedAnswer },
        { "payment required", 402, """{"error":{"message":"insufficient credits"}}""", AgentFailureKind.ProviderRefused },
    };

    [Theory]
    [MemberData(nameof(FailedAnswers))]
    public async Task A_failed_turn_is_degraded_and_never_quotes_the_users_message(
        string scenario, int status, string body, AgentFailureKind expected)
    {
        // The defect this pins: the fallback reply was "Here are some action picks for '<your
        // sentence>' — leaning into what you tend to love", cached and replayed as history. Every
        // failure shape must now yield the one reply that claims nothing, flagged as degraded.
        const string message = "none of the above are ninja movies. I want ninja movies";
        var handler = new CannedHandler(new CannedResponse((HttpStatusCode)status, body));

        var reply = await Agent(handler).InterpretAsync([], message, "likes action", []);

        Assert.True(reply.Degraded, scenario);
        Assert.Equal(expected, reply.Failure);
        Assert.Equal(
            expected == AgentFailureKind.ProviderRefused ? AgentReply.OutOfCreditReply : AgentReply.DegradedReply,
            reply.Reply);
        Assert.DoesNotContain("ninja", reply.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("picks for", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("\u201C", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_attempt_retries_the_agent_once_and_a_good_retry_is_a_normal_turn()
    {
        var handler = new CannedHandler(
            new CannedResponse(HttpStatusCode.InternalServerError, "{}"),
            Ok("""{"reply":"Four ninja films you haven't seen.","genres":["action"],"keywords":["ninja"]}"""));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(0, interpreter.InterpretCalls); // the retry, not the history-blind fallback
        Assert.False(reply.Degraded);
        Assert.Null(reply.Failure);
        Assert.Equal("Four ninja films you haven't seen.", reply.Reply);
        Assert.Contains("ninja", reply.Intent.Keywords);
    }

    [Theory]
    [InlineData("length", 600)]
    [InlineData("empty", 300)]
    public async Task Only_a_truncated_attempt_retries_at_twice_the_ceiling(string firstFailure, int retryBudget)
    {
        // At temperature 0 a ceiling that truncated once truncates again; any other failure has
        // nothing to do with the ceiling and does not earn a bigger bill.
        var first = firstFailure == "length"
            ? new CannedResponse(HttpStatusCode.OK, Envelope("""{"reply":"Here","genres":[]}""", finishReason: "length"))
            : Ok(string.Empty);
        var handler = new CannedHandler(first, Ok("""{"reply":"ok","genres":["action"]}"""));
        var recorder = Recorder();

        var reply = await Agent(handler, recorder).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.False(reply.Degraded);
        Assert.Equal([300, retryBudget], handler.Bodies.Select(MaxTokensOf));
        // Both attempts are on the ledger: the failed one was billed too.
        Assert.Equal(2, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.SearchAgent).Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_is_never_retried_and_never_reaches_the_interpreter(HttpStatusCode status)
    {
        // The key, the credit or the permission is gone: every further call on the same provider
        // is another refusal, so the free reading carries the turn.
        var handler = new CannedHandler(new CannedResponse(status, """{"error":{"message":"no"}}"""));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Single(handler.Bodies);
        Assert.Equal(0, interpreter.InterpretCalls);
        Assert.Equal(1, interpreter.HeuristicCalls);
        Assert.Equal(AgentFailureKind.ProviderRefused, reply.Failure);
        Assert.Equal(["ninja movies"], reply.Intent.Keywords);
    }

    [Fact]
    public async Task A_deterministic_bad_request_is_not_retried_but_still_recovers_through_the_interpreter()
    {
        // An identical request is an identical 400, paid twice. The interpreter's prompt is a
        // different request, so it still gets its one chance at the intent.
        var handler = new CannedHandler(new CannedResponse(HttpStatusCode.BadRequest, """{"error":{"message":"bad"}}"""));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Single(handler.Bodies);
        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.True(reply.Degraded);
        Assert.Equal(AgentFailureKind.ProviderError, reply.Failure);
    }

    [Fact]
    public async Task Where_the_interpreter_still_runs_it_receives_the_conversation_history()
    {
        IReadOnlyList<ChatTurn> history =
        [
            new("user", "ninja movies"),
            new("assistant", "Three ninja films coming up."),
        ];
        var handler = new CannedHandler(new CannedResponse(HttpStatusCode.InternalServerError, "{}"));
        var interpreter = new HeuristicInterpreter();

        await Agent(handler, interpreter).InterpretAsync(
            history, "more like the second one", "likes action", [], standingFilters: "never horror");

        Assert.Equal(1, interpreter.InterpretCalls);
        Assert.Same(history, interpreter.LastHistory);
        Assert.Equal("never horror", interpreter.LastStandingFilters);
    }

    [Fact]
    public async Task An_answer_missing_only_its_reply_keeps_the_models_intent_without_paying_the_interpreter()
    {
        // The model DID resolve the request — only the reply line is missing. Its intent beats any
        // recovery; the turn is still degraded, because the reply the user reads is not the model's.
        var handler = new CannedHandler(Ok("""{"genres":["horror"],"keywords":["slasher"]}"""));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "slashers", "likes horror", []);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(0, interpreter.InterpretCalls);
        Assert.True(reply.Degraded);
        Assert.Equal(AgentFailureKind.MalformedAnswer, reply.Failure);
        Assert.Contains("horror", reply.Intent.Genres);
        Assert.Contains("slasher", reply.Intent.Keywords);
        Assert.Equal(["slashers"], reply.Requested!.Keywords); // the unlaundered reading still rides
    }

    [Fact]
    public async Task The_test_stub_answers_without_an_echo_and_a_keyless_install_says_it_has_no_model()
    {
        const string message = "ninja movies";

        var stub = await StubAgent(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .InterpretAsync([], message, "likes action", []);
        var keyless = await StubAgent(new Dictionary<string, string?>())
            .InterpretAsync([], message, "likes action", []);

        // The test double stands in for a WORKING model: not degraded, and it claims nothing.
        Assert.False(stub.Degraded);
        Assert.DoesNotContain(message, stub.Reply, StringComparison.OrdinalIgnoreCase);
        // A keyless install has no model: degraded, and says so.
        Assert.True(keyless.Degraded);
        Assert.Equal(AgentFailureKind.NoModel, keyless.Failure);
        Assert.Equal(AgentReply.NoModelReply, keyless.Reply);
        Assert.Null(keyless.FailureCode);
    }

    // ── The degraded reply names its cause (IREV2-SYS-02) ──────────────────────────────────

    public static TheoryData<string, int, string, AgentFailureKind, string, string> DegradedCauses => new()
    {
        // A transient failure is the one case "in a moment" is true of.
        { "http 503", 503, "{}", AgentFailureKind.ProviderError, "error", AgentReply.DegradedReply },
        { "http 429", 429, "{}", AgentFailureKind.ProviderError, "rate_limited", AgentReply.DegradedReply },
        { "unreadable twice", 200, Envelope("Sure! Ninja films are great."), AgentFailureKind.MalformedAnswer, "no_answer", AgentReply.DegradedReply },
        // A refusal never recovers by waiting, and each status names a different fix.
        { "402", 402, """{"error":{"message":"insufficient credits"}}""", AgentFailureKind.ProviderRefused, "payment_required", AgentReply.OutOfCreditReply },
        { "401", 401, """{"error":{"message":"bad key"}}""", AgentFailureKind.ProviderRefused, "unauthorized", AgentReply.KeyRejectedReply },
        { "403", 403, """{"error":{"message":"no"}}""", AgentFailureKind.ProviderRefused, "forbidden", AgentReply.ProviderRefusedReply },
    };

    [Theory]
    [MemberData(nameof(DegradedCauses))]
    public async Task A_degraded_reply_is_chosen_by_its_cause_and_carries_the_status_code(
        string scenario, int status, string body, AgentFailureKind kind, string code, string expectedReply)
    {
        var handler = new CannedHandler(new CannedResponse((HttpStatusCode)status, body));

        var reply = await Agent(handler).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.True(reply.Degraded, scenario);
        Assert.Equal(kind, reply.Failure);
        Assert.Equal(code, reply.FailureCode);
        Assert.Equal(expectedReply, reply.Reply);
    }

    [Theory]
    [InlineData("heist movies from the 2010s", 2010, 2019)]
    [InlineData("'90s heist movies", 1990, 1999)]
    [InlineData("heist movies after 2015", 2015, null)]
    public async Task A_refused_turn_plans_on_a_reading_whose_dates_are_bounds_not_keywords(
        string message, int? minYear, int? maxYear)
    {
        // IREV3-SYS-05, end to end through the REAL free reading: a 402 put the plan on it, and the
        // decade came back as keyword "2010" — the supply matched titles tagged "2010" and the
        // reply's topic read "heist or 2010". The window must arrive as bounds and nothing else.
        var handler = new CannedHandler(new CannedResponse(HttpStatusCode.PaymentRequired, """{"error":{"message":"insufficient credits"}}"""));
        var stubConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build();
        var freeReading = new OpenRouterSearchInterpreter(
            new HttpClient(), Recorder(), stubConfig, NullLogger<OpenRouterSearchInterpreter>.Instance);
        var agent = new OpenRouterSearchAgent(
            new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            freeReading, Recorder(), LiveConfig(), NullLogger<OpenRouterSearchAgent>.Instance);

        var reply = await agent.InterpretAsync([], message, "likes action", []);

        Assert.True(reply.Degraded);
        Assert.Equal("payment_required", reply.FailureCode);
        foreach (var reading in new[] { reply.Intent, reply.Requested! })
        {
            Assert.Equal(["heist"], reading.Keywords);
            Assert.Equal(minYear, reading.MinYear);
            Assert.Equal(maxYear, reading.MaxYear);
        }
    }

    [Fact]
    public async Task A_spent_daily_allowance_says_when_it_resets_and_sends_nothing()
    {
        // The proof: with LLM_ASK_PLAN_DAILY_CALLS=17 the gate refused the 18th turn, and the reply
        // told the user to try again in a moment — on a ceiling that resets with the UTC day.
        var handler = new CannedHandler(Ok("""{"reply":"never sent","genres":["action"]}"""));
        var interpreter = new HeuristicInterpreter();
        var agent = new OpenRouterSearchAgent(
            new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            interpreter, Recorder(), LiveConfig(), NullLogger<OpenRouterSearchAgent>.Instance,
            gate: new RefusingGate());

        var reply = await agent.InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Empty(handler.Bodies);
        Assert.Equal(0, interpreter.InterpretCalls); // a refused allowance is not spent on a second prompt
        Assert.True(reply.Degraded);
        Assert.Equal(AgentFailureKind.BudgetSpent, reply.Failure);
        Assert.Equal("budget_spent", reply.FailureCode);
        Assert.Equal(AgentReply.BudgetSpentReply, reply.Reply);
        Assert.Contains("midnight in your account's time zone", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("in a moment", reply.Reply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "payment_required", AgentReply.OutOfCreditReply)]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", AgentReply.KeyRejectedReply)]
    public async Task Turns_after_a_refusal_name_the_open_breakers_cause_not_a_moment(
        HttpStatusCode status, string code, string expectedReply)
    {
        // The 402 opened the breaker for fifteen minutes; the turns after it were refused before
        // anything was sent and said "in a moment" — but the fix is credit, not patience.
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService();
        var handler = new CannedHandler(
            new CannedResponse(status, """{"error":{"message":"no"}}"""), Ok("""{"reply":"never sent","genres":[]}"""));
        var agent = new OpenRouterSearchAgent(
            new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            new HeuristicInterpreter(), Recorder(), LiveConfig(), NullLogger<OpenRouterSearchAgent>.Instance,
            health: health);

        var tripping = await agent.InterpretAsync([], "ninja movies", "likes action", []);
        var next = await agent.InterpretAsync([], "heist films", "likes action", []);

        Assert.Single(handler.Bodies);
        Assert.Equal(AgentFailureKind.ProviderRefused, tripping.Failure);
        Assert.Equal(AgentFailureKind.ProviderUnavailable, next.Failure);
        Assert.Equal(code, tripping.FailureCode);
        Assert.Equal(code, next.FailureCode);
        Assert.Equal(expectedReply, tripping.Reply);
        Assert.Equal(expectedReply, next.Reply);
    }

    // ── The Ask-only request check and typed sources ───────────────────────────────────────

    [Theory]
    [InlineData(ApiUsagePurpose.RerankAsk, true)]
    [InlineData(ApiUsagePurpose.RerankExpand, true)]
    [InlineData(ApiUsagePurpose.RerankFeed, false)]
    [InlineData(ApiUsagePurpose.RerankPage, false)]
    [InlineData(ApiUsagePurpose.Rerank, false)]
    public async Task Only_the_request_judging_purposes_are_asked_whether_a_title_satisfies(string purpose, bool asked)
    {
        var feedHandler = new CannedHandler(Ok("""{"results":[]}"""));
        await Agent(feedHandler).RerankBatchAsync(Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankFeed);
        var feedSystem = SystemMessageOf(feedHandler.Bodies[0]);

        var handler = new CannedHandler(Ok("""{"results":[]}"""));
        await Agent(handler).RerankBatchAsync(Titles(), "ninja movies", "taste", anchorName: null, purpose);
        var system = SystemMessageOf(handler.Bodies[0]);

        Assert.Equal(asked, OpenRouterSearchAgent.JudgesRequest(purpose));
        // The feed's shared prompt is untouched: the Ask prompt is that prompt plus the check.
        Assert.DoesNotContain("satisfies", feedSystem, StringComparison.Ordinal);
        Assert.StartsWith(feedSystem, system, StringComparison.Ordinal);
        Assert.Equal(asked, system.Contains("\"satisfies\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_feed_schema_is_byte_identical_and_the_ask_schema_requires_the_verdict()
    {
        // Pinned against the literal the feed sent before the Ask check existed: a feed purpose must
        // not pay for, or be constrained by, a field it never reads.
        const string feedSchema =
            """{"type":"json_schema","json_schema":{"name":"reel_rerank","strict":true,"schema":{"type":"object","additionalProperties":false,"required":["results"],"properties":{"results":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["titleId","fit","why"],"properties":{"titleId":{"type":"string"},"fit":{"type":"number"},"why":{"type":"string","maxLength":160}}}}}}}}""";
        Assert.Equal(feedSchema, JsonSerializer.Serialize(OpenRouterSearchAgent.RerankSchema(), WebJson));

        using var ask = JsonDocument.Parse(JsonSerializer.Serialize(OpenRouterSearchAgent.AskRerankSchema(), WebJson));
        var items = ask.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties")
            .GetProperty("results").GetProperty("items");
        Assert.Equal(
            ["titleId", "fit", "why", "satisfies"],
            items.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["yes", "partial", "no"],
            items.GetProperty("properties").GetProperty("satisfies").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_verdict_is_read_on_the_ask_purpose_and_ignored_on_the_feed()
    {
        // Same payload, two purposes. An unreadable verdict is UNJUDGED (null), never "no" — the
        // entry's fit still stands.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":0.91,"why":"A shinobi revenge tale you'll love","satisfies":"yes"},
              {"titleId":"{{TitleB}}","fit":0.42,"why":"Your kind of slow drama","satisfies":"PARTIAL"},
              {"titleId":"{{TitleC}}","fit":0.77,"why":"A tense one for you","satisfies":"maybe"}
            ]}
            """;

        var ask = await Agent(new CannedHandler(Ok(payload))).RerankBatchAsync(
            Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);
        var feed = await Agent(new CannedHandler(Ok(payload))).RerankBatchAsync(
            Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankFeed);

        Assert.Equal([Satisfies.Yes, Satisfies.Partial, null], ask.Select(r => r.Satisfies));
        Assert.Equal(0.77d, ask[2].Fit, 3);
        Assert.All(feed, r => Assert.Null(r.Satisfies));
    }

    [Fact]
    public async Task Every_rerank_value_carries_the_source_that_produced_it()
    {
        // Title A: a clean model verdict. Title B: a model fit with a third-person why the guard
        // rejects. Title C: skipped by the model. Each must say exactly where each half came from.
        var payload = $$"""
            {"results":[
              {"titleId":"{{TitleA}}","fit":0.91,"why":"A shinobi revenge tale you'll love","satisfies":"yes"},
              {"titleId":"{{TitleB}}","fit":0.42,"why":"Matches their taste for drama","satisfies":"no"}
            ]}
            """;

        var results = await Agent(new CannedHandler(Ok(payload))).RerankBatchAsync(
            Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);

        Assert.Equal((ResultSource.Llm, ResultSource.Llm), (results[0].FitSource, results[0].WhySource));
        Assert.Equal((ResultSource.Llm, ResultSource.Fallback), (results[1].FitSource, results[1].WhySource));
        Assert.Equal(0.42d, results[1].Fit, 3);
        Assert.Equal(Satisfies.No, results[1].Satisfies); // the guard replaces the sentence, not the verdict
        Assert.Equal((ResultSource.Fallback, ResultSource.Fallback), (results[2].FitSource, results[2].WhySource));
        Assert.Null(results[2].Satisfies);
    }

    [Fact]
    public async Task A_failed_batch_and_the_stub_are_fallback_on_both_halves()
    {
        var failed = await Agent(new CannedHandler(new CannedResponse(HttpStatusCode.InternalServerError, "{}")))
            .RerankBatchAsync(Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);
        var stubbed = await StubAgent(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .RerankBatchAsync(Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);

        Assert.All(failed.Concat(stubbed), r =>
        {
            Assert.Equal(ResultSource.Fallback, r.FitSource);
            Assert.Equal(ResultSource.Fallback, r.WhySource);
            Assert.Null(r.Satisfies);
        });
    }

    // ── Round 1: format slips, the interpret schema, hedges, prompts and the cache salt ──────

    [Fact]
    public async Task Interpret_is_sent_under_a_strict_schema_that_matches_the_prompt_and_the_parser()
    {
        // Round-1 review defect 7: under json_object the model answered "I…" on both attempts and
        // the turn degraded. Constrained decoding cannot open with prose.
        var handler = new CannedHandler(Ok("""{"reply":"ok","genres":["action"]}"""));

        await Agent(handler).InterpretAsync([], "ninja movies", "likes action", []);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.Equal("reel_interpret", schema.GetProperty("name").GetString());
        Assert.True(schema.GetProperty("strict").GetBoolean());

        // Every field the prompt asks for, and nothing else — strict mode refuses the rest.
        var required = schema.GetProperty("schema").GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(
            ["reply", "genres", "keywords", "people", "mediaTypes", "minYear", "maxYear", "freeText", "excludeGenres",
                "excludeKeywords", "referenceTitles", "maxRuntimeMinutes"],
            required);
        foreach (var field in required)
        {
            Assert.Contains(field!, OpenRouterSearchAgent.InterpretSystemPrompt, StringComparison.Ordinal);
        }

        // The genre enum is the prompt's list, every slug is one the catalogue maps, and the list
        // is complete — a slug the prompt offers but the enum forbids would be undecodable.
        foreach (var slug in OpenRouterSearchAgent.InterpretGenreSlugs)
        {
            Assert.Contains(slug, OpenRouterSearchAgent.InterpretSystemPrompt, StringComparison.Ordinal);
            Assert.True(TmdbGenres.SlugToIds.ContainsKey(slug), slug);
        }

        Assert.Contains(
            "EXACTLY: " + string.Join(", ", OpenRouterSearchAgent.InterpretGenreSlugs) + ")",
            OpenRouterSearchAgent.InterpretSystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strict_answer_with_every_field_present_reads_as_the_short_answer_did()
    {
        // Strict mode makes the model write out the fields it used to omit, as [] and null. The
        // parser must read those exactly as absence — vary the fixture, not just the code.
        var full = """
            {"reply":"Here","genres":["action"],"keywords":["ninja"],"people":[],"mediaTypes":[],"minYear":null,
             "maxYear":null,"freeText":null,"excludeGenres":[],"excludeKeywords":[],"referenceTitles":[],"maxRuntimeMinutes":null}
            """;
        var blankText = full.Replace("\"freeText\":null", "\"freeText\":\"\"", StringComparison.Ordinal);
        var shortForm = """{"reply":"Here","genres":["action"],"keywords":["ninja"]}""";

        var intents = new List<SearchIntent>();
        foreach (var answer in new[] { shortForm, full, blankText })
        {
            var reply = await Agent(new CannedHandler(Ok(answer))).InterpretAsync([], "ninja movies", "likes action", []);
            Assert.False(reply.Degraded);
            intents.Add(reply.Intent);
        }

        Assert.All(intents, i =>
        {
            Assert.Equal(intents[0].Genres, i.Genres);
            Assert.Equal(intents[0].Keywords, i.Keywords);
            Assert.Equal(intents[0].MinYear, i.MinYear);
            Assert.Equal(intents[0].MaxYear, i.MaxYear);
            Assert.Equal(intents[0].FreeText, i.FreeText);
            Assert.Equal(intents[0].MediaTypes, i.MediaTypes);
        });
    }

    [Theory]
    [InlineData("I found a few for you. {\"reply\":\"Here are some.\",\"genres\":[\"action\"],\"keywords\":[\"ninja\"]}")]
    [InlineData("{\"reply\":\"Here are some.\",\"genres\":[\"action\"],\"keywords\":[\"ninja\"]}\n{\"reply\":\"again\"}")]
    [InlineData("Sure! {\"reply\":\"Here are some.\",\"genres\":[\"action\"],\"keywords\":[\"ninja\"]} Enjoy.")]
    public async Task An_interpret_object_wrapped_in_text_is_read_and_the_text_discarded(string content)
    {
        // A model on json_object (strict retired for it) still writes text around its object. The
        // object is the answer; the text is not a reason to bill a retry and degrade the turn.
        var handler = new CannedHandler(Ok(content));

        var reply = await Agent(handler).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Single(handler.Bodies);
        Assert.False(reply.Degraded);
        Assert.Equal("Here are some.", reply.Reply);
        Assert.Contains("ninja", reply.Intent.Keywords);
    }

    [Fact]
    public async Task Prose_with_no_object_counts_as_a_malformed_answer_retries_once_and_degrades()
    {
        // The exact eval failure: both attempts open "I…" and carry no object at all. It is retried
        // once, then the turn is DEGRADED with MalformedAnswer on its record — the figure the
        // fallback rate is counted from — and the reply claims nothing.
        var handler = new CannedHandler(Ok("I can help you find ninja films."), Ok("I'd suggest some ninja films."));
        var interpreter = new HeuristicInterpreter();

        var reply = await Agent(handler, interpreter).InterpretAsync([], "ninja movies", "likes action", []);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(reply.Degraded);
        Assert.Equal(AgentFailureKind.MalformedAnswer, reply.Failure);
        Assert.Equal(AgentReply.DegradedReply, reply.Reply);
        Assert.Equal(1, interpreter.InterpretCalls); // recovery reads the intent; the reply stays degraded
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("preamble")]
    public async Task A_rerank_object_with_text_around_it_keeps_its_verdicts(string shape)
    {
        // The heist turn's retry: one complete results object, then a stray quote on the next line —
        // and six titles lost their verdicts to "invalid after a single JSON value".
        var obj = $$"""{"results":[{"titleId":"{{TitleA}}","fit":0.81,"why":"Your kind of slow-burn chase."},{"titleId":"{{TitleB}}","fit":0.4,"why":"A quiet courtroom piece you rate."}]}""";
        var content = shape == "trailing" ? obj + "\n\"" : "Here are the scores: " + obj;

        var results = await Agent(new CannedHandler(Ok(content))).RerankBatchAsync(Titles(), "q", "taste");

        Assert.Equal(ResultSource.Llm, results[0].FitSource);
        Assert.Equal(0.81d, results[0].Fit, 3);
        Assert.Equal(ResultSource.Llm, results[1].FitSource);
        Assert.Equal(ResultSource.Fallback, results[2].FitSource); // the title it skipped
    }

    [Fact]
    public async Task A_cut_off_rerank_object_is_still_no_object()
    {
        // Salvage reads a COMPLETE object only; it never repairs one.
        var content = $$"""{"results":[{"titleId":"{{TitleA}}","fit":0.81,"why":"Your kind of slow""";

        var results = await Agent(new CannedHandler(Ok(content))).RerankBatchAsync(Titles(), "q", "taste");

        Assert.All(results, r => Assert.Equal(ResultSource.Fallback, r.FitSource));
    }

    [Theory]
    [InlineData(ApiUsagePurpose.RerankFeed, null, true)]
    [InlineData(ApiUsagePurpose.RerankPage, null, true)]
    [InlineData(ApiUsagePurpose.RerankAsk, "yes", true)]
    [InlineData(ApiUsagePurpose.RerankAsk, "partial", false)]
    [InlineData(ApiUsagePurpose.RerankExpand, "no", false)]
    public async Task A_hedged_why_is_dropped_where_it_would_caption_a_pick(string purpose, string? satisfies, bool dropped)
    {
        // Round-1 surface review defect 2: "…though this is a much slower drama than the action and
        // adventure you often prefer" captioned a pick. On a pick the hedge goes and the fit stays;
        // where the title only partly or does not satisfy the request, the line explains the miss.
        const string Hedged = "A boxing drama about legacy, though this is a much slower drama than the action and adventure you often prefer.";
        var verdict = satisfies is null ? string.Empty : $",\"satisfies\":\"{satisfies}\"";
        var content = $$"""{"results":[{"titleId":"{{TitleA}}","fit":0.8,"why":"{{Hedged}}"{{verdict}}}]}""";

        var results = await Agent(new CannedHandler(Ok(content))).RerankBatchAsync(Titles(), "q", "taste", anchorName: null, purpose);

        Assert.Equal(ResultSource.Llm, results[0].FitSource);
        Assert.Equal(0.8d, results[0].Fit, 3);
        Assert.Equal(dropped ? ResultSource.Fallback : ResultSource.Llm, results[0].WhySource);
        Assert.Equal(dropped ? string.Empty : Hedged, results[0].Why);
    }

    [Fact]
    public async Task A_clean_why_on_a_pick_is_kept()
    {
        // The control for the hedge rule: the same pick, a line that does not argue against itself.
        var content = $$"""{"results":[{"titleId":"{{TitleA}}","fit":0.8,"why":"A boxing drama about legacy, told with the patience you rate.","satisfies":"yes"}]}""";

        var results = await Agent(new CannedHandler(Ok(content))).RerankBatchAsync(
            Titles(), "q", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);

        Assert.Equal(ResultSource.Llm, results[0].WhySource);
    }

    [Fact]
    public void The_prompts_carry_no_anchor_jargon_example_and_no_golden_topic_noun()
    {
        // "shares the anchor's slow-burn small-town dread" taught the model the jargon seven of
        // seven anchored lines then used. And a topic noun a golden scenario asks about, written
        // into a prompt as the worked example, coaches exactly that scenario.
        Assert.DoesNotContain("anchor's", OpenRouterSearchAgent.RerankSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anchor's", OpenRouterSearchAgent.AskRerankSystemPrompt, StringComparison.OrdinalIgnoreCase);
        foreach (var prompt in new[]
                 {
                     OpenRouterSearchAgent.InterpretSystemPrompt, OpenRouterSearchAgent.RerankSystemPrompt,
                     OpenRouterSearchAgent.AskRerankSystemPrompt,
                 })
        {
            Assert.DoesNotContain("samurai", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ninja", prompt, StringComparison.OrdinalIgnoreCase);
        }

        // WS-2's ask, carried by the integrator: the user's own topic word leads the keywords, so
        // the counts and the copy describe what was asked for rather than a synonym the model chose.
        Assert.Contains("the FIRST is the user's own topic word", OpenRouterSearchAgent.InterpretSystemPrompt, StringComparison.Ordinal);

        // The feed's prompt is still the prefix of the request-judging one.
        Assert.StartsWith(OpenRouterSearchAgent.RerankSystemPrompt, OpenRouterSearchAgent.AskRerankSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cache_salt_carries_a_fingerprint_of_the_prompt_and_schema_each_purpose_is_sent_with()
    {
        var agent = Agent(new CannedHandler(Ok("{}")));
        var feed = OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.RerankFeed);
        var ask = OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.RerankAsk);
        var interpret = OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.SearchAgent);

        Assert.Matches("^p[0-9a-f]{8}$", feed);

        // One prompt, one version: feed, page and the legacy purpose share it; Ask and expand share
        // theirs; interpret is its own. Edit any of them and its version moves on its own.
        Assert.Equal(feed, OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.RerankPage));
        Assert.Equal(feed, OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.Rerank));
        Assert.Equal(ask, OpenRouterSearchAgent.PromptVersion(ApiUsagePurpose.RerankExpand));
        Assert.Equal(3, new[] { feed, ask, interpret }.Distinct().Count());

        Assert.EndsWith(":" + feed, agent.CacheSalt(ApiUsagePurpose.RerankFeed), StringComparison.Ordinal);
        Assert.Contains("|none:", agent.CacheSalt(ApiUsagePurpose.RerankFeed), StringComparison.Ordinal); // the effort
        Assert.StartsWith(RerankCacheVersion.Current + ":", agent.CacheSalt(ApiUsagePurpose.RerankAsk), StringComparison.Ordinal);
        Assert.NotEqual(agent.CacheSalt(ApiUsagePurpose.RerankFeed), agent.CacheSalt(ApiUsagePurpose.RerankAsk));
    }

    [Fact]
    public void The_rerank_cache_key_carries_the_salt_the_context_and_the_candidate_set()
    {
        var account = Guid.NewGuid();
        var batch = new[] { TitleA, TitleB, TitleC };
        var key = RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", account, TitleA, "row-1", batch);

        // Order and duplicates do not matter — the agent shuffles the prompt itself.
        Assert.Equal(key, RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", account, TitleA, "row-1", [TitleC, TitleA, TitleB, TitleA]));

        // Every part does. The same title judged among other companions is another observation.
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", account, TitleA, "row-1", [TitleA, TitleB]));
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|low:p00000000", account, TitleA, "row-1", batch));
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|none:p11111111", account, TitleA, "row-1", batch));
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", account, TitleA, "row-2", batch));
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", Guid.NewGuid(), TitleA, "row-1", batch));
        Assert.NotEqual(key, RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", account, TitleB, "row-1", batch));
        Assert.Matches("^c[0-9a-f]{16}$", RerankCacheKey.CandidateSet(batch));
    }

    [Fact]
    public async Task Every_ask_path_call_reserves_against_a_per_account_ceiling()
    {
        // Round-1 review defect 5: the turn's question-reading calls (search-agent and its recovery
        // interpreter, search-interpret) were admitted with no per-account ceiling. This drives every
        // model call the shipping Ask path can make — the interpret, its retry, the recovery
        // interpreter, the request-judging re-rank and the expansion — through a recording gate.
        var gate = new RecordingGate();
        var account = new FrostAura.Reel.Application.Tenancy.AccountContext();
        account.SetAccount(Guid.NewGuid());
        var config = LiveConfig();
        var interpreterClient = new HttpClient(new InterpreterHandler(Envelope("""{"genres":["action"],"keywords":["ninja"]}""")))
        {
            BaseAddress = new Uri("https://openrouter.invalid/api/v1/"),
        };
        var interpreter = new OpenRouterSearchInterpreter(
            interpreterClient, Recorder(), config, NullLogger<OpenRouterSearchInterpreter>.Instance, account, gate: gate);
        var handler = new CannedHandler(
            Ok("I can help with that."), Ok("Still prose."), Ok("""{"results":[]}"""), Ok("""{"results":[]}"""));
        var agent = new OpenRouterSearchAgent(
            new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            interpreter, Recorder(), config, NullLogger<OpenRouterSearchAgent>.Instance, account, gate: gate);

        await agent.InterpretAsync([], "ninja movies", "likes action", []);
        await agent.RerankBatchAsync(Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankAsk);
        await agent.RerankBatchAsync(Titles(), "ninja movies", "taste", anchorName: null, ApiUsagePurpose.RerankExpand);

        Assert.Equal(
            [ApiUsagePurpose.SearchAgent, ApiUsagePurpose.SearchAgent, ApiUsagePurpose.SearchInterpret,
                ApiUsagePurpose.RerankAsk, ApiUsagePurpose.RerankExpand],
            gate.Calls.Select(c => c.Purpose));
        Assert.All(gate.Calls, c =>
        {
            Assert.Equal(account.AccountId, c.AccountId);
            Assert.True(FrostAura.Reel.Application.Llm.LlmCallGate.IsGated(c.Purpose), c.Purpose);
            Assert.True(
                FrostAura.Reel.Application.Llm.AskBudget.For(config, c.Purpose) is not null
                    || ApiUsagePurpose.RerankFamily.Contains(c.Purpose),
                c.Purpose);
        });

        // And the v2 purposes, which no shipping path calls yet, are gated the same way.
        Assert.All(ApiUsagePurpose.AskFamily, p => Assert.True(FrostAura.Reel.Application.Llm.LlmCallGate.IsGated(p), p));
    }

    private static IConfiguration LiveConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();

    /// <summary>Admits every call and records who asked for what.</summary>
    private sealed class RecordingGate : FrostAura.Reel.Application.Llm.ILlmCallGate
    {
        public List<(Guid? AccountId, string Purpose)> Calls { get; } = [];

        public Task<FrostAura.Reel.Application.Llm.LlmAdmission> TryReserveAsync(
            Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((accountId, purpose));
            }

            return Task.FromResult(FrostAura.Reel.Application.Llm.LlmAdmission.Unmetered);
        }
    }

    /// <summary>Refuses every call, as a spent daily ceiling does.</summary>
    private sealed class RefusingGate : FrostAura.Reel.Application.Llm.ILlmCallGate
    {
        public Task<FrostAura.Reel.Application.Llm.LlmAdmission> TryReserveAsync(
            Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct) =>
            Task.FromResult(FrostAura.Reel.Application.Llm.LlmAdmission.Refuse(
                "search-agent ceiling would be passed (17/17 calls)"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterSearchAgent Agent(CannedHandler handler) => Agent(handler, new HeuristicInterpreter());

    private static OpenRouterSearchAgent Agent(CannedHandler handler, ApiUsageRecorder recorder) =>
        Agent(handler, new HeuristicInterpreter(), recorder);

    private static OpenRouterSearchAgent Agent(
        CannedHandler handler, ApiUsageRecorder recorder, FrostAura.Reel.Infrastructure.Llm.ModelCapabilities capabilities) =>
        Agent(handler, new HeuristicInterpreter(), recorder, capabilities);

    private static OpenRouterSearchAgent Agent(CannedHandler handler, HeuristicInterpreter interpreter) =>
        Agent(handler, interpreter, Recorder());

    private static ApiUsageRecorder Recorder()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
    }

    private static OpenRouterSearchAgent Agent(
        CannedHandler handler, HeuristicInterpreter interpreter, ApiUsageRecorder recorder,
        FrostAura.Reel.Infrastructure.Llm.ModelCapabilities? capabilities = null)
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
            client, interpreter, recorder, config, NullLogger<OpenRouterSearchAgent>.Instance, capabilities: capabilities);
    }

    private static string? EffortOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetProperty("effort").GetString() : null;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>An agent whose configuration decides stub mode: no key, with or without the test flag.</summary>
    private static OpenRouterSearchAgent StubAgent(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var client = new HttpClient(new CannedHandler(new CannedResponse(HttpStatusCode.InternalServerError, "{}")))
        {
            BaseAddress = new Uri("https://openrouter.invalid/api/v1/"),
        };
        return new OpenRouterSearchAgent(
            client, new HeuristicInterpreter(), Recorder(), config, NullLogger<OpenRouterSearchAgent>.Instance);
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

        /// <summary>The conversation the recovery path handed down, through the history overload.</summary>
        public IReadOnlyList<ChatTurn>? LastHistory { get; private set; }

        public Task<SearchIntent> InterpretAsync(
            string query, CancellationToken ct = default, string? standingFilters = null)
        {
            InterpretCalls++;
            LastStandingFilters = standingFilters;
            return Task.FromResult(Intent(query));
        }

        public Task<SearchIntent> InterpretAsync(
            string query, IReadOnlyList<ChatTurn> history, CancellationToken ct = default, string? standingFilters = null)
        {
            LastHistory = history;
            return InterpretAsync(query, ct, standingFilters);
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
