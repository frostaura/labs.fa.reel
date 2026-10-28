using System.Diagnostics;
using FrostAura.Reel.Api.Endpoints;

namespace FrostAura.Reel.Tests.Observability;

/// <summary>
/// The metrics report's <c>clientErrors.since</c> and <c>llmSince</c> (HREV5-PLAT-01). Both in-process
/// tallies are DI singletons that every report and every model call resolves before it is counted, so
/// nothing is counted before the process starts and everything after is: the honest "since" is the
/// process's own start, never the first time something happened to read the stamp.
/// </summary>
public sealed class MetricsSinceTests
{
    [Fact]
    public void The_since_stamp_is_the_operating_systems_record_of_this_process_start()
    {
        DateTime processStartedAt;
        using (var self = Process.GetCurrentProcess())
        {
            processStartedAt = self.StartTime.ToUniversalTime();
        }

        // The test host has been up for seconds by the time any test runs, so a stamp taken on first
        // read lands well outside this tolerance; the operating system's record lands inside it.
        Assert.True(
            (ObservabilityEndpoints.ProcessStartedAt - processStartedAt).Duration() < TimeSpan.FromSeconds(1),
            $"since {ObservabilityEndpoints.ProcessStartedAt:O} is not this process's start {processStartedAt:O}");
        Assert.Equal(DateTimeKind.Utc, ObservabilityEndpoints.ProcessStartedAt.Kind);
    }

    [Fact]
    public void The_since_stamp_precedes_anything_this_process_could_have_counted_and_does_not_move()
    {
        var first = ObservabilityEndpoints.ProcessStartedAt;
        Thread.Sleep(20);
        var now = DateTime.UtcNow;

        Assert.True(first < now, $"since {first:O} is not before now {now:O}");
        Assert.Equal(first, ObservabilityEndpoints.ProcessStartedAt);
    }
}
