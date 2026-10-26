using FrostAura.Reel.Application.Jobs;

namespace FrostAura.Reel.Tests.Sync;

public class DeltaDiffTests
{
    private const string Baseline = """
        {
          "all": "2026-06-12T08:00:00.000Z",
          "movies": { "watched_at": "2026-06-10T20:00:00.000Z", "rated_at": "2026-06-11T09:00:00.000Z" },
          "episodes": { "watched_at": "2026-06-12T07:00:00.000Z", "rated_at": "2026-06-01T10:00:00.000Z" },
          "shows": { "rated_at": "2026-05-30T12:00:00.000Z" },
          "seasons": { "rated_at": "2026-04-01T12:00:00.000Z" }
        }
        """;

    [Fact]
    public void Identical_snapshots_report_no_changes()
    {
        var changes = DeltaSyncJobHandler.DiffActivities(Baseline, Baseline, force: false);
        Assert.False(changes.Any);
    }

    [Fact]
    public void Movie_watch_only_triggers_movies()
    {
        var current = Baseline.Replace("2026-06-10T20:00:00.000Z", "2026-06-12T21:00:00.000Z");
        var changes = DeltaSyncJobHandler.DiffActivities(Baseline, current, force: false);
        Assert.True(changes.Movies);
        Assert.False(changes.Shows);
        Assert.False(changes.Ratings);
    }

    [Fact]
    public void Episode_watch_triggers_shows_only()
    {
        var current = Baseline.Replace("2026-06-12T07:00:00.000Z", "2026-06-12T22:00:00.000Z");
        var changes = DeltaSyncJobHandler.DiffActivities(Baseline, current, force: false);
        Assert.False(changes.Movies);
        Assert.True(changes.Shows);
        Assert.False(changes.Ratings);
    }

    [Theory]
    [InlineData("2026-06-11T09:00:00.000Z")] // movies.rated_at
    [InlineData("2026-05-30T12:00:00.000Z")] // shows.rated_at
    [InlineData("2026-04-01T12:00:00.000Z")] // seasons.rated_at
    [InlineData("2026-06-01T10:00:00.000Z")] // episodes.rated_at
    public void Any_rating_category_triggers_ratings(string timestampToBump)
    {
        var current = Baseline.Replace(timestampToBump, "2026-06-12T23:59:59.000Z");
        var changes = DeltaSyncJobHandler.DiffActivities(Baseline, current, force: false);
        Assert.True(changes.Ratings);
    }

    [Fact]
    public void First_sync_and_force_refetch_everything()
    {
        Assert.True(DeltaSyncJobHandler.DiffActivities(null, Baseline, force: false) is { Movies: true, Shows: true, Ratings: true, Watchlist: true, Hidden: true });
        Assert.True(DeltaSyncJobHandler.DiffActivities(Baseline, Baseline, force: true) is { Movies: true, Shows: true, Ratings: true, Watchlist: true, Hidden: true });
    }

    private const string WithWatchlistAndHidden = """
        {
          "movies": { "watched_at": "2026-06-10T20:00:00.000Z", "watchlisted_at": "2026-06-02T01:00:00.000Z", "hidden_at": "2026-03-01T01:00:00.000Z" },
          "shows": { "watchlisted_at": "2026-06-03T01:00:00.000Z", "hidden_at": "2026-03-02T01:00:00.000Z", "dropped_at": "2026-03-03T01:00:00.000Z" },
          "seasons": { "hidden_at": "2026-03-04T01:00:00.000Z" },
          "watchlist": { "updated_at": "2026-06-04T01:00:00.000Z" }
        }
        """;

    [Theory]
    [InlineData("2026-06-02T01:00:00.000Z")] // movies.watchlisted_at
    [InlineData("2026-06-03T01:00:00.000Z")] // shows.watchlisted_at
    [InlineData("2026-06-04T01:00:00.000Z")] // watchlist.updated_at
    public void A_moved_watchlist_triggers_the_watchlist_and_nothing_that_rebuilds(string timestampToBump)
    {
        var current = WithWatchlistAndHidden.Replace(timestampToBump, "2026-06-12T23:59:59.000Z");
        var changes = DeltaSyncJobHandler.DiffActivities(WithWatchlistAndHidden, current, force: false);
        Assert.True(changes.Watchlist);
        Assert.False(changes.Hidden);
        Assert.False(changes.Any);
    }

    [Theory]
    [InlineData("2026-03-01T01:00:00.000Z")] // movies.hidden_at
    [InlineData("2026-03-02T01:00:00.000Z")] // shows.hidden_at
    [InlineData("2026-03-03T01:00:00.000Z")] // shows.dropped_at
    [InlineData("2026-03-04T01:00:00.000Z")] // seasons.hidden_at
    public void A_moved_hidden_list_triggers_hidden_only(string timestampToBump)
    {
        var current = WithWatchlistAndHidden.Replace(timestampToBump, "2026-06-12T23:59:59.000Z");
        var changes = DeltaSyncJobHandler.DiffActivities(WithWatchlistAndHidden, current, force: false);
        Assert.True(changes.Hidden);
        Assert.False(changes.Watchlist);
        Assert.False(changes.Any);
    }
}
