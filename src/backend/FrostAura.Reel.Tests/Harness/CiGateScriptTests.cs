using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// <c>deploy/ci/require-tests.sh</c> gates the integration and replay suites. It must fail on a
/// failed run, on a run that executed nothing, and on a run whose test host died after printing a
/// pass count — the last one it once missed, because <c>$?</c> after a pipe is <c>tee</c>'s.
/// </summary>
public class CiGateScriptTests
{
    [Theory]
    [InlineData("echo 'Passed!  - Failed: 0, Passed: 3'; exit 0", 0)]
    [InlineData("echo 'Passed!  - Failed: 0, Passed: 3'; exit 134", 134)]
    [InlineData("echo 'Passed!  - Failed: 0, Passed: 3'; kill -9 $$", 137)]
    [InlineData("echo 'No test matches the given testcase filter'; exit 0", 1)]
    [InlineData("echo 'Failed!  - Failed: 1, Passed: 3'; exit 1", 1)]
    [InlineData("echo 'Failed!  - Failed: 1, Passed: 3'; exit 0", 1)]
    public void Require_tests_passes_only_a_run_that_exited_zero_and_ran_something(string run, int expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var script = Path.Combine(ComposeDescriptor.RepositoryRoot(), "deploy", "ci", "require-tests.sh");
        var start = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { script, "sh", "-c", run })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(expected, process.ExitCode);
    }

    /// <summary>
    /// R1-33: <c>make ci</c> had no Trivy on a laptop while ci.yml installed its own, so the gate was
    /// red locally and its version was pinned in two places. One digest-pinned image now runs in both.
    /// </summary>
    [Fact]
    public void Trivy_is_pinned_once_by_digest_and_the_workflow_runs_it_through_make()
    {
        var root = ComposeDescriptor.RepositoryRoot();
        var makefile = File.ReadAllText(Path.Combine(root, "Makefile"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        var version = Regex.Match(makefile, @"^TRIVY_VERSION := (\d+\.\d+\.\d+)$", RegexOptions.Multiline);
        Assert.True(version.Success, "Makefile: no TRIVY_VERSION pin.");
        Assert.Matches(
            new Regex(@"^TRIVY_IMAGE := aquasec/trivy:\$\(TRIVY_VERSION\)@sha256:[0-9a-f]{64}$", RegexOptions.Multiline),
            makefile);

        Assert.Contains("        run: make ci-trivy\n", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("TRIVY_VERSION", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("aquasecurity/trivy", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("aquasec/trivy", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Trivy_without_docker_or_the_pinned_binary_fails_and_says_how_to_get_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A Docker that cannot be reached and a PATH with no trivy: the gate must fail, not skip.
        var start = new ProcessStartInfo("make") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-s", "-C", ComposeDescriptor.RepositoryRoot(), "ci-trivy" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOCKER_HOST"] = "unix:///nonexistent/reel-ci-trivy.sock";
        start.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Where(dir => !File.Exists(Path.Combine(dir, "trivy"))));

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("ci-trivy: needs Docker running", error, StringComparison.Ordinal);
        Assert.Contains("start Docker Desktop, or install exactly v", error, StringComparison.Ordinal);
    }
}
