using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// HREV3-ASK-E: a plan no model wrote reads a mood as a mood and a year phrase as a year window. With
/// the planner down, "tense heist movies from the last ten years" searched the hard topics "tense
/// heist" and "ten years", the supply read 0, and three unrelated titles were shown. Pinned over the
/// golden set's first-turn messages that carry a mood, a year phrase or filler, through BOTH fallbacks
/// (the planner failing, and no model at all), at a fixed current year.
/// </summary>
public class AskReelKeylessRequestTests
{
    private const int Now = 2026;

    [Theory]
    [InlineData("tense heist movies from the last ten years", new[] { "heist" }, new[] { "tense" }, 2016, 2026)]
    [InlineData("heist movies from the last 10 years", new[] { "heist" }, new string[0], 2016, 2026)]
    // HREV4-RET-02: a genre word ending a topic phrase is the genre the topic sits in.
    [InlineData("a tense heist thriller from the 2010s, nothing too long", new[] { "heist" }, new[] { "tense" }, 2010, 2019, "thriller")]
    [InlineData("80s sci-fi movies", new[] { "sci" }, new string[0], 1980, 1989)]
    [InlineData("a cozy feel-good movie", new string[0], new[] { "cozy", "feel good" }, null, null)]
    [InlineData("dark gritty heist, no superheroes", new[] { "heist" }, new[] { "dark", "gritty" }, null, null)]
    [InlineData("quiet character dramas", new[] { "character" }, new[] { "quiet" }, null, null, "drama")]
    [InlineData("something genuinely funny", new string[0], new[] { "funny" }, null, null)]
    [InlineData("romantic comedies", new[] { "romantic comedies" }, new string[0], null, null)]
    [InlineData("good samurai films before 2000", new[] { "samurai" }, new string[0], null, 1999)]
    [InlineData("zombie movies since 2015", new[] { "zombie" }, new string[0], 2015, null)]
    [InlineData("war movies from 1940 to 1945", new[] { "war" }, new string[0], 1940, 1945)]
    [InlineData("spy thrillers, nothing from the 80s", new[] { "spy" }, new string[0], null, null, "thriller")]
    [InlineData("time travel movies", new[] { "time travel" }, new string[0], null, null)]
    [InlineData("heist movies", new[] { "heist" }, new string[0], null, null)]
    public void A_plan_no_model_wrote_reads_moods_and_years_as_what_they_are(
        string message, string[] concepts, string[] moods, int? from, int? to, string? genre = null)
    {
        var expectedYears = from is null && to is null ? null : new YearRange(from, to);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        foreach (var plan in new[]
                 {
                     FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now),
                     FallbackPlans.Keyless(message, ConversationState.Empty, Now),
                 })
        {
            Assert.Equal(concepts, plan.Delta.Concepts);
            Assert.Equal(moods, plan.Delta.Moods);
            Assert.Equal(expectedYears, plan.Delta.Years);
            Assert.Equal(genre is null ? [] : [genre], plan.Delta.Genres);
        }
    }

    /// <summary>
    /// A carried-forward follow-up keeps adding only soft words — and a year phrase in it narrows the
    /// years rather than becoming three moods ("last", "five", "years").
    /// </summary>
    [Fact]
    public void A_carried_forward_follow_up_reads_its_year_phrase_as_years_not_moods()
    {
        var state = ConversationState.Empty with
        {
            Version = 2,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm("heist", [], [])] },
        };
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None);

        var plan = FallbackPlans.CarriedForward(failed, "from the past five years, funnier", state, Now);

        Assert.Equal(TurnAct.Refine, plan.Act);
        Assert.Empty(plan.Delta.Concepts);
        Assert.Equal(new YearRange(2021, 2026), plan.Delta.Years);
        // A comparison is an attribute steer (HREV4-ASK-05), never a mood or a topic.
        Assert.Empty(plan.Delta.Moods);
        Assert.Equal([new AttributeDirection("humor", AttributeTrend.More)], plan.Delta.Directions);
    }

    /// <summary>
    /// HREV4-ASK-05 / HREV4-RET-02: the model-less reader on the follow-ups and shapes that misread —
    /// through BOTH fallbacks, in a conversation that has shown cards. A question about a card is an
    /// item question; "like &lt;title&gt;" is a reference; comparisons are steers; connectives and
    /// generic nouns are never topics; a negation is the whole phrase; a genre ending a phrase is a genre.
    /// </summary>
    [Theory]
    [InlineData("is the first one violent?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("how long is the second one?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("is Heat violent?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("more like the first one", TurnAct.Refine, "", "", "", "", "", null)]
    [InlineData("like John Wick but funnier", TurnAct.NewRequest, "", "", "", "john wick", "humor:More", null)]
    [InlineData("movies like Inception", TurnAct.NewRequest, "", "", "", "inception", "", MediaScope.Movies)]
    [InlineData("something similar to Heat but darker", TurnAct.NewRequest, "", "", "", "heat", "darkness:More", null)]
    [InlineData("I'd like a heist movie", TurnAct.NewRequest, "heist", "", "", "", "", MediaScope.Movies)]
    [InlineData("older stuff is fine", TurnAct.Refine, "", "", "", "", "", null)]
    [InlineData("what about movies instead", TurnAct.Refine, "", "", "", "", "", MediaScope.Movies)]
    [InlineData("shows instead", TurnAct.Refine, "", "", "", "", "", MediaScope.Shows)]
    [InlineData("love stories", TurnAct.NewRequest, "", "romance", "", "", "", null)]
    [InlineData("no walking dead", TurnAct.Refine, "", "", "walking dead", "", "", null)]
    [InlineData("zombie shows, no walking dead or vampires", TurnAct.NewRequest, "zombie", "", "walking dead|vampires", "", "", MediaScope.Shows)]
    [InlineData("spy thrillers", TurnAct.NewRequest, "spy", "thriller", "", "", "", null)]
    [InlineData("true crime documentaries", TurnAct.NewRequest, "true crime", "documentary", "", "", "", null)]
    [InlineData("romantic comedies", TurnAct.NewRequest, "romantic comedies", "", "", "", "", null)]
    [InlineData("time travel movies", TurnAct.NewRequest, "time travel", "", "", "", "", MediaScope.Movies)]
    public void The_model_less_reader_reads_follow_ups_references_comparisons_and_negations_as_what_they_are(
        string message, TurnAct act, string concepts, string genres, string negations, string references, string directions, MediaScope? media)
    {
        var state = ConversationState.Empty with
        {
            Version = 2,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm("zombie", [], [])], Media = MediaScope.Shows },
            LastPresented =
            [
                new PresentedRef(Guid.NewGuid(), new TurnHandle(2, 1), "Heat", 1995, Domain.Catalog.MediaType.Movie),
                new PresentedRef(Guid.NewGuid(), new TurnHandle(2, 2), "Ronin", 1998, Domain.Catalog.MediaType.Movie),
            ],
        };
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        var keyless = FallbackPlans.Keyless(message, state, Now);
        var carried = FallbackPlans.CarriedForward(failed, message, state, Now);

        Assert.Equal(act, keyless.Act);
        Assert.Equal(List(concepts), keyless.Delta.Concepts);
        Assert.Equal(List(genres), keyless.Delta.Genres);
        Assert.Equal(List(negations), keyless.Delta.Negations);
        Assert.Equal(List(references), keyless.Delta.ReferenceNames);
        Assert.Equal(List(directions), keyless.Delta.Directions.Select(d => $"{d.Attribute}:{d.Trend}"));
        Assert.Equal(media, keyless.Delta.Media);

        // The carried-forward follow-up reads the same words; on a later turn its topics are soft only.
        Assert.Equal(act == TurnAct.ItemQuestion ? TurnAct.ItemQuestion : TurnAct.Refine, carried.Act);
        Assert.Empty(carried.Delta.Concepts);
        Assert.Equal(List(negations), carried.Delta.Negations);
        Assert.Equal(List(references), carried.Delta.ReferenceNames);
        Assert.Equal(List(directions), carried.Delta.Directions.Select(d => $"{d.Attribute}:{d.Trend}"));
        foreach (var plan in new[] { keyless, carried })
        {
            Assert.DoesNotContain(plan.Delta.Concepts.Concat(plan.Delta.Moods), w =>
                w is "funnier" or "older" or "fine" or "instead" or "stories" or "first" or "violent" or "inception" or "john wick");
            if (act == TurnAct.ItemQuestion)
            {
                Assert.Equal([AskTool.TitleFacts], plan.Tools.Select(t => t.Tool));
            }
        }
    }

    /// <summary>A first-turn carried-forward plan keeps its topics hard, exactly as the keyless one does.</summary>
    [Theory]
    [InlineData("spy thrillers", "spy", "thriller")]
    [InlineData("love stories", "", "romance")]
    [InlineData("found footage horror", "found footage", "horror")]
    public void A_first_turn_fallback_keeps_the_reading_hard(string message, string concepts, string genres)
    {
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var plan in new[] { FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now), FallbackPlans.Keyless(message, ConversationState.Empty, Now) })
        {
            Assert.Equal(TurnAct.NewRequest, plan.Act);
            Assert.Equal(List(concepts), plan.Delta.Concepts);
            Assert.Equal(List(genres), plan.Delta.Genres);
        }
    }

    /// <summary>
    /// HREV4-ASK-04: v1's degraded reading (the interpreter's heuristic) drops the floor word inside a
    /// topic; its keywords are widened back to the phrase the user wrote. Genre words stay genres.
    /// </summary>
    [Theory]
    [InlineData("time travel movies", "travel", "time travel")]
    [InlineData("time loop movies", "loop", "time loop")]
    [InlineData("found footage horror", "footage", "found footage")]
    [InlineData("shark horrors", "shark", "shark")]
    [InlineData("spy thrillers", "spy", "spy")]
    [InlineData("Liam Neeson movies", "liam|neeson", "liam neeson")]
    [InlineData("heist movies", "heist", "heist")]
    [InlineData("ninja movies", "samurai", "samurai")]
    public void A_degraded_v1_topic_is_the_users_whole_phrase(string message, string heuristic, string expected) =>
        Assert.Equal(
            expected.Split('|'),
            AskReelDegradedTopic.WholePhrases(heuristic.Split('|'), message, Now));

    [Theory]
    [InlineData("movies from last year", 2025, 2025)]
    [InlineData("the last decade of horror", 2016, 2026)]
    [InlineData("the '90s thrillers", 1990, 1999)]
    [InlineData("00s comedies", 2000, 2009)]
    [InlineData("heist films in 2019", 2019, 2019)]
    [InlineData("heist films after 2015", 2016, null)]
    public void Every_written_year_window_is_read(string message, int? from, int? to)
    {
        var request = AskReelIntentSanitizer.ReadKeylessRequest(message, Now);

        Assert.Equal(new YearRange(from, to), request.Years);
        Assert.DoesNotContain(request.Phrases, p => p.Any(char.IsDigit));
    }
}
