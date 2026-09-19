namespace FrostAura.Reel.Application.Ml;

/// <summary>Why a retrain is owed — or, when null, that none is.</summary>
public enum RetrainTrigger
{
    /// <summary>Enough labels have arrived since the artifact trained (the ≥N-ratings rule).</summary>
    NewRatings,

    /// <summary>The artifact is simply old (&gt; 14 days), whatever the label flow has been.</summary>
    StaleArtifact,

    /// <summary>
    /// The model is measurably WRONG on this account's own closed loop — not merely stale. This is
    /// the trigger that earns its keep: staleness is a proxy, calibration breach is the thing itself.
    /// </summary>
    CalibrationBreach,
}

/// <summary>
/// When an account is owed a retrain. Pure arithmetic, no EF, so the gate is testable without a
/// database and — more importantly — so the nightly reconcile and the post-sync "retraining tonight"
/// message read the SAME rules. They used to be separate: the reconcile decided, and the pill
/// guessed, which is how a user could be told a retrain was coming that never was.
///
/// <para>The nightly cadence is deliberately unchanged. This adds one condition to it and one floor
/// under it; it does not move the schedule, and it does not make retraining event-driven (the panel
/// refused that: it churns the hero, resets the online head and evicts snapshot history).</para>
/// </summary>
public static class RetrainPolicy
{
    /// <summary>Default for <c>RETRAIN_MIN_NEW_RATINGS</c> — new labels since the artifact trained.</summary>
    public const int DefaultMinNewRatings = 10;

    /// <summary>An artifact older than this retrains regardless of label flow.</summary>
    public const double StaleArtifactDays = 14d;

    /// <summary>|mean(predicted − actual)| above this is a systematically mis-levelled model.</summary>
    public const double CalibrationBiasBreach = 0.75d;

    /// <summary>Mean |predicted − actual| above this is a model that is wrong per-title, not just off-level.</summary>
    public const double CalibrationMaeBreach = 2.0d;

    /// <summary>
    /// Closed-loop pairs required before a breach counts. Below this the diagnostics are noise, and
    /// retraining on noise is how a rating spree turns into a nightly model churn.
    /// </summary>
    public const int CalibrationMinPairs = 30;

    /// <summary>
    /// Floor between COMPLETED trains, per account. The breach trigger reads the same closed loop the
    /// previous train was fitted against, so without a floor a genuinely hard account would retrain
    /// every single night against numbers that have not moved.
    /// </summary>
    public const double MinDaysBetweenTrains = 1d;

    /// <summary>The hour (UTC) the nightly reconcile runs — the answer to "retraining when?".</summary>
    public const int NightlyRunHourUtc = 3;

    /// <summary>The next 03:00 UTC at or after <paramref name="utcNow"/>.</summary>
    public static DateTime NextNightlyRunUtc(DateTime utcNow)
    {
        var todayRun = utcNow.Date.AddHours(NightlyRunHourUtc);
        return utcNow < todayRun ? todayRun : todayRun.AddDays(1);
    }

    /// <summary>The original drift rule, unchanged: enough new labels, or an artifact past its shelf life.</summary>
    public static bool DriftDue(int newRatingsSinceTrain, int threshold, DateTime trainedAtUtc, DateTime utcNow) =>
        newRatingsSinceTrain >= threshold || (utcNow - trainedAtUtc).TotalDays > StaleArtifactDays;

    /// <summary>
    /// True when the account's own predicted-vs-actual pairs say the model is wrong: enough fresh
    /// pairs to measure, and either the level or the per-title error past its threshold.
    /// </summary>
    public static bool CalibrationBreached(int pairCount, double bias, double meanAbsoluteError) =>
        pairCount >= CalibrationMinPairs
        && (Math.Abs(bias) > CalibrationBiasBreach || meanAbsoluteError > CalibrationMaeBreach);

    /// <summary>True while the one-day floor since the last completed train still holds.</summary>
    public static bool WithinTrainFloor(DateTime lastTrainedAtUtc, DateTime utcNow) =>
        (utcNow - lastTrainedAtUtc).TotalDays < MinDaysBetweenTrains;

    /// <summary>
    /// The whole gate in one call. Returns the trigger that fired, or null when the account is not
    /// owed a retrain. The floor applies ONLY to the calibration trigger: the ≥N-ratings and
    /// stale-artifact rules are the established nightly cadence and are not narrowed by this work.
    /// </summary>
    public static RetrainTrigger? Decide(
        int newRatingsSinceTrain,
        int threshold,
        DateTime trainedAtUtc,
        int calibrationPairCount,
        double calibrationBias,
        double calibrationMae,
        DateTime utcNow)
    {
        if (newRatingsSinceTrain >= threshold)
        {
            return RetrainTrigger.NewRatings;
        }

        if ((utcNow - trainedAtUtc).TotalDays > StaleArtifactDays)
        {
            return RetrainTrigger.StaleArtifact;
        }

        if (CalibrationBreached(calibrationPairCount, calibrationBias, calibrationMae)
            && !WithinTrainFloor(trainedAtUtc, utcNow))
        {
            return RetrainTrigger.CalibrationBreach;
        }

        return null;
    }
}
