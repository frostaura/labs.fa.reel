using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The request the judge rules on (D2): the message in its conversation, never the bare follow-up.
/// </summary>
public class AskReelJudgeRequestTests
{
    private static readonly SearchIntent FeelGood = new(
        ["comedy", "family"], ["feel-good"], [MediaType.Movie], "feel-good", null, null,
        ExcludeKeywords: ["superhero"]);

    [Fact]
    public void A_follow_up_carries_the_earlier_asks_and_the_resolved_reading()
    {
        var text = AskReelJudgeRequest.Compose(
            "no superheroes please",
            [
                new ChatTurn("user", "something cosy"),
                new ChatTurn("assistant", "Cosy it is."),
                new ChatTurn("user", "feel-good movie"),
                new ChatTurn("assistant", "Here are some feel-good films. [Shown: T1.1 Paddington (2014)]"),
            ],
            FeelGood,
            []);

        Assert.Equal(
            "no superheroes please\n"
            + "Earlier in this conversation the user asked: “something cosy”; “feel-good movie”\n"
            + "Read in context, they want a movie; genres: comedy, family; about: feel-good; nothing that is: superhero.",
            text);
    }

    [Fact]
    public void Only_the_last_two_asks_are_quoted_and_long_ones_are_clipped()
    {
        var longAsk = new string('x', 400);
        var text = AskReelJudgeRequest.Compose(
            "more",
            [new ChatTurn("user", "first"), new ChatTurn("user", "second"), new ChatTurn("user", longAsk)],
            new SearchIntent([], [], [], string.Empty, 2000, 2010),
            ["Heat"]);

        Assert.DoesNotContain("“first”", text, StringComparison.Ordinal);
        Assert.Contains("“second”", text, StringComparison.Ordinal);
        Assert.Contains(new string('x', AskReelJudgeRequest.EarlierAskChars - 1) + "…”", text, StringComparison.Ordinal);
        Assert.Contains("The user wants something like: Heat", text, StringComparison.Ordinal);
        Assert.Contains("released 2000 or later; released 2010 or earlier", text, StringComparison.Ordinal);
        Assert.Contains("they want a movie or a TV show", text, StringComparison.Ordinal);
    }

    /// <summary>The same words in two conversations are two requests — and so two cache keys.</summary>
    [Fact]
    public void The_same_words_in_different_conversations_are_different_requests()
    {
        var intent = new SearchIntent([], [], [], "more", null, null);
        var ninja = AskReelJudgeRequest.Compose("more please", [new ChatTurn("user", "ninja movies")], intent, []);
        var romcom = AskReelJudgeRequest.Compose("more please", [new ChatTurn("user", "romcoms")], intent, []);

        Assert.NotEqual(AskReelJudge.NormalizeQuery(ninja), AskReelJudge.NormalizeQuery(romcom));
        Assert.Equal(ninja, AskReelJudgeRequest.Compose("more please", [new ChatTurn("user", "ninja movies")], intent, []));
    }
}
