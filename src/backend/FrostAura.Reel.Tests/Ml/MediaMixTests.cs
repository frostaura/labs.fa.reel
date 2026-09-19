using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The hero and rail media-mix floor. Pure, and asserted against the case that produced it: a
/// five-card hero of five television shows on a 90%-movies account.
/// </summary>
public class MediaMixTests
{
    [Fact]
    public void A_ninety_percent_movie_account_gets_a_hero_floor_of_four_films()
    {
        var (wantMovies, count) = MediaMix.TargetFor(5, 0.9m);

        Assert.True(wantMovies);
        Assert.Equal(4, count);
    }

    [Fact]
    public void The_floor_never_claims_the_whole_row()
    {
        // An all-movies hero is the same failure as an all-TV one, mirrored. One seat always stays
        // open for the other medium to win on merit.
        Assert.Equal(4, MediaMix.TargetFor(5, 1.0m).Count);
        Assert.Equal(9, MediaMix.TargetFor(10, 1.0m).Count);
        Assert.Equal(0, MediaMix.TargetFor(1, 1.0m).Count);
    }

    [Fact]
    public void A_mixed_library_asks_for_no_floor_at_all()
    {
        foreach (var share in new[] { 0.5m, 0.7m, 0.8m, 0.3m, 0.2m })
        {
            Assert.Equal(0, MediaMix.TargetFor(5, share).Count);
        }
    }

    [Fact]
    public void The_floor_is_symmetric_for_a_television_account()
    {
        var (wantMovies, count) = MediaMix.TargetFor(5, 0.05m);

        Assert.False(wantMovies);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Balancing_replaces_the_lowest_ranked_wrong_medium_seats_and_never_changes_length()
    {
        // Seats in rank order; spares in rank order. The best spare takes the WORST wrong-medium
        // seat, so meeting the floor costs the row as little ranking as it can.
        var selected = new[] { "show-a", "show-b", "show-c", "show-d", "show-e" };
        var spares = new[] { "movie-1", "movie-2", "movie-3" };

        var (balanced, met) = MediaMix.Balance(selected, spares, s => s.StartsWith("movie"), 2);

        Assert.True(met);
        Assert.Equal(5, balanced.Count);
        Assert.Equal(["show-a", "show-b", "show-c", "movie-2", "movie-1"], balanced);
    }

    [Fact]
    public void Balancing_reports_honestly_when_the_spares_cannot_reach_the_floor()
    {
        // This is the case the build has to SAY out loud rather than quietly miss: the shortlist
        // genuinely did not hold enough of that medium above the floor.
        var (balanced, met) = MediaMix.Balance(
            new[] { "show-a", "show-b", "show-c" }, new[] { "movie-1" }, s => s.StartsWith("movie"), 2);

        Assert.False(met);
        Assert.Equal(3, balanced.Count);
        Assert.Single(balanced, s => s.StartsWith("movie"));
    }

    [Fact]
    public void A_row_already_meeting_the_floor_is_returned_untouched()
    {
        var selected = new[] { "movie-1", "movie-2", "show-a" };

        var (balanced, met) = MediaMix.Balance(selected, ["movie-9"], s => s.StartsWith("movie"), 2);

        Assert.True(met);
        Assert.Equal(selected, balanced);
    }

    [Fact]
    public void A_zero_floor_is_a_no_op()
    {
        var selected = new[] { "show-a", "show-b" };

        var (balanced, met) = MediaMix.Balance(selected, ["movie-1"], s => s.StartsWith("movie"), 0);

        Assert.True(met);
        Assert.Equal(selected, balanced);
    }
}
