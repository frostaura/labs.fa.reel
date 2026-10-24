using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The interim gate's placement table — pure, so it is pinned exhaustively here rather than through
/// a trained model whose predictions no test can dictate.
/// </summary>
public class AskReelGateTests
{
    [Theory]
    [InlineData(Satisfies.Yes, 7.0, 6.0, AskReelGate.Placement.Pick)]
    [InlineData(Satisfies.Yes, 5.0, 6.0, AskReelGate.Placement.BelowFloor)]
    [InlineData(Satisfies.Partial, 9.0, 6.0, AskReelGate.Placement.NearMiss)]
    [InlineData(Satisfies.Partial, 3.0, 6.0, AskReelGate.Placement.NearMiss)]
    [InlineData(Satisfies.No, 9.9, 0.0, AskReelGate.Placement.Dropped)]
    [InlineData(null, 7.0, 6.0, AskReelGate.Placement.UnjudgedPick)]
    [InlineData(null, 5.0, 6.0, AskReelGate.Placement.BelowFloor)]
    public void A_card_lands_where_its_verdict_and_the_bar_put_it(
        Satisfies? satisfies, double predicted, double floor, AskReelGate.Placement expected) =>
        Assert.Equal(expected, AskReelGate.Place(satisfies, (decimal)predicted, (decimal)floor));

    /// <summary>
    /// The floor never relaxes (C-7, N+2): every card under the bar goes to the tray however few
    /// clear it — the old rule dropped the bar for a turn with fewer than six cards above it.
    /// </summary>
    [Fact]
    public void The_floor_binds_even_when_nothing_clears_it()
    {
        var placements = new[] { 5.9m, 5.0m, 4.0m, 3.0m }
            .Select(p => AskReelGate.Place(Satisfies.Yes, p, 6m))
            .ToList();

        Assert.All(placements, p => Assert.Equal(AskReelGate.Placement.BelowFloor, p));
        Assert.Equal(SearchTurnMode.NoneFound, AskReelGate.ModeFor(0));
    }

    /// <summary>The bar is compared at the precision the badge renders: 5.95 is badged "6.0".</summary>
    [Theory]
    [InlineData(5.95, true)]
    [InlineData(5.94, false)]
    [InlineData(6.0, true)]
    public void The_bar_is_compared_at_badge_precision(double predicted, bool clears) =>
        Assert.Equal(clears, AskReelGate.Clears((decimal)predicted, 6m));

    [Fact]
    public void A_card_with_no_prediction_or_no_floor_clears()
    {
        Assert.True(AskReelGate.Clears(null, 6m));
        Assert.True(AskReelGate.Clears(1m, 0m));
    }

    [Fact]
    public void Every_shown_placement_has_a_stored_tier_and_a_drop_has_none()
    {
        Assert.Equal(SearchTurnCardTier.Pick, AskReelGate.TierOf(AskReelGate.Placement.Pick));
        Assert.Equal(SearchTurnCardTier.Pick, AskReelGate.TierOf(AskReelGate.Placement.UnjudgedPick));
        Assert.Equal(SearchTurnCardTier.NearMiss, AskReelGate.TierOf(AskReelGate.Placement.NearMiss));
        Assert.Equal(SearchTurnCardTier.BelowFloor, AskReelGate.TierOf(AskReelGate.Placement.BelowFloor));
        Assert.Null(AskReelGate.TierOf(AskReelGate.Placement.Dropped));
    }

    /// <summary>
    /// Rollback safety (§5.0 rule 8): a row older than the tier reads from its BelowFloor flag, and a
    /// tier this build does not know is never promoted into the grid.
    /// </summary>
    [Theory]
    [InlineData(null, false, SearchTurnCardTier.Pick)]
    [InlineData(null, true, SearchTurnCardTier.BelowFloor)]
    [InlineData("Pick", false, SearchTurnCardTier.Pick)]
    [InlineData("NearMiss", true, SearchTurnCardTier.NearMiss)]
    [InlineData("BelowFloor", true, SearchTurnCardTier.BelowFloor)]
    [InlineData("SomethingNew", false, SearchTurnCardTier.NearMiss)]
    public void A_stored_tier_reads_back_safely(string? tier, bool belowFloor, string expected) =>
        Assert.Equal(expected, SearchTurnCardTier.Of(new SearchTurnCard { Tier = tier, BelowFloor = belowFloor }));

    [Theory]
    [InlineData(null, SearchTurnMode.Picks)]
    [InlineData("Picks", SearchTurnMode.Picks)]
    [InlineData("NoneFound", SearchTurnMode.NoneFound)]
    [InlineData("Clarify", SearchTurnMode.Picks)]
    public void A_stored_mode_reads_back_safely(string? stored, string expected) =>
        Assert.Equal(expected, SearchTurnMode.Parse(stored));

    [Theory]
    [InlineData(null, false)]
    [InlineData("plan", true)]
    [InlineData("judge:1/3", false)]
    [InlineData("judge:1/3,plan", true)]
    [InlineData("planned", false)]
    public void A_degraded_plan_is_read_off_the_flags(string? flags, bool planFailed) =>
        Assert.Equal(planFailed, SearchTurnDegraded.PlanFailed(flags));
}
