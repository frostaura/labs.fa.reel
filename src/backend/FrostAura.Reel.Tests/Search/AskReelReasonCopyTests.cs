using System.Globalization;
using FrostAura.Reel.Application.Search;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The reason line's copy, pinned verbatim — the sentences a person reads under the answer.
///
/// <para>A-11: the old line read "24 picks ranked for you, from 60 pulled · widened to find enough ·
/// only 4 picks cleared your 6.0 bar, so it was relaxed". "Pulled" and "widened" describe a pipeline
/// the user never sees, and "relaxed" described the account's own bar being ignored for 20 of 24
/// cards. The floor no longer relaxes, and every clause now names something on the screen.</para>
/// </summary>
public class AskReelReasonCopyTests
{
    [Fact]
    public void A_plain_answer_counts_its_picks_in_the_users_words() =>
        Assert.Equal("8 picks for you", AskReelReason.Build(new TurnCounts(8, 0, 0), 0m));

    [Fact]
    public void One_pick_is_singular() =>
        Assert.Equal("1 pick for you", AskReelReason.Build(new TurnCounts(1, 0, 0), 0m));

    [Fact]
    public void An_outside_channel_is_named_and_never_counted_as_picks() =>
        Assert.Equal(
            "2 picks match what you asked · 3 close but outside what you asked, shown separately",
            AskReelReason.Build(new TurnCounts(2, 3, 0), 0m));

    [Fact]
    public void The_floor_tray_quotes_the_bar_at_one_decimal() =>
        Assert.Equal(
            "6 picks for you · 4 held back by your 6.0 bar — shown below",
            AskReelReason.Build(new TurnCounts(6, 0, 4), 6m));

    [Fact]
    public void The_near_miss_tray_is_named_after_the_floor_tray() =>
        Assert.Equal(
            "5 picks for you · 1 held back by your 7.5 bar — shown below · 2 close but not quite — tucked away below",
            AskReelReason.Build(new TurnCounts(5, 0, 1, 2), 7.5m));

    [Fact]
    public void Nothing_in_the_grid_says_so_and_still_accounts_for_the_trays() =>
        Assert.Equal(
            "no picks matched what you asked · 1 held back by your 6.0 bar — shown below",
            AskReelReason.Build(new TurnCounts(0, 0, 1), 6m));

    /// <summary>D10: cards the judge never ruled on are counted apart, never as matching.</summary>
    [Fact]
    public void Unchecked_cards_in_the_grid_are_never_counted_as_matches() =>
        Assert.Equal(
            "2 picks match what you asked · 1 close but outside what you asked, shown separately · 4 more not checked against what you asked",
            AskReelReason.Build(new TurnCounts(2, 1, 0, Unchecked: 4), 0m));

    /// <summary>A turn whose judge failed outright claims no verdict at all — not even "none matched".</summary>
    [Fact]
    public void A_turn_nobody_could_check_says_so_instead_of_none_matched() =>
        Assert.Equal(
            "8 not checked against what you asked — shown below · 2 held back by your 6.0 bar — shown below",
            AskReelReason.Build(new TurnCounts(0, 0, 2, UncheckedTray: 8), 6m));

    [Fact]
    public void An_empty_turn_is_the_empty_constant() =>
        Assert.Equal(AskReelReason.Empty, AskReelReason.Build(new TurnCounts(0, 0, 0), 6m));

    /// <summary>Invariant whatever the host culture: a comma decimal separator would be a wrong answer.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void The_bar_is_invariant_under_any_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(
                "6 picks for you · 1 held back by your 6.5 bar — shown below",
                AskReelReason.Build(new TurnCounts(6, 0, 1), 6.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
