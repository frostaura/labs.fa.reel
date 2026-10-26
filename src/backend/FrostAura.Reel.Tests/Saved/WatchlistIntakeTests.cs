using FrostAura.Reel.Application.Saved;

namespace FrostAura.Reel.Tests.Saved;

/// <summary>
/// N-5: which Trakt watchlist entries become saves. One fact varied at a time from an entry that
/// does — so each assertion names the single fact that changed the answer.
/// </summary>
public class WatchlistIntakeTests
{
    private static readonly DateTime Listed = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly WatchlistFacts Nothing = new(false, false, null, false, null);

    [Fact]
    public void An_entry_reel_knows_nothing_about_becomes_a_save() =>
        Assert.Equal(WatchlistDecision.Save, WatchlistIntake.Decide(Listed, Nothing));

    [Fact]
    public void A_live_save_is_left_alone() =>
        Assert.Equal(WatchlistDecision.AlreadySaved, WatchlistIntake.Decide(Listed, Nothing with { HasLiveSave = true }));

    [Fact]
    public void A_watchlist_write_back_still_in_flight_is_reels_own() =>
        Assert.Equal(WatchlistDecision.ReelsOwnWrite, WatchlistIntake.Decide(Listed, Nothing with { WatchlistWriteInFlight = true }));

    [Fact]
    public void An_entry_listed_before_reel_revoked_the_save_is_reels_leftover() =>
        Assert.Equal(WatchlistDecision.ReelsOwnWrite, WatchlistIntake.Decide(Listed, Nothing with { LatestSaveRevokedAt = Listed.AddDays(3) }));

    [Fact]
    public void An_entry_listed_after_the_revocation_is_the_user_putting_it_back() =>
        Assert.Equal(WatchlistDecision.Save, WatchlistIntake.Decide(Listed, Nothing with { LatestSaveRevokedAt = Listed.AddDays(-3) }));

    [Fact]
    public void Watched_or_rated_since_listing_is_consumed() =>
        Assert.Equal(WatchlistDecision.Consumed, WatchlistIntake.Decide(Listed, Nothing with { LastConsumedAt = Listed.AddHours(1) }));

    [Fact]
    public void Seen_before_listing_is_a_rewatch_and_becomes_a_save() =>
        Assert.Equal(WatchlistDecision.Save, WatchlistIntake.Decide(Listed, Nothing with { LastConsumedAt = Listed.AddYears(-2) }));

    [Fact]
    public void A_title_turned_down_in_reel_stays_turned_down() =>
        Assert.Equal(WatchlistDecision.Declined, WatchlistIntake.Decide(Listed, Nothing with { HasLiveDecline = true }));
}
