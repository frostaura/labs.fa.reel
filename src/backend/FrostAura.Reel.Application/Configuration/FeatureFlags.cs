using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Configuration;

/// <summary>
/// Environment switches, read straight off <see cref="IConfiguration"/>.
///
/// Deliberately NOT <c>IConfiguration.GetValue&lt;bool&gt;</c>: that extension lives in
/// Microsoft.Extensions.Configuration.Binder, which this project does not reference, and pulling a
/// package in to parse "true" would be a poor trade. Anything unset, blank or unparseable falls back
/// to the caller's default, so a typo in a compose file degrades to the documented behaviour rather
/// than silently flipping a switch.
/// </summary>
public static class FeatureFlags
{
    /// <summary>The online residual head. Off ⇒ it loads as zero and drops observes.</summary>
    public const string ResidualHeadEnabled = "RESIDUAL_HEAD_ENABLED";

    /// <summary>Feed Trakt-synced ratings to the residual head. Off until the replay dry run says otherwise.</summary>
    public const string ResidualObserveTraktRatings = "RESIDUAL_OBSERVE_TRAKT_RATINGS";

    /// <summary>
    /// The rating calibrator's selection policy: <c>auto</c> | <c>shift</c> | <c>isotonic</c> |
    /// <c>off</c>, case-insensitive, defaulting to <c>auto</c>. Exists so the policy can be pinned
    /// from the environment while it is evaluated on live data — pinning it is a diagnostic, not a
    /// deployment posture.
    /// </summary>
    public const string CalibratorMode = "CALIBRATOR_MODE";

    /// <summary>New labels since the artifact trained before a retrain is owed (see RetrainPolicy).</summary>
    public const string RetrainMinNewRatings = "RETRAIN_MIN_NEW_RATINGS";

    /// <summary>Reads a whole-number switch, falling back when it is absent, blank or unparseable.</summary>
    public static int ReadInt(IConfiguration? configuration, string key, int fallback) =>
        configuration?[key] is { } raw && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>Reads a boolean switch, falling back to <paramref name="fallback"/> when it is absent or unparseable.</summary>
    public static bool IsEnabled(IConfiguration? configuration, string key, bool fallback) =>
        configuration?[key] is { } raw && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Reads a named-choice switch, falling back to <paramref name="fallback"/> when it is absent or
    /// is not one of the enum's names. <see cref="Enum.IsDefined{TEnum}(TEnum)"/> is the second half
    /// of the guard: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> happily accepts a
    /// bare number, so without it "CALIBRATOR_MODE=7" would parse into a mode that does not exist.
    /// </summary>
    public static TEnum Read<TEnum>(IConfiguration? configuration, string key, TEnum fallback)
        where TEnum : struct, Enum =>
        configuration?[key] is { } raw && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
