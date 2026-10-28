using System.Text.Json;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
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

    private static ConversationState ShownNamed(params (Guid Id, int Turn, int Rank, string Name)[] cards) =>
        ConversationState.Empty with
        {
            Version = 1,
            LastPresented = cards.Select(c => new PresentedRef(c.Id, new TurnHandle(c.Turn, c.Rank), c.Name, 2020, MediaType.Movie)).ToList(),
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

    /// <summary>
    /// R1-30: "more like the second one" resolved once in six live runs — the planner, at effort none,
    /// mostly returned no reference at all, and the turn then searched the topic instead. The rank a
    /// pointer names is in the user's words, so the server guarantees it: a missing reference is filled
    /// from the newest answer, and a reference at the wrong rank is moved to the named rank in the
    /// answer the model chose. Which answer an "earlier" pointer means stays the model's call.
    /// </summary>
    [Theory]
    [InlineData("more like the second one", TurnAct.Refine, "", "T2.2")]
    [InlineData("More like the SECOND one!", TurnAct.Refine, "", "T2.2")]
    [InlineData("is the first one violent?", TurnAct.ItemQuestion, "", "T2.1")]
    [InlineData("less dark than the third film", TurnAct.CritiqueItem, "", "T2.3")]
    [InlineData("more like the last one", TurnAct.Refine, "", "T2.3")]
    [InlineData("more like #3", TurnAct.Refine, "", "T2.3")]
    [InlineData("more like the second one", TurnAct.Refine, "T2.1", "T2.2")]
    [InlineData("more like the second one", TurnAct.Refine, "T1.1", "T1.2")]
    [InlineData("more like the second one you showed me first time round", TurnAct.Refine, "T1.2", "T1.2")]
    [InlineData("more like the second one you showed me first time round", TurnAct.Refine, "", "")]
    [InlineData("more like the fourth one", TurnAct.Refine, "", "")]
    [InlineData("thanks, the second one was great", TurnAct.Chitchat, "", "")]
    [InlineData("the second one sounds fun, now find me heist movies", TurnAct.NewRequest, "", "")]
    [InlineData("more like the first and the third one", TurnAct.Refine, "T2.1,T2.3", "T2.1,T2.3")]
    [InlineData("more heist movies", TurnAct.Refine, "", "")]
    // A reference to a card the message NAMES is the model reading the user right, not a miscount: the
    // ordinal is the other side of a comparison, and moving the reference answers the wrong card.
    [InlineData("is Prometheus scarier than the first film?", TurnAct.ItemQuestion, "T2.3", "T2.3")]
    [InlineData("Is PROMETHEUS any scarier than the first one", TurnAct.CritiqueItem, "T2.3", "T2.3")]
    [InlineData("is Aliens scarier than the first film?", TurnAct.ItemQuestion, "T2.2", "T2.2")]
    [InlineData("is Ronin tenser than the first one?", TurnAct.ItemQuestion, "T1.2", "T1.2")]
    [InlineData("is Prometheus scarier than the first film?", TurnAct.ItemQuestion, "T2.2", "T2.1")]
    [InlineData("is Aliens better than the first one?", TurnAct.ItemQuestion, "T2.3", "T2.1")]
    [InlineData("is Prometheus scarier than the first film?", TurnAct.ItemQuestion, "", "T2.1")]
    public void A_pointer_the_planner_missed_or_misranked_resolves_to_the_rank_the_user_named(
        string message, TurnAct act, string modelReferences, string expected)
    {
        var state = ShownNamed(
            (A, 2, 1, "Alien"), (B, 2, 2, "Aliens"), (C, 2, 3, "Prometheus"),
            (Guid.NewGuid(), 1, 1, "Heat"), (Guid.NewGuid(), 1, 2, "Ronin"), (Guid.NewGuid(), 1, 3, "Thief"));
        var given = modelReferences.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => TurnHandle.Parse(h)!.Value).ToArray();

        var clean = PlanSanitizer.Clean(Plan(act, null, given), message, state, FilterRuleSet.FromRows([]));

        Assert.Equal(expected, string.Join(",", clean.References.Select(r => r.ToString())));
    }

    /// <summary>
    /// R1-30, the judge's half: the judge is shown the request and the titles, never the screen, so a
    /// bare "more like the second one" left it to guess which card was meant. Live, it guessed a card
    /// from an EARLIER answer ("not a dog-training story" against McFarland, USA) and returned
    /// none-found on every run. The request now names the titles the pointer resolved to, by name and
    /// year; a reference the conversation no longer shows has no name to give and is left out, and a
    /// turn with no reference adds nothing.
    /// </summary>
    [Theory]
    [InlineData("A", "Points at: McFarland, USA (2015)")]
    [InlineData("B", "Points at: Pay It Forward (2000)")]
    [InlineData("A,B", "Points at: McFarland, USA (2015), Pay It Forward (2000)")]
    [InlineData("C", "Points at: A Dog's Journey")]
    [InlineData("A,D", "Points at: McFarland, USA (2015)")]
    [InlineData("D", null)]
    [InlineData("", null)]
    public void The_judge_is_told_which_titles_a_pointer_resolved_to(string references, string? expected)
    {
        var d = Guid.Parse("00000000-0000-0000-0000-00000000000d");
        var ids = new Dictionary<string, Guid> { ["A"] = A, ["B"] = B, ["C"] = C, ["D"] = d };
        var state = ConversationState.Empty with
        {
            Version = 2,
            LastPresented =
            [
                new PresentedRef(B, new TurnHandle(2, 1), "Pay It Forward", 2000, MediaType.Movie),
                new PresentedRef(A, new TurnHandle(2, 2), "McFarland, USA", 2015, MediaType.Movie),
                new PresentedRef(C, new TurnHandle(1, 2), "A Dog's Journey", null, MediaType.Movie),
            ],
            Soft = new SoftPreferences([], [], references.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => ids[r]).ToList()),
        };

        var request = AskReelEngine.JudgeRequest("more like the second one", state);

        Assert.StartsWith("Request: more like the second one", request, StringComparison.Ordinal);
        if (expected is null)
        {
            Assert.DoesNotContain("Points at", request, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, request, StringComparison.Ordinal);
            Assert.DoesNotContain(expected + ",", request, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HREV3-ASK-C: a v1 person request whose every unseen credit sits under the bar says so, rather
    /// than "here are 0 you haven't". Varied over seen credits and the singular.
    /// </summary>
    [Theory]
    [InlineData(39, 3, "You've seen 39 movies starring Liam Neeson; the 3 you haven't all sit under your 6.0 bar.")]
    [InlineData(1, 1, "You've seen 1 movie starring Liam Neeson; the one you haven't sits under your 6.0 bar.")]
    [InlineData(0, 2, "The 2 movies starring Liam Neeson you haven't seen all sit under your 6.0 bar.")]
    [InlineData(0, 1, "The one movie starring Liam Neeson you haven't seen sits under your 6.0 bar.")]
    public void A_v1_person_reply_whose_every_credit_is_under_the_bar_says_so(int seen, int underBar, string expected) =>
        Assert.Equal(
            expected,
            LiveSearchExpansionService.PersonReply(
                new PersonQuery("Liam Neeson", PersonRole.Cast), 0, seen, [MediaType.Movie], underBar, 6.0m));

    /// <summary>
    /// HREV3-RET-01: the judge is told what the request REQUIRES and what it only PREFERS, labelled, so
    /// a mood never decides satisfies. "Tense heist movies from the last ten years" passed "Mood: tense"
    /// beside the requirements and every bar-clearing heist was ruled partial for its tone. Varied over
    /// a mood, a steer, both, and neither.
    /// </summary>
    [Theory]
    [InlineData(new[] { "tense" }, false, "Prefers (never required): tense")]
    [InlineData(new string[0], true, "Prefers (never required): less darkness")]
    [InlineData(new[] { "cozy" }, true, "Prefers (never required): cozy, less darkness")]
    [InlineData(new string[0], false, null)]
    public void The_judge_is_told_the_requirements_apart_from_the_preferences(string[] moods, bool steer, string? preferences)
    {
        var state = ConversationState.Empty with
        {
            Hard = new HardConstraints([new ConceptTerm("heist", [], [])], [], new YearRange(2016, 2026), null, MediaScope.Movies),
            Soft = new SoftPreferences(moods, steer ? [new AttributeDirection("darkness", AttributeTrend.Less)] : [], []),
        };

        var request = AskReelEngine.JudgeRequest("tense heist movies from the last ten years", state);

        Assert.Contains("Requires: heist", request, StringComparison.Ordinal);
        Assert.Contains("Years: 2016-2026", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Mood:", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires: tense", request, StringComparison.Ordinal);
        if (preferences is null)
        {
            Assert.DoesNotContain("Prefers", request, StringComparison.Ordinal);
        }
        else
        {
            Assert.EndsWith(preferences, request, StringComparison.Ordinal);
        }

        // The prompt the request is read under says a preference never decides satisfies.
        Assert.Contains("A preference (a mood, a tone, a steer) is NEVER a reason for \"partial\" or \"no\"", OpenRouterAskJudge.SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV3-RET-07: "more like the third one" pivots to the card and drops the earlier topic, so the
    /// judge had nothing to say yes TO. It is now told the facts of each title the request wants
    /// something like, and the prompt says what likeness earns a yes. Varied over one reference, two,
    /// a reference with no keywords, and none.
    /// </summary>
    [Fact]
    public void The_judge_is_told_what_each_reference_is_like()
    {
        var state = Shown((A, 1, 1), (B, 1, 2), (C, 1, 3)) with { Soft = new SoftPreferences([], [], [C]) };
        var like = new List<AskReelEngine.LikeFacts>
        {
            new("Shanghai", 2010, ["thriller", "drama"], ["spy", "shanghai, china", "1940s", "japanese occupation", "casino", "a", "b", "c", "dropped"]),
            new("Heat", 1995, ["crime"], []),
        };

        var request = AskReelEngine.JudgeRequest("more like the third one", state, like);
        var none = AskReelEngine.JudgeRequest("more like the third one", state);

        Assert.Contains("Like: Shanghai (2010) — genres thriller, drama; keywords spy, shanghai, china, 1940s, japanese occupation, casino, a, b, c", request, StringComparison.Ordinal);
        Assert.DoesNotContain("dropped", request, StringComparison.Ordinal);
        Assert.Contains("Like: Heat (1995) — genres crime", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Like:", none, StringComparison.Ordinal);
        Assert.Contains("Points at: Title 3 (2020)", none, StringComparison.Ordinal);
        Assert.Contains("When the request lists Like titles", OpenRouterAskJudge.SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV3-ASK-D: TMDB's TV taxonomy has no thriller (or horror, romance, history, music) genre, so
    /// the judge ruled every spy series "partial" because "its listed genres omit thriller". It now
    /// reads the genre the way eligibility does — from the stand-in keywords — marked inferred. Varied:
    /// a film with the same keyword is NOT inferred a thriller (its taxonomy would have tagged it), a
    /// show already tagged by an alias is, and a show with no stand-in keyword gets nothing.
    /// </summary>
    [Theory]
    [InlineData(MediaType.Show, new[] { "crime", "drama" }, new[] { "spy", "suspenseful" }, new[] { "crime", "drama", "thriller (inferred)" })]
    [InlineData(MediaType.Movie, new[] { "crime", "drama" }, new[] { "spy", "suspenseful" }, new[] { "crime", "drama" })]
    [InlineData(MediaType.Show, new[] { "drama", "suspense" }, new[] { "spy" }, new[] { "drama", "suspense", "thriller (inferred)" })]
    [InlineData(MediaType.Show, new[] { "drama" }, new[] { "spy", "cia" }, new[] { "drama", "thriller (inferred)" })]
    [InlineData(MediaType.Show, new[] { "drama" }, new[] { "family", "small town" }, new[] { "drama" })]
    [InlineData(MediaType.Show, new[] { "drama" }, new[] { "spy thriller", "love triangle" }, new[] { "drama", "romance (inferred)", "thriller (inferred)" })]
    public void The_judge_reads_a_genre_the_tv_taxonomy_cannot_tag_from_its_keywords(
        MediaType media, string[] genres, string[] keywords, string[] expected)
    {
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            Name = "Covert Affairs",
            TraktSlug = string.Empty,
            Genres = [.. genres],
            Keywords = [.. keywords],
            CreatedAt = DateTime.UtcNow,
        };

        Assert.Equal(expected, TitleFacts.JudgeGenres(title));
        Assert.Contains("A genre marked \"(inferred)\"", OpenRouterAskJudge.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("A title's medium (film or series) is never a reason", OpenRouterAskJudge.SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV4 (refuter, round 4, the HREV3-ASK-D residual): a TV spy series whose keywords carry no
    /// thriller stand-in was still ruled partial for "spy thrillers" — Alias, Nikita, Restless and Burn
    /// Notice ("no thriller evidence is given"), Spooks ("the listed genres omit thriller"). Espionage
    /// now infers thriller for the judge on TV. The rows are the snapshot's own genres and keywords
    /// (reel-2026-10-23). Varied: the same keywords on a film infer nothing (its taxonomy tags
    /// thriller), and a spy COMEDY or animation (Archer, Chuck, Get Smart) is not inferred a thriller.
    /// </summary>
    [Theory]
    [InlineData("Alias", MediaType.Show, new[] { "action", "adventure", "drama" }, new[] { "central intelligence agency (cia)", "martial arts", "double agent", "undercover agent", "female spy" }, true)]
    [InlineData("Nikita", MediaType.Show, new[] { "drama", "action", "adventure" }, new[] { "spy", "strong woman", "secret government organization", "female spy" }, true)]
    [InlineData("Restless", MediaType.Show, new[] { "drama", "war" }, new[] { "spy", "based on novel or book", "miniseries", "british secret service" }, true)]
    [InlineData("Burn Notice", MediaType.Show, new[] { "action", "adventure", "drama", "crime" }, new[] { "miami, florida", "espionage", "caper", "private investigator", "ex spy" }, true)]
    [InlineData("Spooks", MediaType.Show, new[] { "drama", "action", "adventure", "crime" }, new[] { "england", "spy", "mi5" }, true)]
    [InlineData("Spooks", MediaType.Movie, new[] { "drama", "action", "crime" }, new[] { "england", "spy", "mi5" }, false)]
    [InlineData("Archer", MediaType.Show, new[] { "comedy", "action", "adventure", "animation" }, new[] { "spy", "central intelligence agency (cia)", "secret agent" }, false)]
    [InlineData("Chuck", MediaType.Show, new[] { "action", "adventure", "comedy", "drama" }, new[] { "central intelligence agency (cia)", "secret agent", "nerd" }, false)]
    [InlineData("Get Smart", MediaType.Show, new[] { "comedy", "crime", "mystery" }, new[] { "spy", "secret agent", "sitcom" }, false)]
    [InlineData("Ciao", MediaType.Show, new[] { "drama" }, new[] { "special agent", "spyglass", "mission" }, false)]
    public void A_tv_spy_series_reads_as_a_thriller_to_the_judge_unless_it_is_a_comedy(
        string name, MediaType media, string[] genres, string[] keywords, bool inferred)
    {
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            Name = name,
            TraktSlug = string.Empty,
            Genres = [.. genres],
            Keywords = [.. keywords],
            CreatedAt = DateTime.UtcNow,
        };

        var read = TitleFacts.JudgeGenres(title);

        Assert.Equal(inferred, read.Contains("thriller (inferred)"));
        Assert.Equal(genres, read.Take(genres.Length));
        Assert.Equal(inferred ? 1 : 0, read.Count(g => g.StartsWith("thriller", StringComparison.Ordinal)));
    }

    /// <summary>HREV3-ASK-A: the planner is shown a question about a title it NAMES, with the name as a reference name.</summary>
    [Fact]
    public void The_planner_is_shown_a_named_title_question()
    {
        Assert.Contains(
            "\"is Heat violent?\" -> {act:ItemQuestion, referenceNames:[Heat], tools:[title_facts], question:\"is it violent?\"}",
            OpenRouterAskPlanner.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("or the title a question is about", OpenRouterAskPlanner.SystemPrompt, StringComparison.Ordinal);
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
    // HREV1-ASK-02 / RET-04: the live planner answered "any" for all three, and the turns returned shows.
    [InlineData("samurai films", MediaScope.Any, MediaScope.Movies)]
    [InlineData("submarine movies", MediaScope.Any, MediaScope.Movies)]
    [InlineData("Liam Neeson movies", MediaScope.Any, MediaScope.Movies)]
    [InlineData("zombie series", MediaScope.Any, MediaScope.Shows)]
    // "any" stands where the words name both, neither, or one in addition.
    [InlineData("ninja movies or shows", MediaScope.Any, MediaScope.Any)]
    [InlineData("something cosy", MediaScope.Any, MediaScope.Any)]
    [InlineData("include shows too", MediaScope.Any, MediaScope.Any)]
    [InlineData("shows as well please", MediaScope.Any, MediaScope.Any)]
    // A medium ruled out asks for the other one; a word ruled out is never the one asked for.
    [InlineData("samurai, no tv shows", MediaScope.Any, MediaScope.Movies)]
    [InlineData("not a movie, a series", MediaScope.Any, MediaScope.Shows)]
    [InlineData("samurai, no tv shows", null, MediaScope.Movies)]
    // "show" in "show me" / "show us" is the verb, never the medium: the defect's own requests
    // phrased as a command, a mood that names no medium ("any" stands), and a noun "show" that
    // happens to be followed by "me" (a determiner owns it).
    [InlineData("show me samurai films", MediaScope.Any, MediaScope.Movies)]
    [InlineData("can you show me Liam Neeson movies", MediaScope.Any, MediaScope.Movies)]
    [InlineData("show me submarine movies", null, MediaScope.Movies)]
    [InlineData("Show us some kaiju films please", MediaScope.Any, MediaScope.Movies)]
    [InlineData("show me something feel-good", MediaScope.Any, MediaScope.Any)]
    [InlineData("show me something feel-good", null, null)]
    [InlineData("please show us a show about chefs", MediaScope.Any, MediaScope.Shows)]
    [InlineData("a cosy show me and my wife can watch", MediaScope.Any, MediaScope.Shows)]
    [InlineData("show me samurai, no tv shows", MediaScope.Any, MediaScope.Movies)]
    [InlineData("I loved those films, show me more", null, MediaScope.Movies)]
    public void The_users_own_media_word_survives_a_plan_that_left_media_unset(string message, MediaScope? planned, MediaScope? expected)
    {
        var plan = Plan(TurnAct.NewRequest, ConversationStateDelta.None with { Media = planned });
        Assert.Equal(expected, PlanSanitizer.Clean(plan, message, ConversationState.Empty, FilterRuleSet.FromRows([])).Delta.Media);
    }

    /// <summary>
    /// HREV2-ASK-01: "romantic comedies" was planned once as romantic + comedy + romance (145 titles,
    /// because "romantic" as a concept also demanded the tag) and once as comedy + romance (every
    /// romantic comedy). A concept whose words only restate a genre the plan requires is dropped, so
    /// both plans search, count and name the same shelf. Varied: a concept naming a genre the plan does
    /// not require, a concept naming no genre, and a phrase naming two required genres.
    /// </summary>
    [Theory]
    [InlineData("romantic", "comedy,romance", "")]
    [InlineData("romantic comedies", "comedy,romance", "")]
    [InlineData("romantic", "comedy", "romantic")]
    [InlineData("ninja", "action", "ninja")]
    [InlineData("heist,horror", "horror", "heist")]
    public void A_concept_that_only_restates_a_planned_genre_is_dropped(string concepts, string genres, string kept)
    {
        var plan = Plan(TurnAct.NewRequest, ConversationStateDelta.None with
        {
            Concepts = [.. concepts.Split(',')],
            Genres = [.. genres.Split(',')],
        });

        var cleaned = PlanSanitizer.Clean(plan, $"{concepts.Replace(',', ' ')} {genres.Replace(',', ' ')}", ConversationState.Empty, FilterRuleSet.FromRows([]));

        Assert.Equal(kept, string.Join(',', cleaned.Delta.Concepts));
        Assert.Equal(genres.Split(','), cleaned.Delta.Genres);
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

    /// <summary>
    /// HREV2-ASK-01: the supply is counted over the AND of every hard concept and the person, so the
    /// words that name it are all of them — "romantic comedies" planned as romantic, comedy and
    /// romance was described as "the 145 romantic titles". Varied over the plans the live runs
    /// produced (three concepts with a restated genre, two genre concepts in either order), a phrase
    /// that already holds another concept, a person with a concept, a person alone, and nothing.
    /// </summary>
    [Theory]
    [InlineData("romantic|-;comedy|comedy;romance|romance", null, "romantic comedy")]
    [InlineData("romance|romance;comedy|comedy", null, "romance comedy")]
    [InlineData("comedy|comedy;romance|romance", null, "comedy romance")]
    [InlineData("romantic comedy|-;comedy|comedy", null, "romantic comedy")]
    [InlineData("comedy|comedy;romantic comedy|-", null, "romantic comedy")]
    [InlineData("heist|-;bank robbery|-", null, "heist bank robbery")]
    [InlineData("ninja|-", null, "ninja")]
    [InlineData("thriller|thriller", "Liam Neeson", "Liam Neeson thriller")]
    [InlineData("-", "Liam Neeson", "Liam Neeson")]
    [InlineData("-", null, null)]
    public void The_shelf_names_every_constraint_the_supply_intersects(string concepts, string? person, string? expected)
    {
        Assert.Equal(expected, AskReelTopic.Shelf(Hard(concepts, person, MediaScope.Movies)));
    }

    /// <summary>
    /// HREV2-ASK-01, through the composer: the none-found counts and the picks line name the shelf the
    /// count was taken over, for a two-concept request and a person-plus-concept request alike.
    /// </summary>
    [Theory]
    [InlineData("romantic|-;comedy|comedy;romance|romance", null,
        "There are no romantic comedy movies left for you. Of the 145 romantic comedy movies in the catalogue with at least 50 votes, you've seen 140 and your conditions hold out 5.")]
    [InlineData("thriller|thriller", "Liam Neeson",
        "There are no Liam Neeson thriller movies left for you. Of the 145 Liam Neeson thriller movies in the catalogue with at least 50 votes, you've seen 140 and your conditions hold out 5.")]
    public void A_none_found_reply_names_the_whole_shelf_it_counted(string concepts, string? person, string expected)
    {
        var hard = Hard(concepts, person, MediaScope.Movies);
        var supply = new SupplyReport(145, 140, 0, 5, 0, 0, 0, [], 0, 50, null, true);
        var reply = new TurnComposer().Compose(Context(FinalMode.NoneFound) with
        {
            Topic = AskReelTopic.Shelf(hard),
            Floor = 0m,
            Supply = supply,
        });
        Assert.Equal(expected, reply.Reply);
    }

    [Theory]
    [InlineData("romance|romance;comedy|comedy", null, "4 romance comedy movies that fit what you asked.")]
    [InlineData("thriller|thriller", "Liam Neeson", "4 Liam Neeson thriller movies that fit what you asked.")]
    public void A_picks_reply_names_the_whole_shelf(string concepts, string? person, string expected)
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with
        {
            Topic = AskReelTopic.Shelf(Hard(concepts, person, MediaScope.Movies)),
            Picks = 4,
        });
        Assert.Equal(expected, reply.Reply);
    }

    [Fact]
    public void A_supply_answer_names_every_concept_it_counted()
    {
        var hard = Hard("romance|romance;comedy|comedy", "Hugh Grant", MediaScope.Movies);
        var report = new SupplyReport(30, 20, 0, 0, 10, 0, 0, [], 0, 50, null, true);
        Assert.Equal(
            "You have 10 unseen Hugh Grant romance comedy movies left inside your conditions; you've seen 20.",
            AskReelEngine.SupplyAnswer(report, hard));
    }

    /// <summary>
    /// HREV2-RET-01: the judge's order is the retrieved candidates the bar lets through, then the
    /// supply's top-up ('t'), then the retrieved candidates with no prediction ('u'), then those under
    /// the bar ('h') — and each group keeps its own order. Varied over every mix, with and without a
    /// top-up, and with no candidate of a group at all.
    /// </summary>
    [Theory]
    [InlineData("hhhhcc", 0, "cchhhh")]
    [InlineData("chch", 0, "cchh")]
    [InlineData("cccc", 0, "cccc")]
    [InlineData("hhhh", 0, "hhhh")]
    [InlineData("hhhh", 2, "tthhhh")]
    [InlineData("huhcuc", 3, "cctttuuhh")]
    [InlineData("uuu", 1, "tuuu")]
    [InlineData("", 2, "tt")]
    public void The_judge_order_puts_what_can_become_a_pick_first_and_keeps_each_groups_order(string retrieved, int topUps, string expected)
    {
        var candidates = retrieved.Select((kind, i) => (Kind: kind, Candidate: new Candidate(Guid.NewGuid(), 1d - (i / 100d), null, ["keyword:x"]))).ToList();
        var topUp = Enumerable.Range(0, topUps).Select(_ => new Candidate(Guid.NewGuid(), 0d, null, ["keyword:x"])).ToList();
        var kinds = candidates.ToDictionary(c => c.Candidate.TitleId, c => c.Kind);
        foreach (var t in topUp)
        {
            kinds[t.TitleId] = 't';
        }

        var ordered = AskReelEngine.OrderForJudge(
            candidates.Select(c => c.Candidate).ToList(),
            topUp,
            id => kinds[id] switch
            {
                'c' => AskReelEngine.JudgeTier.Clears,
                'u' => AskReelEngine.JudgeTier.Unscored,
                _ => AskReelEngine.JudgeTier.Under,
            });

        Assert.Equal(expected, string.Concat(ordered.Select(c => kinds[c.TitleId])));
        foreach (var kind in "chu")
        {
            Assert.Equal(
                candidates.Where(c => c.Kind == kind).Select(c => c.Candidate).ToList(),
                ordered.Where(c => kinds[c.TitleId] == kind).ToList());
        }

        Assert.Equal(topUp, ordered.Where(c => kinds[c.TitleId] == 't').ToList());
    }

    /// <summary>"words|genre,genre;words|-" → concepts ("-" for none), plus an optional cast person.</summary>
    private static HardConstraints Hard(string concepts, string? person, MediaScope media) =>
        HardConstraints.None with
        {
            Concepts = concepts == "-"
                ? []
                : [.. concepts.Split(';').Select(c => c.Split('|')).Select(p =>
                    new ConceptTerm(p[0], [], p[1] == "-" ? [] : [.. p[1].Split(',')]))],
            People = person is null ? [] : [new PersonConstraint(person, null, PersonRole.Cast)],
            Media = media,
        };

    [Fact]
    public void Only_one_clarifying_question_is_asked_per_conversation()
    {
        var plan = Plan(TurnAct.NewRequest) with { ClarifyQuestion = "Movies or shows?", ClarifyOptions = ["Movies", "Shows"] };
        Assert.NotNull(PlanSanitizer.Clean(plan, "something good", ConversationState.Empty, FilterRuleSet.FromRows([])).ClarifyQuestion);
        Assert.Null(PlanSanitizer.Clean(
            plan, "something good", ConversationState.Empty with { ClarifiedOnce = true }, FilterRuleSet.FromRows([])).ClarifyQuestion);
    }

    /// <summary>
    /// HREV1-ASK-06: a plan no model wrote keeps the user's topic as the phrase they wrote ("time
    /// travel movies" searched "travel"; "Liam Neeson movies" required "liam" AND "neeson") and takes
    /// the medium their words name. Varied over both fallbacks, a negated clause and a found-footage lead.
    /// </summary>
    [Theory]
    [InlineData("time travel movies", new[] { "time travel" }, MediaScope.Movies)]
    [InlineData("Liam Neeson movies", new[] { "liam neeson" }, MediaScope.Movies)]
    [InlineData("found footage horror shows", new[] { "found footage horror" }, MediaScope.Shows)]
    [InlineData("heist films, no gore", new[] { "heist" }, MediaScope.Movies)]
    [InlineData("kaiju or mecha", new[] { "kaiju", "mecha" }, null)]
    public void A_plan_no_model_wrote_searches_the_users_phrases_in_their_medium(string message, string[] concepts, MediaScope? media)
    {
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.ProviderUnavailable, null, false), LlmCallMeta.None);
        foreach (var plan in new[] { FallbackPlans.CarriedForward(failed, message, ConversationState.Empty), FallbackPlans.Keyless(message, ConversationState.Empty) })
        {
            Assert.Equal(concepts, plan.Delta.Concepts);
            Assert.Equal(media, plan.Delta.Media);
        }
    }

    /// <summary>A plan no model wrote reads "show me" as the verb too (HREV1-ASK-02 follow-on).</summary>
    [Theory]
    [InlineData("show me time travel movies", MediaScope.Movies)]
    [InlineData("show me something feel-good", null)]
    [InlineData("show us zombie series", MediaScope.Shows)]
    public void A_plan_no_model_wrote_reads_show_me_as_the_verb(string message, MediaScope? media)
    {
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.ProviderUnavailable, null, false), LlmCallMeta.None);
        Assert.Equal(media, FallbackPlans.CarriedForward(failed, message, ConversationState.Empty).Delta.Media);
        Assert.Equal(media, FallbackPlans.Keyless(message, ConversationState.Empty).Delta.Media);
    }

    [Theory]
    [InlineData("I want some time travel", new[] { "time travel" })]
    [InlineData("more like the second one you showed me first time round", new string[0])]
    [InlineData("none of the above are ninja movies. I want ninja movies", new[] { "ninja" })]
    // HREV3-ASK-E: "dark" and "gritty" are moods, read as moods, never part of the topic every title must carry.
    [InlineData("dark gritty heist, no superheroes", new[] { "heist" })]
    public void The_keyless_phrases_are_the_positive_words_as_written(string message, string[] expected) =>
        Assert.Equal(expected, AskReelIntentSanitizer.KeylessPhrases(message));

    [Fact]
    public void A_carried_forward_refine_takes_the_medium_the_words_name()
    {
        var state = ConversationState.Empty with { Version = 2, Hard = HardConstraints.None with { Concepts = [new ConceptTerm("ninja", [], [])] } };
        var plan = FallbackPlans.CarriedForward(
            PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None), "now just shows", state);
        Assert.Equal(MediaScope.Shows, plan.Delta.Media);
        Assert.Equal(TurnAct.Refine, plan.Act);
    }

    // ── Pointers and the similar_to pivot ─────────────────────────────────────────────────

    /// <summary>
    /// HREV1-ASK-03: a search plan whose message points at a card it resolved to no handle is asked
    /// back. Varied over the acts (a question and a new request are not), and a resolved pointer.
    /// </summary>
    [Theory]
    [InlineData(TurnAct.Refine, false, "more like the third one", 3)]
    [InlineData(TurnAct.CritiqueItem, false, "less dark than the second one", 2)]
    [InlineData(TurnAct.Refine, true, "more like the third one", null)]
    [InlineData(TurnAct.Refine, false, "more of those", null)]
    [InlineData(TurnAct.NewRequest, false, "the first one was great, now heist movies", null)]
    [InlineData(TurnAct.ItemQuestion, false, "how long is the second one?", null)]
    public void A_search_pointer_nothing_resolved_is_unresolved(TurnAct act, bool resolved, string message, int? expected)
    {
        var plan = resolved ? Plan(act, null, new TurnHandle(1, 3)) : Plan(act);
        Assert.Equal(expected, PlanSanitizer.UnresolvedPointer(plan, message));
    }

    [Fact]
    public void A_fallback_plan_resolves_the_pointer_to_the_newest_answers_card()
    {
        var state = Shown((A, 1, 1), (B, 1, 2), (C, 1, 3));
        var keyless = PlanSanitizer.PointFallback(FallbackPlans.Keyless("more like the second one", state), "more like the second one", state);
        Assert.Equal([new TurnHandle(1, 2)], keyless.References);
        Assert.Null(PlanSanitizer.UnresolvedPointer(keyless, "more like the second one"));
    }

    /// <summary>
    /// HREV1-RET-02: after "Liam Neeson movies", "more like the third one" searches near that card —
    /// the person stops being a hard filter, unless the message restates him; the medium and the
    /// user's negations still hold. Varied: a restated person, a restated topic, a critique, a
    /// message that does not ask for likeness, and a reference nothing resolved.
    /// </summary>
    [Theory]
    [InlineData(TurnAct.Refine, "more like the third one", true, 0, 0)]
    [InlineData(TurnAct.Refine, "something similar to the third one with Neeson", true, 1, 0)]
    [InlineData(TurnAct.Refine, "more spy films like the third one", true, 0, 1)]
    [InlineData(TurnAct.CritiqueItem, "less dark than the third one", true, 1, 1)]
    [InlineData(TurnAct.Refine, "more of the third one's kind but older", true, 1, 1)]
    [InlineData(TurnAct.Refine, "more like the third one", false, 1, 1)]
    public void More_like_a_card_pivots_to_it_and_drops_the_earlier_filters_unless_restated(
        TurnAct act, string message, bool resolves, int people, int concepts)
    {
        var state = Shown((A, 1, 1), (B, 1, 2), (C, 1, 3)) with
        {
            Hard = new HardConstraints(
                [new ConceptTerm("spy", [], [])], [new PersonConstraint("Liam Neeson", null, PersonRole.Cast)], null, null, MediaScope.Movies),
            UserNegations = ["gore"],
        };
        var plan = resolves ? Plan(act, null, new TurnHandle(1, 3)) : Plan(act);

        var next = ConversationStateReducer.SimilarPivot(ConversationStateReducer.Apply(state, plan, MediaScope.Any), plan, message);

        Assert.Equal(people, next.Hard.People.Count);
        Assert.Equal(concepts, next.Hard.Concepts.Count);
        Assert.Equal(MediaScope.Movies, next.Hard.Media);
        Assert.Equal(["gore"], next.UserNegations);
        if (resolves && act == TurnAct.Refine)
        {
            Assert.Contains(C, next.Soft.ReferenceTitleIds);
        }
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

    /// <summary>HREV1-ASK-06: a conversation's first turn has no "before" to carry.</summary>
    [Theory]
    [InlineData("time travel", "I couldn't fully read that, so I searched for time travel. 3 time travel movies that fit what you asked.")]
    [InlineData(null, "I couldn't fully read that, so this is a broad search. 3 movies that fit what you asked.")]
    public void A_first_turn_fallback_never_says_as_before(string? topic, string expected)
    {
        var reply = new TurnComposer().Compose(Context(FinalMode.Picks) with { Topic = topic, Picks = 3, CarriedForward = true, FirstTurn = true });
        Assert.Equal(expected, reply.Reply);
        Assert.DoesNotContain("before", reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// HREV1-ASK-01 / RET-01: a none-found reply claims a check only for verdicts that came back.
    /// The Liam Neeson follow-up (person credits, nothing judged, all four already shown), a judge
    /// that failed outright, a turn with no model, and a turn that judged some of the tagged titles.
    /// </summary>
    [Theory]
    [InlineData(4, 4, 0, 0, 0, "All 4 unseen Liam Neeson titles inside your conditions were already shown in this conversation.")]
    [InlineData(3, 0, 0, 0, 7, "You have 3 unseen Liam Neeson titles tagged that way, but I couldn't check them against what you asked this time. 7 titles I couldn't check against your request are below, marked as unchecked.")]
    [InlineData(1, 0, 0, 0, 1, "You have 1 unseen Liam Neeson title tagged that way, but I couldn't check it against what you asked this time. 1 title I couldn't check against your request is below, marked as unchecked.")]
    [InlineData(6, 2, 16, 1, 0, "2 of your unseen Liam Neeson titles were already shown in this conversation. You have 4 other unseen Liam Neeson titles tagged that way. I checked 1 of them and it isn't really a match; the other 3 weren't among the 16 titles I checked this time.")]
    [InlineData(4, 0, 16, 4, 0, "You have 4 unseen Liam Neeson titles tagged that way, but none of the ones I checked is really a match.")]
    public void A_none_found_reply_claims_only_the_checks_that_happened(
        int clearing, int shownHere, int judged, int checkedOfTagged, int notChecked, string expected)
    {
        var supply = new SupplyReport(clearing, 0, 0, 0, clearing, 0, 0, [], 0, 50, null, true, shownHere);
        var reply = new TurnComposer().Compose(Context(FinalMode.NoneFound) with
        {
            Topic = "Liam Neeson",
            Media = MediaScope.Any,
            Floor = 0m,
            Supply = supply,
            Judged = judged,
            CheckedOfTagged = checkedOfTagged,
            ShownHere = shownHere,
            Unchecked = notChecked,
            JudgeFailed = judged == 0 && notChecked > 0,
        });
        Assert.Equal(expected, reply.Reply);
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

    /// <summary>
    /// HREV2-ASK-03: v1's person reply is the v2 composer's sentence, counts only, so the default
    /// engine and the new one say the same thing. Varied over role, media, the singular and no seen —
    /// and (HREV3-ASK-C) over credits the bar held back, which both count the same way.
    /// </summary>
    [Theory]
    [InlineData(PersonRole.Director, MediaScope.Movies, 3, 9, 0)]
    [InlineData(PersonRole.Cast, MediaScope.Movies, 1, 1, 0)]
    [InlineData(PersonRole.Cast, MediaScope.Shows, 2, 0, 0)]
    [InlineData(PersonRole.Any, MediaScope.Any, 4, 39, 0)]
    [InlineData(PersonRole.Cast, MediaScope.Movies, 3, 39, 3)]
    [InlineData(PersonRole.Cast, MediaScope.Movies, 2, 0, 1)]
    public void The_v1_person_reply_is_the_composers_sentence(PersonRole role, MediaScope media, int unseen, int seen, int underBar)
    {
        MediaType[] types = media switch
        {
            MediaScope.Movies => [MediaType.Movie],
            MediaScope.Shows => [MediaType.Show],
            _ => [],
        };
        var composed = new TurnComposer().Compose(Context(FinalMode.Picks) with
        {
            Topic = null,
            Media = media,
            Picks = unseen,
            UnderBar = underBar,
            Person = new PersonConstraint("Liam Neeson", null, role),
            PersonSeen = seen,
        });

        var v1 = LiveSearchExpansionService.PersonReply(new PersonQuery("Liam Neeson", role), unseen, seen, types, underBar, 6.0m);

        Assert.Equal(composed.Reply, v1);
        Assert.DoesNotContain("Here are films", v1, StringComparison.Ordinal);
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

    /// <summary>
    /// HREV1-ASK-05: the plan's output was mostly empty fields the strict schema forced ("minYear":null,
    /// "clarifyOptions":[] …). The schema now requires only the act and the planner omits what is empty;
    /// a missing field reads as empty or unchanged, and a genre outside the slug list is dropped.
    /// </summary>
    [Fact]
    public void The_planner_writes_only_what_carries_something_and_reads_a_missing_field_as_empty()
    {
        using var doc = JsonDocument.Parse("""{"act":"Refine","negations":["gore"],"genres":["horror","not-a-genre"]}""");
        var plan = OpenRouterAskPlanner.Parse(doc.RootElement, PlanSource.Llm, LlmCallMeta.None);

        Assert.Equal(TurnAct.Refine, plan.Act);
        Assert.Equal(["gore"], plan.Delta.Negations);
        Assert.Equal(["horror"], plan.Delta.Genres);
        Assert.Null(plan.Delta.Media);
        Assert.Null(plan.Delta.Years);
        Assert.Empty(plan.Delta.Concepts);
        Assert.Empty(plan.References);
        Assert.Null(plan.ClarifyQuestion);

        var schema = JsonSerializer.SerializeToElement(OpenRouterAskPlanner.Schema()).GetProperty("json_schema");
        Assert.False(schema.GetProperty("strict").GetBoolean());
        Assert.Equal(["act"], schema.GetProperty("schema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("Omit EVERY other field that would be empty", OpenRouterAskPlanner.SystemPrompt, StringComparison.Ordinal);
        // "show me" is the verb: the media line must not send "show me something feel-good" to tv.
        Assert.Contains("never the verb in \"show me\"/\"show us\"", OpenRouterAskPlanner.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("\"show me samurai films\" -> {act:NewRequest, concepts:[samurai], media:movie}", OpenRouterAskPlanner.SystemPrompt, StringComparison.Ordinal);
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
        // No app filter reads "none", never "any" — the planner copied "any" into media (HREV1-ASK-02).
        Assert.Contains("\"mediaFilter\":\"none\"", messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("never copy it into media", messages[0].Content, StringComparison.Ordinal);
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

        // A "no" carries no why (it is never shown, and output tokens are what a batch waits on): the
        // verdict still counts as judged.
        Assert.Contains("When satisfies is \"no\", why is an empty string", OpenRouterAskJudge.SystemPrompt, StringComparison.Ordinal);
        using var silentNo = JsonDocument.Parse("""{"results":[{"id":"1","satisfies":"no","fit":0.1,"why":"","evidence":"genre: drama"}]}""");
        var no = OpenRouterAskJudge.Parse(silentNo.RootElement, input)[A];
        Assert.True(no.Judged);
        Assert.Equal(Satisfies.No, no.Satisfies);
        Assert.Null(no.Why);
    }

    [Theory]
    [InlineData("It's rated R for violence. It runs 110 minutes. It stars Keanu Reeves.", "It's rated R for violence. It runs 110 minutes.")]
    [InlineData("**Yes** — it is rated R.", "Yes — it is rated R.")]
    [InlineData("   ", null)]
    // HREV1-ASK-07: an answer that cites Reel's inputs instead of answering is not an answer.
    [InlineData("The runtime fact says it is 197 minutes long.", null)]
    [InlineData("The keywords and overview identify Blade of the 47 Ronin as a samurai film; the facts don't identify it as a ninja film.", null)]
    [InlineData("On the Beach runs 134 minutes.", "On the Beach runs 134 minutes.")]
    // The idiom "in fact" cites nothing, and an answer using it is kept; a citation still is not.
    [InlineData("In fact, Heat runs 170 minutes.", "In fact, Heat runs 170 minutes.")]
    [InlineData("Yes, it is rated R, in fact for strong violence.", "Yes, it is rated R, in fact for strong violence.")]
    [InlineData("As a matter of fact, Heat does not feature ninjas.", "As a matter of fact, Heat does not feature ninjas.")]
    [InlineData("It in fact doesn't feature ninjas.", "It in fact doesn't feature ninjas.")]
    [InlineData("All the President's Men dramatises the facts of the Watergate case.", "All the President's Men dramatises the facts of the Watergate case.")]
    [InlineData("Despite the fact that it is long, Heat runs 170 minutes.", "Despite the fact that it is long, Heat runs 170 minutes.")]
    [InlineData("It in fact shows a heist.", "It in fact shows a heist.")]
    [InlineData("In fact, the facts say it runs 99 minutes.", null)]
    [InlineData("According to the facts, Page Eight runs 99 minutes.", null)]
    [InlineData("The given facts list no rating.", null)]
    [InlineData("Facts provided don't mention a rating.", null)]
    [InlineData("Its keywords mark it as a heist film.", null)]
    [InlineData("Based on the data, it runs 99 minutes.", null)]
    public void An_answer_is_two_plain_sentences_at_most(string raw, string? expected) =>
        Assert.Equal(expected, OpenRouterAskAnswerer.Clean(raw));

    /// <summary>HREV1-ASK-07: the answerer is told to name the title and never to cite its inputs, and is not primed with "facts".</summary>
    [Fact]
    public void The_answerer_names_the_title_and_never_its_sources()
    {
        Assert.Contains("Name the title and state the answer directly", OpenRouterAskAnswerer.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("never write \"fact\", \"facts\", \"keyword\", \"keywords\", \"overview\"", OpenRouterAskAnswerer.SystemPrompt, StringComparison.Ordinal);
        var sheet = new TitleFactSheet(A, "On the Beach", 1959, MediaType.Movie, ["drama"], ["nuclear war"], "After the war.", 134, "PG", ["Stanley Kramer"], [], [], null, false);
        var prompt = OpenRouterAskAnswerer.UserPrompt(new AnswerInput("how long is the second one?", sheet));
        Assert.Contains("\"title\":{\"name\":\"On the Beach\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"facts\"", prompt, StringComparison.Ordinal);
    }

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

    /// <summary>
    /// HREV1-ASK-05: a first batch judged ahead of the turn (beside the plan) is never sent again —
    /// the turn's batch takes its verdicts from the cache, or waits for the call still in flight — and
    /// is judged afresh only for a different request or where the early call failed.
    /// </summary>
    [Theory]
    [InlineData("in flight", 1, 3)]
    [InlineData("finished", 1, 3)]
    [InlineData("other request", 2, 4)]
    [InlineData("early call failed", 2, 4)]
    public async Task A_first_batch_judged_ahead_is_never_sent_twice(string scenario, int firstBatchSends, int calls)
    {
        var titles = Enumerable.Range(0, 16).Select(_ => Title(Guid.NewGuid())).ToList();
        var account = Guid.NewGuid();
        var model = new GatedJudgeModel(holdFirst: scenario == "in flight", failFirst: scenario == "early call failed");
        var judge = new RequestJudge(model, new MemoryCache(new MemoryCacheOptions()), NullLogger<RequestJudge>.Instance);

        var early = judge.PrejudgeAsync(new JudgeInput(account, "Request: samurai films. Media: movie", "persona", titles), CancellationToken.None);
        if (scenario != "in flight")
        {
            await early;
        }

        var request = scenario == "other request" ? "Request: samurai films. Media: tv" : "Request: samurai films.  media: MOVIE";
        var reading = Task.Run(async () =>
        {
            var read = new List<JudgeVerdict>();
            await foreach (var v in judge.JudgeAsync(new JudgeInput(account, request, "persona", titles), CancellationToken.None))
            {
                read.Add(v);
            }

            return read;
        });
        if (scenario == "in flight")
        {
            // The turn's own batches are out before the early call returns: the first batch must wait.
            await model.Sent(RequestJudge.MaxJudged).WaitAsync(TimeSpan.FromSeconds(10));
            model.Release();
        }

        var verdicts = await reading;
        Assert.Equal(scenario != "early call failed", await early is not null);
        Assert.Equal(titles.Select(t => t.TitleId).Order(), verdicts.Select(v => v.TitleId).Order());
        Assert.All(verdicts, v => Assert.True(v.Judged));
        Assert.All(titles.Take(RequestJudge.FirstBatchSize), t => Assert.Equal(firstBatchSends, model.SendsOf(t.TitleId)));
        Assert.All(titles.Skip(RequestJudge.FirstBatchSize), t => Assert.Equal(1, model.SendsOf(t.TitleId)));
        Assert.Equal(calls, model.Calls);
    }

    /// <summary>
    /// A judge model that counts every title it is sent, can hold its first call until released, or
    /// fail it. <see cref="Sent"/> completes once that many titles have been sent in total.
    /// </summary>
    private sealed class GatedJudgeModel(bool holdFirst, bool failFirst) : IRequestJudgeModel
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, int> _sends = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private int _sent;

        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "gated";

        public string CacheSalt => "gated";

        public int SendsOf(Guid id)
        {
            lock (_gate)
            {
                return _sends.GetValueOrDefault(id);
            }
        }

        public Task Sent(int count)
        {
            lock (_gate)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_sent >= count)
                {
                    done.SetResult();
                }
                else
                {
                    _waiters.Add((count, done));
                }

                return done.Task;
            }
        }

        public void Release() => _release.TrySetResult();

        public async Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            bool first;
            lock (_gate)
            {
                first = Calls++ == 0;
                foreach (var t in input.Titles)
                {
                    _sends[t.TitleId] = _sends.GetValueOrDefault(t.TitleId) + 1;
                }

                _sent += input.Titles.Count;
                foreach (var (count, done) in _waiters.Where(w => _sent >= w.Count))
                {
                    done.TrySetResult();
                }
            }

            if (first && holdFirst)
            {
                await _release.Task.WaitAsync(ct);
            }

            if (first && failFirst)
            {
                return new JudgeBatchResult(input.Titles.Select(t => JudgeVerdict.Unjudged(t.TitleId)).ToList(), LlmCallMeta.None, true);
            }

            return new JudgeBatchResult(
                input.Titles.Select(t => new JudgeVerdict(t.TitleId, Satisfies.Yes, 0.8, "You get samurai.", "keyword: samurai", ResultSource.Llm, ResultSource.Llm)).ToList(),
                LlmCallMeta.None,
                false);
        }
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
    }

    /// <summary>
    /// R1-12: "no conditions" never claims the whole catalogue. With no rows the English-only default
    /// still binds, and with every language allowed the account's own footprint still does — so the
    /// line names what is held back instead of "every unseen title in the catalogue".
    /// </summary>
    [Fact]
    public void No_conditions_names_the_default_and_the_footprint_never_the_whole_catalogue()
    {
        var none = AskReelEngine.ConditionsAnswer(FilterRuleSet.FromRows([]), null);
        Assert.Equal(
            "You have no standing conditions set. Reel's default still applies: English-language titles only, minus the ones "
            + "you've seen, saved, dropped or marked not interested.",
            none);

        // Varied: the measured report changes nothing — a no-rules answer quotes no pool count.
        Assert.Equal(none, AskReelEngine.ConditionsAnswer(
            FilterRuleSet.FromRows([]), new FilterCostReport(5319, 4000, [new FilterCostRule("Language", "en", 900)], [])));

        var everyLanguage = FilterRuleSet.FromRows(
            [new ContentFilter { Id = Guid.NewGuid(), Kind = FilterKind.Language, Value = FilterRuleSet.AllLanguages }]);
        Assert.Equal(
            "You have no standing conditions set. Nothing else is held back except titles you've seen, saved, dropped or marked not interested.",
            AskReelEngine.ConditionsAnswer(everyLanguage, null));
        Assert.DoesNotContain("every unseen title", none, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1-12: a maturity ceiling is a condition. Describe names it, so an account whose ONLY condition
    /// is a ceiling is never told it has none — and, because the cost meter cannot apply a ceiling, no
    /// pool count or marginal is quoted beside one.
    /// </summary>
    [Theory]
    [InlineData("PG-13")]
    [InlineData("R")]
    public void A_maturity_ceiling_is_named_and_never_quoted_a_pool_the_meter_cannot_count(string ceiling)
    {
        var bare = AskReelEngine.ConditionsAnswer(FilterRuleSet.FromRows([]), null, ceiling);
        Assert.Equal($"Your standing conditions: max maturity {ceiling}.", bare);
        Assert.DoesNotContain("no standing conditions", bare, StringComparison.Ordinal);

        var excluded = FilterRuleSet.FromRows([new ContentFilter { Id = Guid.NewGuid(), Kind = FilterKind.ExcludeGenre, Value = "comedy" }]);
        var cost = new FilterCostReport(5319, 421, [new FilterCostRule("ExcludeGenre", "comedy", 812)], []);
        Assert.Equal(
            $"Your standing conditions: excludes: comedy; max maturity {ceiling}.",
            AskReelEngine.ConditionsAnswer(excluded, cost, ceiling));
        Assert.Equal($"excludes: comedy; max maturity {ceiling}", AskReelStandingFilters.Describe(excluded, ceiling));
        Assert.Equal("excludes: comedy", AskReelStandingFilters.Describe(excluded));
    }

    /// <summary>R1-12: the bar is a condition in the editor, so a bar-only account is told about it.</summary>
    [Fact]
    public void The_bar_is_named_as_a_condition_with_what_it_does()
    {
        Assert.Equal(
            "Your only standing condition is your bar of 6.5: titles predicted under it, or not scored yet, are shown apart "
            + "rather than as picks. Reel's default still applies: English-language titles only, minus the ones you've seen, saved, "
            + "dropped or marked not interested.",
            AskReelEngine.ConditionsAnswer(FilterRuleSet.FromRows([]), null, bar: 6.5m));

        var standing = FilterRuleSet.FromRows([new ContentFilter { Id = Guid.NewGuid(), Kind = FilterKind.MinYear, Value = "2000" }]);
        Assert.Equal(
            "Your standing conditions: min year 2000. Your bar is 7.0: titles predicted under it, or not scored yet, are shown apart "
            + "rather than as picks. They leave 12 unseen titles open to you, out of 50 in the catalogue.",
            AskReelEngine.ConditionsAnswer(standing, new FilterCostReport(50, 12, [], []), bar: 7m));
    }
}
