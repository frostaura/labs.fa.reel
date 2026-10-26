using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// Which Ask Reel engine answers a turn (§4.13, WS-2's own typed options). <c>ASK_ENGINE</c> is
/// <c>v1</c> (the default) or <c>v2</c>; anything else reads as <c>v1</c>, so a typo can never switch
/// the engine on. v1 stays selectable until the cutover soak ends.
/// </summary>
public static class AskReelEngineOptions
{
    public const string Setting = "ASK_ENGINE";

    public const string V1 = "v1";

    public const string V2 = "v2";

    /// <summary>The configured engine.</summary>
    public static string Configured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Setting]) ?? V1;
    }

    /// <summary>
    /// The engine for one request: a founder's explicit <c>?engine=</c> override when it names a known
    /// engine, else the configured one. The override is the caller's to gate.
    /// </summary>
    public static string For(IConfiguration configuration, string? founderOverride) =>
        Parse(founderOverride) ?? Configured(configuration);

    private static string? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            V1 => V1,
            V2 => V2,
            _ => null,
        };
}
