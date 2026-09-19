using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The retrain gate's arithmetic. The nightly cadence and the ≥N-ratings rule are unchanged and
/// pinned here so the calibration trigger cannot quietly narrow them; the new rule — retrain when
/// the model is measurably WRONG, not merely stale — is pinned with its floor, because a breach
/// reads the same pairs the last train was fitted against and would otherwise fire every night.
/// </summary>
public class RetrainPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_new_ratings_rule_is_unchanged()
    {
        Assert.True(RetrainPolicy.DriftDue(10, 10, Now.AddHours(-1), Now));
        Assert.False(RetrainPolicy.DriftDue(9, 10, Now.AddHours(-1), Now));
    }

    [Fact]
    public void The_fourteen_day_shelf_life_is_unchanged()
    {
        Assert.True(RetrainPolicy.DriftDue(0, 10, Now.AddDays(-15), Now));
        Assert.False(RetrainPolicy.DriftDue(0, 10, Now.AddDays(-13), Now));
    }

    [Theory]
    [InlineData(30, 0.80, 1.0, true)]   // bias breach at exactly the minimum sample
    [InlineData(30, 0.0, 2.5, true)]    // MAE breach — wrong per title, not merely off-level
    [InlineData(30, -0.9, 1.0, true)]   // the breach is on |bias|, so under-promising counts too
    [InlineData(29, 5.0, 9.0, false)]   // wildly wrong on 29 pairs is still noise
    [InlineData(30, 0.75, 2.0, false)]  // exactly at both thresholds is not past them
    [InlineData(200, 0.37, 1.94, false)] // the founder's live numbers today: bad, but not a breach
    public void A_calibration_breach_needs_enough_pairs_and_a_threshold_actually_crossed(
        int pairs, double bias, double mae, bool expected) =>
        Assert.Equal(expected, RetrainPolicy.CalibrationBreached(pairs, bias, mae));

    [Fact]
    public void The_one_day_floor_holds_for_a_train_less_than_a_day_old()
    {
        Assert.True(RetrainPolicy.WithinTrainFloor(Now.AddHours(-23), Now));
        Assert.False(RetrainPolicy.WithinTrainFloor(Now.AddHours(-25), Now));
    }

    [Fact]
    public void A_breach_inside_the_floor_does_not_retrain() =>
        Assert.Null(RetrainPolicy.Decide(
            newRatingsSinceTrain: 0, threshold: 10, trainedAtUtc: Now.AddHours(-6),
            calibrationPairCount: 50, calibrationBias: 3.0, calibrationMae: 4.0, utcNow: Now));

    [Fact]
    public void A_breach_past_the_floor_retrains() =>
        Assert.Equal(RetrainTrigger.CalibrationBreach, RetrainPolicy.Decide(
            newRatingsSinceTrain: 0, threshold: 10, trainedAtUtc: Now.AddDays(-3),
            calibrationPairCount: 50, calibrationBias: 3.0, calibrationMae: 4.0, utcNow: Now));

    [Fact]
    public void The_floor_never_suppresses_the_established_rules() =>
        // Trained an hour ago — inside the floor — but ten new labels have landed. The nightly
        // cadence must not be narrowed by a rule that was added beside it.
        Assert.Equal(RetrainTrigger.NewRatings, RetrainPolicy.Decide(
            newRatingsSinceTrain: 10, threshold: 10, trainedAtUtc: Now.AddHours(-1),
            calibrationPairCount: 0, calibrationBias: 0, calibrationMae: 0, utcNow: Now));

    [Fact]
    public void A_healthy_well_fed_recent_model_is_owed_nothing() =>
        Assert.Null(RetrainPolicy.Decide(
            newRatingsSinceTrain: 3, threshold: 10, trainedAtUtc: Now.AddDays(-2),
            calibrationPairCount: 40, calibrationBias: 0.2, calibrationMae: 1.1, utcNow: Now));

    [Theory]
    [InlineData("2026-09-09T02:59:00Z", "2026-09-09T03:00:00Z")]
    [InlineData("2026-09-09T03:00:00Z", "2026-09-10T03:00:00Z")]
    [InlineData("2026-09-09T18:30:00Z", "2026-09-10T03:00:00Z")]
    public void The_next_nightly_run_is_the_next_oh_three_hundred_utc(string now, string expected) =>
        Assert.Equal(
            DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal),
            RetrainPolicy.NextNightlyRunUtc(DateTime.Parse(now, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal)));
}
