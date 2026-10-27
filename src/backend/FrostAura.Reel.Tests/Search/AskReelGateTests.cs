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
    [InlineData(null, 7.0, 6.0, AskReelGate.Placement.Unjudged)]
    [InlineData(null, 5.0, 6.0, AskReelGate.Placement.Unjudged)]
    [InlineData(null, 3.0, 0.0, AskReelGate.Placement.Unjudged)]
    public void A_card_lands_where_its_verdict_and_the_bar_put_it(
        Satisfies? satisfies, double predicted, double floor, AskReelGate.Placement expected) =>
        Assert.Equal(expected, AskReelGate.Place(satisfies, (decimal)predicted, (decimal)floor));

    /// <summary>
    /// §4.8: "Unscored cards with no on-demand score are never picks." Under a bar a judged match with
    /// no score is held back with the under-bar cards; with no bar there is nothing to miss. A card
    /// with no verdict is unjudged whatever its score (R1-01) — it never lands in the floor tray, which
    /// is read as "matches your bar held back". A <c>no</c> is dropped and a <c>partial</c> is a near
    /// miss either way — neither reaches the grid.
    /// </summary>
    [Theory]
    [InlineData(Satisfies.Yes, 6.0, AskReelGate.Placement.BelowFloor)]
    [InlineData(null, 6.0, AskReelGate.Placement.Unjudged)]
    [InlineData(Satisfies.Partial, 6.0, AskReelGate.Placement.NearMiss)]
    [InlineData(Satisfies.No, 6.0, AskReelGate.Placement.Dropped)]
    [InlineData(Satisfies.Yes, 0.0, AskReelGate.Placement.Pick)]
    [InlineData(null, 0.0, AskReelGate.Placement.Unjudged)]
    public void An_unscored_card_never_clears_a_bar(Satisfies? satisfies, double floor, AskReelGate.Placement expected) =>
        Assert.Equal(expected, AskReelGate.Place(satisfies, predicted: null, (decimal)floor));

    /// <summary>
    /// §4.8 and §4.10 together: an unjudged card sits below the judged picks when the turn has one,
    /// and in the unchecked tray — with the turn in NoneFound — when it has none.
    /// </summary>
    [Theory]
    [InlineData(0, true, SearchTurnCardTier.Unchecked, SearchTurnMode.NoneFound)]
    [InlineData(0, false, SearchTurnCardTier.Unchecked, SearchTurnMode.NoneFound)]
    [InlineData(1, true, SearchTurnCardTier.Pick, SearchTurnMode.Picks)]
    [InlineData(1, false, SearchTurnCardTier.Unchecked, SearchTurnMode.Picks)]
    [InlineData(6, true, SearchTurnCardTier.Pick, SearchTurnMode.Picks)]
    public void Unjudged_cards_reach_the_grid_only_beneath_a_judged_pick_and_above_the_bar(
        int judgedPicks, bool clearsFloor, string tier, string mode)
    {
        Assert.Equal(tier, AskReelGate.UnjudgedTier(judgedPicks, clearsFloor));
        Assert.Equal(mode, AskReelGate.ModeFor(judgedPicks));
    }

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
    public void With_no_floor_everything_clears_and_with_one_a_missing_score_does_not()
    {
        Assert.True(AskReelGate.Clears(null, 0m));
        Assert.True(AskReelGate.Clears(1m, 0m));
        Assert.False(AskReelGate.Clears(null, 6m));
        Assert.False(AskReelGate.Clears(null, 0.1m));
    }

    [Fact]
    public void Every_shown_placement_has_a_stored_tier_and_a_drop_has_none()
    {
        Assert.Equal(SearchTurnCardTier.Pick, AskReelGate.TierOf(AskReelGate.Placement.Pick));
        Assert.Equal(SearchTurnCardTier.Unchecked, AskReelGate.TierOf(AskReelGate.Placement.Unjudged));
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
    [InlineData("Unchecked", true, SearchTurnCardTier.Unchecked)]
    [InlineData("SomethingNew", false, SearchTurnCardTier.NearMiss)]
    public void A_stored_tier_reads_back_safely(string? tier, bool belowFloor, string expected) =>
        Assert.Equal(expected, SearchTurnCardTier.Of(new SearchTurnCard { Tier = tier, BelowFloor = belowFloor }));

    [Theory]
    [InlineData(null, SearchTurnMode.Picks)]
    [InlineData("Picks", SearchTurnMode.Picks)]
    [InlineData("NoneFound", SearchTurnMode.NoneFound)]
    [InlineData("Clarify", SearchTurnMode.Clarify)]
    [InlineData("SomethingNew", SearchTurnMode.Picks)]
    public void A_stored_mode_reads_back_safely(string? stored, string expected) =>
        Assert.Equal(expected, SearchTurnMode.Parse(stored));

    [Theory]
    [InlineData(null, false)]
    [InlineData("plan", true)]
    [InlineData("judge:1/3", false)]
    [InlineData("judge:1/3,plan", true)]
    [InlineData("planned", false)]
    [InlineData("no-model", true)]
    [InlineData("judge-missing:2/16", false)]
    public void A_degraded_plan_is_read_off_the_flags(string? flags, bool planFailed) =>
        Assert.Equal(planFailed, SearchTurnDegraded.PlanFailed(flags));

    [Theory]
    [InlineData(null, false)]
    [InlineData("plan", false)]
    [InlineData("judge:1/3", true)]
    [InlineData("plan,judge:3/3", true)]
    [InlineData("judge:0/3", false)]
    [InlineData("judge:", false)]
    [InlineData("judge-missing:2/16", true)]
    [InlineData("judge-missing:0/16", false)]
    [InlineData("judge:0/3,judge-missing:1/16", true)]
    [InlineData("no-model", true)]
    public void A_failed_judge_is_read_off_the_flags(string? flags, bool judgeFailed) =>
        Assert.Equal(judgeFailed, SearchTurnDegraded.JudgeFailed(flags));
    /// <summary>XM-06: an unjudged card the bar holds back is the floor tray's — under the bar, or unscored while one is set.</summary>
    [Theory]
    [InlineData(5.9, 6.0, SearchTurnCardTier.BelowFloor)]
    [InlineData(null, 6.0, SearchTurnCardTier.BelowFloor)]
    [InlineData(5.96, 6.0, SearchTurnCardTier.Unchecked)]
    [InlineData(null, 0.0, SearchTurnCardTier.Unchecked)]
    [InlineData(3.0, 0.0, SearchTurnCardTier.Unchecked)]
    public void An_unjudged_card_the_bar_holds_back_goes_to_the_floor_tray(double? predicted, double floor, string tier) =>
        Assert.Equal(tier, AskReelGate.UncheckedTier((decimal?)predicted, (decimal)floor));
}
