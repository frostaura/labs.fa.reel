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
            "6 picks for you · 4 under your 6.0 bar — shown below",
            AskReelReason.Build(new TurnCounts(6, 0, 4), 6m));

    [Fact]
    public void The_near_miss_tray_is_named_after_the_floor_tray() =>
        Assert.Equal(
            "5 picks for you · 1 under your 7.5 bar — shown below · 2 close but not quite — tucked away below",
            AskReelReason.Build(new TurnCounts(5, 0, 1, 2), 7.5m));

    [Fact]
    public void Nothing_in_the_grid_says_so_and_still_accounts_for_the_trays() =>
        Assert.Equal(
            "no picks matched what you asked · 1 under your 6.0 bar — shown below",
            AskReelReason.Build(new TurnCounts(0, 0, 1), 6m));

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
                "6 picks for you · 1 under your 6.5 bar — shown below",
                AskReelReason.Build(new TurnCounts(6, 0, 1), 6.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
