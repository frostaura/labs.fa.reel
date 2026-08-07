using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The conditions-editor input rules. These values are cast to a number inside SQL by the
/// eligibility builder, so a bad one is a runtime failure rather than a wrong answer — the
/// validation here is what keeps that cast total.
/// </summary>
public class ContentFilterRulesTests
{
    [Fact]
    public void Inverted_year_bounds_are_swapped_rather_than_left_unmatchable()
    {
        Assert.True(ContentFilterRules.TryNormalizeYears(2010, 1990, out var window, out var error));
        Assert.Null(error);
        Assert.Equal(1990, window.MinYear);
        Assert.Equal(2010, window.MaxYear);
    }

    [Fact]
    public void A_single_open_ended_bound_is_kept_as_given()
    {
        Assert.True(ContentFilterRules.TryNormalizeYears(1990, null, out var lower, out _));
        Assert.Equal(1990, lower.MinYear);
        Assert.Null(lower.MaxYear);

        Assert.True(ContentFilterRules.TryNormalizeYears(null, 2010, out var upper, out _));
        Assert.Null(upper.MinYear);
        Assert.Equal(2010, upper.MaxYear);

        Assert.True(ContentFilterRules.TryNormalizeYears(null, null, out var none, out _));
        Assert.Null(none.MinYear);
        Assert.Null(none.MaxYear);
    }

    [Theory]
    [InlineData(1869, null)]
    [InlineData(2101, null)]
    [InlineData(null, 1869)]
    [InlineData(null, 2101)]
    [InlineData(0, null)]
    public void Out_of_range_years_are_rejected_not_clamped(int? min, int? max)
    {
        Assert.False(ContentFilterRules.TryNormalizeYears(min, max, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_range_bounds_themselves_are_inclusive()
    {
        Assert.True(ContentFilterRules.TryNormalizeYears(
            ContentFilterRules.MinAllowedYear, ContentFilterRules.MaxAllowedYear, out var window, out _));
        Assert.Equal(ContentFilterRules.MinAllowedYear, window.MinYear);
        Assert.Equal(ContentFilterRules.MaxAllowedYear, window.MaxYear);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0d, null)]
    [InlineData(-3d, null)]
    [InlineData(7.5d, 7.5d)]
    [InlineData(11d, 10d)]
    public void Crowd_floor_is_clamped_to_the_zero_to_ten_scale_and_zero_means_no_floor(double? input, double? expected)
    {
        var result = ContentFilterRules.NormalizeCrowdFloor(input is null ? null : (decimal)input.Value);

        Assert.Equal(expected is null ? null : (decimal)expected.Value, result);
    }

    [Fact]
    public void Stored_values_round_trip_through_the_canonical_format()
    {
        Assert.Equal(7.5m, ContentFilterRules.ParseRating(ContentFilterRules.FormatRating(7.5m)));
        Assert.Equal(1990, ContentFilterRules.ParseYear(ContentFilterRules.FormatYear(1990)));
        Assert.Null(ContentFilterRules.ParseRating("not a number"));
        Assert.Null(ContentFilterRules.ParseYear("not a number"));
    }

    [Fact]
    public void Only_a_genre_rail_waives_the_genre_filters()
    {
        Assert.True(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.Genre, "documentary")));

        // A genre context with no genre is not a rail — waiving the filters there would silently
        // widen a lane that has nothing narrowing it in the first place.
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.Genre, null)));
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.Hero, null)));
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.DeepCuts, null)));
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.NewForYou, null)));
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.BecauseYouLoved, null)));
        Assert.False(FeedPaginator.IgnoresGenreFilters(Request(FeedRowContext.Similar, null)));
    }

    private static MoreFeedRequest Request(FeedRowContext context, string? genre) =>
        new(context, AnchorTitleId: null, Genre: genre, ExcludeTitleIds: [], Limit: 12);
}
