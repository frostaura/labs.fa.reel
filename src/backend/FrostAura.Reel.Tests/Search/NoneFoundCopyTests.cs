using System.Globalization;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The none-found reply, pinned verbatim. Every number comes from the supply report; nothing comes
/// from a model, and no seen title is ever named (the FD-3 default: prose gives counts only).
/// </summary>
public class NoneFoundCopyTests
{
    /// <summary>The founder's ninja request, shaped like the SQL truth the assessment counted.</summary>
    private static SupplyReport Ninja(params ConditionKnockout[] knockouts) =>
        new(
            MatchingInCatalogue: 11, AlreadySeen: 6, SavedOrDismissed: 1, ExcludedByConditions: 4, Eligible: 0,
            BelowFloor: 0, Unscored: 0, Knockouts: knockouts, LowVoteMatches: 9, MinVotes: 50,
            PredictedFloor: 6m, KnockoutsMeasured: true);

    private static ConditionKnockout Knockout(string label, int unlock) =>
        new(FilterKind.Language, "en", null, label, unlock, [new TitleRef(Guid.NewGuid(), "Shinobi", 2005, MediaType.Movie)]);

    [Fact]
    public void Nothing_left_names_the_partition_and_the_relaxations()
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja(Knockout("English only", 11), Knockout("2000 onwards", 3), Knockout("crowd 6.0 or better", 2)),
            "ninja", MediaScope.Movies, [], 0, 6m));

        Assert.Equal(
            "There are no ninja movies left for you. "
            + "Of the 11 ninja movies in the catalogue with at least 50 votes, you've seen 6, 1 is saved or dismissed and your conditions hold out 4. "
            + "Turning off “English only” would unlock 11; turning off “2000 onwards” would unlock 3.",
            text);
    }

    [Fact]
    public void Matches_under_the_bar_are_named_because_they_are_on_screen_below()
    {
        var supply = Ninja() with { Eligible = 1, BelowFloor = 1 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, "samurai", MediaScope.Movies, ["Blade of the 47 Ronin"], 0, 6m));

        Assert.Contains("Under your 6.0 bar: Blade of the 47 Ronin — shown below.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Acceptance D5: the live samurai reply said "There are no samurai movies left for you" above a
    /// tray holding one. With titles under the bar, the honest sentence is that none clears it.
    /// </summary>
    [Fact]
    public void Titles_under_the_bar_are_never_called_none_left()
    {
        var supply = Ninja() with { Eligible = 1, BelowFloor = 1 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, "samurai", MediaScope.Movies, ["Blade of the 47 Ronin"], 0, 6m));

        Assert.StartsWith("None of your unseen samurai movies clears your 6.0 bar.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left for you", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// D4: titles this conversation already showed were excluded before the judge saw anything, so
    /// they are named as shown — never "unseen … none of the ones I checked is a match".
    /// </summary>
    [Fact]
    public void Titles_already_shown_here_are_named_as_shown_not_as_rejected()
    {
        var all = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 6 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 6));
        Assert.Equal("All 6 unseen ninja movies inside your conditions were already shown in this conversation.", all);

        var one = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 1 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 1));
        Assert.Equal("The one unseen ninja movie inside your conditions was already shown in this conversation.", one);

        var some = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 8 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 6));
        Assert.Equal(
            "6 of your unseen ninja movies were already shown in this conversation. "
            + "You have 2 other unseen ninja movies tagged that way, but none of the ones I checked is really a match.",
            some);
    }

    /// <summary>D1: with the judge down, no sentence claims a verdict, and the unchecked tray is announced.</summary>
    [Fact]
    public void A_failed_judge_claims_no_verdict_and_points_at_the_unchecked_tray()
    {
        var tagged = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 3 }, "ninja", MediaScope.Movies, [], 0, 6m, Unchecked: 3, JudgedAny: false));
        Assert.Equal(
            "You have 3 unseen ninja movies tagged that way, but I couldn't check them against what you asked this time. "
            + "3 titles I couldn't check against your request are below, marked as unchecked.",
            tagged);
        Assert.DoesNotContain("is really a match", tagged, StringComparison.Ordinal);

        var untagged = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            null, "feel-good", MediaScope.Movies, [], 0, 0m, Unchecked: 1, JudgedAny: false));
        Assert.Equal(
            "I couldn't check what I found against what you asked this time. "
            + "1 title I couldn't check against your request is below, marked as unchecked.",
            untagged);
    }

    /// <summary>D3: a match held back only because it has no score is never said to be under the bar.</summary>
    [Fact]
    public void A_match_with_no_score_is_named_as_unscored_not_as_under_the_bar()
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            null, null, MediaScope.Movies, [], 0, 6m, UnscoredHeld: ["Arrival", "Dune"]));
        Assert.EndsWith("Arrival and Dune have no score from your model yet, so your 6.0 bar holds them back — shown below.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Under your", text, StringComparison.Ordinal);
    }

    /// <summary>Acceptance "weak": the ninja follow-up repeated the first reply word for word.</summary>
    [Fact]
    public void A_reply_identical_to_the_previous_one_says_it_looked_again()
    {
        const string reply = "There are no ninja movies left for you.";
        Assert.Equal($"{NoneFoundCopy.AgainLead} {reply}", NoneFoundCopy.Again(reply, reply));
        Assert.Equal($"{NoneFoundCopy.AgainLead} {reply}", NoneFoundCopy.Again(reply, $"{NoneFoundCopy.AgainLead} {reply}"));
        Assert.Equal(reply, NoneFoundCopy.Again(reply, "Here are three samurai films."));
        Assert.Equal(reply, NoneFoundCopy.Again(reply, null));
    }

    [Fact]
    public void Unscored_titles_and_near_misses_are_counted()
    {
        var supply = Ninja() with { Eligible = 2, Unscored = 2 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(supply, "ninja", MediaScope.Movies, [], 3, 6m));

        Assert.StartsWith("None of your unseen ninja movies clears your 6.0 bar.", text, StringComparison.Ordinal);
        Assert.Contains("2 have no score from your model yet.", text, StringComparison.Ordinal);
        Assert.Contains("3 close-but-not-quite titles are tucked away below.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tagged_titles_the_judge_rejected_are_not_called_none_left()
    {
        var supply = Ninja() with { Eligible = 3 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(supply, "ninja", MediaScope.Movies, [], 0, 6m));

        Assert.StartsWith(
            "You have 3 unseen ninja movies tagged that way, but none of the ones I checked is really a match.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("left for you", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// No count stands behind the topic words, so they are not repeated back: the live "like John
    /// Wick but funnier" turn read "I couldn't find any witty action playful tone movies".
    /// </summary>
    [Theory]
    [InlineData(MediaScope.Movies)]
    [InlineData(MediaScope.Any)]
    public void No_supply_still_answers_honestly_without_repeating_the_models_words(MediaScope media) =>
        Assert.Equal(
            "Nothing I found matches what you asked closely enough to show.",
            NoneFoundCopy.Compose(new NoneFoundCopy.Facts(null, "witty action", media, [], 0, 0m)));

    [Fact]
    public void No_concept_says_nothing_matched()
    {
        Assert.Equal(
            "Nothing I found matches what you asked closely enough to show.",
            NoneFoundCopy.Compose(new NoneFoundCopy.Facts(null, null, MediaScope.Any, [], 0, 0m)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void The_copy_is_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
                Ninja() with { Eligible = 1, BelowFloor = 1 }, "ninja", MediaScope.Movies, ["Blade"], 0, 6.5m));
            Assert.Contains("6.5 bar", text, StringComparison.Ordinal);
            Assert.Contains("Of the 11 ninja movies", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
