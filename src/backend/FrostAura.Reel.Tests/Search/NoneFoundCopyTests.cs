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

    [Fact]
    public void Unscored_titles_and_near_misses_are_counted()
    {
        var supply = Ninja() with { Eligible = 2, Unscored = 2 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(supply, "ninja", MediaScope.Movies, [], 3, 6m));

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
