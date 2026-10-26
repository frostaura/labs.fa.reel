using System.Reflection;

namespace FrostAura.Reel.Api;

/// <summary>
/// The commit this process was built from, for <c>/health</c> (A-17) and the deploy job's health
/// poll, which fails unless the running SHA is the one it just shipped.
///
/// <para>The SDK stamps the commit into <c>AssemblyInformationalVersion</c> as <c>version+sha</c>
/// (<c>SourceRevisionId</c>): from the git checkout on a developer build, and from the
/// <c>GIT_SHA</c> build argument in the image, where <c>.git</c> is not in the build context.</para>
/// </summary>
public static class ReleaseInfo
{
    /// <summary>The full commit SHA, or <c>"unknown"</c> for a build that was given none.</summary>
    public static string Commit { get; } = Parse(
        typeof(ReleaseInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The SHA after the <c>+</c> of an informational version, or <c>"unknown"</c>.</summary>
    internal static string Parse(string? informational)
    {
        var plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        var sha = plus >= 0 ? informational![(plus + 1)..] : string.Empty;
        return sha.Length > 0 ? sha : "unknown";
    }
}
