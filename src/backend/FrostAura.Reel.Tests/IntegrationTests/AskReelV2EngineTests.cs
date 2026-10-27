using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Ask Reel v2 on a real catalogue (WS-2 tasks 2 and 4): the engine with scripted model ports over
/// the real retriever, eligibility, scoring and persistence — and one turn through the endpoint
/// with <c>ASK_ENGINE=v2</c>, resuming a recorded v1 conversation.
///
/// <para>Titles are scripted by name — "V2 Yes", "V2 Partial", "V2 No" — and the judge rules on
/// the name, so every placement is asserted on the wire, in the stored state and in the call log.
/// Each test seeds its own topic word, so the facet retriever finds only that test's titles.</para>
/// </summary>
[Collection("postgres")]
public class AskReelV2EngineTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_search_turn_streams_picks_per_batch_and_places_every_verdict()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 4), ("V2 Partial", 3), ("V2 No", 3));
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var judge = new NameJudge();
        var persisted = new List<LiveSearchExpansionService.TurnCard>();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input($"{topic} movies") with { PersistCards = cards => { persisted.AddRange(cards); return Task.CompletedTask; } },
            accountId, emit, CancellationToken.None);

        var names = events.Select(e => e.Event).ToList();
        Assert.Equal("phase", names[0]);
        Assert.True(names.IndexOf("plan") < names.IndexOf("picks"));
        Assert.Equal("done", names[^1]);
        Assert.Equal("assistant-message", names[^2]);

        var plan = Assert.Single(events, e => e.Event == "plan").Data;
        Assert.Equal("NewRequest", plan.GetProperty("act").GetString());
        Assert.Equal([topic], plan.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()));

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray()).ToList();
        Assert.Equal(4, picks.Count);
        Assert.All(picks, c => Assert.StartsWith("V2 Yes", c.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.Equal(["T1.1", "T1.2", "T1.3", "T1.4"], picks.Select(c => c.GetProperty("handle").GetString()));
        Assert.All(picks, c => Assert.Equal("keyword: " + topic, c.GetProperty("evidence").GetString()));

        var tray = Assert.Single(events, e => e.Event == "tray").Data;
        Assert.Equal("nearMiss", tray.GetProperty("kind").GetString());
        Assert.Equal(3, tray.GetProperty("cards").GetArrayLength());
        Assert.DoesNotContain(events, e => e.Data.ToString().Contains("V2 No", StringComparison.Ordinal));

        var done = events[^1].Data;
        Assert.Equal("Picks", done.GetProperty("mode").GetString());
        Assert.Equal("v2", done.GetProperty("engine").GetString());
        Assert.Equal(4, done.GetProperty("results").GetArrayLength());
        Assert.Equal(2, judge.Batches);

        // Written as emitted, and the turn record carries the v2 fields and the new state.
        Assert.Equal(7, persisted.Count);
        Assert.Equal(7, outcome.Cards.Count);
        Assert.Equal("NewRequest", outcome.Act);
        Assert.Equal("Llm", outcome.PlanSource);
        var state = ConversationStateJson.Read(outcome.ConversationStateJson);
        Assert.NotNull(state);
        Assert.Equal(4, state.LastPresented.Count);
        Assert.Equal(1, state.Version);
        // "movies" because the message says so (the sanitizer keeps the user's media word).
        Assert.Equal($"4 {topic} movies that fit what you asked. 3 more are close but not quite — tucked away below.", outcome.AssistantText);
    }

    [Fact]
    public async Task An_item_question_answers_from_the_facts_with_no_judge_call_and_an_untouched_grid()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion, references: new TurnHandle(1, 2)) with
        {
            Tools = [new ToolRequest(AskTool.TitleFacts)],
            Question = "is it violent?",
        });
        var judge = new NameJudge();
        var answerer = new ScriptedAnswerer("It is rated R, and its keywords include " + topic + ".");
        var (engine, events, emit) = Build(accountId, planner, judge, answerer);

        var presented = new List<PresentedCard>
        {
            new(1, 1, ids[0], "V2 Yes 0", 2010, MediaType.Movie),
            new(1, 2, ids[1], "V2 Yes 1", 2010, MediaType.Movie),
        };
        var outcome = await engine.StreamAsync(
            Input("is the second one violent?") with { Presented = presented, ShownTitleIds = ids, AnswerNumber = 2 },
            accountId, emit, CancellationToken.None);

        var answer = Assert.Single(events, e => e.Event == "answer").Data;
        Assert.Equal(answerer.Text, answer.GetProperty("text").GetString());
        Assert.Equal("T1.2", answer.GetProperty("handle").GetString());
        Assert.Equal(ids[1], answer.GetProperty("titleId").GetGuid());
        Assert.Equal(ids[1], answerer.AskedAbout);
        Assert.Equal(0, judge.Batches);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray");
        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Empty(outcome.Cards);
        Assert.Equal("ItemQuestion", outcome.Act);
        Assert.Equal(answerer.Text, outcome.AssistantText);
        // The `answer` frame IS the reply: no `assistant-message` repeats it (a client renders both).
        Assert.DoesNotContain(events, e => e.Event == "assistant-message");
        Assert.Equal(["answer", "done"], events.Select(e => e.Event).TakeLast(2));
    }

    /// <summary>
    /// R1-12, through the engine: "what are my conditions?" reads the account's maturity ceiling and
    /// bar, so an account whose only conditions are those two is told about them — never that it has
    /// no conditions — and no pool count is quoted that the meter could not apply the ceiling to.
    /// Varied: the same account with no ceiling falls back to the rules-only answer with the count.
    /// </summary>
    [Theory]
    [InlineData("PG-13", 0, "Your standing conditions: max maturity PG-13.")]
    [InlineData("PG-13", 6.5, "Your standing conditions: max maturity PG-13. Your bar is 6.5:")]
    [InlineData(null, 6.5, "Your only standing condition is your bar of 6.5:")]
    public async Task The_conditions_tool_names_the_ceiling_and_the_bar_and_quotes_no_count_under_a_ceiling(
        string? ceiling, double bar, string expectedLead)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Settings.MaturityCeiling = ceiling;
            await db.SaveChangesAsync();
        }

        if (bar > 0)
        {
            await SeedBarAsync(accountId, (decimal)bar);
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion) with { Tools = [new ToolRequest(AskTool.Conditions)] });
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input("what are my conditions?"), accountId, emit, CancellationToken.None);

        var text = Assert.Single(events, e => e.Event == "answer").Data.GetProperty("text").GetString()!;
        Assert.StartsWith(expectedLead, text, StringComparison.Ordinal);
        Assert.DoesNotContain("no standing conditions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("every unseen title", text, StringComparison.Ordinal);
        if (ceiling is not null)
        {
            Assert.DoesNotContain("open to you, out of", text, StringComparison.Ordinal);
        }

        Assert.Equal(0, judge.Batches);
        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Contains(events, e => e.Event == "phase" && e.Data.ToString().Contains("Reading your conditions", StringComparison.Ordinal));
    }

    /// <summary>
    /// R1-30, through the engine: a planner that names NO handle for "more like the second one" (what
    /// the live planner did five times in six) still searches near the newest answer's second card —
    /// the plan frame names T1.2, the state carries that title as the reference, and it is not shown
    /// again. Varied: "the last one" lands on the newest answer's last card.
    /// </summary>
    [Theory]
    [InlineData("more like the second one", 1)]
    [InlineData("more like the last one", 2)]
    public async Task A_pointer_the_planner_left_unresolved_still_searches_near_the_card_it_names(string message, int index)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 6));
        var presented = ids.Take(3).Select((id, i) => new PresentedCard(1, i + 1, id, $"V2 Yes {i}", 2010, MediaType.Movie)).ToList();
        var conversationId = await SeedConversationAsync(
            accountId,
            ConversationState.Empty with
            {
                Version = 1,
                Hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [], [])] },
            });
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.Refine));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input(message) with { ConversationId = conversationId, AnswerNumber = 2, Presented = presented, ShownTitleIds = ids.Take(3).ToList() },
            accountId, emit, CancellationToken.None);

        var expected = presented[index];
        // The judge never sees the screen, so it is told which title the pointer resolved to; left to
        // read "the second one" itself, the live judge picked a card from another answer (R1-30).
        Assert.NotEmpty(judge.Requests);
        Assert.All(judge.Requests, r => Assert.Contains($"Points at: {expected.Name} (2010)", r, StringComparison.Ordinal));
        Assert.All(
            presented.Where(p => p.TitleId != expected.TitleId),
            p => Assert.All(judge.Requests, r => Assert.DoesNotContain(p.Name + " (", r, StringComparison.Ordinal)));
        var plan = Assert.Single(events, e => e.Event == "plan").Data;
        Assert.Equal([$"T1.{index + 1}"], plan.GetProperty("references").EnumerateArray().Select(r => r.GetString()));
        var state = ConversationStateJson.Read(outcome.ConversationStateJson);
        Assert.NotNull(state);
        Assert.Contains(expected.TitleId, state.Soft.ReferenceTitleIds);
        Assert.DoesNotContain(
            events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray()),
            c => c.GetProperty("titleId").GetGuid() == expected.TitleId);
    }

    /// <summary>Task 4: "how many X are left?" is the supply tool — counts only, no cards, no judge.</summary>
    [Fact]
    public async Task A_how_many_question_is_answered_from_the_supply_with_no_cards()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 5));
        await MarkSeenAsync(accountId, ids.Take(2));
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion, ConversationStateDelta.None with { Concepts = [topic] }) with
        {
            Tools = [new ToolRequest(AskTool.Supply)],
        });
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"how many {topic} movies are left?"), accountId, emit, CancellationToken.None);

        Assert.Equal($"You have 3 unseen {topic} movies left inside your conditions; you've seen 2.", outcome.AssistantText);
        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Equal(0, judge.Batches);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray");
    }

    /// <summary>
    /// Clarify: two readings that differ in what they require are asked back once — no search, no
    /// judge — and the state remembers it, so the planner is never allowed a second one.
    /// </summary>
    [Fact]
    public async Task A_clarifying_question_is_asked_once_with_no_search()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest) with
        {
            ClarifyQuestion = "Do you mean the film or the series?",
            ClarifyOptions = ["The film", "The series"],
        });
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input("something like fargo"), accountId, emit, CancellationToken.None);

        var clarify = Assert.Single(events, e => e.Event == "clarify").Data;
        Assert.Equal("Do you mean the film or the series?", clarify.GetProperty("question").GetString());
        Assert.Equal(2, clarify.GetProperty("options").GetArrayLength());
        Assert.Equal(SearchTurnMode.Clarify, outcome.Mode);
        Assert.Equal(0, judge.Batches);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray" or "none-found");
        Assert.True(ConversationStateJson.Read(outcome.ConversationStateJson)!.ClarifiedOnce);
        Assert.DoesNotContain(events, e => e.Event == "assistant-message");
        Assert.Equal("Do you mean the film or the series?", outcome.AssistantText);
    }

    /// <summary>
    /// XM-06: the bar binds every card. With the judge down, nothing is checked, and a card the
    /// account's bar holds back (here: unscored while a bar is set) goes to the labelled floor tray —
    /// never the unchecked tray, which reads as cards that cleared it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unchecked_card_the_bar_holds_back_is_in_the_floor_tray_never_the_unchecked_one(bool judgeAvailable)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 3));
        await SeedBarAsync(accountId, 6.0m);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, events, emit) = Build(accountId, planner, new DownJudge(judgeAvailable));

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        var trays = events.Where(e => e.Event == "tray").ToDictionary(e => e.Data.GetProperty("kind").GetString()!, e => e.Data);
        Assert.Equal(3, trays["belowFloor"].GetProperty("cards").GetArrayLength());
        Assert.False(trays.ContainsKey("unchecked"));
        var done = events[^1].Data;
        Assert.Equal(0, done.GetProperty("unchecked").GetArrayLength());
        Assert.Equal(3, done.GetProperty("belowFloor").GetArrayLength());
        Assert.All(done.GetProperty("belowFloor").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("satisfies").ValueKind));
        Assert.All(outcome.Cards, c => Assert.Equal(SearchTurnCardTier.BelowFloor, c.Tier));
        Assert.DoesNotContain(events, e => e.Event == "picks");
    }

    /// <summary>
    /// A mood request's text is embedded before retrieval, so the semantic arm — its only topical arm —
    /// has the vector; a request with a concept is not held up for it.
    /// </summary>
    [Fact]
    public async Task A_mood_request_is_embedded_before_retrieval_and_a_concept_request_is_not()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var mood = $"cosy{Guid.NewGuid():N}"[..12];
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 1));
        var embeddings = new RecordingEmbeddings();
        var planner = new ScriptedPlanner(input => input.Message.StartsWith(mood, StringComparison.Ordinal)
            ? Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Moods = [mood] })
            : Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, _, emit) = Build(accountId, planner, new NameJudge(), embeddings: embeddings);

        var moodTurn = await engine.StreamAsync(Input($"{mood} please"), accountId, emit, CancellationToken.None);
        await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal([mood], embeddings.Asked);
        Assert.Contains(AskStageTimings.RetrieveEmbed, moodTurn.StageTimingsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// WS-3 CR-2b/c: the user's own rejections, the message and the region ride into retrieval, and an
    /// arm that contributed nothing is noted on the stored row only — never sent as a degraded turn.
    /// </summary>
    [Fact]
    public async Task Retrieval_gets_the_rejections_message_and_region_and_a_skipped_arm_is_stored_not_shown()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            Concepts = [topic],
            Negations = ["horror", "superheroes"],
        }));
        RetrievalQuery? seen = null;
        var (engine, events, emit) = Build(
            accountId, planner, new NameJudge(), wrap: inner => new SkippingRetriever(inner, q => seen = q, "tmdb-late"));

        var message = $"{topic} movies, no horror and no superheroes";
        var outcome = await engine.StreamAsync(Input(message), accountId, emit, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(["horror"], seen.ExcludeGenres);
        Assert.Contains("superhero", seen.ExcludeKeywords);
        Assert.Equal(message, seen.Message);
        Assert.Equal("US", seen.Region);
        Assert.Contains(SearchTurnDegraded.ArmPrefix + "tmdb-late", outcome.DegradedFlags!.Split(','));
        Assert.DoesNotContain(events, e => e.Event == "degraded");
        Assert.Equal(0, events[^1].Data.GetProperty("degraded").GetArrayLength());
        Assert.Equal(2, events.Where(e => e.Event == "picks").Sum(e => e.Data.GetProperty("cards").GetArrayLength()));
    }


    [Fact]
    public async Task A_spent_plan_allowance_is_a_coded_refusal_before_any_work()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.BudgetSpent, "budget_spent", Refusal: true), LlmCallMeta.None));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input("heist movies"), accountId, emit, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("ask_budget_spent", outcome.FailureCode);
        Assert.Equal(0, judge.Batches);
        Assert.DoesNotContain(events, e => e.Event is "plan" or "picks" or "tray");
        Assert.True(events[^1].Data.GetProperty("failed").GetBoolean());
        Assert.Equal(AgentReply.BudgetSpentReply, outcome.AssistantText);
    }

    [Fact]
    public async Task A_plan_that_fails_is_carried_forward_and_says_so()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var conversationId = await SeedConversationAsync(
            accountId,
            ConversationState.Empty with
            {
                Version = 1,
                Hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [], [])] },
            });
        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.Truncated, null, Refusal: false), new LlmCallMeta("m", "none", true, 2, 10)));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(
            Input("more of those but the third thing I said") with { ConversationId = conversationId, AnswerNumber = 2 },
            accountId, emit, CancellationToken.None);

        Assert.Equal("CarriedForward", outcome.PlanSource);
        Assert.Contains(SearchTurnDegraded.Plan, outcome.DegradedFlags!.Split(','));
        Assert.StartsWith($"I couldn't fully read that — searching for {topic} as before.", outcome.AssistantText, StringComparison.Ordinal);
        Assert.Equal(3, events.Where(e => e.Event == "picks").Sum(e => e.Data.GetProperty("cards").GetArrayLength()));
        Assert.Equal(1, outcome.TruncationCount);
    }

    [Fact]
    public async Task A_person_request_returns_unseen_credits_counts_the_seen_ones_and_never_judges()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var director = $"Director {Guid.NewGuid():N}"[..20];
        var credited = await SeedCreditedAsync(director, 3);
        await MarkSeenAsync(accountId, [credited[0]]);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            People = [new PersonConstraint(director, null, PersonRole.Director)],
        }) with
        { Tools = [new ToolRequest(AskTool.Filmography)] });
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"what has {director} directed?"), accountId, emit, CancellationToken.None);

        Assert.Equal(0, judge.Batches);
        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray()).ToList();
        Assert.Equal(credited.Skip(1).Order(), picks.Select(c => c.GetProperty("titleId").GetGuid()).Order());
        Assert.All(picks, c => Assert.Equal("yes", c.GetProperty("satisfies").GetString()));
        Assert.Equal($"You've seen 1 title directed by {director}; here are 2 you haven't.", outcome.AssistantText);
        Assert.DoesNotContain("Credited 0", outcome.AssistantText, StringComparison.Ordinal);
        Assert.All(outcome.Cards, c => Assert.Equal("Facet", c.WhySource));
    }

    [Fact]
    public async Task Nothing_left_is_an_honest_none_found_with_the_supply_behind_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        await MarkSeenAsync(accountId, ids);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        var supply = Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply");
        Assert.Equal(3, supply.GetProperty("alreadySeen").GetInt32());
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(0, judge.Batches);
        Assert.StartsWith($"There are no {topic} movies left for you.", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// §4.6 acceptance: a recorded v1 conversation resumed under v2 repeats nothing — through the
    /// endpoint, with the state rebuilt from the turn record and written back beside it.
    /// </summary>
    [Fact]
    public async Task A_v1_conversation_resumed_under_v2_repeats_nothing_and_records_its_state()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var judge = new NameJudge();
        var planner = new ScriptedPlanner(input => Plan(TurnAct.Refine) with { Tools = [new ToolRequest(AskTool.SearchCatalog)] });
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AskReelEngineOptions.Setting, AskReelEngineOptions.V2);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAskReelPlanner>();
                services.AddSingleton<IAskReelPlanner>(planner);
                services.RemoveAll<IRequestJudgeModel>();
                services.AddSingleton<IRequestJudgeModel>(judge);
                services.RemoveAll<ITmdbClient>();
                services.AddSingleton<ITmdbClient, SilentTmdbClient>();
            });
        });
        var (accountId, client) = await SignInAsync(factory, $"ask-v2-{Guid.NewGuid():N}");
        using (client)
        {
            var ids = await SeedTopicalAsync(topic, ("V2 Yes", 6));
            var conversationId = await SeedV1ConversationAsync(accountId, topic, ids.Take(3).ToList());

            var stream = await AskAsync(client, new { query = "more please", conversationId, clientTurnId = Guid.NewGuid() });

            Assert.Equal("v2", stream.Single(f => f.Event == "conversation").Data.GetProperty("engine").GetString());
            var shown = stream.Where(f => f.Event == "picks")
                .SelectMany(f => f.Data.GetProperty("cards").EnumerateArray())
                .Select(c => c.GetProperty("titleId").GetGuid())
                .ToList();
            Assert.Equal(ids.Skip(3).Order(), shown.Order());
            Assert.Empty(shown.Intersect(ids.Take(3)));
            Assert.Equal(["T2.1", "T2.2", "T2.3"], stream.Where(f => f.Event == "picks")
                .SelectMany(f => f.Data.GetProperty("cards").EnumerateArray())
                .Select(c => c.GetProperty("handle").GetString()));

            // The planner saw the v1 answer's handles and the topic the v1 reading stored.
            var seen = Assert.Single(planner.Inputs);
            Assert.Equal(["T1.1", "T1.2", "T1.3"], seen.State.LastPresented.Select(p => p.Handle.ToString()));
            Assert.Equal([topic], seen.State.Hard.Concepts.Select(c => c.UserWords));

            var scope = new AccountContext();
            scope.SetAccount(accountId);
            await using var db = Fixture.CreateContext(scope);
            var conversation = await db.SearchConversations.SingleAsync(c => c.Id == conversationId);
            Assert.Equal(2, conversation.StateVersion);
            Assert.NotNull(ConversationStateJson.Read(conversation.StateJson));
            var turn = await db.SearchTurns.Where(t => t.ConversationId == conversationId && t.Role == "assistant")
                .OrderByDescending(t => t.Sequence).FirstAsync();
            Assert.Equal("Refine", turn.Act);
            Assert.Equal("Llm", turn.PlanSource);
            Assert.Equal(SearchTurnStatus.Completed, turn.Status);
            Assert.Equal(3, await db.SearchTurnCards.CountAsync(c => c.TurnId == turn.Id));

            // Resume renders every turn's own cards under its own row, and repeats none.
            var resumed = await client.GetFromJsonAsync<JsonElement>($"/api/search/conversations/{conversationId}");
            var perTurn = resumed.GetProperty("turns").EnumerateArray()
                .Where(t => t.GetProperty("role").GetString() == "assistant")
                .Select(t => t.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("titleId").GetGuid()).ToList())
                .ToList();
            Assert.Equal(2, perTurn.Count);
            Assert.Equal(ids.Take(3).Order(), perTurn[0].Order());
            Assert.Equal(ids.Skip(3).Order(), perTurn[1].Order());
        }
    }

    /// <summary>
    /// An item question answers BESIDE the grid, and a resume keeps it that way: the restored grid is
    /// the search the question was about — its cards, its mode — not the question's empty answer.
    /// </summary>
    [Fact]
    public async Task A_resume_after_an_item_question_restores_the_answer_it_was_about()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var planner = new ScriptedPlanner(input => input.Message.StartsWith("is the first", StringComparison.Ordinal)
            ? Plan(TurnAct.ItemQuestion, references: new TurnHandle(1, 1)) with { Tools = [new ToolRequest(AskTool.TitleFacts)] }
            : Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AskReelEngineOptions.Setting, AskReelEngineOptions.V2);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAskReelPlanner>();
                services.AddSingleton<IAskReelPlanner>(planner);
                services.RemoveAll<IRequestJudgeModel>();
                services.AddSingleton<IRequestJudgeModel>(new NameJudge());
                services.RemoveAll<ITmdbClient>();
                services.AddSingleton<ITmdbClient, SilentTmdbClient>();
            });
        });
        var (_, client) = await SignInAsync(factory, $"ask-v2-iq-{Guid.NewGuid():N}");
        using (client)
        {
            var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
            var first = await AskAsync(client, new { query = $"{topic} movies", clientTurnId = Guid.NewGuid() });
            var conversationId = first.Single(f => f.Event == "conversation").Data.GetProperty("id").GetGuid();
            var question = await AskAsync(client, new { query = "is the first one violent?", conversationId, clientTurnId = Guid.NewGuid() });
            Assert.Single(question, f => f.Event == "answer");
            Assert.DoesNotContain(question, f => f.Event == "assistant-message");

            var resumed = await client.GetFromJsonAsync<JsonElement>($"/api/search/conversations/{conversationId}");

            Assert.Equal(ids.Order(), resumed.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("titleId").GetGuid()).Order());
            Assert.Equal("Picks", resumed.GetProperty("mode").GetString());
            var answers = resumed.GetProperty("turns").EnumerateArray().Where(t => t.GetProperty("role").GetString() == "assistant").ToList();
            Assert.Equal(["Picks", "Answer"], answers.Select(t => t.GetProperty("mode").GetString()));
            Assert.Equal(0, answers[1].GetProperty("cards").GetArrayLength());
        }
    }

    /// <summary>
    /// R1-31: a resume hands back every earlier answer's OWN trays, nested under <c>trays</c> as the
    /// chat UI reads them — so an earlier answer still says "· N in its trays" after a reload. The two
    /// answers carry different near-miss counts (2, then 3), so a payload that repeated the newest
    /// answer's trays on every row, or the top-level trays, would fail.
    /// </summary>
    [Fact]
    public async Task A_resume_returns_each_earlier_answers_own_trays_nested_under_trays()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var first = Topic();
        var second = Topic();
        var planner = new ScriptedPlanner(input => Plan(
            TurnAct.NewRequest,
            ConversationStateDelta.None with { Concepts = [input.Message.StartsWith(first, StringComparison.Ordinal) ? first : second] }));
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AskReelEngineOptions.Setting, AskReelEngineOptions.V2);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAskReelPlanner>();
                services.AddSingleton<IAskReelPlanner>(planner);
                services.RemoveAll<IRequestJudgeModel>();
                services.AddSingleton<IRequestJudgeModel>(new NameJudge());
                services.RemoveAll<ITmdbClient>();
                services.AddSingleton<ITmdbClient, SilentTmdbClient>();
            });
        });
        var (_, client) = await SignInAsync(factory, $"ask-v2-trays-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedTopicalAsync(first, ("V2 Yes", 2), ("V2 Partial", 2));
            await SeedTopicalAsync(second, ("V2 Yes", 1), ("V2 Partial", 3));
            var opened = await AskAsync(client, new { query = $"{first} movies", clientTurnId = Guid.NewGuid() });
            var conversationId = opened.Single(f => f.Event == "conversation").Data.GetProperty("id").GetGuid();
            await AskAsync(client, new { query = $"{second} movies", conversationId, clientTurnId = Guid.NewGuid() });

            var resumed = await client.GetFromJsonAsync<JsonElement>($"/api/search/conversations/{conversationId}");

            var rows = resumed.GetProperty("turns").EnumerateArray().ToList();
            var answers = rows.Where(t => t.GetProperty("role").GetString() == "assistant").ToList();
            Assert.Equal(2, answers.Count);
            int Tray(JsonElement row, string kind) => row.GetProperty("trays").GetProperty(kind).GetArrayLength();
            Assert.Equal([2, 3], answers.Select(a => Tray(a, "nearMiss")));
            Assert.Equal([2, 1], answers.Select(a => a.GetProperty("cards").GetArrayLength()));
            Assert.All(answers, a => Assert.Equal(0, Tray(a, "belowFloor")));
            Assert.All(answers, a => Assert.Equal(0, Tray(a, "unchecked")));
            // Nested, never flat: a flat `unchecked` collides with the client's judge-degraded flag.
            Assert.All(answers, a => Assert.False(a.TryGetProperty("nearMiss", out _)));
            Assert.All(answers, a => Assert.False(a.TryGetProperty("unchecked", out _)));
            Assert.All(rows.Where(t => t.GetProperty("role").GetString() == "user"),
                u => Assert.Equal(JsonValueKind.Null, u.GetProperty("trays").ValueKind));
            // The earlier answer's near misses are its own titles, not the newest answer's.
            Assert.All(answers[0].GetProperty("trays").GetProperty("nearMiss").EnumerateArray(),
                c => Assert.StartsWith("V2 Partial", c.GetProperty("name").GetString(), StringComparison.Ordinal));
            Assert.Equal(3, resumed.GetProperty("nearMiss").GetArrayLength());
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The catalogue arms these tests read match a concept by its own words; nothing to resolve.</summary>
    private sealed class PassThroughResolver : IConceptResolver
    {
        public Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct) => Task.FromResult(hard);

        public Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct) => Task.FromResult(concept);
    }

    private static string Topic() => $"vtopic{Guid.NewGuid():N}"[..18];

    private static PlanResult Plan(TurnAct act, ConversationStateDelta? delta = null, params TurnHandle[] references) =>
        new(act, delta ?? ConversationStateDelta.None, references, [new ToolRequest(AskTool.SearchCatalog)], null, [], PlanSource.Llm,
            new LlmCallMeta("scripted-plan", "none", false, 1, 5));

    private static LiveSearchExpansionService.AskTurnInput Input(string message) =>
        new(message, [], [], [], "US", [], Domain.Sync.ApiUsagePurpose.RerankAsk);

    private (AskReelEngine Engine, List<(string Event, JsonElement Data)> Events, LiveSearchExpansionService.Emit Emit) Build(
        Guid accountId, IAskReelPlanner planner, IRequestJudgeModel judge, IAskReelAnswerer? answerer = null,
        Func<IAskReelRetriever, IAskReelRetriever>? wrap = null, IEmbeddingProvider? embeddings = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var supply = new SupplyReporter(db, eligibility);
        var features = new FeatureVectorBuilder(db);
        var model = new ConstantModelEngine(8.4f);
        var scorer = new OnDemandScorer(
            db, features, model, new OnlineResidualModel(db, features, model, cache, NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, cache), new RatingCalibrationService(db, cache));
        var engine = new AskReelEngine(
            db, planner, new RequestJudge(judge, cache, NullLogger<RequestJudge>.Instance), judge, answerer ?? new ScriptedAnswerer("-"),
            new TitleFacts(db), (wrap ?? (r => r))(new FacetRetriever(db, eligibility, supply)), supply, eligibility, scorer,
            new ExposureLog(db, NullLogger<ExposureLog>.Instance), features, new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new TurnComposer(), new PassThroughResolver(), new FilterCostMeter(db, eligibility, scoped, cache),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            cache, new ConceptEmbeddings(embeddings ?? new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            NullLogger<AskReelEngine>.Instance);

        var events = new List<(string, JsonElement)>();
        LiveSearchExpansionService.Emit emit = (name, payload) =>
        {
            lock (events)
            {
                events.Add((name, JsonSerializer.SerializeToElement(payload, WireJson)));
            }

            return Task.CompletedTask;
        };
        return (engine, events, emit);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"v2-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<List<Guid>> SeedTopicalAsync(string topic, params (string Prefix, int Count)[] groups)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var ids = new List<Guid>();
        var votes = 5_000;
        foreach (var (prefix, count) in groups)
        {
            for (var i = 0; i < count; i++)
            {
                var title = NewTitle($"{prefix} {i} {Guid.NewGuid():N}"[..(prefix.Length + 8)], votes--);
                title.Keywords = [topic];
                db.Titles.Add(title);
                ids.Add(title.Id);
            }
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<List<Guid>> SeedCreditedAsync(string director, int count)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue), Name = director };
        db.Persons.Add(person);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"Credited {i} {Guid.NewGuid():N}"[..18], 4_000 - i);
            db.Titles.Add(title);
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = person.Id, Role = CreditRole.Director });
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static Title NewTitle(string name, int votes) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
        TraktSlug = string.Empty,
        Name = name,
        Language = "en",
        Year = 2010,
        RuntimeMinutes = 100,
        PosterPath = "/p.jpg",
        Genres = ["action"],
        Keywords = [],
        TmdbVoteAverage = 7.5m,
        TmdbVoteCount = votes,
        TmdbPopularity = 0m,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task SeedBarAsync(Guid accountId, decimal bar)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new Domain.Filters.ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = Domain.Filters.FilterKind.MinPredictedRating,
            Value = bar.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task MarkSeenAsync(Guid accountId, IEnumerable<Guid> titleIds)

    {
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var titleId in titleIds)
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = DateTime.UtcNow,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedConversationAsync(Guid accountId, ConversationState state)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "seeded",
            CreatedAt = DateTime.UtcNow,
            LastTurnAt = DateTime.UtcNow,
            StateJson = ConversationStateJson.Write(state),
            StateVersion = state.Version,
        };
        db.SearchConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>A v1 conversation as v1 recorded it: an ask, an answer with a v1 reading, three cards, no state.</summary>
    private async Task<Guid> SeedV1ConversationAsync(Guid accountId, string topic, IReadOnlyList<Guid> shown)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow.AddMinutes(-5);
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = $"{topic} movies",
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 2,
            LastCardsAt = now,
        };
        var user = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "user",
            Text = $"{topic} movies",
            CreatedAt = now,
            Sequence = 0,
            Status = SearchTurnStatus.Completed,
            CompletedAt = now,
        };
        var intent = new SearchIntent([], [topic], [], string.Empty, null, null);
        var answer = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = $"3 {topic} picks for you.",
            CreatedAt = now,
            Sequence = 1,
            ClientTurnId = Guid.NewGuid(),
            Status = SearchTurnStatus.Completed,
            CompletedAt = now,
            IntentJson = JsonSerializer.Serialize(intent),
            Mode = SearchTurnMode.Picks,
        };
        db.AddRange(conversation, user, answer);
        db.SearchTurnCards.AddRange(shown.Select((id, rank) => new SearchTurnCard
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            TurnId = answer.Id,
            TitleId = id,
            Rank = rank,
            Blend = 0.5m,
            WhySource = "None",
            CreatedAt = now,
        }));
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(WebApplicationFactory<Program> factory, string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        var cookies = new CookieContainerHandler();
        cookies.Container.Add(
            factory.ClientOptions.BaseAddress,
            new Cookie("reel_at", token, "/", factory.ClientOptions.BaseAddress.Host));
        return (accountId, factory.CreateDefaultClient(cookies));
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private static async Task<List<SseFrame>> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        return raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(chunk => !chunk.StartsWith(':'))
            .Select(chunk => Regex.Match(chunk, @"^(id: \d+\n)?event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$", RegexOptions.Singleline))
            .Select(m =>
            {
                Assert.True(m.Success);
                return new SseFrame(m.Groups["event"].Value, JsonDocument.Parse(m.Groups["data"].Value).RootElement.Clone());
            })
            .ToList();
    }

    // ── Scripted ports ──────────────────────────────────────────────────────────────────────

    private sealed class ScriptedPlanner(Func<PlanInput, PlanResult> script) : IAskReelPlanner
    {
        public List<PlanInput> Inputs { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "scripted-plan";

        public Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct)
        {
            lock (Inputs)
            {
                Inputs.Add(input);
            }

            return Task.FromResult(script(input));
        }
    }

    /// <summary>Rules on the title's name: "V2 Yes" → yes, "V2 Partial" → partial, anything else → no.</summary>
    private sealed class NameJudge : IRequestJudgeModel
    {
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        /// <summary>The request text every batch was judged against.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

        public bool IsAvailable => true;

        public string ModelId => "name-judge";

        public string CacheSalt => $"name-judge-{Guid.NewGuid():N}";

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _batches);
            Requests.Enqueue(input.Request);
            var verdicts = input.Titles.Select(t =>
            {
                var satisfies = t.Name.StartsWith("V2 Yes", StringComparison.Ordinal) ? Satisfies.Yes
                    : t.Name.StartsWith("V2 Partial", StringComparison.Ordinal) ? Satisfies.Partial
                    : Satisfies.No;
                return new JudgeVerdict(
                    t.TitleId, satisfies, 0.8, "You get the thing you asked for.", $"keyword: {t.Keywords.FirstOrDefault()}",
                    ResultSource.Llm, ResultSource.Llm);
            }).ToList();
            return Task.FromResult(new JudgeBatchResult(verdicts, new LlmCallMeta("name-judge", "none", false, 1, 3), false));
        }
    }

    /// <summary>No embedding provider: the default for these tests, whose retriever never embeds.</summary>
    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider in this fixture.");
    }

    /// <summary>Records what it was asked to embed and answers with a constant unit vector.</summary>
    private sealed class RecordingEmbeddings : IEmbeddingProvider
    {
        public List<string> Asked { get; } = [];

        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            lock (Asked)
            {
                Asked.AddRange(texts);
            }

            return Task.FromResult(texts.Select(_ => Enumerable.Repeat(1f / MathF.Sqrt(1536), 1536).ToArray()).ToArray());
        }
    }

    /// <summary>A judge that is down: unavailable altogether, or available with every batch failing.</summary>
    private sealed class DownJudge(bool available) : IRequestJudgeModel
    {
        public bool IsAvailable => available;

        public string ModelId => "down-judge";

        public string CacheSalt => $"down-judge-{Guid.NewGuid():N}";

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct) =>
            Task.FromResult(new JudgeBatchResult([], new LlmCallMeta("down-judge", "none", false, 1, 3, "provider_error"), true));
    }

    /// <summary>The real retriever, with the query it was asked recorded and some arms reported as skipped.</summary>
    private sealed class SkippingRetriever(IAskReelRetriever inner, Action<RetrievalQuery> record, params string[] skipped) : IAskReelRetriever
    {
        public async Task<Application.Search.Retrieval.RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct)
        {
            record(q);
            return await inner.RetrieveAsync(q, ct) with { SkippedArms = skipped };
        }
    }

    private sealed class ScriptedAnswerer(string text) : IAskReelAnswerer

    {
        public string Text { get; } = text;

        public Guid? AskedAbout { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "scripted-answer";

        public Task<AnswerResult> AnswerAsync(AnswerInput input, CancellationToken ct)
        {
            AskedAbout = input.Facts.TitleId;
            return Task.FromResult(new AnswerResult(Text, new LlmCallMeta("scripted-answer", "none", false, 1, 3), null));
        }
    }

    private sealed class ConstantModelEngine(float score) : IModelEngine
    {
        public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson) =>
            throw new NotSupportedException("This fixture scores; it never trains.");

        public float[] Score(byte[] artifactBytes, float[][] features) => [.. features.Select(_ => score)];

        public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features) => Score(artifactBytes, features);

        public ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            [.. features.Select(_ => new ScoredRow(score, []))];

        public ScoredRow[] ScoreWithContributionsCached(
            Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            ScoreWithContributions(artifactBytes, features, featureNames, topK);
    }
}
