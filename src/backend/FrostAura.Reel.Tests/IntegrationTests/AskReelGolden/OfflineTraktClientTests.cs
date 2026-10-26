using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Eval;
using FrostAura.Reel.Tests.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// The eval stack's fake <see cref="ITraktClient"/> (R1-38, program §4.12 and §7 rule 1): with
/// <c>TRAKT_CLIENT_ID=eval-no-trakt</c> no Trakt call can leave the process, so no eval run can rotate the
/// founder's single-use refresh token or write to his profile. Pure: no database, no network, no host.
/// </summary>
public class OfflineTraktClientTests
{
    /// <summary>Every member of the port, so a method added to it later is held to the same rule.</summary>
    public static TheoryData<string> PortMethods()
    {
        var data = new TheoryData<string>();
        foreach (var method in typeof(ITraktClient).GetMethods())
        {
            data.Add(method.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PortMethods))]
    public async Task Every_Trakt_call_fails_locally_the_way_an_unreachable_Trakt_fails(string name)
    {
        var client = new OfflineTraktClient(NullLogger<OfflineTraktClient>.Instance);
        var method = typeof(ITraktClient).GetMethod(name)!;
        var arguments = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : p.ParameterType == typeof(string) ? "x" : new object()).ToArray();

        var task = (Task)method.Invoke(client, arguments)!;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => task);
        Assert.Contains("offline", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.StatusCode); // no status: callers treat it as the network being down, never as a 401 to refresh on
        Assert.Equal(1, client.Refused);
    }

    [Fact]
    public async Task Each_refusal_is_counted_and_logged_as_a_warning()
    {
        var log = new RecordingLogger();
        var client = new OfflineTraktClient(log);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshTokenAsync("refresh"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostSyncBatchAsync("access", "sync/history", new { }, RatePriority.Backfill));

        Assert.Equal(2, client.Refused);
        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains("oauth/token (refresh)", log.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("sync/history", log.Warnings[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eval-no-trakt", true)]
    [InlineData(" eval-no-trakt ", true)]
    [InlineData("EVAL-NO-TRAKT", true)]
    [InlineData("a1b2c3-real-trakt-client", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_eval_client_id_replaces_the_real_adapter_and_nothing_else_does(string? clientId, bool offline)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The registration DependencyInjection.Data.cs makes for the real adapter.
        services.AddHttpClient<ITraktClient, TraktClient>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TRAKT_CLIENT_ID"] = clientId }).Build();

        services.AddReelEvalIsolation(configuration);

        Assert.Equal(offline ? 1 : 0, services.Count(d => d.ServiceType == typeof(OfflineTraktClient)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(ITraktClient)));
        if (offline)
        {
            using var provider = services.BuildServiceProvider();
            Assert.IsType<OfflineTraktClient>(provider.GetRequiredService<ITraktClient>());
            Assert.Same(provider.GetRequiredService<OfflineTraktClient>(), provider.GetRequiredService<ITraktClient>());
        }
    }

    [Fact]
    public void Both_eval_launchers_run_with_the_eval_client_id()
    {
        var compose = ComposeDescriptor.Load("docker-compose.eval.yml").Environment("eval-api");
        Assert.Equal(OfflineTraktClient.EvalClientId, compose["TRAKT_CLIENT_ID"]);

        var runApi = File.ReadAllText(Path.Combine(ComposeDescriptor.RepositoryRoot(), "scripts", "eval", "run-api.sh"));
        Assert.Contains($"export TRAKT_CLIENT_ID={OfflineTraktClient.EvalClientId} ", runApi, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger<OfflineTraktClient>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
