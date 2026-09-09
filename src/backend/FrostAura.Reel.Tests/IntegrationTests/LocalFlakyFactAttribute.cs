namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Marks an integration test that is a KNOWN local-only failure on the dev Mac and passes on
/// CI's <c>pgvector/pgvector:pg16</c> service, which is authoritative for this suite.
///
/// The quarantine is deliberately one-way: the test still RUNS everywhere by default, so CI
/// keeps gating on it and a genuine regression still turns the build red. Only an explicit
/// <c>REEL_SKIP_LOCAL_FLAKY=1</c> in the environment skips it — that variable is set nowhere
/// in <c>.github/workflows/ci.yml</c> and belongs only in the recommended local command
/// (see <c>README.md</c> / <c>CLAUDE.md</c> "Build / test / run").
///
/// Why the marker exists at all: before it, the split between "known local flake" and "real
/// regression" lived only in <c>memory/state.md</c>, so a genuine break in one of these files
/// was likely to be waved off locally. Every use carries an inline note of the observed local
/// symptom, so the next reader can tell the two apart without leaving the file.
///
/// Pair it with <c>[Trait("Flaky", "local")]</c> at each site so the set is also selectable
/// from the runner (<c>--filter "Flaky!=local"</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalFlakyFactAttribute : FactAttribute
{
    /// <summary>Set this to <c>1</c> locally to skip the quarantined set. Never set in CI.</summary>
    public const string SkipVariable = "REEL_SKIP_LOCAL_FLAKY";

    public LocalFlakyFactAttribute()
    {
        if (ShouldSkip(Environment.GetEnvironmentVariable(SkipVariable)))
        {
            Skip = $"Known local-only failure; skipped because {SkipVariable}=1. CI runs it.";
        }
    }

    /// <summary>
    /// The whole decision, as a pure function so it can be asserted without an environment.
    /// Only the exact string "1" skips — an empty or absent variable must never quarantine
    /// anything, because that is the CI configuration.
    /// </summary>
    public static bool ShouldSkip(string? skipVariableValue) => skipVariableValue == "1";
}
