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

    /// <summary>
    /// IREV1-SYS-04: the keyless reading — what a keyword search may run on, and what it must drop.
    /// "no superheroes please" names nothing to search FOR; a pointer question leaves only filler;
    /// a negation inside a request keeps the request and rules the rest out — including a word no
    /// vocabulary lists ("marvel"), since a keyless card that trips it is dropped, never captioned.
    /// </summary>
    [Theory]
    [InlineData("no superheroes please", "", "superheroes")]
    [InlineData("how long is the second one?", "long", "")]
    [InlineData("heist movies but no superheroes", "heist", "superheroes")]
    [InlineData("I don't want horror, give me a heist", "heist", "horror")]
    [InlineData("samurai films without ghosts or zombies", "samurai", "ghosts zombies")]
    [InlineData("nothing set in space", "", "space")]
    [InlineData("superhero movies but no marvel", "superhero", "marvel")]
    [InlineData("not too long please", "", "long")]
    [InlineData("ninja ninja movies", "ninja", "")]
    public void A_keyless_reading_searches_only_what_was_asked_for(string raw, string positive, string negated)
    {
        var reading = AskReelIntentSanitizer.ReadKeyless(raw);

        Assert.Equal(positive, string.Join(' ', reading.Positive));
        Assert.Equal(negated, string.Join(' ', reading.Negated));
    }

    /// <summary>
    /// Acceptance D2: the model wrote the standing MinYear=2000 into the ninja and samurai intents, so
    /// the supply counted post-2000 titles only (43 of a true 62) and never offered the year rule. A
    /// bound equal to the standing one is dropped unless the user's words name the year.
    /// </summary>
    [Theory]
    [InlineData(2000, 2000, "ninja movies", true)]
    [InlineData(2000, 2000, "samurai films", true)]
    [InlineData(2000, 2000, "ninja movies from 2000 on", false)]
    [InlineData(2000, 2000, "2000s ninja movies", false)]
    [InlineData(2000, 2000, "'00s ninja movies", false)]
    [InlineData(1990, 1990, "ninja movies from the 90s", false)]
    [InlineData(1990, 1990, "ninja movies from the '90s", false)]
    [InlineData(1990, 1990, "ninja movies from the 90's", false)]
    [InlineData(2010, 2000, "ninja movies", false)]
    [InlineData(2000, null, "ninja movies", false)]
    [InlineData(null, 2000, "ninja movies", false)]
    [InlineData(2000, 2000, "ninja movies, 20000 leagues", true)]
    public void A_year_bound_that_only_restates_the_standing_one_is_dropped(int? asked, int? standing, string raw, bool copied) =>
        Assert.Equal(copied, AskReelIntentSanitizer.CopiedStandingYear(asked, standing, raw));

    [Fact]
    public void Sanitize_drops_a_copied_standing_window_and_keeps_an_asked_one()
    {
        var standing = FilterRuleSet.Create(
            excludeGenres: null, includeGenres: null, excludeKeywords: null, languages: null,
            minCrowdRating: null, minYear: 2000, maxYear: 2020);

        var copied = AskReelIntentSanitizer.Sanitize(
            Intent(["ninja"]) with { MinYear = 2000, MaxYear = 2020 }, standing, "ninja movies");
        Assert.Null(copied.MinYear);
        Assert.Null(copied.MaxYear);

        var asked = AskReelIntentSanitizer.Sanitize(
            Intent(["ninja"]) with { MinYear = 2010, MaxYear = 2020 }, standing, "ninja movies since 2010, up to 2020");
        Assert.Equal(2010, asked.MinYear);
        Assert.Equal(2020, asked.MaxYear);
    }
}
