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
/// </summary>
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
}
