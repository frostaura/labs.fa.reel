using System.Globalization;
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
        // …and what that title IS, so the judge can rule a title like it "yes" (HREV3-RET-07).
        Assert.All(judge.Requests, r => Assert.Contains($"(2010) — genres action; keywords {topic}", r, StringComparison.Ordinal));
        Assert.All(judge.Requests, r => Assert.Single(Regex.Matches(r, "Like: ")));
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

    /// <summary>
    /// HREV3-ASK-A / HREV3-RET-06: a question about a title the message names in full is answered about
    /// that title with nothing on screen — whether the planner named it (with quotes and a year, as a
    /// model writes it), named it on the tool request, or named nothing at all. "Is Blade of the 47
    /// Ronin a ninja film or a samurai film?" was asked back "which title do you mean?". A title whose
    /// name is a run inside the full one is not the one meant: the longest name wins, whatever the votes.
    /// </summary>
    [Theory]
    [InlineData("message")]
    [InlineData("reference")]
    [InlineData("tool")]
    public async Task A_question_about_a_title_named_in_full_is_answered_about_it_with_nothing_on_screen(string namedBy)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var token = $"r{Guid.NewGuid():N}"[..9];
        var full = $"Blade of the {token} Ronin";
        var accountId = await SeedAccountAsync();
        Guid fullId;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle(full, 236);
            var inside = NewTitle($"Blade of the {token}", 90_000);
            db.Titles.AddRange(title, inside);
            await db.SaveChangesAsync();
            fullId = title.Id;
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion) with
        {
            Delta = ConversationStateDelta.None with { ReferenceNames = namedBy == "reference" ? [$"“{full}” (2010)"] : [] },
            Tools = [new ToolRequest(AskTool.TitleFacts, Name: namedBy == "tool" ? full : null)],
            Question = "is it a ninja film or a samurai film?",
        });
        var judge = new NameJudge();
        var answerer = new ScriptedAnswerer("It is a samurai film: its keywords include samurai.");
        var (engine, events, emit) = Build(accountId, planner, judge, answerer);

        var outcome = await engine.StreamAsync(
            Input($"wait, is {full} actually a ninja film or a samurai film?"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Equal(fullId, answerer.AskedAbout);
        Assert.Equal(fullId, Assert.Single(events, e => e.Event == "answer").Data.GetProperty("titleId").GetGuid());
        Assert.DoesNotContain(events, e => e.Event == "clarify");
        Assert.Equal(0, judge.Batches);
    }

    /// <summary>
    /// The control: a single word names a title only when the user capitalised it, so "is it violent?"
    /// never answers about It. Lower-cased, the question is asked back; capitalised, it is answered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_single_word_names_a_title_only_when_the_user_capitalised_it(bool capitalised)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var word = $"Zq{Guid.NewGuid():N}"[..10];
        var accountId = await SeedAccountAsync();
        Guid id;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle(word, 1_000);
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            id = title.Id;
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion) with { Tools = [new ToolRequest(AskTool.TitleFacts)] });
        var answerer = new ScriptedAnswerer("It is rated R.");
        var (engine, _, emit) = Build(accountId, planner, new NameJudge(), answerer);

        var outcome = await engine.StreamAsync(
            Input($"is {(capitalised ? word : word.ToLowerInvariant())} violent?"), accountId, emit, CancellationToken.None);

        Assert.Equal(capitalised ? SearchTurnMode.Answer : SearchTurnMode.Clarify, outcome.Mode);
        Assert.Equal(capitalised ? id : null, answerer.AskedAbout);
    }

    /// <summary>
    /// HREV4 (refuter, round 4): the same bar at ANY length. A lower-cased common phrase of several
    /// words is not a name the user gave: "is it based on a true story?" must not answer about Based on
    /// a True Story (2017), nor "does it have a happy ending?" about Happy Ending (2023), when nothing is
    /// on screen and the planner named nothing. The question is asked back, as it was before the
    /// fallback. The user's own signal (a capitalised or digit-led word that is not the sentence's
    /// first, or quotes) names the title. Varied: lower-cased, capitalised only by the sentence, title
    /// case, quoted.
    /// </summary>
    [Theory]
    [InlineData("is it based on a {0} story?", "Based on a {1} Story", false)]
    [InlineData("does it have a {0} ending?", "{1} Ending", false)]
    [InlineData("Based on a {0} story, is it?", "Based on a {1} Story", false)]
    [InlineData("is it based on a {1} Story?", "Based on a {1} Story", true)]
    [InlineData("does it have a \"{0} ending\"?", "{1} Ending", true)]
    public async Task A_lower_cased_phrase_of_several_words_names_a_title_only_with_the_users_own_signal(
        string message, string stored, bool named)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var token = $"zq{Guid.NewGuid():N}"[..10];
        var capital = "Z" + token[1..];
        var accountId = await SeedAccountAsync();
        Guid id;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle(string.Format(CultureInfo.InvariantCulture, stored, token, capital), 1_000);
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            id = title.Id;
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.ItemQuestion) with { Tools = [new ToolRequest(AskTool.TitleFacts)] });
        var answerer = new ScriptedAnswerer("It is based on a novel.");
        var (engine, events, emit) = Build(accountId, planner, new NameJudge(), answerer);

        var outcome = await engine.StreamAsync(
            Input(string.Format(CultureInfo.InvariantCulture, message, token, capital)), accountId, emit, CancellationToken.None);

        Assert.Equal(named ? SearchTurnMode.Answer : SearchTurnMode.Clarify, outcome.Mode);
        Assert.Equal(named ? id : null, answerer.AskedAbout);
        Assert.Equal(!named, events.Any(e => e.Event == "clarify"));
    }

    /// <summary>
    /// HREV3-ASK-B / HREV3-RET-03 (WS-3 CR-3): the top-up and the checked count read the request's
    /// RESOLVED constraints, as the supply does. "Heist" resolves to heist and robbery; a title tagged
    /// only "robbery" that cleared the bar was never topped up into the judge (Criminal), and one the
    /// judge did rule on was called "not among the titles I checked" (Burn). Here retrieval reads the
    /// user's own word, so the synonym-only title is reached only through the top-up. Varied: the judge
    /// says it IS a match (a pick), and says it is not (a none-found that counts it as checked).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_title_tagged_only_with_a_synonym_that_clears_the_bar_is_topped_up_and_counted_as_checked(bool matches)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var synonym = Topic();
        var accountId = await SeedAccountAsync();
        var tagged = await SeedTopicalAsync(topic, ("V2 No tagged", 3));
        var synonymOnly = await SeedTopicalAsync(synonym, (matches ? "V2 Yes synonym" : "V2 No synonym", 1));
        await SeedPredictionsAsync(accountId, [.. tagged, .. synonymOnly], 8.4m);
        await SeedBarAsync(accountId, 6.0m);
        var judge = new CountingJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, events, emit) = Build(
            accountId, planner, judge, model: new RuntimeModelEngine(), resolver: new SynonymResolver(topic, synonym));

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(1, judge.SendsOf(synonymOnly[0]));
        Assert.All(tagged, id => Assert.Equal(1, judge.SendsOf(id)));
        if (matches)
        {
            Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
            var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
                .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
            Assert.Equal(synonymOnly, picks);
        }
        else
        {
            Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
            Assert.Equal(
                $"You have 4 unseen {topic} movies tagged that way, but none of the ones I checked is really a match.",
                outcome.AssistantText);
        }
    }

    /// <summary>
    /// HREV3-ASK-E, through the engine: a first-turn plan failure reads the mood as a mood and the year
    /// phrase as years, so "tense X movies from the 2010s" searches X in 2010–2019 and answers with the
    /// titles tagged X — not none-found over the hard topics "tense X" and "2010s".
    /// </summary>
    [Fact]
    public async Task A_first_turn_plan_failure_reads_the_mood_and_the_years_and_searches_the_topic()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.TimedOut, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"tense {topic} movies from the 2010s"), accountId, emit, CancellationToken.None);

        Assert.Equal([topic], Assert.Single(events, e => e.Event == "plan").Data.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()));
        var state = ConversationStateJson.Read(outcome.ConversationStateJson);
        Assert.NotNull(state);
        Assert.Equal(["tense"], state.Soft.Moods);
        Assert.Equal(new YearRange(2010, 2019), state.Hard.Years);
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.StartsWith("I couldn't fully read that, so I searched for ", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("tense", outcome.AssistantText, StringComparison.Ordinal);
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
    /// HREV2-ASK-01: a request with two concepts is counted over BOTH when that is where it runs dry,
    /// and the reply names both — "Of the 145 romantic titles in the catalogue" once described a
    /// romance-and-comedy count. Here five titles carry the first concept and three of them the second
    /// too; the two not carrying the second are unseen but judged no, so the first concept alone is not
    /// empty and the intersection is what the answer reports. Varied over the order the plan lists the
    /// concepts in, which changed the number the founder was told for the same question.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_none_found_reply_over_two_concepts_names_both_and_counts_their_intersection(bool reversed)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var first = Topic();
        var second = Topic();
        var accountId = await SeedAccountAsync();
        var both = await SeedTopicalAsync(first, ("V2 Yes", 3));
        await SeedTopicalAsync(first, ("V2 No", 2));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => both.Contains(t.Id)).ToListAsync())
            {
                title.Keywords = [first, second];
            }

            await db.SaveChangesAsync();
        }

        await MarkSeenAsync(accountId, both);
        string[] concepts = reversed ? [second, first] : [first, second];
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = concepts }));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"{first} {second} movies"), accountId, emit, CancellationToken.None);

        var shelf = string.Join(' ', concepts);
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(3, Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply").GetProperty("alreadySeen").GetInt32());
        Assert.StartsWith(
            $"There are no {shelf} movies left for you. Of the 3 {shelf} movies in the catalogue with at least ",
            outcome.AssistantText, StringComparison.Ordinal);
        Assert.Contains("you've seen 3", outcome.AssistantText, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Event == "phase" && e.Data.ToString().Contains($"Searching the catalogue for {shelf}", StringComparison.Ordinal));

        // The wider rung was searched (the first concept alone), and the judge still read BOTH words.
        Assert.True(judge.Batches > 0);
        Assert.All(judge.Requests, r => Assert.Contains($"Requires: {string.Join(", ", concepts)}", r, StringComparison.Ordinal));
    }

    /// <summary>
    /// Residual 1/2 of the final golden eval: a request is reported where it runs dry. When the first
    /// concept the user said is ITSELF empty — every title carrying it seen — the answer counts that
    /// shelf (five seen), not the narrower intersection (three), so the numbers describe the level at
    /// which the request has nothing left, exactly as the SQL supply truth counts it. Varied over the
    /// plan's order: the user's words decide which concept anchors the request, never the plan's list.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_none_found_reply_over_two_concepts_reports_the_first_when_it_is_empty_on_its_own(bool reversed)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var first = Topic();
        var second = Topic();
        var accountId = await SeedAccountAsync();
        var both = await SeedTopicalAsync(first, ("V2 Yes", 3));
        var firstOnly = await SeedTopicalAsync(first, ("V2 Yes", 2));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => both.Contains(t.Id)).ToListAsync())
            {
                title.Keywords = [first, second];
            }

            await db.SaveChangesAsync();
        }

        await MarkSeenAsync(accountId, both.Concat(firstOnly));
        string[] concepts = reversed ? [second, first] : [first, second];
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = concepts }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{first} {second} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(5, Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply").GetProperty("alreadySeen").GetInt32());
        Assert.StartsWith(
            $"There are no {first} movies left for you. Of the 5 {first} movies in the catalogue with at least ",
            outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV2-ASK-01, a person with a concept: the supply is the person's credits that carry the concept,
    /// and the reply named it "the N {concept} movies in the catalogue" — every film with the concept.
    /// </summary>
    [Fact]
    public async Task A_none_found_reply_over_a_person_and_a_concept_names_both()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var director = $"Director {Guid.NewGuid():N}"[..20];
        var credited = await SeedCreditedAsync(director, 4);
        await SeedTopicalAsync(topic, ("V2 Yes", 3));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => credited.Take(2).Contains(t.Id)).ToListAsync())
            {
                title.Keywords = [topic];
            }

            await db.SaveChangesAsync();
        }

        await MarkSeenAsync(accountId, credited.Take(2));
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            Concepts = [topic],
            People = [new PersonConstraint(director, null, PersonRole.Director)],
        }));
        var (engine, _, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{director} {topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.StartsWith(
            $"There are no {director} {topic} movies left for you. Of the 2 {director} {topic} movies in the catalogue with at least ",
            outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV1-ASK-01: the Liam Neeson follow-up. A person's every unseen credit was already on screen,
    /// so retrieval had nothing left and nothing was judged — and the reply said "You have 4 unseen
    /// Liam Neeson titles tagged that way, but none of the ones I checked is really a match". They are
    /// named as shown, and no check is claimed.
    /// </summary>
    [Fact]
    public async Task A_follow_up_whose_whole_supply_was_shown_says_so_and_claims_no_check()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var director = $"Director {Guid.NewGuid():N}"[..20];
        var credited = await SeedCreditedAsync(director, 3);
        await SeedScoresAsync(accountId, credited, 8.4m);
        var conversationId = await SeedConversationAsync(
            accountId,
            ConversationState.Empty with
            {
                Version = 1,
                Hard = HardConstraints.None with { People = [new PersonConstraint(director, null, PersonRole.Director)] },
            });
        var presented = credited.Select((id, i) => new PresentedCard(1, i + 1, id, $"Credited {i}", 2010, MediaType.Movie)).ToList();
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, new ScriptedPlanner(_ => Plan(TurnAct.Refine)), judge);

        var outcome = await engine.StreamAsync(
            Input("any more of those?") with { ConversationId = conversationId, AnswerNumber = 2, Presented = presented, ShownTitleIds = credited },
            accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(0, judge.Batches);
        Assert.Equal($"All 3 unseen {director} titles inside your conditions were already shown in this conversation.", outcome.AssistantText);
        Assert.DoesNotContain("checked", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV1-ASK-01: a judge that ruled on nothing — every batch failed, or no model at all — checked
    /// nothing, so the tagged supply is "couldn't check", never "none of the ones I checked".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_judge_that_ruled_on_nothing_claims_no_check(bool judgeAvailable)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedScoresAsync(accountId, await SeedTopicalAsync(topic, ("V2 Yes", 3)), 8.4m);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, _, emit) = Build(accountId, planner, new DownJudge(judgeAvailable));

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(
            $"You have 3 unseen {topic} movies tagged that way, but I couldn't check them against what you asked this time. "
            + "3 titles I couldn't check against your request are below, marked as unchecked.",
            outcome.AssistantText);
    }

    /// <summary>
    /// HREV1-RET-01: the judge rules on at most sixteen titles, so with twenty tagged titles clearing
    /// the bar the reply says it checked sixteen of them — and "none of the ones I checked" only when
    /// it really did check every one. Varied over the size of the tagged supply.
    /// </summary>
    [Theory]
    [InlineData(20, "You have 20 unseen {0} movies tagged that way. I checked 16 of them and none is really a match; the other 4 weren't among the 16 titles I checked this time.")]
    [InlineData(5, "You have 5 unseen {0} movies tagged that way, but none of the ones I checked is really a match.")]
    public async Task A_none_found_reply_says_how_many_of_the_tagged_titles_were_checked(int count, string expected)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedScoresAsync(accountId, await SeedTopicalAsync(topic, ("V2 No", count)), 8.4m);
        var judge = new NameJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, _, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, topic), outcome.AssistantText);
    }

    /// <summary>
    /// HREV2-RET-01: the judge rules on sixteen titles, and v2 took the first sixteen in retrieval
    /// order — so "scary horror films" judged sixteen titles under the bar, answered none-found, and
    /// never judged the five that cleared it. Here the titles under the bar are retrieved first and
    /// the ones that clear it rank seventeenth and later; every one that clears it is still judged
    /// and picked. Varied over how many sit under the bar and how many clear it, and over where the
    /// predictions come from: the stored rows, or — with none stored — scored on demand.
    /// </summary>
    [Theory]
    [InlineData(18, 4, true)]
    [InlineData(16, 1, true)]
    [InlineData(22, 3, true)]
    [InlineData(18, 4, false)]
    public async Task Titles_that_clear_the_bar_are_judged_first_when_more_are_retrieved_than_the_judge_rules_on(int under, int clearing, bool stored)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        // One seeding, so retrieval (most voted first) returns every held title before any that clears.
        var seeded = await SeedTopicalAsync(topic, ("V2 Yes held", under), ("V2 Yes clears", clearing));
        var held = seeded.Take(under).ToList();
        var clears = seeded.Skip(under).ToList();
        await SeedPredictionsAsync(accountId, held, 2.0m, stored);
        await SeedPredictionsAsync(accountId, clears, 8.4m, stored);
        await SeedBarAsync(accountId, 6.0m);
        var judge = new CountingJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var model = new RuntimeModelEngine();
        var (engine, events, emit) = Build(accountId, planner, judge, model: model);

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Equal(clears.Order(), picks.Order());
        Assert.All(clears, id => Assert.Equal(1, judge.SendsOf(id)));
        Assert.Equal(RequestJudge.MaxJudged, held.Concat(clears).Count(id => judge.SendsOf(id) > 0));
        Assert.Equal(stored ? 0 : 1, model.PoolScorings);
    }

    /// <summary>
    /// HREV2-RET-01's control: with no bar set every title can become a pick, so nothing is scored
    /// early and the judge takes retrieval's first sixteen exactly as before.
    /// </summary>
    [Fact]
    public async Task With_no_bar_the_judge_takes_retrievals_first_sixteen()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var seeded = await SeedTopicalAsync(topic, ("V2 Yes first", 16), ("V2 Yes later", 3));
        var first = seeded.Take(16).ToList();
        var later = seeded.Skip(16).ToList();
        await SeedPredictionsAsync(accountId, first, 2.0m);
        await SeedPredictionsAsync(accountId, later, 8.4m);
        var judge = new CountingJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, _, emit) = Build(accountId, planner, judge, model: new RuntimeModelEngine());

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.All(first, id => Assert.Equal(1, judge.SendsOf(id)));
        Assert.All(later, id => Assert.Equal(0, judge.SendsOf(id)));
        Assert.DoesNotContain(AskStageTimings.JudgeOrder, outcome.StageTimingsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV2-RET-01 with the plan's speculation: the early first batch is chosen in the same order the
    /// turn judges in, so it is spent on titles that clear the bar — and none is sent twice. Both
    /// order on the stored predictions, so neither scores the pool on demand.
    /// </summary>
    [Fact]
    public async Task The_early_first_batch_takes_the_titles_that_clear_the_bar()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var seeded = await SeedTopicalAsync(topic, ("V2 Yes held", 18), ("V2 Yes clears", 2));
        var held = seeded.Take(18).ToList();
        var clears = seeded.Skip(18).ToList();
        await SeedPredictionsAsync(accountId, held, 2.0m);
        await SeedPredictionsAsync(accountId, clears, 8.4m);
        await SeedBarAsync(accountId, 6.0m);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var judge = new CountingJudge();
        var services = new ServiceCollection();
        services.AddScoped<AccountContext>();
        services.AddScoped<IAccountContext>(sp => sp.GetRequiredService<AccountContext>());
        services.AddScoped(sp => Fixture.CreateContext(sp.GetRequiredService<AccountContext>()));
        services.AddScoped<Application.Persistence.IReelDbContext>(sp => sp.GetRequiredService<Infrastructure.Persistence.ReelDbContext>());
        services.AddScoped<IAskReelRetriever>(sp => new OwnedRetriever(Fixture.CreateContext(sp.GetRequiredService<AccountContext>()), _ => { }));
        services.AddScoped(sp => new TitleFacts(sp.GetRequiredService<Application.Persistence.IReelDbContext>()));
        services.AddScoped<IRequestJudge>(_ => new RequestJudge(judge, cache, NullLogger<RequestJudge>.Instance));
        var model = new RuntimeModelEngine();
        services.AddScoped(sp => Scorer(sp.GetRequiredService<Infrastructure.Persistence.ReelDbContext>(), model, cache));
        await using var provider = services.BuildServiceProvider();
        var planner = new AwaitingPlanner(async _ =>
        {
            await judge.FirstSend.WaitAsync(TimeSpan.FromSeconds(10));
            return Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic], Media = MediaScope.Movies });
        });
        var (engine, events, emit) = Build(
            accountId, planner, judge, scopes: provider.GetRequiredService<IServiceScopeFactory>(), cache: cache, model: model);

        var outcome = await engine.StreamAsync(Input($"{topic} films"), accountId, emit, CancellationToken.None);

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
        Assert.Equal(clears.Order(), picks.Order());
        Assert.All(held.Concat(clears), id => Assert.True(judge.SendsOf(id) <= 1));
        Assert.Contains(AskStageTimings.JudgeSpeculative, outcome.StageTimingsJson, StringComparison.Ordinal);

        // Ordered on the stored predictions: no candidate pool was scored on demand, by either path.
        Assert.Equal(0, model.PoolScorings);
    }

    /// <summary>
    /// HREV2-RET-01, the "Show me more" page: ordering the retrieved pool is not enough when the pool
    /// itself holds few titles that clear the bar. Live, page two of "scary horror films" judged ten
    /// titles under the bar and answered none-found while the supply held 43 more that cleared it,
    /// none of them retrieved. Here the titles that clear the bar sit OUTSIDE the retrieved pool; they
    /// are still judged and picked. Varied: past retrieval's depth on a first page; on a second page
    /// whose first page's titles are excluded; and a short retrieval of fewer titles than the judge
    /// rules on. In every case a seen title and one under the supply's vote floor, both "clearing" on
    /// a stored score, are never judged — the top-up is the supply's own clearing set, nothing wider.
    /// </summary>
    [Theory]
    [InlineData(44, 0, 0)]
    [InlineData(44, 20, 0)]
    [InlineData(10, 0, 8)]
    public async Task Titles_that_clear_the_bar_outside_the_retrieved_pool_are_still_judged_and_picked(int under, int shownBefore, int retrievedOnly)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        // One seeding, most voted first: retrieval returns the shown and held titles before any other.
        var seeded = await SeedTopicalAsync(
            topic, ("V2 Yes shown", shownBefore), ("V2 Yes held", under), ("V2 Yes clears", 3), ("V2 Yes seen", 1), ("V2 Yes thin", 1));
        var shown = seeded.Take(shownBefore).ToList();
        var held = seeded.Skip(shownBefore).Take(under).ToList();
        var clears = seeded.Skip(shownBefore + under).Take(3).ToList();
        var seen = seeded[^2];
        var thin = seeded[^1];
        await SeedPredictionsAsync(accountId, shown, 8.4m);
        await SeedPredictionsAsync(accountId, held, 2.0m);
        await SeedPredictionsAsync(accountId, [.. clears, seen, thin], 8.4m);
        await MarkSeenAsync(accountId, [seen]);
        await SetVotesAsync([thin], CrowdVotes.MinRelaxVotes - 1);
        await SeedBarAsync(accountId, 6.0m);
        var judge = new CountingJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var model = new RuntimeModelEngine();
        var (engine, events, emit) = Build(
            accountId, planner, judge, model: model,
            wrap: retrievedOnly > 0 ? inner => new ShortRetriever(inner, retrievedOnly) : null);

        var outcome = await engine.StreamAsync(
            Input($"{topic} movies") with { ShownTitleIds = shown }, accountId, emit, CancellationToken.None);

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Equal(clears.Order(), picks.Order());
        Assert.All(clears, id => Assert.Equal(1, judge.SendsOf(id)));
        Assert.All(shown.Append(seen).Append(thin), id => Assert.Equal(0, judge.SendsOf(id)));
        // The top-up comes from the stored predictions the supply splits by: no pool scored on demand.
        Assert.Equal(0, model.PoolScorings);
    }

    /// <summary>
    /// HREV2-ASK-04 with HREV2-RET-01: scoring the retrieved pool on demand put 0.4–3 s in front of the
    /// first card, and the active model stores predictions for few titles, so the pool usually has
    /// none. When the supply's stored predictions alone fill the judge's slice with titles that clear
    /// the bar, nothing is scored before the judge starts; only when they cannot is the pool scored.
    /// Varied over how many stored titles clear the bar outside the pool: enough to fill the judge,
    /// and too few.
    /// </summary>
    [Theory]
    [InlineData(RequestJudge.MaxJudged, 0)]
    [InlineData(3, 1)]
    public async Task The_pool_is_scored_on_demand_only_when_the_stored_predictions_cannot_fill_the_judge(int clearing, int poolScorings)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var seeded = await SeedTopicalAsync(topic, ("V2 Yes unscored", 44), ("V2 Yes clears", clearing));
        var unscored = seeded.Take(44).ToList();
        var clears = seeded.Skip(44).ToList();
        await SeedPredictionsAsync(accountId, unscored, 2.0m, stored: false);
        await SeedPredictionsAsync(accountId, clears, 8.4m);
        await SeedBarAsync(accountId, 6.0m);
        var judge = new CountingJudge();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var model = new RuntimeModelEngine();
        var (engine, events, emit) = Build(accountId, planner, judge, model: model);

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
        Assert.Equal(clears.Order(), picks.Order());
        Assert.All(clears, id => Assert.Equal(1, judge.SendsOf(id)));
        Assert.Equal(poolScorings, model.PoolScorings);
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        // Timed apart from the slice scored beside the judge, so the gate run can read it alone.
        using var timings = JsonDocument.Parse(outcome.StageTimingsJson!);
        Assert.True(timings.RootElement.TryGetProperty(AskStageTimings.JudgeOrder, out _));
    }

    /// <summary>
    /// HREV1-ASK-02 / RET-04: the planner answered media "any" for "samurai films", and the turn
    /// returned shows and counted them. The user's own media word wins over "any"; a message naming
    /// both keeps both. Varied: a none-found turn, whose supply counts the films alone.
    /// </summary>
    [Theory]
    [InlineData("{0} films", 2, "Movies")]
    [InlineData("{0} films or shows", 4, "Any")]
    public async Task The_users_media_word_outranks_a_plan_that_said_any(string message, int picks, string media)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var movies = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var shows = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        await MakeShowsAsync(shows);
        await SeedScoresAsync(accountId, [.. movies, .. shows], 8.4m);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic], Media = MediaScope.Any }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(
            Input(string.Format(System.Globalization.CultureInfo.InvariantCulture, message, topic)), accountId, emit, CancellationToken.None);

        Assert.Equal(media, Assert.Single(events, e => e.Event == "plan").Data.GetProperty("media").GetString());
        var cards = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray()).ToList();
        Assert.Equal(picks, cards.Count);
        if (media == "Movies")
        {
            Assert.All(cards, c => Assert.Equal("Movie", c.GetProperty("mediaType").GetString()));
            Assert.Equal([0], JsonDocument.Parse(outcome.IntentJson!).RootElement.GetProperty("MediaTypes").EnumerateArray().Select(m => m.GetInt32()));
        }
    }

    [Fact]
    public async Task A_film_request_counts_only_films_in_its_none_found_supply()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var movies = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var shows = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        await MakeShowsAsync(shows);
        await MarkSeenAsync(accountId, movies);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic], Media = MediaScope.Any }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{topic} films"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        var supply = Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply");
        Assert.Equal(2, supply.GetProperty("matchingInCatalogue").GetInt32());
        Assert.Equal(2, supply.GetProperty("alreadySeen").GetInt32());
        Assert.StartsWith($"There are no {topic} movies left for you.", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV1-ASK-03 (R1-04 in v2): an ordinal that points past every answer on screen is asked back
    /// before any model call — no plan, no search, no judge. Varied: one pick on screen and "the third
    /// one"; nothing ever shown and "the second one you showed me first time round".
    /// </summary>
    [Theory]
    [InlineData("more like the third one", 1, "My answers here have shown at most 1 title at a time, so there’s no third one to point at. Which title did you mean?")]
    [InlineData("more like the second one you showed me first time round", 0, "There’s nothing on screen yet for “the second one” to point at — I haven’t shown you any titles in this conversation. Tell me what you’re in the mood for and I’ll look.")]
    public async Task A_pointer_at_nothing_on_screen_is_asked_back_before_any_model_call(string message, int onScreen, string expected)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var presented = ids.Take(onScreen).Select((id, i) => new PresentedCard(1, i + 1, id, $"V2 Yes {i}", 2010, MediaType.Movie)).ToList();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.Refine, ConversationStateDelta.None with { Concepts = [topic] }));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input(message) with { Presented = presented, ShownTitleIds = ids.Take(onScreen).ToList(), AnswerNumber = onScreen == 0 ? 1 : 2 },
            accountId, emit, CancellationToken.None);

        Assert.Empty(planner.Inputs);
        Assert.Equal(0, judge.Batches);
        Assert.Equal(SearchTurnMode.Clarify, outcome.Mode);
        Assert.Equal(expected, Assert.Single(events, e => e.Event == "clarify").Data.GetProperty("question").GetString());
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray" or "none-found" or "plan");
    }

    /// <summary>
    /// HREV1-ASK-03: a pointer some answer could satisfy, but which neither the planner nor the
    /// server's rank rule settled ("the second one from before", no handle named), is asked back —
    /// never searched with the judge left to read "the second one" itself.
    /// </summary>
    [Fact]
    public async Task A_pointer_nothing_resolved_is_asked_back_and_never_judged()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 6));
        var presented = ids.Take(3).Select((id, i) => new PresentedCard(1, i + 1, id, $"V2 Yes {i}", 2010, MediaType.Movie))
            .Append(new PresentedCard(2, 1, ids[3], "V2 Yes 3", 2010, MediaType.Movie))
            .ToList();
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.Refine));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input("more like the second one from before") with { Presented = presented, ShownTitleIds = ids.Take(4).ToList(), AnswerNumber = 3 },
            accountId, emit, CancellationToken.None);

        Assert.Single(planner.Inputs);
        Assert.Equal(0, judge.Batches);
        Assert.Equal(SearchTurnMode.Clarify, outcome.Mode);
        Assert.Equal(AskReelPointer.Unresolved(2), outcome.AssistantText);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray" or "none-found");
    }

    /// <summary>
    /// HREV1-RET-02: "more like the second one" after a person request searches near that card — the
    /// person is no longer a hard filter, so retrieval is not confined to their filmography and the
    /// judge rules on what it found. Varied: a message that restates the person keeps them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task More_like_a_card_after_a_person_request_searches_near_the_card(bool restated)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var director = $"Director {Guid.NewGuid():N}"[..20];
        var credited = await SeedCreditedAsync(director, 4);
        var conversationId = await SeedConversationAsync(
            accountId,
            ConversationState.Empty with
            {
                Version = 1,
                Hard = HardConstraints.None with { People = [new PersonConstraint(director, null, PersonRole.Director)] },
            });
        var presented = credited.Take(3).Select((id, i) => new PresentedCard(1, i + 1, id, $"Credited {i}", 2010, MediaType.Movie)).ToList();
        RetrievalQuery? seen = null;
        var judge = new NameJudge();
        var (engine, _, emit) = Build(
            accountId, new ScriptedPlanner(_ => Plan(TurnAct.Refine)), judge, wrap: inner => new SkippingRetriever(inner, q => seen = q));
        var message = restated ? $"more like the second one with {director.Split(' ')[1]}" : "more like the second one";

        await engine.StreamAsync(
            Input(message) with { ConversationId = conversationId, AnswerNumber = 2, Presented = presented, ShownTitleIds = credited.Take(3).ToList() },
            accountId, emit, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Contains(credited[1], seen.Soft.ReferenceTitleIds);
        Assert.Equal(restated ? 1 : 0, seen.Hard.People.Count);
        // Unrestated, the search left the filmography, so the judge ruled on what it found; restated,
        // the person's credits are the evidence again and nothing is judged.
        Assert.Equal(restated, judge.Batches == 0);
    }

    /// <summary>
    /// HREV1-ASK-06: a plan that fails on a conversation's first turn searches the user's phrase whole
    /// and never says "as before" — the dead-provider "time travel movies" searched "travel".
    /// </summary>
    [Fact]
    public async Task A_first_turn_plan_failure_searches_the_users_phrase_and_never_says_as_before()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var phrase = $"{Topic()} {Topic()}";
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(phrase, ("V2 Yes", 3));
        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.TimedOut, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{phrase} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal([phrase], Assert.Single(events, e => e.Event == "plan").Data.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("Movies", Assert.Single(events, e => e.Event == "plan").Data.GetProperty("media").GetString());
        Assert.Equal($"I couldn't fully read that, so I searched for {phrase}. 3 {phrase} movies that fit what you asked.", outcome.AssistantText);
    }

    /// <summary>
    /// HREV7-ASK-01: a plan that fails on the turn after an opening that searched nothing — small talk
    /// or a question about a title read with no model, a clarifying question a model asked — searches
    /// the user's topic as a requirement and says so. Read by the answer number, "heist movies" was the
    /// soft mood "heist" (non-heist titles could be ruled Yes), the reply said "I searched the way I
    /// did last time" about no earlier search, and a none-found had no supply because nothing was
    /// required. Each opening runs through the engine and its stored state is what the second turn
    /// loads. Varied over the second turn's outcome (picks, and a none-found whose supply is counted)
    /// and a control whose opening did search, which is still carried forward as before.
    /// </summary>
    [Theory]
    [InlineData("chitchat", false)]
    [InlineData("item", false)]
    [InlineData("clarify", false)]
    [InlineData("chitchat", true)]
    [InlineData("item", true)]
    [InlineData("clarify", true)]
    [InlineData("search", false)]
    public async Task A_plan_failure_after_an_opening_that_searched_nothing_requires_the_topic_and_never_says_last_time(string opening, bool allSeen)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var earlier = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        if (allSeen)
        {
            await MarkSeenAsync(accountId, ids);
        }

        var named = $"Better Call {Topic()}";
        if (opening == "item")
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            db.Titles.Add(NewTitle(named, 9_001));
            await db.SaveChangesAsync();
        }

        var (first, opener) = opening switch
        {
            "chitchat" => ("what can you do?", (IAskReelPlanner)new KeylessPlanner()),
            "item" => ($"what is {named} about?", new KeylessPlanner()),
            "clarify" => ("something like fargo", new ScriptedPlanner(_ => Plan(TurnAct.NewRequest) with
            {
                ClarifyQuestion = "Do you mean the film or the series?",
                ClarifyOptions = ["The film", "The series"],
            })),
            _ => ($"{earlier} movies", new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [earlier] }))),
        };
        var (openEngine, _, openEmit) = Build(accountId, opener, new NameJudge(), new NoAnswerer());
        var opened = await openEngine.StreamAsync(Input(first), accountId, openEmit, CancellationToken.None);
        var state = ConversationStateJson.Read(opened.ConversationStateJson)!;
        Assert.Equal(1, state.Version);
        Assert.Equal(opening == "search", state.HoldsSearch);
        var conversationId = await SeedConversationAsync(accountId, state);

        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.TimedOut, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(
            Input($"{topic} movies") with { ConversationId = conversationId, AnswerNumber = 2 }, accountId, emit, CancellationToken.None);

        var concepts = Assert.Single(events, e => e.Event == "plan").Data.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal("CarriedForward", outcome.PlanSource);
        Assert.DoesNotContain("last time", outcome.AssistantText, StringComparison.Ordinal);
        if (opening == "search")
        {
            Assert.Equal([earlier], concepts);
            Assert.StartsWith($"I couldn't fully read that — searching for {earlier} as before.", outcome.AssistantText, StringComparison.Ordinal);
            return;
        }

        Assert.Equal([topic], concepts);
        Assert.DoesNotContain("before", outcome.AssistantText, StringComparison.Ordinal);
        if (allSeen)
        {
            Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
            var supply = Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply");
            Assert.Equal(3, supply.GetProperty("alreadySeen").GetInt32());
            Assert.StartsWith($"I couldn't fully read that, so I searched for {topic}.", outcome.AssistantText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal($"I couldn't fully read that, so I searched for {topic}. 3 {topic} movies that fit what you asked.", outcome.AssistantText);
        }
    }

    /// <summary>
    /// HREV4-ASK-01: the user's own negations reach the counts the founder reads. "ninja movies but no
    /// turtles" ended NoneFound with "Of the 70 ninja movies … you've seen 18 and your conditions hold
    /// out 52 … turning off 'excluded genre: animation' would unlock 3" — and that knockout's sample
    /// held Turtles Forever: the recount was the plain ninja shelf, while retrieval and the judge both
    /// applied the negation. A title the user ruled out is not one they asked for: it is in no bucket,
    /// no knockout counts it and no sample names it. Varied over a keyword-and-name negation and a
    /// genre negation, each with negated titles in every bucket (seen, held out, unlocked).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_none_found_counts_and_knockouts_leave_out_what_the_user_ruled_out(bool genreNegation)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var negation = genreNegation ? "comedy" : $"turtq{Guid.NewGuid():N}"[..12];
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "animation");

        // Asked for and kept: three seen, one held out by the animation rule (the knockout's whole unlock).
        var seen = await SeedTopicalAsync(topic, ("V2 Yes Seen", 3));
        await MarkSeenAsync(accountId, seen);
        var heldOut = await SeedTopicalAsync(topic, ("V2 Yes Held", 1));
        await SetGenresAsync(heldOut, ["action", "animation"]);

        // Ruled out by the user, in every bucket: seen ones, and ones the animation rule also holds out.
        var negatedSeen = await SeedTopicalAsync(topic, ("V2 Yes NegSeen", 2));
        await MarkSeenAsync(accountId, negatedSeen);
        var negatedHeld = await SeedTopicalAsync(topic, ("V2 Yes NegHeld", 2));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => negatedSeen.Contains(t.Id) || negatedHeld.Contains(t.Id)).ToListAsync())
            {
                var held = negatedHeld.Contains(title.Id);
                if (genreNegation)
                {
                    title.Genres = held ? ["comedy", "animation"] : ["action", "comedy"];
                }
                else if (held)
                {
                    // Named, not tagged: "Turtles Forever" carried no turtle keyword.
                    title.Name = $"{negation} Forever {title.Name[^4..]}";
                    title.Genres = ["action", "animation"];
                }
                else
                {
                    title.Keywords = [topic, negation];
                }
            }

            await db.SaveChangesAsync();
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            Concepts = [topic],
            Negations = [negation],
            Media = MediaScope.Movies,
        }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{topic} movies but no {negation}"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        var supply = Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply");
        Assert.Equal(4, supply.GetProperty("matchingInCatalogue").GetInt32());
        Assert.Equal(3, supply.GetProperty("alreadySeen").GetInt32());
        Assert.Equal(1, supply.GetProperty("excludedByConditions").GetInt32());
        var knockout = Assert.Single(supply.GetProperty("knockouts").EnumerateArray());
        Assert.Equal(1, knockout.GetProperty("wouldUnlock").GetInt32());
        Assert.Equal([heldOut[0]], knockout.GetProperty("sample").EnumerateArray().Select(t => t.GetProperty("titleId").GetGuid()));
        Assert.Contains(
            $"Of the 4 {topic} movies in the catalogue with at least 50 votes, you've seen 3 and your conditions hold out 1.",
            outcome.AssistantText, StringComparison.Ordinal);
        Assert.Contains("would unlock 1.", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV4-ASK-05: with no model at all, "is the first one violent?" was planned as a new request
    /// with no topic — the conversation's Movies scope reset to any medium and 16 unchecked titles,
    /// mostly shows, came back instead of an answer. A question about a card is answered from its
    /// facts, nothing is searched or judged, and the conversation's media and topic stay as they were.
    /// Varied over the ordinal and the question's shape.
    /// </summary>
    [Theory]
    [InlineData("is the first one violent?", 0)]
    [InlineData("how long is the second one?", 1)]
    [InlineData("Is the last one any good?", 1)]
    public async Task A_model_less_question_about_a_card_is_answered_from_its_facts_and_keeps_the_conversation(string message, int index)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var shows = await SeedTopicalAsync(topic, ("V2 Show", 3));
        await MakeShowsAsync(shows);
        var presented = ids.Select((id, i) => new PresentedCard(1, i + 1, id, $"V2 Yes {i}", 2010, MediaType.Movie)).ToList();
        var conversationId = await SeedConversationAsync(
            accountId,
            ConversationState.Empty with
            {
                Version = 1,
                Hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [], [])], Media = MediaScope.Movies },
            });
        var (engine, events, emit) = Build(accountId, new KeylessPlanner(), new DownJudge(available: false), new NoAnswerer());

        var outcome = await engine.StreamAsync(
            Input(message) with { ConversationId = conversationId, AnswerNumber = 2, Presented = presented, ShownTitleIds = ids },
            accountId, emit, CancellationToken.None);

        var answer = Assert.Single(events, e => e.Event == "answer").Data;
        Assert.Equal($"T1.{index + 1}", answer.GetProperty("handle").GetString());
        Assert.Equal(ids[index], answer.GetProperty("titleId").GetGuid());
        Assert.EndsWith("That's what I know about it — I can't say more than that.", answer.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("ItemQuestion", outcome.Act);
        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Empty(outcome.Cards);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray" or "none-found");
        var state = ConversationStateJson.Read(outcome.ConversationStateJson);
        Assert.NotNull(state);
        Assert.Equal(MediaScope.Movies, state.Hard.Media);
        Assert.Equal([topic], state.Hard.Concepts.Select(c => c.UserWords));
    }

    /// <summary>
    /// HREV4-RET-02 / HREV4-ASK-05: with no model, "movies like Inception" searched the concept
    /// "inception" and replied "There are no inception movies left for you. Of the 2 inception movies…".
    /// A title the catalogue holds by that name is a reference — searched near, never returned, never a
    /// topic. A name it does not hold is the user's words searched as a topic, and the reply owns up to
    /// the guess: it leads with what it searched, never says "there are no X left", and offers no
    /// relaxation of the user's conditions for a shelf they may not have asked for.
    /// </summary>
    [Theory]
    [InlineData(true, "movies like {0}")]
    [InlineData(true, "like {0} but funnier")]
    [InlineData(false, "movies like {0}")]
    [InlineData(false, "something similar to {0}")]
    public async Task A_model_less_like_a_title_is_a_reference_when_the_catalogue_holds_it_and_an_owned_guess_when_not(
        bool held, string shape)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var name = $"refq{Guid.NewGuid():N}"[..14];
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "drama");
        Guid? reference = null;
        if (held)
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            var title = NewTitle(name, 9_000);
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            reference = title.Id;
        }

        // Titles TAGGED with the words: seen ones, and one only the standing drama exclusion holds out —
        // the shelf a guess would count and the relaxation it must not offer.
        var tagged = await SeedTopicalAsync(name, ("V2 Tagged", 2));
        await MarkSeenAsync(accountId, tagged);
        var heldOut = await SeedTopicalAsync(name, ("V2 Drama", 1));
        await SetGenresAsync(heldOut, ["action", "drama"]);
        var queries = new List<RetrievalQuery>();
        var (engine, events, emit) = Build(
            accountId, new KeylessPlanner(), new DownJudge(available: false), wrap: r => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } }));

        var outcome = await engine.StreamAsync(
            Input(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, name)), accountId, emit, CancellationToken.None);

        var query = Assert.Single(queries);
        Assert.DoesNotContain("There are no", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("funnier", query.Hard.Concepts.Select(c => c.UserWords));
        if (held)
        {
            Assert.Empty(query.Hard.Concepts);
            Assert.Contains(reference!.Value, query.Soft.ReferenceTitleIds);
            Assert.DoesNotContain(outcome.Cards, c => c.TitleId == reference);
            Assert.DoesNotContain("I couldn't fully read that", outcome.AssistantText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal([name], query.Hard.Concepts.Select(c => c.UserWords));
            Assert.Empty(query.Soft.ReferenceTitleIds);
            Assert.StartsWith(
                $"I couldn't fully read that, so I searched for {name}. {NoneFoundCopy.GuessedLead}", outcome.AssistantText, StringComparison.Ordinal);
            Assert.DoesNotContain("turning off", outcome.AssistantText, StringComparison.Ordinal);
            Assert.Contains("you've seen 2", outcome.AssistantText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HREV5-RET-02: with no model, "movies like the matrix" replied "There are no matrix movies left
    /// for you" — "the" was a pointer word, so no title starting with "The" was ever read as a name. A
    /// held title is a reference; a name the catalogue lacks is the user's words without the article,
    /// searched as an owned guess.
    /// </summary>
    [Theory]
    [InlineData(true, "movies like the {0}")]
    [InlineData(true, "shows like The {0}, no cops")]
    [InlineData(false, "movies like the {0}")]
    public async Task A_model_less_like_the_title_is_a_reference_too(bool held, string shape)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var name = $"refq{Guid.NewGuid():N}"[..14];
        var accountId = await SeedAccountAsync();
        Guid? reference = null;
        if (held)
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            var title = NewTitle($"The {name}", 9_000);
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            reference = title.Id;
        }

        var tagged = await SeedTopicalAsync(name, ("V2 Tagged", 2));
        await MarkSeenAsync(accountId, tagged);
        var queries = new List<RetrievalQuery>();
        var (engine, _, emit) = Build(
            accountId, new KeylessPlanner(), new DownJudge(available: false), wrap: r => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } }));

        var outcome = await engine.StreamAsync(
            Input(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, name)), accountId, emit, CancellationToken.None);

        var query = Assert.Single(queries);
        Assert.DoesNotContain("There are no", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("tagged that way", outcome.AssistantText, StringComparison.Ordinal);
        if (held)
        {
            Assert.Empty(query.Hard.Concepts);
            Assert.Contains(reference!.Value, query.Soft.ReferenceTitleIds);
            Assert.DoesNotContain(outcome.Cards, c => c.TitleId == reference);
        }
        else
        {
            Assert.Equal([name], query.Hard.Concepts.Select(c => c.UserWords));
            Assert.StartsWith(
                $"I couldn't fully read that, so I searched for {name}. {NoneFoundCopy.GuessedLead}", outcome.AssistantText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HREV5-RET-02 residual: a model-less "like &lt;title&gt;" follow-up was a new request with no
    /// media, so after "heist movies", "like John Wick but funnier" searched every medium and showed TV
    /// shows. With no media word, the request searches the medium of the title it names.
    /// </summary>
    [Theory]
    [InlineData(MediaScope.Movies, false, MediaScope.Movies)]
    [InlineData(MediaScope.Shows, false, MediaScope.Movies)]
    [InlineData(MediaScope.Movies, true, MediaScope.Shows)]
    [InlineData(MediaScope.Any, false, MediaScope.Movies)]
    public async Task A_model_less_like_a_title_searches_the_medium_of_the_title_it_names(MediaScope conversation, bool referenceIsShow, MediaScope expected)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var name = $"refq{Guid.NewGuid():N}"[..14];
        var accountId = await SeedAccountAsync();
        Guid reference;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle(name, 9_000);
            title.MediaType = referenceIsShow ? MediaType.Show : MediaType.Movie;
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            reference = title.Id;
        }

        await SeedTopicalAsync(topic, ("V2 Yes", 2));
        var state = ConversationState.Empty with
        {
            Version = 1,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [], [])], Media = conversation },
        };
        var queries = new List<RetrievalQuery>();
        var (engine, _, emit) = Build(
            accountId, new KeylessPlanner(), new DownJudge(available: false), wrap: r => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } }));

        var outcome = await engine.StreamAsync(
            Input($"like {name} but funnier") with { ConversationId = await SeedConversationAsync(accountId, state), AnswerNumber = 2 },
            accountId, emit, CancellationToken.None);

        var query = Assert.Single(queries);
        Assert.Contains(reference, query.Soft.ReferenceTitleIds);
        Assert.Equal(expected, query.Hard.Media);
        Assert.Equal(expected, ConversationStateJson.Read(outcome.ConversationStateJson)!.Hard.Media);
    }

    /// <summary>
    /// HREV5-ASK-02 (regression of the HREV4-ASK-02 fix): on an account that excludes romance and
    /// comedy, "a good love story" planned as romance and "something funny tonight" planned as comedy
    /// lost the genre — no conflict frame (or a frame with no supply behind it), an empty request and
    /// "Nothing I found matches what you asked closely enough to show". With no model, "love stories" was
    /// read as romance and then dropped the same way. The genre the words ask for stays the request, and
    /// the collision with the standing exclusion is named.
    /// </summary>
    [Theory]
    [InlineData("a good love story", "romance", false)]
    [InlineData("something funny tonight", "comedy", false)]
    [InlineData("love stories", "romance", true)]
    [InlineData("a good love story", "romance", true)]
    // HREV5-ASK-02 (round 6): a synonym the planner maps to the genre, and the same words with no model.
    [InlineData("sitcoms", "comedy", false)]
    [InlineData("chick flicks", "romance", false)]
    [InlineData("sitcoms", "comedy", true)]
    [InlineData("chick flicks", "romance", true)]
    public async Task A_genre_asked_for_by_another_name_keeps_its_conflict_frame_and_its_supply(string message, string genre, bool keyless)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "romance");
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "comedy");
        var held = await SeedTopicalAsync(Topic(), ("V2 Genre", 3));
        await SetGenresAsync(held, [genre]);
        IAskReelPlanner planner = keyless
            ? new KeylessPlanner()
            : new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Genres = [genre] }));
        var queries = new List<RetrievalQuery>();
        var (engine, events, emit) = Build(
            accountId, planner, new DownJudge(available: false), wrap: r => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } }));

        var outcome = await engine.StreamAsync(Input(message), accountId, emit, CancellationToken.None);

        var conflict = Assert.Single(events, e => e.Event == "conflict").Data;
        Assert.Equal([genre], conflict.GetProperty("excludedGenres").EnumerateArray().Select(g => g.GetString()));
        // Read as the predicate reads the stored state (HardConstraintFilter.Read): "sitcoms" is a word of
        // the concept vocabulary's own genre table, so it may stand as a topic that READS as comedy.
        var query = Assert.Single(queries);
        Assert.Contains(HardConstraintFilter.Read(query.Hard.Concepts), c => c.Genres.Contains(genre, StringComparer.Ordinal));
        Assert.Contains(
            HardConstraintFilter.Read(ConversationStateJson.Read(outcome.ConversationStateJson)!.Hard.Concepts),
            c => c.Genres.Contains(genre, StringComparer.Ordinal));
        Assert.DoesNotContain("Nothing I found matches", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV4-RET-02: with no model, "what about movies instead" in a zombie-series conversation searched
    /// the concept "instead" ("There are no instead movies left for you") and streamed 16 unrelated
    /// movies; "no walking dead" ruled out "walking" and "dead" as separate words. A media switch carries
    /// the topic forward into the medium named; a negation is the phrase the user wrote.
    /// </summary>
    [Fact]
    public async Task A_model_less_media_switch_carries_the_topic_and_a_negation_is_the_whole_phrase()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var phrase = $"walkq{Guid.NewGuid():N}"[..12] + " " + $"deadq{Guid.NewGuid():N}"[..12];
        var dead = phrase.Split(' ')[1];
        var accountId = await SeedAccountAsync();
        var movies = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var shows = await SeedTopicalAsync(topic, ("V2 Show", 3));
        await MakeShowsAsync(shows);
        var named = await SeedTopicalAsync(topic, ("V2 Named", 1));
        var overview = await SeedTopicalAsync(topic, ("V2 Overview", 1));
        await MakeShowsAsync([.. named, .. overview]);
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            (await db.Titles.SingleAsync(t => t.Id == named[0])).Name = $"The {phrase}";
            (await db.Titles.SingleAsync(t => t.Id == overview[0])).Overview = $"Everyone here is {dead} tired.";
            await db.SaveChangesAsync();
        }

        var state = ConversationState.Empty with
        {
            Version = 1,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [], [])], Media = MediaScope.Shows },
        };
        var queries = new List<RetrievalQuery>();
        IAskReelRetriever Recording(IAskReelRetriever r) => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } });

        var (switchEngine, _, switchEmit) = Build(accountId, new KeylessPlanner(), new DownJudge(available: false), wrap: Recording);
        var switched = await switchEngine.StreamAsync(
            Input("what about movies instead") with { ConversationId = await SeedConversationAsync(accountId, state), AnswerNumber = 2 },
            accountId, switchEmit, CancellationToken.None);

        var switchQuery = Assert.Single(queries);
        Assert.Equal([topic], switchQuery.Hard.Concepts.Select(c => c.UserWords));
        Assert.Equal(MediaScope.Movies, switchQuery.Hard.Media);
        Assert.All(switched.Cards, c => Assert.Contains(c.TitleId, movies));
        Assert.DoesNotContain("instead", switched.AssistantText, StringComparison.Ordinal);

        queries.Clear();
        var (negationEngine, _, negationEmit) = Build(accountId, new KeylessPlanner(), new DownJudge(available: false), wrap: Recording);
        var negated = await negationEngine.StreamAsync(
            Input($"no {phrase}") with { ConversationId = await SeedConversationAsync(accountId, state), AnswerNumber = 2 },
            accountId, negationEmit, CancellationToken.None);

        var negationQuery = Assert.Single(queries);
        Assert.Equal([topic], negationQuery.Hard.Concepts.Select(c => c.UserWords));
        // The phrase rides into retrieval as one exclusion (every arm reads the name, the overview and the
        // tags by it); the bare word, which would rule out every title whose overview says "dead", never does.
        Assert.Contains(phrase, negationQuery.ExcludeKeywords);
        Assert.DoesNotContain(dead, negationQuery.ExcludeKeywords);
        Assert.Equal([phrase], ConversationStateJson.Read(negated.ConversationStateJson)!.UserNegations);
    }

    /// <summary>
    /// HREV1-ASK-05: a plain topic request is retrieved WHILE the plan runs — the planner here waits
    /// until the speculative retrieval has started, so a turn that did not overlap them would time
    /// out — and the speculation is used only when the plan asks for exactly its query. Varied: a
    /// plan that reads the request differently (shows, not films) is retrieved afresh.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_plain_topic_is_retrieved_while_the_plan_runs_and_used_only_when_the_plan_agrees(bool agrees)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var movies = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var shows = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        await MakeShowsAsync(shows);
        var speculative = new List<RetrievalQuery>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped<AccountContext>();
        services.AddScoped<IAccountContext>(sp => sp.GetRequiredService<AccountContext>());
        services.AddScoped<IAskReelRetriever>(sp => new OwnedRetriever(Fixture.CreateContext(sp.GetRequiredService<AccountContext>()), q =>
        {
            lock (speculative)
            {
                speculative.Add(q);
            }

            started.TrySetResult();
        }));
        await using var provider = services.BuildServiceProvider();
        var planner = new AwaitingPlanner(async _ =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Plan(TurnAct.NewRequest, ConversationStateDelta.None with
            {
                Concepts = [topic],
                Media = agrees ? MediaScope.Movies : MediaScope.Shows,
            });
        });
        var main = new List<RetrievalQuery>();
        var (engine, events, emit) = Build(
            accountId, planner, new NameJudge(), wrap: inner => new SkippingRetriever(inner, main.Add),
            scopes: provider.GetRequiredService<IServiceScopeFactory>());

        var outcome = await engine.StreamAsync(Input($"{topic} films"), accountId, emit, CancellationToken.None);

        var spec = Assert.Single(speculative);
        Assert.Equal(MediaScope.Movies, spec.Hard.Media);
        Assert.Equal([topic], spec.Hard.Concepts.Select(c => c.UserWords));
        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray()).ToList();
        if (agrees)
        {
            Assert.Empty(main);
            Assert.Equal(movies.Order(), picks.Select(c => c.GetProperty("titleId").GetGuid()).Order());
            Assert.Contains(AskStageTimings.RetrieveSpeculative, outcome.StageTimingsJson, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(MediaScope.Shows, Assert.Single(main).Hard.Media);
            Assert.Equal(shows.Order(), picks.Select(c => c.GetProperty("titleId").GetGuid()).Order());
            Assert.DoesNotContain(AskStageTimings.RetrieveSpeculative, outcome.StageTimingsJson, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A client may close the stream the moment `done` arrives — the answer is complete on its
    /// screen. The work left after `done` (the exposure log) ran on the request's token, so that
    /// close threw, and the endpoint stored a finished turn as Failed/client_aborted with no intent,
    /// act or conversation state (found measuring HREV1-ASK-05 with a client that stops at `done`).
    /// Varied: with an active model artifact the exposures are still written; without one there is
    /// nothing to write.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_that_leaves_at_done_still_gets_a_finished_turn(bool trained)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var yes = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        if (trained)
        {
            await SeedScoresAsync(accountId, yes, 8.4m);
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());
        using var client = new CancellationTokenSource();
        LiveSearchExpansionService.Emit leaving = async (name, payload) =>
        {
            await emit(name, payload);
            if (name == "done")
            {
                await client.CancelAsync();
            }
        };

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, leaving, client.Token);

        Assert.True(client.IsCancellationRequested);
        Assert.False(outcome.Failed);
        Assert.NotNull(outcome.IntentJson);
        Assert.NotNull(outcome.ConversationStateJson);
        await using var db = Fixture.CreateContext(new AccountContext());
        var exposed = await db.ExposureEvents.IgnoreQueryFilters()
            .Where(e => e.AccountId == accountId && e.Surface == Domain.Feed.ExposureSurface.AskReel)
            .Select(e => e.TitleId)
            .ToListAsync();
        Assert.Equal(trained ? yes.Order() : [], exposed.Order());
    }

    /// <summary>
    /// HREV1-ASK-05: the first judge batch of a plain topic is judged WHILE the plan runs — the
    /// planner here waits until the judge has been sent titles, so a turn that judged only after its
    /// plan would time out — and the turn never sends those titles again. Varied: a plan that reads
    /// the request differently (shows, not films) judges its own titles afresh, and none of the
    /// early ones reach its picks.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_judge_batch_of_a_plain_topic_is_judged_while_the_plan_runs_and_never_twice(bool agrees)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var movies = await SeedTopicalAsync(topic, ("V2 Yes", 3), ("V2 No", 3));
        var shows = await SeedTopicalAsync(topic, ("V2 Yes", 2));
        await MakeShowsAsync(shows);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var judge = new CountingJudge();
        var services = new ServiceCollection();
        services.AddScoped<AccountContext>();
        services.AddScoped<IAccountContext>(sp => sp.GetRequiredService<AccountContext>());
        services.AddScoped(sp => Fixture.CreateContext(sp.GetRequiredService<AccountContext>()));
        services.AddScoped<Application.Persistence.IReelDbContext>(sp => sp.GetRequiredService<Infrastructure.Persistence.ReelDbContext>());
        services.AddScoped<IAskReelRetriever>(sp => new OwnedRetriever(Fixture.CreateContext(sp.GetRequiredService<AccountContext>()), _ => { }));
        services.AddScoped(sp => new TitleFacts(sp.GetRequiredService<Application.Persistence.IReelDbContext>()));
        services.AddScoped<IRequestJudge>(_ => new RequestJudge(judge, cache, NullLogger<RequestJudge>.Instance));
        await using var provider = services.BuildServiceProvider();
        var planner = new AwaitingPlanner(async _ =>
        {
            await judge.FirstSend.WaitAsync(TimeSpan.FromSeconds(10));
            return Plan(TurnAct.NewRequest, ConversationStateDelta.None with
            {
                Concepts = [topic],
                Media = agrees ? MediaScope.Movies : MediaScope.Shows,
            });
        });
        var (engine, events, emit) = Build(accountId, planner, judge, scopes: provider.GetRequiredService<IServiceScopeFactory>(), cache: cache);

        var outcome = await engine.StreamAsync(Input($"{topic} films"), accountId, emit, CancellationToken.None);

        var picks = events.Where(e => e.Event == "picks").SelectMany(e => e.Data.GetProperty("cards").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid()).ToList();
        if (agrees)
        {
            Assert.Equal(movies.Take(3).Order(), picks.Order());
            Assert.All(movies, id => Assert.Equal(1, judge.SendsOf(id)));
            Assert.Contains(AskStageTimings.JudgeSpeculative, outcome.StageTimingsJson, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(shows.Order(), picks.Order());
            Assert.All(shows, id => Assert.Equal(1, judge.SendsOf(id)));
            Assert.DoesNotContain(AskStageTimings.JudgeSpeculative, outcome.StageTimingsJson, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HREV2-ASK-04: a plan call stuck on a slow route ("romantic comedies" planned in 7.6 s against a
    /// p50 under 2 s) is sent once more, and the turn goes on with the first good plan; the hedge is
    /// recorded in the stage timings. Varied: a plan that answers in time is sent once, unrecorded.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_plan_stuck_on_its_first_call_is_sent_once_more_and_the_turn_goes_on(bool firstHangs)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var planner = new StuckFirstPlanner(firstHangs, Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, _, emit) = Build(accountId, planner, new NameJudge(), planHedgeAfter: TimeSpan.FromMilliseconds(100));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await engine.StreamAsync(Input($"{topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the turn waited {clock.Elapsed} on a stuck plan");
        Assert.Equal(firstHangs ? 2 : 1, planner.Calls);
        Assert.Equal(firstHangs, outcome.StageTimingsJson?.Contains(AskStageTimings.PlanHedge, StringComparison.Ordinal) ?? false);
        if (firstHangs)
        {
            await planner.FirstCancelled.WaitAsync(TimeSpan.FromSeconds(10));
        }
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

    /// <summary>
    /// HREV5-RET-02: with no model — or a planner that failed twice — "movies with Liam Neeson" read the
    /// name as a keyword concept, the supply was all zeros, and the reply said "I didn't find anything to
    /// show for that this time" over a catalogue holding about 80 of his films. The name is a person:
    /// the turn is a person request over their credits, and the reply names them. Varied over the
    /// keyless and the failed-plan paths, over the name with a topic word left beside it, and over a name
    /// nobody in the catalogue has, which says the reading failed rather than that nothing exists.
    /// <para>HREV8-ASK-02 / HREV8-RET-03 / HREV8-PLAT-03: the reply names the person in the role the
    /// message read, so the expected credit is a parameter rather than one string for every shape —
    /// "movies starring X" is the cast role and reads "starring X" (the composer's pinned copy, see
    /// <c>AskReelV2UnitTests</c> and <c>LiveSearchExpansionTests</c>), while "with X" and a bare name
    /// carry no role and read "with X". The round-7 rows asserted "with X" for the starring shape and
    /// had never run against a database.</para>
    /// </summary>
    [Theory]
    [InlineData(true, "movies with {0}", true, "with {0}")]
    [InlineData(false, "movies with {0}", true, "with {0}")]
    [InlineData(true, "{0} films", true, "with {0}")]
    [InlineData(false, "{0} films", true, "with {0}")]
    [InlineData(true, "movies with {0}", false, null)]
    [InlineData(false, "movies with {0}", false, null)]
    // Round 7: a role word before the name is the role, never a topic left beside the person.
    [InlineData(true, "movies starring {0}", true, "starring {0}")]
    [InlineData(false, "movies starring {0}", true, "starring {0}")]
    public async Task A_model_less_person_request_searches_the_persons_credits(bool keyless, string shape, bool known, string? credit)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var name = $"Liamq{Guid.NewGuid():N}"[..13] + " " + $"Neesonq{Guid.NewGuid():N}"[..14];
        var credited = new List<Guid>();
        if (known)
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue), Name = name };
            db.Persons.Add(person);
            for (var i = 0; i < 3; i++)
            {
                var title = NewTitle($"Acted {i} {Guid.NewGuid():N}"[..16], 4_000 - i);
                db.Titles.Add(title);
                db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = person.Id, Role = CreditRole.Actor, CastOrder = 0 });
                credited.Add(title.Id);
            }

            await db.SaveChangesAsync();
        }

        IAskReelPlanner planner = keyless
            ? new KeylessPlanner()
            : new ScriptedPlanner(_ => PlanResult.Failed(
                new PlanFailure(AgentFailureKind.MalformedAnswer, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input(string.Format(CultureInfo.InvariantCulture, shape, name)), accountId, emit, CancellationToken.None);

        var plan = Assert.Single(events, e => e.Event == "plan").Data;
        Assert.DoesNotContain(NoneFoundCopy.NothingFoundUnchecked, outcome.AssistantText, StringComparison.Ordinal);
        if (!known)
        {
            Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
            Assert.Contains(keyless ? NoneFoundCopy.UnreadNothing : NoneFoundCopy.UnreadNothingLed, outcome.AssistantText, StringComparison.Ordinal);
            return;
        }

        Assert.Equal([name], plan.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Empty(plan.GetProperty("concepts").EnumerateArray());
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Equal(credited.Order(), outcome.Cards.Select(c => c.TitleId).Order());
        var expectedCredit = string.Format(CultureInfo.InvariantCulture, credit!, name);
        Assert.Contains(expectedCredit, outcome.AssistantText, StringComparison.Ordinal);
        // And never the other role's wording: a role the message did not state is not invented, and
        // one it did state is not dropped.
        var otherCredit = credit!.StartsWith("starring", StringComparison.Ordinal) ? $"with {name}" : $"starring {name}";
        Assert.DoesNotContain(otherCredit, outcome.AssistantText, StringComparison.Ordinal);
        Assert.Equal(0, judge.Batches);
    }

    /// <summary>
    /// HREV5-RET-02 (round 7): names with a stop word, a word of two letters or fewer, or punctuation in
    /// them never reached the grounder whole — "Will Smith" was the topic "smith", "Al Pacino" "pacino",
    /// "Samuel L. Jackson" "samuel" and "jackson", and "Robert Downey Jr." "robert downey", which the
    /// exact pattern could not match against the stored "Robert Downey Jr.". Each is now the person.
    /// Varied over the keyless and failed-plan paths, over an initial, a suffix typed and left off, a
    /// stop-word first name, a two-letter first name and a capitalised particle.
    /// </summary>
    [Theory]
    [InlineData(true, "{0} L. {1}", "movies with {0} L. {1}")]
    [InlineData(false, "{0} L. {1}", "{0} L. {1} films")]
    [InlineData(true, "{0} {1} Jr.", "anything with {0} {1} Jr. please")]
    [InlineData(false, "{0} {1} Jr.", "movies with {0} {1}")]
    [InlineData(true, "Will {1}", "movies starring Will {1}")]
    [InlineData(false, "Al {1}", "something with Al {1}")]
    [InlineData(true, "{0} De {1}", "movies with {0} De {1}")]
    public async Task A_model_less_person_name_with_short_or_punctuated_words_grounds_whole(bool keyless, string stored, string typed)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var first = $"Samq{Guid.NewGuid():N}"[..12];
        var last = $"Jacksonq{Guid.NewGuid():N}"[..14];
        var name = string.Format(CultureInfo.InvariantCulture, stored, first, last);
        var accountId = await SeedAccountAsync();
        var credited = new List<Guid>();
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue), Name = name };
            db.Persons.Add(person);
            for (var i = 0; i < 3; i++)
            {
                var title = NewTitle($"Acted {i} {Guid.NewGuid():N}"[..16], 4_000 - i);
                db.Titles.Add(title);
                db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = person.Id, Role = CreditRole.Actor, CastOrder = 0 });
                credited.Add(title.Id);
            }

            await db.SaveChangesAsync();
        }

        IAskReelPlanner planner = keyless
            ? new KeylessPlanner()
            : new ScriptedPlanner(_ => PlanResult.Failed(
                new PlanFailure(AgentFailureKind.MalformedAnswer, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(
            Input(string.Format(CultureInfo.InvariantCulture, typed, first, last)), accountId, emit, CancellationToken.None);

        var plan = Assert.Single(events, e => e.Event == "plan").Data;
        Assert.Equal([name], plan.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Empty(plan.GetProperty("concepts").EnumerateArray());
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Equal(credited.Order(), outcome.Cards.Select(c => c.TitleId).Order());
    }

    /// <summary>
    /// The control for HREV5-RET-02: words someone with a credit or two happens to bear as a name are
    /// still the topic the user asked for — the people a request names have a filmography.
    /// </summary>
    [Fact]
    public async Task A_model_less_topic_that_an_obscure_person_bears_as_a_name_stays_a_topic()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var phrase = $"{Topic()} {Topic()}";
        var accountId = await SeedAccountAsync();
        var tagged = await SeedTopicalAsync(phrase, ("V2 Yes", 3));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue), Name = phrase };
            db.Persons.Add(person);
            var title = NewTitle($"Bit part {Guid.NewGuid():N}"[..18], 10);
            db.Titles.Add(title);
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = person.Id, Role = CreditRole.Actor, CastOrder = 9 });
            await db.SaveChangesAsync();
        }

        var (engine, events, emit) = Build(accountId, new KeylessPlanner(), new DownJudge(available: false));

        var outcome = await engine.StreamAsync(Input($"{phrase} movies"), accountId, emit, CancellationToken.None);

        var plan = Assert.Single(events, e => e.Event == "plan").Data;
        Assert.Empty(plan.GetProperty("people").EnumerateArray());
        Assert.Equal([phrase], plan.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(tagged.Order(), outcome.Cards.Select(c => c.TitleId).Order());
    }

    /// <summary>
    /// HREV5-ASK-01 through the engine: the planner forced to fail, "X movies but nothing with Y" planned
    /// the concepts [X, Y], the judge was sent "Requires: X, Y", and the reply said "I searched for X Y"
    /// above Y titles. What the user ruled out reaches neither the hard concepts nor the judge's
    /// requirements, nor the reply's lead; it is a negation, and the titles carrying it are not shown.
    /// Varied over the cue ("nothing with", "without", "except ones with").
    /// </summary>
    [Theory]
    [InlineData("{0} movies but nothing with {1}")]
    [InlineData("{0} movies without {1}")]
    [InlineData("{0} movies except ones with {1}")]
    public async Task A_failed_plan_never_searches_what_the_message_rules_out(string shape)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var ruledOut = $"supq{Guid.NewGuid():N}"[..12];
        var accountId = await SeedAccountAsync();
        var kept = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var both = await SeedTopicalAsync(topic, ("V2 Yes Both", 2));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => both.Contains(t.Id)).ToListAsync())
            {
                title.Keywords = [topic, ruledOut];
            }

            await db.SaveChangesAsync();
        }

        var planner = new ScriptedPlanner(_ => PlanResult.Failed(
            new PlanFailure(AgentFailureKind.MalformedAnswer, null, Refusal: false), new LlmCallMeta("m", "none", false, 2, 10)));
        var judge = new NameJudge();
        var (engine, events, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(
            Input(string.Format(CultureInfo.InvariantCulture, shape, topic, ruledOut)), accountId, emit, CancellationToken.None);

        Assert.Equal([topic], Assert.Single(events, e => e.Event == "plan").Data.GetProperty("concepts").EnumerateArray().Select(c => c.GetString()));
        var state = ConversationStateJson.Read(outcome.ConversationStateJson);
        Assert.NotNull(state);
        Assert.DoesNotContain(state.Hard.Concepts, c => c.UserWords.Contains(ruledOut, StringComparison.Ordinal));
        Assert.Contains(ruledOut, state.UserNegations);
        Assert.NotEmpty(judge.Requests);
        Assert.All(judge.Requests, r =>
        {
            Assert.Contains($"Requires: {topic}.", r, StringComparison.Ordinal);
            Assert.Contains($"Rules out: {ruledOut}", r, StringComparison.Ordinal);
            Assert.DoesNotContain($"Requires: {topic}, ", r, StringComparison.Ordinal);
        });
        Assert.StartsWith($"I couldn't fully read that, so I searched for {topic}.", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.Cards, c => both.Contains(c.TitleId));
        Assert.Equal(kept.Order(), outcome.Cards.Select(c => c.TitleId).Order());
    }

    /// <summary>
    /// HREV5-ASK-01: with no model, "what can you do?" searched sixteen random titles and showed them
    /// unchecked, and "what is Better Call Saul about?" counted "9 unseen call saul titles". Small talk
    /// answers what Ask Reel does; a question about a named title is answered from its facts; neither
    /// searches.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_model_less_question_about_ask_reel_or_a_named_title_is_answered_not_searched(bool aboutTitle)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var token = $"saulq{Guid.NewGuid():N}"[..12];
        var name = $"Better Call {token}";
        var accountId = await SeedAccountAsync();
        Guid titleId;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle(name, 9_000);
            db.Titles.Add(title);
            await SeedTopicalAsync(token, ("V2 Yes", 3));
            await db.SaveChangesAsync();
            titleId = title.Id;
        }

        var queries = new List<RetrievalQuery>();
        var (engine, events, emit) = Build(
            accountId, new KeylessPlanner(), new DownJudge(available: false), new NoAnswerer(),
            wrap: r => new SkippingRetriever(r, q => { lock (queries) { queries.Add(q); } }));

        var outcome = await engine.StreamAsync(
            Input(aboutTitle ? $"what is {name} about?" : "what can you do?"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.Answer, outcome.Mode);
        Assert.Empty(outcome.Cards);
        Assert.DoesNotContain(events, e => e.Event is "picks" or "tray" or "clarify");
        var answer = Assert.Single(events, e => e.Event == "answer").Data;
        if (aboutTitle)
        {
            Assert.Equal(titleId, answer.GetProperty("titleId").GetGuid());
            Assert.StartsWith(name, outcome.AssistantText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(TurnComposer.ChitchatReply, outcome.AssistantText);
        }
    }

    // ── Final golden eval: answer quality ────────────────────────────────────────────────────

    /// <summary>
    /// Residual 1: a word the planner made a requirement it never was ("big action", "isolation and
    /// silence") emptied the screen while the topic itself had titles clearing the bar. With nothing
    /// offerable as asked, the request is searched one rung wider — and the judge still reads both
    /// words. Varied over a catalogue where the request AS ASKED has a title: then nothing is relaxed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_with_nothing_offerable_as_asked_is_searched_one_rung_wider_and_judged_as_asked(bool conjunctionExists)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var descriptor = Topic();
        var accountId = await SeedAccountAsync();
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 4));
        if (conjunctionExists)
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            (await db.Titles.SingleAsync(t => t.Id == ids[0])).Keywords = [topic, descriptor];
            await db.SaveChangesAsync();
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic, descriptor] }));
        var judge = new NameJudge();
        var (engine, _, emit) = Build(accountId, planner, judge);

        var outcome = await engine.StreamAsync(Input($"{topic} movies about {descriptor}"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Equal(conjunctionExists ? [ids[0]] : ids.Order().ToList(), outcome.Cards.Select(c => c.TitleId).Order().ToList());
        Assert.All(judge.Requests, r => Assert.Contains($"Requires: {topic}, {descriptor}", r, StringComparison.Ordinal));
        Assert.Equal(!conjunctionExists, (outcome.DegradedFlags ?? string.Empty).Contains($"relaxed:{descriptor}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Residual 1 (a person's genre): "Jason Statham action movies" with every Statham film seen is
    /// empty at Statham, and the answer counts his films — not only those also tagged action — as the
    /// SQL truth does. The relaxed person rung is judged, never listed unjudged.
    /// </summary>
    [Fact]
    public async Task A_person_request_that_runs_dry_at_the_person_reports_the_person()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        var director = $"Director {Guid.NewGuid():N}"[..20];
        var credited = await SeedCreditedAsync(director, 4);
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => credited.Take(2).Contains(t.Id)).ToListAsync())
            {
                title.Keywords = [topic];
            }

            await db.SaveChangesAsync();
        }

        await MarkSeenAsync(accountId, credited);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            Concepts = [topic],
            People = [new PersonConstraint(director, null, PersonRole.Director)],
        }));
        var (engine, _, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input($"{director} {topic} movies"), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.StartsWith(
            $"There are no {director} movies left for you. Of the 4 {director} movies in the catalogue with at least ",
            outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Residuals 2 and 3: "korean thrillers" under English only named no conflict and counted keyword
    /// tags. The language is a requirement on the title's language, so the collision is named and the
    /// answer is the honest none-found with the language relaxation. Varied over an account that allows
    /// every language: the Korean titles are the picks, and the English one carrying the same topic is not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_language_the_account_does_not_allow_is_a_named_collision_and_one_it_allows_is_searched(bool allLanguages)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.Language, allLanguages ? FilterRuleSet.AllLanguages : "en");
        var korean = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        var english = await SeedTopicalAsync(topic, ("V2 Yes", 1));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in await db.Titles.Where(t => korean.Contains(t.Id)).ToListAsync())
            {
                title.Language = "ko";
            }

            await db.SaveChangesAsync();
        }

        await using var resolverDb = Fixture.CreateContext(new AccountContext());
        var resolver = RealResolver(resolverDb, topic);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = ["korean", topic] }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge(), resolver: resolver, wrap: r => new ResolvingRetriever(r, resolver));

        var outcome = await engine.StreamAsync(Input($"korean {topic} movies"), accountId, emit, CancellationToken.None);

        if (allLanguages)
        {
            Assert.DoesNotContain(events, e => e.Event == "conflict");
            Assert.Equal(korean.Order(), outcome.Cards.Select(c => c.TitleId).Order());
            Assert.DoesNotContain(english[0], outcome.Cards.Select(c => c.TitleId));
            return;
        }

        var conflict = Assert.Single(events, e => e.Event == "conflict").Data;
        Assert.Equal(["Korean"], conflict.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        var knockouts = Assert.Single(events, e => e.Event == "none-found").Data.GetProperty("supply").GetProperty("knockouts").EnumerateArray().ToList();
        Assert.Contains(knockouts, k => k.GetProperty("kind").GetString() == "Language" && k.GetProperty("wouldUnlock").GetInt32() >= 3);
        Assert.StartsWith("There are no korean ", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Residual 3: "Scooby doo" names no condition, but every unseen Scooby-Doo title is animated or a
    /// comedy, both excluded — the conditions are why the screen is empty, and that is a collision the
    /// user is told about. Varied over titles that are merely seen: an empty answer with no condition
    /// behind it names none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conditions_that_hold_out_every_unseen_match_are_a_named_collision(bool heldOut)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "animation");
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        if (heldOut)
        {
            await SetGenresAsync(ids, ["animation"]);
        }
        else
        {
            await MarkSeenAsync(accountId, ids);
        }

        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(Input(topic), accountId, emit, CancellationToken.None);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        if (!heldOut)
        {
            Assert.DoesNotContain(events, e => e.Event == "conflict");
            Assert.Null(outcome.ConflictJson);
            return;
        }

        var conflict = Assert.Single(events, e => e.Event == "conflict").Data;
        Assert.Equal(["animation"], conflict.GetProperty("excludedGenres").EnumerateArray().Select(g => g.GetString()));
        Assert.StartsWith("Your conditions hold out every unseen title that matches", conflict.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.NotNull(outcome.ConflictJson);
    }

    /// <summary>
    /// Residual 7: "animated movies for kids" was planned as kids' films (the planner is told never to
    /// copy a standing condition), and the judge passed a live-action children's film as a pick under a
    /// notice saying animation is excluded. The genre the user named is required, so the excluded shelf
    /// is honestly empty. Varied over a request that names no excluded genre: the same titles are picks.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_excluded_genre_the_user_named_is_required_so_the_answer_is_honestly_empty(bool namesAnimation)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = Topic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, Domain.Filters.FilterKind.ExcludeGenre, "animation");
        var ids = await SeedTopicalAsync(topic, ("V2 Yes", 3));
        await SetGenresAsync(ids, ["family"]);
        var planner = new ScriptedPlanner(_ => Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }));
        var (engine, events, emit) = Build(accountId, planner, new NameJudge());

        var outcome = await engine.StreamAsync(
            Input(namesAnimation ? $"animated {topic} movies" : $"{topic} movies"), accountId, emit, CancellationToken.None);

        if (namesAnimation)
        {
            Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
            Assert.DoesNotContain(outcome.Cards, c => c.Tier == SearchTurnCardTier.Pick);
            Assert.Single(events, e => e.Event == "conflict");
            Assert.Contains(ConversationStateJson.Read(outcome.ConversationStateJson)!.Hard.Concepts, c => c.Genres.Contains("animation"));
        }
        else
        {
            Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
            Assert.Equal(ids.Order(), outcome.Cards.Select(c => c.TitleId).Order());
        }
    }

    private static ConceptResolver RealResolver(Infrastructure.Persistence.ReelDbContext db, params string[] labels)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(KeywordVocabulary.CacheKey, (IReadOnlyList<VocabularyLabel>)[.. labels.Select(l => new VocabularyLabel(l, 10))]);
        return new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
    }

    /// <summary>Resolves the query's constraints before the facet arm reads them, as the hybrid retriever does.</summary>
    private sealed class ResolvingRetriever(IAskReelRetriever inner, IConceptResolver resolver) : IAskReelRetriever
    {
        public async Task<Application.Search.Retrieval.RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct) =>
            await inner.RetrieveAsync(q with { Hard = await resolver.ResolveAsync(q.Hard, ct) }, ct);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The catalogue arms these tests read match a concept by its own words; nothing to resolve.</summary>
    private sealed class PassThroughResolver : IConceptResolver
    {
        public Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct) => Task.FromResult(hard);

        public Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct) => Task.FromResult(concept);
    }

    /// <summary>
    /// Resolves <c>topic</c> to itself and a synonym, as the curated synonym table resolves "heist" to
    /// heist, robbery and bank robbery; every other concept passes through.
    /// </summary>
    private sealed class SynonymResolver(string topic, string synonym) : IConceptResolver
    {
        public Task<HardConstraints> ResolveAsync(HardConstraints hard, CancellationToken ct) =>
            Task.FromResult(hard with { Concepts = [.. hard.Concepts.Select(Resolve)] });

        public Task<ConceptTerm> ResolveAsync(ConceptTerm concept, CancellationToken ct) => Task.FromResult(Resolve(concept));

        private ConceptTerm Resolve(ConceptTerm concept) =>
            concept.UserWords == topic ? new ConceptTerm(topic, [topic, synonym], [], null) : concept;
    }

    private static string Topic() => $"vtopic{Guid.NewGuid():N}"[..18];

    private static PlanResult Plan(TurnAct act, ConversationStateDelta? delta = null, params TurnHandle[] references) =>
        new(act, delta ?? ConversationStateDelta.None, references, [new ToolRequest(AskTool.SearchCatalog)], null, [], PlanSource.Llm,
            new LlmCallMeta("scripted-plan", "none", false, 1, 5));

    private static LiveSearchExpansionService.AskTurnInput Input(string message) =>
        new(message, [], [], [], "US", [], Domain.Sync.ApiUsagePurpose.RerankAsk);

    private (AskReelEngine Engine, List<(string Event, JsonElement Data)> Events, LiveSearchExpansionService.Emit Emit) Build(
        Guid accountId, IAskReelPlanner planner, IRequestJudgeModel judge, IAskReelAnswerer? answerer = null,
        Func<IAskReelRetriever, IAskReelRetriever>? wrap = null, IEmbeddingProvider? embeddings = null,
        IServiceScopeFactory? scopes = null, IMemoryCache? cache = null, IModelEngine? model = null, TimeSpan? planHedgeAfter = null,
        IConceptResolver? resolver = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);
        cache ??= new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var supply = new SupplyReporter(db, eligibility);
        // The retriever reads the user's own words, as the facet arm does; the engine's supply and its
        // resolver read the resolution, as production's do (HREV3-ASK-B).
        var engineSupply = resolver is null ? supply : new SupplyReporter(db, eligibility, resolver);
        var features = new FeatureVectorBuilder(db);
        var scorer = Scorer(db, model ?? new ConstantModelEngine(8.4f), cache);
        var engine = new AskReelEngine(
            db, planner, new RequestJudge(judge, cache, NullLogger<RequestJudge>.Instance), judge, answerer ?? new ScriptedAnswerer("-"),
            new TitleFacts(db), (wrap ?? (r => r))(new FacetRetriever(db, eligibility, supply)), engineSupply, eligibility, scorer,
            new ExposureLog(db, NullLogger<ExposureLog>.Instance), features, new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new TurnComposer(), resolver ?? new PassThroughResolver(), new FilterCostMeter(db, eligibility, scoped, cache),
            scopes ?? new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            cache, new ConceptEmbeddings(embeddings ?? new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            NullLogger<AskReelEngine>.Instance)
        {
            PlanHedgeAfter = planHedgeAfter ?? AskReelEngine.DefaultPlanHedgeAfter,
        };

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

    /// <summary>The real on-demand scorer over <paramref name="model"/>: residual head, calibration and demotion included.</summary>
    private static OnDemandScorer Scorer(Infrastructure.Persistence.ReelDbContext db, IModelEngine model, IMemoryCache cache)
    {
        var features = new FeatureVectorBuilder(db);
        return new OnDemandScorer(
            db, features, model, new OnlineResidualModel(db, features, model, cache, NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, cache), new RatingCalibrationService(db, cache));
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

    /// <summary>
    /// An Active artifact for the account (bytes the constant model never reads) and a stored score
    /// for each title, so the supply splits them by score as it would for a trained account.
    /// </summary>
    private async Task SeedScoresAsync(Guid accountId, IEnumerable<Guid> titleIds, decimal predicted)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var artifactId = await db.ModelArtifacts.IgnoreQueryFilters()
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
        if (artifactId is null)
        {
            var artifact = new ModelArtifact
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Version = 1,
                ArtifactBytes = [1],
                TrainingRunId = Guid.NewGuid(),
                Status = ArtifactStatus.Active,
                TrainedAt = DateTime.UtcNow,
                FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            };
            db.Add(artifact);
            artifactId = artifact.Id;
        }

        foreach (var titleId in titleIds)
        {
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifactId.Value,
                PredictedRating = predicted,
                ScoredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The account's model predicts <paramref name="predicted"/> for each title: a stored score (what
    /// the supply splits by; skipped when <paramref name="stored"/> is false) and a runtime
    /// <see cref="RuntimeModelEngine"/> reads the same number from.
    /// </summary>
    private async Task SeedPredictionsAsync(Guid accountId, IReadOnlyList<Guid> titleIds, decimal predicted, bool stored = true)
    {
        // With nothing stored, only the account's active model exists: every prediction is made on demand.
        await SeedScoresAsync(accountId, stored ? titleIds : [], predicted);
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var title in await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync())
        {
            title.RuntimeMinutes = RuntimeModelEngine.RuntimeFor(predicted);
        }

        await db.SaveChangesAsync();
    }

    private async Task SetVotesAsync(IReadOnlyList<Guid> titleIds, int votes)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var title in await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync())
        {
            title.TmdbVoteCount = votes;
            title.TraktVotes = 0;
        }

        await db.SaveChangesAsync();
    }

    private async Task MakeShowsAsync(IReadOnlyList<Guid> titleIds)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var title in await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync())
        {
            title.MediaType = MediaType.Show;
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedFilterAsync(Guid accountId, Domain.Filters.FilterKind kind, string value)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new Domain.Filters.ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetGenresAsync(IEnumerable<Guid> titleIds, string[] genres)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var title in await db.Titles.Where(t => titleIds.Contains(t.Id)).ToListAsync())
        {
            title.Genres = genres;
        }

        await db.SaveChangesAsync();
    }

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

    /// <summary>No planner configured: the keyless install, read by the deterministic fallback.</summary>
    private sealed class KeylessPlanner : IAskReelPlanner
    {
        public bool IsAvailable => false;

        public string ModelId => "none";

        public Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct) =>
            throw new InvalidOperationException("A keyless planner is never called.");
    }

    /// <summary>No answer model: an item question is answered from the facts alone.</summary>
    private sealed class NoAnswerer : IAskReelAnswerer
    {
        public bool IsAvailable => false;

        public string ModelId => "none";

        public Task<AnswerResult> AnswerAsync(AnswerInput input, CancellationToken ct) =>
            throw new InvalidOperationException("An unavailable answerer is never called.");
    }

    /// <summary>A planner whose answer is awaited — it can wait on something the turn should already have started.</summary>
    /// <summary>Its first call hangs until cancelled (when told to); every other call answers <paramref name="plan"/>.</summary>
    private sealed class StuckFirstPlanner(bool firstHangs, PlanResult plan) : IAskReelPlanner
    {
        private readonly TaskCompletionSource _firstCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task FirstCancelled => _firstCancelled.Task;

        public bool IsAvailable => true;

        public string ModelId => "stuck-first-plan";

        public async Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1 && firstHangs)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    _firstCancelled.TrySetResult();
                    throw;
                }
            }

            return plan;
        }
    }

    private sealed class AwaitingPlanner(Func<PlanInput, Task<PlanResult>> script) : IAskReelPlanner
    {
        public bool IsAvailable => true;

        public string ModelId => "awaiting-plan";

        public Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct) => script(input);
    }

    /// <summary>A facet retriever on a context of its own, released with the scope that made it, recording each query.</summary>
    private sealed class OwnedRetriever(Infrastructure.Persistence.ReelDbContext db, Action<RetrievalQuery> record) : IAskReelRetriever, IDisposable
    {
        private readonly EligibilityQueryBuilder _eligibility = new(db);

        public async Task<Application.Search.Retrieval.RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct)
        {
            record(q);
            return await new FacetRetriever(db, _eligibility, new SupplyReporter(db, _eligibility)).RetrieveAsync(q, ct);
        }

        public void Dispose() => db.Dispose();
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

    /// <summary>
    /// <see cref="NameJudge"/>'s rulings with a stable cache salt (so verdicts are shared across
    /// scopes, as in production), counting every time a title is sent; <see cref="FirstSend"/>
    /// completes when the first title is.
    /// </summary>
    private sealed class CountingJudge : IRequestJudgeModel
    {
        private readonly NameJudge _rulings = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _sends = new();
        private readonly TaskCompletionSource _firstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstSend => _firstSend.Task;

        public bool IsAvailable => true;

        public string ModelId => "counting-judge";

        public string CacheSalt => "counting-judge";

        public int SendsOf(Guid id) => _sends.GetValueOrDefault(id);

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            foreach (var t in input.Titles)
            {
                _sends.AddOrUpdate(t.TitleId, 1, (_, n) => n + 1);
            }

            _firstSend.TrySetResult();
            return _rulings.JudgeBatchAsync(input, ct);
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

    /// <summary>The real retriever, cut short: it returns only its first <paramref name="take"/> candidates.</summary>
    private sealed class ShortRetriever(IAskReelRetriever inner, int take) : IAskReelRetriever
    {
        public async Task<Application.Search.Retrieval.RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct)
        {
            var result = await inner.RetrieveAsync(q, ct);
            return result with { Candidates = [.. result.Candidates.Take(take)] };
        }
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

    /// <summary>
    /// Predicts from the title's runtime (<see cref="RuntimeFor"/> inverts it), so a fixture can give
    /// each title its own prediction through the real scoring path.
    /// </summary>
    private sealed class RuntimeModelEngine : IModelEngine
    {
        private static readonly int Runtime = Array.IndexOf(FeatureSchema.AllNames(), "runtimeMinutes");
        private int _poolScorings;

        public static int RuntimeFor(decimal predicted) => 100 + (int)(predicted * 10m);

        /// <summary>How many calls scored more titles than the judge rules on — a whole candidate pool.</summary>
        public int PoolScorings => Volatile.Read(ref _poolScorings);

        public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson) =>
            throw new NotSupportedException("This fixture scores; it never trains.");

        public float[] Score(byte[] artifactBytes, float[][] features)
        {
            if (features.Length > RequestJudge.MaxJudged)
            {
                Interlocked.Increment(ref _poolScorings);
            }

            return [.. features.Select(f => (f[Runtime] - 100f) / 10f)];
        }

        public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features) => Score(artifactBytes, features);

        public ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            [.. Score(artifactBytes, features).Select(s => new ScoredRow(s, []))];

        public ScoredRow[] ScoreWithContributionsCached(
            Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            ScoreWithContributions(artifactBytes, features, featureNames, topK);
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
