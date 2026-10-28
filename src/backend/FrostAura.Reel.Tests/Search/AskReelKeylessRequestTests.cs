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
    [InlineData("a tense heist thriller from the 2010s, nothing too long", new[] { "heist thriller" }, new[] { "tense" }, 2010, 2019)]
    [InlineData("80s sci-fi movies", new[] { "sci" }, new string[0], 1980, 1989)]
    [InlineData("a cozy feel-good movie", new string[0], new[] { "cozy", "feel good" }, null, null)]
    [InlineData("dark gritty heist, no superheroes", new[] { "heist" }, new[] { "dark", "gritty" }, null, null)]
    [InlineData("quiet character dramas", new[] { "character dramas" }, new[] { "quiet" }, null, null)]
    [InlineData("something genuinely funny", new string[0], new[] { "funny" }, null, null)]
    [InlineData("romantic comedies", new[] { "romantic comedies" }, new string[0], null, null)]
    [InlineData("good samurai films before 2000", new[] { "samurai" }, new string[0], null, 1999)]
    [InlineData("zombie movies since 2015", new[] { "zombie" }, new string[0], 2015, null)]
    [InlineData("war movies from 1940 to 1945", new[] { "war" }, new string[0], 1940, 1945)]
    [InlineData("spy thrillers, nothing from the 80s", new[] { "spy thrillers" }, new string[0], null, null)]
    [InlineData("time travel movies", new[] { "time travel" }, new string[0], null, null)]
    [InlineData("heist movies", new[] { "heist" }, new string[0], null, null)]
    public void A_plan_no_model_wrote_reads_moods_and_years_as_what_they_are(
        string message, string[] concepts, string[] moods, int? from, int? to)
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
        Assert.Equal(["funnier"], plan.Delta.Moods);
    }

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
