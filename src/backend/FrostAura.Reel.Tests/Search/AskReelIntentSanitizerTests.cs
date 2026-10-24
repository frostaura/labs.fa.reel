using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The server's correction of a model-read intent, pinned on the live failures that produced it:
/// "no superheroes please" read as a positive keyword (A-9, C NEW-1), "more like the second one" read
/// as the keywords ["second", "one"] (A-3), and standing conditions restated as turn exclusions
/// (B-12, M NEW-3).
/// </summary>
public class AskReelIntentSanitizerTests
{
    private static SearchIntent Intent(
        IReadOnlyList<string>? keywords = null, IReadOnlyList<string>? excludeGenres = null,
        IReadOnlyList<string>? excludeKeywords = null) =>
        new([], keywords ?? [], [], "free text", null, null, ExcludeGenres: excludeGenres, ExcludeKeywords: excludeKeywords);

    [Theory]
    [InlineData("no superheroes please", "no superheroes please")]
    [InlineData("not horror", "something tense, not horror")]
    [InlineData("without ghosts", "a thriller without ghosts")]
    [InlineData("second", "more like the second one")]
    [InlineData("one", "more like the second one")]
    [InlineData("first one", "is the first one violent?")]
    [InlineData("none of the above are ninja movies", "none of the above are ninja movies")]
    [InlineData("movies", "ninja movies")]
    public void A_keyword_that_is_not_a_pull_signal_is_dropped(string keyword, string raw) =>
        Assert.True(AskReelIntentSanitizer.RejectedKeyword(keyword, raw));

    [Theory]
    [InlineData("ninja", "ninja movies")]
    [InlineData("ninja", "ninja")]
    [InlineData("first contact", "first contact stories")]
    [InlineData("heist", "a chaotic heist")]
    [InlineData("ninja movies", "ninja movies")]
    public void A_real_topic_survives(string keyword, string raw) =>
        Assert.False(AskReelIntentSanitizer.RejectedKeyword(keyword, raw));

    [Fact]
    public void Standing_conditions_are_stripped_from_the_turns_exclusions_in_either_list_and_number()
    {
        var standing = FilterRuleSet.Create(
            excludeGenres: ["comedy"], includeGenres: null, excludeKeywords: ["musical"], languages: null,
            minCrowdRating: null, minYear: null, maxYear: null);

        var clean = AskReelIntentSanitizer.Sanitize(
            Intent(["heist"], excludeGenres: ["comedy", "horror"], excludeKeywords: ["comedy", "musicals", "ghosts"]),
            standing,
            "a heist, no horror or ghosts");

        Assert.Equal(["heist"], clean.Keywords);
        Assert.Equal(["horror"], clean.ExcludeGenres);
        Assert.Equal(["ghosts"], clean.ExcludeKeywords);
    }

    [Theory]
    [InlineData("no superheroes please", "")]
    [InlineData("feel-good, no superheroes please", "feel good")]
    [InlineData("a thriller without ghosts but with heart", "a thriller heart")]
    [InlineData("horror, not gory", "horror")]
    public void The_positive_half_of_a_sentence_carries_no_negated_word(string raw, string expected) =>
        Assert.Equal(expected, AskReelIntentSanitizer.PositiveText(raw, ["superheroes", "ghosts", "gory"]));
}
