using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// The hero honesty label's derivation. It has no state and no database, and it is the whole reason
/// the label needs neither a column nor a migration: a hero pick below the bar came from the
/// never-blank backfill by construction, because everything above the bar was taken first.
/// </summary>
public class HeroConfidenceTests
{
    [Fact]
    public void A_pick_under_the_bar_is_reaching_and_one_over_it_is_confident()
    {
        var bar = HeroConfidence.BarFor(null);

        Assert.Equal(HeroConfidence.Reaching, HeroConfidence.For(6.6m, bar));
        Assert.Equal(HeroConfidence.Confident, HeroConfidence.For(8.1m, bar));
    }

    [Fact]
    public void The_bar_itself_counts_as_cleared()
    {
        // Inclusive, because the build selects the hero pool with >= the same bar. An exclusive
        // comparison here would label a title the build treated as confident as a reach.
        Assert.Equal(HeroConfidence.Confident, HeroConfidence.For(HeroConfidence.Floor, HeroConfidence.BarFor(null)));
    }

    [Fact]
    public void An_account_can_raise_the_bar_but_never_lower_it()
    {
        // A personal minimum is a floor on what may be SHOWN at all. Setting it to 5 is not a request
        // for a weaker headline, so the hero bar stays at the product floor.
        Assert.Equal(HeroConfidence.Floor, HeroConfidence.BarFor(null));
        Assert.Equal(HeroConfidence.Floor, HeroConfidence.BarFor(0m));
        Assert.Equal(HeroConfidence.Floor, HeroConfidence.BarFor(5m));
        Assert.Equal(8.5m, HeroConfidence.BarFor(8.5m));
    }

    [Fact]
    public void Raising_the_bar_can_turn_a_previously_confident_pick_into_a_reach()
    {
        var strict = HeroConfidence.BarFor(8.5m);

        Assert.Equal(HeroConfidence.Confident, HeroConfidence.For(8.1m, HeroConfidence.BarFor(null)));
        Assert.Equal(HeroConfidence.Reaching, HeroConfidence.For(8.1m, strict));
    }

    [Fact]
    public void The_live_hero_the_founder_is_looking_at_reads_as_reaching()
    {
        // Dossier 05, 2026-09-09: the active artifact scores 31 of 470 titles at or above 7.5, and the
        // live hero is five network procedurals averaging 6.86 with a minimum of 6.6 — every one of
        // them from the backfill, under the heading "Tonight's picks", with the UI saying nothing.
        var bar = HeroConfidence.BarFor(6.0m);
        decimal[] livePicks = [6.6m, 6.7m, 6.9m, 7.0m, 7.0m];

        Assert.All(livePicks, p => Assert.Equal(HeroConfidence.Reaching, HeroConfidence.For(p, bar)));
    }
}
