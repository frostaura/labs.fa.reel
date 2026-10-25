using System.Diagnostics;

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
}
