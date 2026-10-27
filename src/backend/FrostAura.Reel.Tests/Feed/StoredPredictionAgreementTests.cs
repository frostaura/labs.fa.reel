using System.Globalization;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// R1-04, the writers' half. Three code paths store a "for you" prediction: the feed build, the
/// paginator and the on-demand scorer (the title page, Ask Reel's cards). The paginator stored
/// <see cref="ShownRating.Stored"/>, but the build wrote its TitleScores row with a half-to-even
/// <c>Math.Round</c> beside a FeedItem stored half away from zero, and the on-demand scorer rounded
/// half-to-even twice, before and after the demotion. At a midpoint the same title read "7.4" on the
/// feed and "7.3" on its own page. These pin one stored value and one badge for every writer.
/// </summary>
public class StoredPredictionAgreementTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(7.345, 0.0, "7.35")]  // a midpoint: half-to-even stored 7.34, a "7.3"
    [InlineData(7.325, 0.0, "7.33")]
    [InlineData(6.055, 0.0, "6.06")]
    [InlineData(7.3449, 0.0, "7.34")] // control: no midpoint, both rules agree
    [InlineData(7.545, 0.2, "7.35")]  // demoted unrounded, rounded once (two-step rounding gave 7.34)
    [InlineData(7.5, 0.0, "7.50")]
    [InlineData(0.7, 0.5, "0.70")]    // the demotion never takes a score below the floor
    public void The_on_demand_scorer_stores_what_the_feed_stores(double calibrated, double demotion, string stored)
    {
        var feedStores = ShownRating.Stored(DislikeSignal.ApplyDemotion((decimal)calibrated, demotion));

        Assert.Equal(D(stored), OnDemandScorer.Predicted(calibrated, demotion));
        Assert.Equal(feedStores, OnDemandScorer.Predicted(calibrated, demotion));
    }

    [Fact]
    public void The_on_demand_badge_matches_the_feed_badge_across_every_half_step()
    {
        // Every third-decimal midpoint from 5 to 9, under no demotion and three different ones.
        var disagreements = new List<string>();
        foreach (var demotion in new[] { 0d, 0.15d, 0.2d, 0.333d })
        {
            for (var milli = 5000; milli <= 9000; milli += 5)
            {
                var calibrated = milli / 1000d;
                var feed = ShownRating.Of(DislikeSignal.ApplyDemotion((decimal)calibrated, demotion));
                var onDemand = ShownRating.Of(OnDemandScorer.Predicted(calibrated, demotion));
                if (feed != onDemand)
                {
                    disagreements.Add(string.Create(CultureInfo.InvariantCulture, $"{calibrated}-{demotion}: feed {feed}, on demand {onDemand}"));
                }
            }
        }

        Assert.True(disagreements.Count == 0, $"{disagreements.Count} disagreements, e.g. {string.Join("; ", disagreements.Take(5))}");
    }

    [Theory]
    [InlineData("7.25", "7.26", true)]  // both badges read "7.3"; half-to-even read the walk as 7.2
    [InlineData("7.45", "7.54", true)]  // both "7.5"
    [InlineData("7.35", "7.35", true)]
    [InlineData("7.34", "7.30", true)]
    [InlineData("7.25", "7.24", false)] // "7.3" against "7.2"; half-to-even called them both 7.2
    [InlineData("7.35", "7.34", false)]
    [InlineData("10.00", "9.95", true)]
    public void The_waterfall_lands_when_the_badges_agree(string walkEnd, string shown, bool lands)
    {
        Assert.Equal(lands, OnDemandScorer.LandsOnTheBadge(D(walkEnd), D(shown)));
    }

    [Theory]
    [InlineData("7.25", "7.3")]  // half-to-even read "7.2"
    [InlineData("8.35", "8.4")]  // and here "8.4" by luck: the control that the rule, not the digit, decides
    [InlineData("6.05", "6.1")]
    [InlineData("7.24", "7.2")]
    [InlineData("10.00", "10.0")]
    public void A_blend_card_shows_what_the_feed_shows_for_the_same_stored_score(string stored, string shown)
    {
        Assert.Equal(D(shown), BlendEndpoints.Shown(D(stored)));
        Assert.Equal(ShownRating.Of(D(stored)), BlendEndpoints.Shown(D(stored)));
    }

    [Theory]
    [InlineData("8.25")]
    [InlineData("7.85")]
    [InlineData("6.05")]
    [InlineData("8.24")]  // controls: no midpoint
    [InlineData("8.2666666666666666666666666667")]
    [InlineData("10")]
    public void A_persons_breakdown_average_reads_what_the_why_line_prints(string average)
    {
        // The why-line ("you rate Tom Hardy 8.3 on average") and the breakdown's "your average" read
        // the same VerifiedPeople average; the card must not show it as two numbers.
        var why = D(average).ToString("0.0", CultureInfo.InvariantCulture);
        Assert.Equal(why, ContributionDetails.OneDecimal(D(average)).ToString("0.0", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("7.345", "7.35")]
    [InlineData("7.325", "7.33")]
    [InlineData("7.3449", "7.34")]
    [InlineData("7.35", "7.35")]
    [InlineData("10.000", "10.00")]
    public void Every_writer_stores_a_score_half_away_from_zero(string raw, string stored)
    {
        // The one door to TitleScores applies the stored rule itself, so no writer can store a
        // half-to-even value however it computed the score.
        Assert.Equal(D(stored), new TitleScoreWriter.Score(Guid.Empty, D(raw), "[]").PredictedRating);
    }
}
