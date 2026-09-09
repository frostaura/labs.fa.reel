using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Configuration;

/// <summary>
/// Boolean environment switches, read straight off <see cref="IConfiguration"/>.
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

    /// <summary>Reads a boolean switch, falling back to <paramref name="fallback"/> when it is absent or unparseable.</summary>
    public static bool IsEnabled(IConfiguration? configuration, string key, bool fallback) =>
        configuration?[key] is { } raw && bool.TryParse(raw, out var parsed) ? parsed : fallback;
}
