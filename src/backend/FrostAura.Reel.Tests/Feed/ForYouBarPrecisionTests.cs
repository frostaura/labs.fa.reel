using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// R1-04: one rule for the "for you" bar, held to the number the badge shows. The feed build and its
/// read path compared the raw score (a 7.46 held out of a 7.5 bar while its badge read "7.5"), Saved
/// and the digest rounded half-to-even (7.35 passed a 7.4 bar while the badge read "7.3"), and Ask
/// Reel and the supply truth rounded half away from zero. These pin the single rule at every
/// midpoint the defect named, against a bar at each value.
/// </summary>
public class ForYouBarPrecisionTests
{
    [Theory]
    [InlineData("7.35", "7.4")]
    [InlineData("6.05", "6.1")]
    [InlineData("7.45", "7.5")]
    [InlineData("7.25", "7.3")]
    [InlineData("7.34", "7.3")]
    [InlineData("7.46", "7.5")]
    [InlineData("10.00", "10.0")]
    [InlineData("0.05", "0.1")]
    // A raw score is stored at two decimals first, the way the numeric(4,2) column keeps it, so the
    // build (raw) and the read (stored) can never round one title two ways.
    [InlineData("7.346", "7.4")]
    [InlineData("7.3449", "7.3")]
    public void The_shown_number_is_the_stored_value_at_one_decimal_half_away_from_zero(string predicted, string shown)
    {
        Assert.Equal(Dec(shown), ForYouBar.Shown(Dec(predicted)));
    }

    [Theory]
    // At the midpoints, against a bar at each value.
    [InlineData("7.35", "7.4", true)]
    [InlineData("7.35", "7.35", true)]
    [InlineData("7.35", "7.3", true)]
    [InlineData("7.34", "7.4", false)]
    [InlineData("6.05", "6.1", true)]
    [InlineData("6.05", "6.05", true)]
    [InlineData("6.04", "6.1", false)]
    [InlineData("7.45", "7.5", true)]
    [InlineData("7.45", "7.45", true)]
    [InlineData("7.44", "7.5", false)]
    [InlineData("7.46", "7.5", true)]
    // A two-decimal bar is reached by the shown number, not the stored one: a 7.45 bar needs a "7.5".
    [InlineData("7.41", "7.45", false)]
    // No bar (0) clears everything, including a zero prediction.
    [InlineData("0.00", "0", true)]
    public void A_card_clears_the_bar_when_its_badge_does(string predicted, string bar, bool clears)
    {
        Assert.Equal(clears, ForYouBar.Clears(Dec(predicted), Dec(bar)));
        Assert.Equal(clears, ForYouBar.Clears(Dec(predicted), (decimal?)Dec(bar)));
    }

    [Fact]
    public void A_null_bar_clears_everything()
    {
        Assert.True(ForYouBar.Clears(0.01m, (decimal?)null));
    }

    /// <summary>
    /// The rule the exclusion matrix and the supply truth use (one decimal, half away from zero, on
    /// the stored two) — and Ask Reel's gate — agrees with this one on every stored value, so a
    /// surface and the check that audits it cannot disagree. Exhaustive over the 0–10 scale.
    /// </summary>
    [Fact]
    public void Every_stored_value_agrees_with_the_audit_rule()
    {
        for (var cents = 0; cents <= 1000; cents++)
        {
            var stored = cents / 100m;
            Assert.Equal(Math.Round(stored, 1, MidpointRounding.AwayFromZero), ForYouBar.Shown(stored));
        }
    }

    /// <summary>
    /// The SQL bound the feed's refill uses on the stored column is exactly the in-memory check, for
    /// every stored value against every one-decimal bar and a spread of two-decimal ones.
    /// </summary>
    [Fact]
    public void The_sql_bound_admits_exactly_what_the_check_admits()
    {
        var bars = Enumerable.Range(1, 100).Select(t => t / 10m)
            .Concat([7.45m, 7.41m, 6.05m, 0.01m, 9.99m]);
        foreach (var bar in bars)
        {
            var lowest = ForYouBar.LowestStoredClearing(bar);
            for (var cents = 0; cents <= 1000; cents++)
            {
                var stored = cents / 100m;
                Assert.True(
                    (stored >= lowest) == ForYouBar.Clears(stored, bar),
                    $"stored {stored} against bar {bar}: SQL bound {lowest}");
            }
        }
    }

    /// <summary>
    /// The value sent to the client is an exact one-decimal number, so the browser's
    /// <c>toFixed(1)</c> prints the digit the bar was held to. A stored 7.35 sent as-is prints "7.3"
    /// (the double is 7.3499…) next to a bar of 7.4 it passed.
    /// </summary>
    [Theory]
    [InlineData("7.35", "7.4")]
    [InlineData("6.05", "6.1")]
    [InlineData("7.25", "7.3")]
    [InlineData("7.40", "7.4")]
    public void The_client_is_sent_an_exact_one_decimal_number(string stored, string json)
    {
        Assert.Equal(json, JsonSerializer.Serialize(ForYouBar.Shown(Dec(stored))));
    }

    [Theory]
    [InlineData("7.45", "7.5", HeroConfidence.Confident)]
    [InlineData("7.46", "7.5", HeroConfidence.Confident)]
    [InlineData("7.44", "7.5", HeroConfidence.Reaching)]
    [InlineData("7.95", "8.0", HeroConfidence.Confident)]
    public void The_hero_label_is_measured_against_the_badge_beside_it(string predicted, string bar, string label)
    {
        Assert.Equal(label, HeroConfidence.For(Dec(predicted), Dec(bar)));
    }

    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
