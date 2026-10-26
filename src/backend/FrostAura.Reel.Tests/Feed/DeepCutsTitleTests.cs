using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// "Deep cuts — under-the-radar, high for you" is a claim about the row's numbers (R1-14): the
/// founder's row averaged 6.51 against a median rating of 7. The title says "high for you" only when
/// the row's mean prediction reaches the reader's median, and the median is the plain middle of the
/// reader's movie and show ratings.
/// </summary>
public class DeepCutsTitleTests
{
    [Theory]
    [InlineData(new[] { 6.4, 6.5, 6.64 }, 7.0, false)] // the founder's row: 6.51 < 7
    [InlineData(new[] { 6.9, 7.0, 7.1 }, 7.0, true)]   // exactly at the median clears it
    [InlineData(new[] { 6.99 }, 7.0, false)]           // a hair under does not
    [InlineData(new[] { 8.2, 7.6 }, 6.5, true)]
    public void High_for_you_needs_the_row_mean_at_or_above_the_median(double[] row, double median, bool high)
    {
        var title = FeedEndpoints.DeepCutsTitle(row.Select(r => (decimal)r).ToList(), (decimal)median);

        Assert.Equal(high ? FeedEndpoints.DeepCutsHighForYouTitle : FeedEndpoints.DeepCutsPlainTitle, title);
    }

    [Fact]
    public void Without_a_median_or_a_row_the_claim_is_not_made()
    {
        Assert.Equal(FeedEndpoints.DeepCutsPlainTitle, FeedEndpoints.DeepCutsTitle([9.5m], null));
        Assert.Equal(FeedEndpoints.DeepCutsPlainTitle, FeedEndpoints.DeepCutsTitle([], 5m));
        Assert.DoesNotContain("high for you", FeedEndpoints.DeepCutsPlainTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_median_is_the_middle_rating_or_the_mean_of_the_middle_two()
    {
        // 1 × 6, 2 × 7, 2 × 8 → 6 7 7 8 8 → 7.
        Assert.Equal(7m, AccountRatingMedian.Of([(8, 2), (6, 1), (7, 2)]));
        // 6 6 7 7 → 6.5.
        Assert.Equal(6.5m, AccountRatingMedian.Of([(6, 2), (7, 2)]));
        // A single rating is its own median; zero counts are ignored.
        Assert.Equal(9m, AccountRatingMedian.Of([(9, 1), (3, 0)]));
        // 1 × 2, 3 × 10 → 2 10 10 10 → 10; a skewed history follows its mass, not its mean.
        Assert.Equal(10m, AccountRatingMedian.Of([(2, 1), (10, 3)]));
        // 2 × 4, 2 × 9 → 4 4 9 9 → 6.5, between the two middle values.
        Assert.Equal(6.5m, AccountRatingMedian.Of([(9, 2), (4, 2)]));
        Assert.Null(AccountRatingMedian.Of([]));
        Assert.Null(AccountRatingMedian.Of([(7, 0)]));
    }
}
