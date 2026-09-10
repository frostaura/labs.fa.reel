using FrostAura.Reel.Application.Sync;

namespace FrostAura.Reel.Tests.Sync;

/// <summary>
/// The post-sync sentence. The defect this closes is a wrong NUMBER, not a wrong string: the count
/// the sync had in hand was <c>ratings.Count</c> — every row walked from Trakt on a full paginated
/// refetch, ~3,400 for the founder's account including seasons and episodes — so rendering it would
/// have read "Pulled 3,412 new ratings from Trakt" after every single sync, forever.
///
/// <para>Mirrored by <c>syncSummaryCopy</c> in <c>src/frontend/src/lib/syncCopy.ts</c>, which must
/// produce these same sentences: the header pill has two sources (the SSE frame and the 5-second
/// poll of the persisted ProgressMessage) and they must not disagree.</para>
/// </summary>
public class SyncSummaryCopyTests
{
    private static readonly DateTime Retrain = new(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Twelve_new_ratings_and_three_changed_picks() =>
        Assert.Equal(
            "Pulled 12 new ratings from Trakt — 3 picks changed",
            SyncSummaryCopy.Describe(new SyncSummary(12, 0, 0, false, null, 3)));

    [Fact]
    public void Twelve_new_ratings_with_a_retrain_coming_tonight() =>
        Assert.Equal(
            "Pulled 12 new ratings from Trakt — retraining tonight at 03:00 UTC",
            SyncSummaryCopy.Describe(new SyncSummary(12, 0, 0, true, Retrain, null)));

    [Fact]
    public void Nothing_arrived() =>
        Assert.Equal("Already up to date", SyncSummaryCopy.Describe(new SyncSummary(0, 0, 0, false, null, null)));

    [Fact]
    public void Nothing_arrived_but_the_model_still_moves() =>
        Assert.Equal(
            "Already up to date — retraining tonight at 03:00 UTC",
            SyncSummaryCopy.Describe(new SyncSummary(0, 0, 0, true, Retrain, null)));

    [Fact]
    public void A_single_rating_is_singular() =>
        Assert.Equal(
            "Pulled 1 new rating from Trakt — 1 pick changed",
            SyncSummaryCopy.Describe(new SyncSummary(1, 0, 0, false, null, 1)));

    [Fact]
    public void A_re_rate_is_an_update_not_a_new_rating() =>
        Assert.Equal(
            "Updated 2 ratings from Trakt",
            SyncSummaryCopy.Describe(new SyncSummary(0, 2, 0, false, null, null)));

    [Fact]
    public void Watches_carry_the_sentence_when_no_ratings_moved() =>
        Assert.Equal(
            "Pulled 3 new watches from Trakt",
            SyncSummaryCopy.Describe(new SyncSummary(0, 0, 3, false, null, null)));

    [Fact]
    public void A_single_watch_is_singular() =>
        Assert.Equal(
            "Pulled 1 new watch from Trakt",
            SyncSummaryCopy.Describe(new SyncSummary(0, 0, 1, false, null, null)));

    [Fact]
    public void A_train_already_in_flight_is_now_not_tonight() =>
        Assert.Equal(
            "Pulled 4 new ratings from Trakt — retraining now",
            SyncSummaryCopy.Describe(new SyncSummary(4, 0, 0, true, null, null)));

    [Fact]
    public void A_concrete_change_to_the_page_outranks_a_promise_about_tonight() =>
        // Both are true; only one is something the user can go and look at.
        Assert.Equal(
            "Pulled 12 new ratings from Trakt — 3 picks changed",
            SyncSummaryCopy.Describe(new SyncSummary(12, 0, 0, true, Retrain, 3)));

    [Fact]
    public void A_rebuild_that_changed_nothing_says_so_by_omission() =>
        // picksChanged: 0 is knowledge (the build ran and kept the page); it is not a tail clause.
        Assert.Equal(
            "Pulled 12 new ratings from Trakt",
            SyncSummaryCopy.Describe(new SyncSummary(12, 0, 0, false, null, 0)));

    [Fact]
    public void Large_counts_are_grouped_for_reading() =>
        Assert.Equal(
            "Pulled 1,204 new ratings from Trakt",
            SyncSummaryCopy.Describe(new SyncSummary(1204, 0, 0, false, null, null)));
}
