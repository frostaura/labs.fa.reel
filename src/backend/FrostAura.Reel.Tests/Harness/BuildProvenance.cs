using System.Reflection;
using FrostAura.Reel.Api;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// What THIS test build was given to stamp, for the <c>/health</c> commit tests (A-17, HREV1-PLAT-6).
///
/// <para>The SDK stamps <c>SourceRevisionId</c> into every assembly of one build by the same mechanism:
/// from the enclosing git checkout, or from an explicit <c>-p:SourceRevisionId</c> (the image's
/// <c>GIT_SHA</c>). A build from a <c>git archive</c> outside any checkout — the program's own reviewer
/// path — is given neither, and correctly reports <c>"unknown"</c>; a test that demanded a SHA there
/// failed every honest archive build. The test assembly's own stamp is therefore the expectation: the
/// API must report exactly what its sibling assembly was stamped with, and must report a full SHA
/// whenever the build sat inside a checkout or was handed one.</para>
/// </summary>
internal static class BuildProvenance
{
    public const string FullSha = "^[0-9a-f]{40}$";

    /// <summary>The commit stamped into this test assembly, or <c>"unknown"</c>.</summary>
    public static string StampedCommit { get; } = ReleaseInfo.Parse(
        typeof(BuildProvenance).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Whether the build output sits inside a git checkout — a <c>.git</c> directory, or the <c>.git</c>
    /// file of a worktree — found walking up as the SDK's repository lookup does.
    /// </summary>
    public static bool BuiltInsideACheckout { get; } = FindCheckout(AppContext.BaseDirectory);

    /// <summary>Whether the build must have produced a full SHA: inside a checkout, or handed one explicitly.</summary>
    public static bool MustBeStamped => BuiltInsideACheckout || StampedCommit != "unknown";

    internal static bool FindCheckout(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var marker = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }
}
