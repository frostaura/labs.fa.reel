using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The Preserve/Fresh decision, pinned as a pure function. The handler reads UtcNow exactly once
/// and passes it in (the codebase deliberately has no clock abstraction), so the day-rollover
/// semantics — the piece an integration test cannot reach without faking time — are tested here.
/// </summary>
public class FeedBuildPolicyTests
{
    private static readonly Guid Artifact = Guid.NewGuid();
    private static readonly DateTime Noon = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_first_build_with_nothing_to_preserve_is_fresh()
    {
        Assert.Equal(FeedBuildMode.Fresh, FeedBuildPolicy.Decide(null, null, Artifact, "delta-sync", Noon));
    }

    [Fact]
    public void A_same_day_background_rebuild_preserves_tonights_picks()
    {
        // The founder-reported churn case: a delta-sync poll a few minutes after the last build.
        Assert.Equal(
            FeedBuildMode.Preserve,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, "delta-sync", Noon));
    }

    [Fact]
    public void A_rebuild_with_no_recorded_reason_still_preserves_within_the_day()
    {
        // Jobs enqueued before the TriggerReason column existed carry null — they are background
        // work, and background work must not churn the page.
        Assert.Equal(
            FeedBuildMode.Preserve,
            FeedBuildPolicy.Decide(Noon.AddHours(-3), Artifact, Artifact, null, Noon));
    }

    [Fact]
    public void The_utc_day_rolling_over_forces_a_fresh_deal()
    {
        // 23:59 yesterday → 00:01 today is a new day even though only minutes passed; conversely
        // 00:01 → 23:59 the same day preserves even though almost a full day passed. The key is
        // the UTC date, not elapsed time.
        var lateYesterday = new DateTime(2026, 8, 6, 23, 59, 0, DateTimeKind.Utc);
        var earlyToday = new DateTime(2026, 8, 7, 0, 1, 0, DateTimeKind.Utc);
        var lateToday = new DateTime(2026, 8, 7, 23, 59, 0, DateTimeKind.Utc);

        Assert.Equal(FeedBuildMode.Fresh, FeedBuildPolicy.Decide(lateYesterday, Artifact, Artifact, "delta-sync", earlyToday));
        Assert.Equal(FeedBuildMode.Preserve, FeedBuildPolicy.Decide(earlyToday, Artifact, Artifact, "delta-sync", lateToday));
    }

    [Fact]
    public void A_new_model_artifact_forces_a_fresh_deal()
    {
        // The model's opinion of every candidate changed with it; preserving the old model's page
        // would pin picks the new model may no longer stand behind.
        Assert.Equal(
            FeedBuildMode.Fresh,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Guid.NewGuid(), Artifact, "model-trained", Noon));
    }

    [Fact]
    public void User_forced_reasons_break_same_day_stability()
    {
        // The user asked for a different page — stability must never make the refresh button a no-op.
        Assert.Equal(
            FeedBuildMode.Fresh,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, FeedBuildPolicy.ManualRefreshReason, Noon));
        Assert.Equal(
            FeedBuildMode.Fresh,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, FeedBuildPolicy.FiltersChangedReason, Noon));
    }

    [Fact]
    public void User_action_reasons_that_only_vacate_slots_stay_preserve()
    {
        // Reacting to a title changes the feed through eligibility (its slot vacates inside the
        // Preserve build), not by re-dealing everything the user was still reading.
        Assert.Equal(
            FeedBuildMode.Preserve,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, "in-app rating", Noon));
        Assert.Equal(
            FeedBuildMode.Preserve,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, "not interested", Noon));
    }

    [Fact]
    public void The_deploy_purge_forces_fresh_without_counting_as_a_user_request()
    {
        // Fresh, or the no-op suppression would keep tonight's refused captions until midnight...
        Assert.Equal(
            FeedBuildMode.Fresh,
            FeedBuildPolicy.Decide(Noon.AddMinutes(-5), Artifact, Artifact, FeedBuildPolicy.TrustPurgeReason, Noon));

        // ... but not user-forced, so the anchors rotate on the daily hold-back, not away from the
        // page the user is looking at.
        Assert.False(FeedBuildPolicy.IsUserForced(FeedBuildPolicy.TrustPurgeReason));
    }

    /// <summary>
    /// R1-16 (ADD-22): the day is the account's. At +02:00, 23:00 and 01:30 UTC are one local day
    /// (Preserve) while 21:30 and 22:30 UTC straddle local midnight (Fresh); with no zone the UTC day
    /// decides, exactly as before the setting existed.
    /// </summary>
    [Theory]
    [InlineData("Africa/Johannesburg", "2026-10-26T23:00:00Z", "2026-10-27T01:30:00Z", FeedBuildMode.Preserve)]
    [InlineData("Africa/Johannesburg", "2026-10-26T21:30:00Z", "2026-10-26T22:30:00Z", FeedBuildMode.Fresh)]
    [InlineData(null, "2026-10-26T23:00:00Z", "2026-10-27T01:30:00Z", FeedBuildMode.Fresh)]
    [InlineData(null, "2026-10-26T21:30:00Z", "2026-10-26T22:30:00Z", FeedBuildMode.Preserve)]
    [InlineData("Mars/Olympus_Mons", "2026-10-26T21:30:00Z", "2026-10-26T22:30:00Z", FeedBuildMode.Preserve)]
    [InlineData("America/Santiago", "2026-10-27T02:30:00Z", "2026-10-27T03:30:00Z", FeedBuildMode.Fresh)]
    public void The_day_that_rolls_over_is_the_accounts_own(string? timeZone, string previous, string now, FeedBuildMode expected)
    {
        Assert.Equal(expected, FeedBuildPolicy.Decide(Utc(previous), Artifact, Artifact, "delta-sync", Utc(now), timeZone));
    }

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
}
