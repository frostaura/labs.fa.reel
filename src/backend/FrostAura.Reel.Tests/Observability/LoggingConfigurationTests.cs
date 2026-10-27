using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace FrostAura.Reel.Tests.Observability;

/// <summary>
/// The API's shipped logging configuration (C+2, J-17, K-7, E-14, F-26). The base file — the one
/// production reads — keeps SQL text, HTTP client chatter and Polly at Warning, and writes one JSON
/// object per line with scopes, so every request's lines carry its RequestId and every job's lines
/// its JobId. On 2026-10-22 Information-level EF command logging was 64k lines in one hour and
/// rotated the evidence for two incidents away in under three days.
///
/// <para>Runs alone: one test swaps the process-wide <see cref="Console.Out"/> for a writer that
/// stalls, which would stall every other test host's console logging while it ran.</para>
/// </summary>
[Collection(nameof(ConsoleOutCollection))]
public class LoggingConfigurationTests
{
    private static IConfiguration Load(params string[] files)
    {
        var directory = ApiDirectory();
        var builder = new ConfigurationBuilder();
        foreach (var file in files)
        {
            builder.AddJsonFile(Path.Combine(directory, file), optional: false);
        }

        return builder.Build();
    }

    private static string ApiDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "backend", "FrostAura.Reel.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find src/backend/FrostAura.Reel.Api/appsettings.json above the test output.");
    }

    public static TheoryData<string[]> Environments() => new()
    {
        new[] { "appsettings.json" },
        new[] { "appsettings.json", "appsettings.Development.json" },
    };

    [Theory]
    [MemberData(nameof(Environments))]
    public void Sql_text_http_client_chatter_and_polly_are_quiet_below_warning(string[] files)
    {
        using var factory = LoggerFactory.Create(b => b.AddConfiguration(Load(files).GetSection("Logging")).AddConsole());

        foreach (var category in new[]
        {
            "Microsoft.EntityFrameworkCore.Database.Command",
            "Microsoft.EntityFrameworkCore.Infrastructure",
            "System.Net.Http.HttpClient.OpenRouter.LogicalHandler",
            "System.Net.Http.HttpClient.Default.ClientHandler",
            "Polly",
            "Microsoft.AspNetCore.Hosting.Diagnostics",
        })
        {
            var logger = factory.CreateLogger(category);
            Assert.False(logger.IsEnabled(LogLevel.Information), $"{category} logs at Information");
            Assert.True(logger.IsEnabled(LogLevel.Warning), $"{category} hides warnings");
        }

        // The application's own lines are what the logs are for.
        Assert.True(factory.CreateLogger("FrostAura.Reel.Infrastructure.Background.JobRunnerService").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void The_console_writes_single_line_utc_json_with_scopes()
    {
        var console = Load("appsettings.json").GetSection("Logging:Console");

        var options = console.Get<ConsoleLoggerOptions>()!;
        Assert.Equal(ConsoleFormatterNames.Json, options.FormatterName);

        var json = console.GetSection("FormatterOptions").Get<JsonConsoleFormatterOptions>()!;
        Assert.True(json.IncludeScopes, "without scopes a line has no RequestId or JobId");
        Assert.True(json.UseUtcTimestamp);
        Assert.False(json.JsonWriterOptions.Indented);
        Assert.False(string.IsNullOrEmpty(json.TimestampFormat));
    }

    /// <summary>
    /// The F+1 stall mechanism (WS-6 task 9). The console logger queues lines for a background
    /// writer; when the queue is full its default is to make the logging thread WAIT. Under Docker
    /// the writer is the container's stdout, and the default log driver blocks stdout when it cannot
    /// keep up. EF Core logs every command it executes, inside SaveChanges' transaction, between
    /// batches; a thread parked there leaves its transaction idle with its row locks held, where no
    /// command timeout applies. The shipped configuration drops lines instead (the logger reports
    /// how many it dropped), so a slow stdout can cost log lines but never a job or its locks.
    /// </summary>
    [Fact]
    public void A_stalled_stdout_drops_lines_instead_of_blocking_the_thread_that_logs()
    {
        var shipped = Load("appsettings.json").GetSection("Logging:Console").Get<ConsoleLoggerOptions>()!;
        Assert.Equal(ConsoleLoggerQueueFullMode.DropWrite, shipped.QueueFullMode);

        // The shipped mode returns at once with stdout stalled; the framework default would park the caller.
        Assert.True(LogsWithoutBlocking(mode: null), "the shipped configuration blocked a logging thread on a stalled stdout");
        Assert.False(LogsWithoutBlocking(ConsoleLoggerQueueFullMode.Wait), "Wait mode no longer blocks: this test no longer shows the mechanism");
    }

    /// <summary>
    /// Logs a burst through the shipped configuration (optionally overriding the full-queue mode)
    /// while stdout accepts nothing, and says whether the burst returned within two seconds.
    /// </summary>
    private static bool LogsWithoutBlocking(ConsoleLoggerQueueFullMode? mode)
    {
        using var stdoutOpen = new ManualResetEventSlim(false);
        var original = Console.Out;
        Console.SetOut(new StalledWriter(stdoutOpen));
        try
        {
            using var factory = LoggerFactory.Create(b => b
                .AddConfiguration(Load("appsettings.json").GetSection("Logging"))
                .AddConsole(o =>
                {
                    // A small queue so the burst fills it at once; the mode is the shipped one unless overridden.
                    o.MaxQueueLength = 4;
                    if (mode is { } m)
                    {
                        o.QueueFullMode = m;
                    }
                }));
            var logger = factory.CreateLogger("FrostAura.Reel.Tests.Backpressure");
            var burst = Task.Run(() =>
            {
                for (var i = 0; i < 64; i++)
                {
                    logger.LogInformation("Line {Index} while stdout is stalled.", i);
                }
            });
            var returned = burst.Wait(TimeSpan.FromSeconds(2));

            // Unstall stdout so the writer thread and any parked caller drain before the factory is disposed.
            stdoutOpen.Set();
            Assert.True(burst.Wait(TimeSpan.FromSeconds(10)), "the burst never finished after stdout reopened");
            return returned;
        }
        finally
        {
            stdoutOpen.Set();
            Console.SetOut(original);
        }
    }

    /// <summary>A stdout that accepts nothing until it is opened.</summary>
    private sealed class StalledWriter(ManualResetEventSlim open) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => open.Wait();

        public override void Write(string? value) => open.Wait();

        public override void Write(char[] buffer, int index, int count) => open.Wait();
    }
}

/// <summary>Tests that swap the process-wide <see cref="Console.Out"/> run with nothing else running.</summary>
[CollectionDefinition(nameof(ConsoleOutCollection), DisableParallelization = true)]
public sealed class ConsoleOutCollection;
