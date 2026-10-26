using System.Text.Json;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Ask Reel v2's deterministic parts (WS-2 task 2): the state reducer, the plan sanitiser, the
/// composer's copy, the state column, handles, the adapters' readers and the judge's batching.
/// Everything here runs without a database or a model.
/// </summary>
public class AskReelV2UnitTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static PlanResult Plan(TurnAct act, ConversationStateDelta? delta = null, params TurnHandle[] references) =>
        new(act, delta ?? ConversationStateDelta.None, references, [], null, [], PlanSource.Llm, LlmCallMeta.None);

    private static ConversationState Shown(params (Guid Id, int Turn, int Rank)[] cards) =>
        ConversationState.Empty with
        {
            Version = 1,
            LastPresented = cards.Select(c => new PresentedRef(c.Id, new TurnHandle(c.Turn, c.Rank), $"Title {c.Rank}", 2020, MediaType.Movie)).ToList(),
        };

    // ── Reducer ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_request_replaces_the_requirements_and_a_refine_merges_into_them()
    {
        var first = ConversationStateReducer.Apply(
            ConversationState.Empty,
            Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Moods = ["feel-good"], Media = MediaScope.Movies }),
            MediaScope.Any);
        var refined = ConversationStateReducer.Apply(
            first, Plan(TurnAct.Refine, ConversationStateDelta.None with { Negations = ["superheroes"] }), MediaScope.Any);
        var fresh = ConversationStateReducer.Apply(
            refined, Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = ["ninja"] }), MediaScope.Any);

        // "feel-good movie" → "no superheroes please": the media is preserved and the negation added.
        Assert.Equal(MediaScope.Movies, refined.Hard.Media);
        Assert.Equal(["feel-good"], refined.Soft.Moods);
        Assert.Equal(["superheroes"], refined.UserNegations);

        // A new request starts over.
        Assert.Equal(["ninja"], fresh.Hard.Concepts.Select(c => c.UserWords));
        Assert.Empty(fresh.Soft.Moods);
        Assert.Empty(fresh.UserNegations);
        Assert.Equal(MediaScope.Any, fresh.Hard.Media);
    }

    [Fact]
    public void The_latest_mention_of_a_word_decides_whether_it_is_asked_for_or_ruled_out()
    {
        var ruledOut = ConversationStateReducer.Apply(
            ConversationState.Empty, Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Negations = ["ninja"] }), MediaScope.Any);
        var askedAgain = ConversationStateReducer.Apply(
            ruledOut, Plan(TurnAct.Refine, ConversationStateDelta.None with { Concepts = ["ninjas"] }), MediaScope.Any);
        var ruledOutAgain = ConversationStateReducer.Apply(
            askedAgain, Plan(TurnAct.Refine, ConversationStateDelta.None with { Negations = ["ninja"] }), MediaScope.Any);

        Assert.Empty(askedAgain.UserNegations);
        Assert.Single(askedAgain.Hard.Concepts);
        Assert.Equal(["ninja"], ruledOutAgain.UserNegations);
        Assert.Empty(ruledOutAgain.Hard.Concepts);
    }

    [Fact]
    public void A_set_critique_rejects_every_card_of_the_newest_answer_and_keeps_its_topic_hard()
    {
        var state = Shown((A, 2, 1), (B, 2, 2), (C, 1, 1));
        var next = ConversationStateReducer.Apply(
            state, Plan(TurnAct.CritiqueSet, ConversationStateDelta.None with { Concepts = ["ninja"] }), MediaScope.Any);

        Assert.Equal([A, B], next.Rejected.Order());
        Assert.Equal(["ninja"], next.Hard.Concepts.Select(c => c.UserWords));
        Assert.Empty(next.UserNegations);
    }

    [Fact]
    public void An_item_critique_rejects_the_critiqued_card_and_steers_relative_to_it()
    {
        var state = Shown((A, 2, 1), (B, 2, 2));
        var next = ConversationStateReducer.Apply(
            state,
            Plan(TurnAct.CritiqueItem,
                ConversationStateDelta.None with { Directions = [new AttributeDirection("humor", AttributeTrend.More)] },
                new TurnHandle(2, 1)),
            MediaScope.Any);

        Assert.Equal([A], next.Rejected);
        Assert.Equal([A], next.Soft.ReferenceTitleIds);
        Assert.Single(next.Soft.Directions);
    }

    [Fact]
    public void A_question_leaves_the_search_where_it_was()
    {
        var state = Shown((A, 1, 1)) with { Hard = HardConstraints.None with { Concepts = [new ConceptTerm("ninja", [], [])] } };
        var next = ConversationStateReducer.Apply(state, Plan(TurnAct.ItemQuestion, references: new TurnHandle(1, 1)), MediaScope.Any);

        Assert.Same(state.Hard, next.Hard);
        Assert.Empty(next.Rejected);
    }

    [Fact]
    public void The_client_media_filter_outranks_the_plan()
    {
        var next = ConversationStateReducer.Apply(
            ConversationState.Empty, Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Media = MediaScope.Shows }), MediaScope.Movies);
        Assert.Equal(MediaScope.Movies, next.Hard.Media);
    }

    [Fact]
    public void Presented_keeps_the_newest_three_answers()
    {
        var state = Shown((A, 1, 1), (B, 2, 1), (C, 3, 1));
        var next = ConversationStateReducer.Presented(state, [new PresentedRef(Guid.NewGuid(), new TurnHandle(4, 1), "New", 2024, MediaType.Movie)]);
        Assert.Equal([4, 3, 2], next.LastPresented.Select(p => p.Handle.Turn).Distinct());
    }

    // ── Sanitiser ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sanitiser_drops_invented_handles_negations_the_user_never_said_and_standing_conditions()
    {
        var state = Shown((A, 1, 1));
        var standing = FilterRuleSet.FromRows([new ContentFilter { Kind = FilterKind.ExcludeGenre, Value = "horror" }]);
        var plan = Plan(
            TurnAct.Refine,
            ConversationStateDelta.None with { Negations = ["superheroes", "romance", "horror"], Concepts = ["the second one", "heist"] },
            new TurnHandle(1, 1), new TurnHandle(7, 3));

        var clean = PlanSanitizer.Clean(plan, "heist movies, no superheroes and no horror please", state, standing);

        Assert.Equal([new TurnHandle(1, 1)], clean.References);
        Assert.Equal(["superheroes"], clean.Delta.Negations);
        Assert.Equal(["heist"], clean.Delta.Concepts);
    }

    [Fact]
    public void The_acceptance_follow_up_keeps_ninja_as_a_concept_not_a_negation()
    {
        var plan = Plan(TurnAct.CritiqueSet, ConversationStateDelta.None with { Concepts = ["ninja"], Negations = ["ninja"] });
        var clean = PlanSanitizer.Clean(
            plan, "none of the above are ninja movies. I want ninja movies", ConversationState.Empty, FilterRuleSet.FromRows([]));

        Assert.Equal(["ninja"], clean.Delta.Concepts);
        Assert.Empty(clean.Delta.Negations);
    }

    /// <summary>
    /// The live "survival at sea" turn: the planner's one concept was the message itself, the v1 echo
    /// rule threw it away, and an empty request judged sixteen random titles to a none-found. A short
    /// message that IS its topic keeps it; a sentence echoed back as a keyword still does not.
    /// </summary>
    [Theory]
    [InlineData("survival at sea", "survival at sea", true)]
    [InlineData("Survival at sea", "survival at sea", true)]
    [InlineData("something brooding and futuristic for tonight", "something brooding and futuristic for tonight", false)]
    [InlineData("no superheroes please", "no superheroes please", false)]
    public void A_short_message_that_is_its_own_topic_keeps_it_as_the_concept(string message, string concept, bool kept)
    {
        var plan = Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [concept] });
        var clean = PlanSanitizer.Clean(plan, message, ConversationState.Empty, FilterRuleSet.FromRows([]));

        Assert.Equal(kept, clean.Delta.Concepts.Count == 1);
    }

    /// <summary>The live "how many ninja movies are left?" plan left media unset and was counted over shows too.</summary>
    [Theory]
    [InlineData("how many ninja movies are left?", null, MediaScope.Movies)]
    [InlineData("any good ninja shows?", null, MediaScope.Shows)]
    [InlineData("ninja movies or shows", null, null)]
    [InlineData("more of those", null, null)]
    [InlineData("ninja movies", MediaScope.Shows, MediaScope.Shows)]
    public void The_users_own_media_word_survives_a_plan_that_left_media_unset(string message, MediaScope? planned, MediaScope? expected)
    {
        var plan = Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Media = planned });
        Assert.Equal(expected, PlanSanitizer.Clean(plan, message, ConversationState.Empty, FilterRuleSet.FromRows([])).Delta.Media);
    }

    [Fact]
    public void A_supply_answer_agrees_in_number()
    {
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm("ninja", [], [])], Media = MediaScope.Movies };
        var one = new SupplyReport(20, 19, 0, 0, 1, 0, 0, [], 0, 50, 6.0m, true);
        Assert.Equal(
            "You have 1 unseen ninja movie left that clears your conditions and your bar; you've seen 19.",
            AskReelEngine.SupplyAnswer(one, hard));
    }

    [Fact]
    public void Only_one_clarifying_question_is_asked_per_conversation()
    {
        var plan = Plan(TurnAct.NewRequest) with { ClarifyQuestion = "Movies or shows?", ClarifyOptions = ["Movies", "Shows"] };
        Assert.NotNull(PlanSanitizer.Clean(plan, "something good", ConversationState.Empty, FilterRuleSet.FromRows([])).ClarifyQuestion);
        Assert.Null(PlanSanitizer.Clean(
            plan, "something good", ConversationState.Empty with { ClarifiedOnce = true }, FilterRuleSet.FromRows([])).ClarifyQuestion);
    }

    [Fact]
    public void A_carried_forward_plan_adds_only_soft_words_and_never_a_pointer()
    {
        var state = ConversationState.Empty with { Version = 2, Hard = HardConstraints.None with { Concepts = [new ConceptTerm("ninja", [], [])] } };
        var plan = FallbackPlans.CarriedForward(
            PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None),
            "more like the second one but funnier, no gore",
            state);

        Assert.Equal(PlanSource.CarriedForward, plan.Source);
        Assert.Empty(plan.Delta.Concepts);
        Assert.DoesNotContain("second", plan.Delta.Moods);
        Assert.Contains("funnier", plan.Delta.Moods);
        Assert.Equal(["gore"], plan.Delta.Negations);
    }

    // ── Composer ────────────────────────────────────────────────────────────────────────────

    private static TurnContext Context(FinalMode mode) =>
        new(TurnAct.NewRequest, mode, "ninja", MediaScope.Movies, 0, 0, [], 0, [], 0, 0, 6.0m, null, false, false);

    [Fact]
    public void Picks_copy_is_counts_only_and_never_the_stub_template()
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with
        {
            Picks = 6,
            NearMissNames = ["Shinobi"],
            UnderBar = 2,
        });

        Assert.Equal(
            "6 ninja movies that fit what you asked. 1 more is close but not quite — tucked away below. 2 more fit but sit under your 6.0 bar.",
            reply.Reply);
        Assert.DoesNotMatch("^Here are some .* picks for “", reply.Reply);
        Assert.Equal("6 picks for you · 2 held back by your 6.0 bar — shown below · 1 close but not quite — tucked away below", reply.Reason);
    }

    /// <summary>
    /// The live "heist movies" turn said "1 heist movies checked against your request": the noun took the
    /// total's number. A failed batch with one judged pick, and the bar holding back unchecked cards
    /// too, which are counted as unchecked and never said to fit (XM-06).
    /// </summary>
    [Fact]
    public void Picks_copy_agrees_in_number_and_never_says_an_unchecked_card_fits()
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with
        {
            Topic = "heist",
            Picks = 1,
            UnjudgedPicks = 3,
            UnderBar = 7,
            HeldUnchecked = 3,
            JudgeFailed = true,
        });

        Assert.Equal(
            "1 heist movie checked against your request, and 3 more I couldn't check. 4 more fit but sit under your 6.0 bar.",
            reply.Reply);
        // …and the live "something quiet and slow-burning" turn's "1 title that fit what you asked".
        Assert.Equal(
            "1 title that fits what you asked.",
            new TurnComposer().Compose(Context(FinalMode.Picks) with { Topic = null, Media = MediaScope.Any, Picks = 1 }).Reply);
        Assert.Equal(
            "4 picks for you · 3 more not checked against what you asked · 7 held back by your 6.0 bar, 3 of them not checked against what you asked — shown below",
            reply.Reason);
    }

    [Fact]
    public void A_carried_forward_turn_leads_with_the_section_4_10_sentence()
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with { Picks = 3, CarriedForward = true });
        Assert.StartsWith("I couldn't fully read that — searching for ninja as before.", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_person_reply_counts_seen_titles_and_never_names_them()
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with
        {
            Topic = null,
            Picks = 3,
            Person = new PersonConstraint("Christopher Nolan", null, PersonRole.Director),
            PersonSeen = 9,
        });
        Assert.Equal("You've seen 9 movies directed by Christopher Nolan; here are 3 you haven't.", reply.Reply);
    }

    [Fact]
    public void A_person_none_found_names_the_person_as_the_shelf_and_counts_the_seen()
    {
        var supply = new SupplyReport(16, 12, 0, 4, 0, 0, 0, [], 0, 50, 6.0m, true);
        var reply = new TurnComposer().Compose(Context(FinalMode.NoneFound) with
        {
            Topic = null,
            Supply = supply,
            Person = new PersonConstraint("Christopher Nolan", null, PersonRole.Director),
            PersonSeen = 12,
        });
        Assert.StartsWith("There are no Christopher Nolan movies left for you.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("you've seen 12", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_none_found_reply_comes_from_the_supply()
    {
        var supply = new SupplyReport(12, 12, 0, 0, 0, 0, 0, [], 0, 50, 6.0m, true);
        var reply = new TurnComposer().Compose(Context(FinalMode.NoneFound) with { Supply = supply });
        Assert.StartsWith("There are no ninja movies left for you.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("you've seen 12", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Chitchat_and_answers_say_what_they_were_given()
    {
        var composer = new TurnComposer();
        Assert.Equal(TurnComposer.ChitchatReply, composer.Compose(Context(FinalMode.Answer) with { Act = TurnAct.Chitchat }).Reply);
        Assert.Equal("It is rated R.", composer.Compose(Context(FinalMode.Answer) with { Act = TurnAct.ItemQuestion, Answer = "It is rated R." }).Reply);
        Assert.Null(composer.Compose(Context(FinalMode.Answer)).Reason);
    }

    // ── The state column and handles ────────────────────────────────────────────────────────

    [Fact]
    public void The_state_round_trips_with_enums_by_name_and_an_unreadable_row_reads_as_none()
    {
        var state = Shown((A, 2, 1)) with
        {
            Hard = new HardConstraints(
                [new ConceptTerm("ninja", ["ninja", "shinobi"], [])],
                [new PersonConstraint("Christopher Nolan", null, PersonRole.Director)],
                new YearRange(1980, 1989), 120, MediaScope.Movies),
            Soft = new SoftPreferences(["tense"], [new AttributeDirection("humor", AttributeTrend.Less)], [B]),
            UserNegations = ["gore"],
            Rejected = [C],
            LegacyTmdbExclude = [42L],
            ClarifiedOnce = true,
        };

        var json = ConversationStateJson.Write(state);
        var back = ConversationStateJson.Read(json);

        Assert.Contains("\"Movies\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Director\"", json, StringComparison.Ordinal);
        Assert.NotNull(back);
        Assert.Equal(MediaScope.Movies, back.Hard.Media);
        Assert.Equal(["ninja", "shinobi"], back.Hard.Concepts[0].CatalogueLabels);
        Assert.Equal(PersonRole.Director, back.Hard.People[0].Role);
        Assert.Equal(new TurnHandle(2, 1), back.LastPresented[0].Handle);
        Assert.Equal([C], back.Rejected);
        Assert.True(back.ClarifiedOnce);

        Assert.Null(ConversationStateJson.Read(json.Replace("\"Movies\"", "\"Podcasts\"", StringComparison.Ordinal)));
        Assert.Null(ConversationStateJson.Read("{not json"));
        Assert.Null(ConversationStateJson.Read(null));
    }

    [Theory]
    [InlineData("T2.3", 2, 3)]
    [InlineData(" t10.1 ", 10, 1)]
    public void Handles_parse(string text, int turn, int rank) =>
        Assert.Equal(new TurnHandle(turn, rank), TurnHandle.Parse(text));

    [Theory]
    [InlineData("T0.1")]
    [InlineData("2.3")]
    [InlineData("T2")]
    [InlineData("T2.x")]
    [InlineData(null)]
    public void Malformed_handles_do_not_parse(string? text) => Assert.Null(TurnHandle.Parse(text));

    [Fact]
    public void Unknown_stored_acts_and_plan_sources_read_as_their_fallbacks()
    {
        Assert.Equal(TurnAct.NewRequest, TurnActs.Parse("SomethingNewer"));
        Assert.Equal(TurnAct.CritiqueSet, TurnActs.Parse("CritiqueSet"));
        Assert.Equal(PlanSource.CarriedForward, TurnActs.ParseSource("Oracle"));
        Assert.Equal(PlanSource.LlmRetry, TurnActs.ParseSource("LlmRetry"));
    }

    // ── Adapter readers ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_planner_reads_its_schema()
    {
        using var doc = JsonDocument.Parse("""
            {"act":"CritiqueItem","concepts":["heist"],"genres":["comedy"],"people":[{"name":"Rian Johnson","role":"director"}],
             "minYear":1980,"maxYear":1989,"maxRuntimeMinutes":null,"media":"movie","moods":["fun"],
             "directions":[{"attribute":"humor","trend":"less"}],"negations":["gore"],"references":["T2.1","nonsense"],
             "referenceNames":["Knives Out"],"tools":["similar_to","bogus"],"question":null,"clarifyQuestion":null,"clarifyOptions":[]}
            """);
        var plan = OpenRouterAskPlanner.Parse(doc.RootElement, PlanSource.Llm, LlmCallMeta.None);

        Assert.Equal(TurnAct.CritiqueItem, plan.Act);
        Assert.Equal(["heist"], plan.Delta.Concepts);
        Assert.Equal(PersonRole.Director, plan.Delta.People[0].Role);
        Assert.Equal(new YearRange(1980, 1989), plan.Delta.Years);
        Assert.Equal(MediaScope.Movies, plan.Delta.Media);
        Assert.Equal(AttributeTrend.Less, plan.Delta.Directions[0].Trend);
        Assert.Equal([new TurnHandle(2, 1)], plan.References);
        Assert.Equal([AskTool.SimilarTo], plan.Tools.Select(t => t.Tool));
        Assert.Null(plan.ClarifyQuestion);
    }

    [Fact]
    public void The_planner_messages_carry_the_handles_and_never_a_standing_condition_as_state()
    {
        var input = new PlanInput(
            "more like the second one",
            Shown((A, 1, 1), (B, 1, 2)),
            [new ChatTurn("user", "ninja movies"), new ChatTurn("assistant", "2 ninja movies. [Shown: T1.1 · T1.2]")],
            new StandingConditionsView("no horror"),
            "You like tense thrillers.",
            MediaScope.Any);

        var messages = OpenRouterAskPlanner.Messages(input);

        Assert.Equal("system", messages[0].Role);
        Assert.Equal(["user", "assistant", "user"], messages.Skip(1).Select(m => m.Role));
        Assert.Contains("T1.2 Title 2 (2020)", messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("\"standingConditions\":\"no horror\"", messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_judge_reader_keeps_verdicts_and_drops_why_lines_that_hedge_or_cite_nothing()
    {
        var input = new JudgeInput(Guid.NewGuid(), "ninja movies", "You like action.", [Title(A), Title(B), Title(C)]);
        using var doc = JsonDocument.Parse($$"""
            {"results":[
              {"id":"{{A}}","satisfies":"yes","fit":0.9,"why":"You get a shinobi clan war.","evidence":"keyword: ninja"},
              {"id":"{{B}}","satisfies":"yes","fit":0.7,"why":"Though it may be slower than you like.","evidence":"overview: ninja"},
              {"id":"{{C}}","satisfies":"partial","fit":2.5,"why":"x","evidence":"y"},
              {"id":"{{Guid.NewGuid()}}","satisfies":"yes","fit":0.5,"why":"x","evidence":"y"}
            ]}
            """);
        var verdicts = OpenRouterAskJudge.Parse(doc.RootElement, input);

        Assert.Equal(2, verdicts.Count);
        Assert.Equal("You get a shinobi clan war.", verdicts[A].Why);
        Assert.Equal(ResultSource.Llm, verdicts[A].WhySource);
        Assert.Equal(Satisfies.Yes, verdicts[B].Satisfies);
        Assert.Null(verdicts[B].Why);
        Assert.Equal(ResultSource.Fallback, verdicts[B].WhySource);
        Assert.False(verdicts.ContainsKey(C));

        // The prompt numbers titles from 1, and an answer naming a position names that title.
        using var positional = JsonDocument.Parse("""
            {"results":[{"id":"2","satisfies":"no","fit":0.1,"why":"You get no ninjas here.","evidence":"genre: drama"},
                        {"id":"9","satisfies":"yes","fit":0.9,"why":"x","evidence":"y"}]}
            """);
        var byPosition = OpenRouterAskJudge.Parse(positional.RootElement, input);
        Assert.Equal([B], byPosition.Keys);
        Assert.Contains("\"id\":\"1\"", OpenRouterAskJudge.UserPrompt(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("It's rated R for violence. It runs 110 minutes. It stars Keanu Reeves.", "It's rated R for violence. It runs 110 minutes.")]
    [InlineData("**Yes** — it is rated R.", "Yes — it is rated R.")]
    [InlineData("   ", null)]
    public void An_answer_is_two_plain_sentences_at_most(string raw, string? expected) =>
        Assert.Equal(expected, OpenRouterAskAnswerer.Clean(raw));

    private static JudgeTitle Title(Guid id) => new(id, $"Title {id:N}", 2020, MediaType.Movie, ["action"], ["ninja"], "A ninja.", [], null);

    // ── The judge's batching ────────────────────────────────────────────────────────────────

    [Fact]
    public void Batches_are_at_most_six_with_a_smaller_first_and_cover_the_top_sixteen()
    {
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        var batches = RequestJudge.Batches(ids);

        Assert.Equal([4, 6, 6], batches.Select(b => b.Count));
        Assert.Equal(ids.Take(16), batches.SelectMany(b => b));
    }

    [Fact]
    public async Task Every_title_gets_one_verdict_a_failed_batch_is_unjudged_and_at_most_three_run_at_once()
    {
        var titles = Enumerable.Range(0, 16).Select(_ => Title(Guid.NewGuid())).ToList();
        var model = new ScriptedJudgeModel(failBatchContaining: titles[5].TitleId);
        var judge = new RequestJudge(model, new MemoryCache(new MemoryCacheOptions()), NullLogger<RequestJudge>.Instance);

        var verdicts = new List<JudgeVerdict>();
        await foreach (var v in judge.JudgeAsync(new JudgeInput(Guid.NewGuid(), "ninja", "persona", titles), CancellationToken.None))
        {
            verdicts.Add(v);
        }

        Assert.Equal(titles.Select(t => t.TitleId).Order(), verdicts.Select(v => v.TitleId).Order());
        Assert.InRange(model.MaxConcurrent, 2, RequestJudge.MaxConcurrent);
        var failed = RequestJudge.Batches(titles.Select(t => t.TitleId).ToList())[1];
        Assert.All(verdicts.Where(v => failed.Contains(v.TitleId)), v => Assert.False(v.Judged));
        Assert.All(verdicts.Where(v => !failed.Contains(v.TitleId)), v => Assert.True(v.Judged));

        // A second ask of the same request reuses the judged verdicts and re-asks only the failed batch.
        var calls = model.Calls;
        await foreach (var _ in judge.JudgeAsync(new JudgeInput(Guid.NewGuid(), "ninja", "persona", titles), CancellationToken.None))
        {
        }

        Assert.True(model.Calls > calls);
    }

    private sealed class ScriptedJudgeModel(Guid failBatchContaining) : IRequestJudgeModel
    {
        private readonly object _gate = new();
        private int _inFlight;

        public int MaxConcurrent { get; private set; }

        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "scripted";

        public string CacheSalt => "scripted";

        public async Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls++;
                _inFlight++;
                MaxConcurrent = Math.Max(MaxConcurrent, _inFlight);
            }

            try
            {
                await Task.Delay(100, ct);
                if (input.Titles.Any(t => t.TitleId == failBatchContaining))
                {
                    return new JudgeBatchResult(input.Titles.Select(t => JudgeVerdict.Unjudged(t.TitleId)).ToList(), LlmCallMeta.None, true);
                }

                return new JudgeBatchResult(
                    input.Titles.Select(t => new JudgeVerdict(t.TitleId, Satisfies.Yes, 0.8, "You get ninjas.", "keyword: ninja", ResultSource.Llm, ResultSource.Llm)).ToList(),
                    LlmCallMeta.None,
                    false);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }
    }
    // ── Wave B: negations into retrieval, conditions() ────────────────────────────────────────

    [Fact]
    public void Negations_ride_into_retrieval_as_genres_where_they_name_one_and_keywords_in_every_spelling()
    {
        var (genres, keywords) = AskReelEngine.NegationRules(["horror", "superheroes"]);

        Assert.Equal(["horror"], genres);
        Assert.Contains("superheroes", keywords);
        Assert.Contains("superhero", keywords);
        Assert.DoesNotContain("horror", keywords);
        Assert.Equal((Array.Empty<string>(), Array.Empty<string>()), (AskReelEngine.NegationRules([]).Genres.ToArray(), AskReelEngine.NegationRules([]).Keywords.ToArray()));
    }

    [Fact]
    public void The_conditions_answer_names_the_rules_the_pool_they_leave_and_what_each_alone_is_costing()
    {
        var standing = FilterRuleSet.FromRows(
        [
            new ContentFilter { Id = Guid.NewGuid(), Kind = FilterKind.ExcludeGenre, Value = "comedy" },
            new ContentFilter { Id = Guid.NewGuid(), Kind = FilterKind.MinYear, Value = "2000" },
        ]);
        var cost = new FilterCostReport(
            5319, 421,
            [new FilterCostRule("ExcludeGenre", "comedy", 812), new FilterCostRule("MinYear", "2000", 1300)],
            []);

        Assert.Equal(
            "Your standing conditions: excludes: comedy; min year 2000. They leave 421 unseen titles open to you, out of 5,319 in the "
            + "catalogue. Relaxing one alone would open the most for: released 2000 or later (+1,300), excluded genre: comedy (+812).",
            AskReelEngine.ConditionsAnswer(standing, cost));
        // Unmeasured: the rules alone, no number invented.
        Assert.Equal("Your standing conditions: excludes: comedy; min year 2000.", AskReelEngine.ConditionsAnswer(standing, null));
        Assert.Equal(
            "You have no standing conditions set, so every unseen title in the catalogue is open to you.",
            AskReelEngine.ConditionsAnswer(FilterRuleSet.FromRows([]), null));
    }
}
