using System.Collections.Concurrent;
using FrostAura.Reel.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// One id per request in the <c>X-Request-Id</c> response header and in every log line's
/// <c>RequestId</c> (R1-36). Runs the real ASP.NET Core hosting layer in memory — the property
/// under test is that the hosting scope, which is opened before any middleware, carries the
/// header's value — with no database and nothing of Reel's but <see cref="RequestId"/>.
/// </summary>
public sealed class RequestIdTests : IAsyncLifetime
{
    private readonly CapturingLoggerProvider logs = new();
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddRequestIds();

        app = builder.Build();
        app.MapGet("/probe", (ILogger<RequestIdTests> logger) =>
        {
            logger.LogInformation("probe handled");
            return Results.Ok();
        });
        app.MapGet("/cleared", (HttpContext context, ILogger<RequestIdTests> logger) =>
        {
            // What an exception handler does to a half-built response.
            context.Response.Headers["X-Other"] = "dropped";
            context.Response.Clear();
            logger.LogWarning("cleared");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task A_valid_inbound_id_is_echoed_and_is_the_only_request_id_the_log_line_carries()
    {
        var response = await SendAsync("/probe", "3f2a9c0d4e5b6a7c8d9e0f1a2b3c4d5e");

        Assert.Equal("3f2a9c0d4e5b6a7c8d9e0f1a2b3c4d5e", Assert.Single(response.Headers.GetValues(RequestId.HeaderName)));
        var line = Assert.Single(logs.Lines, l => l.Message == "probe handled");
        Assert.Equal(["3f2a9c0d4e5b6a7c8d9e0f1a2b3c4d5e"], line.RequestIds);
    }

    [Fact]
    public async Task Without_an_inbound_id_the_server_mints_one_and_returns_the_one_it_logged()
    {
        var first = await SendAsync("/probe", inbound: null);
        var second = await SendAsync("/probe", inbound: null);

        var firstId = Assert.Single(first.Headers.GetValues(RequestId.HeaderName));
        var secondId = Assert.Single(second.Headers.GetValues(RequestId.HeaderName));
        Assert.True(RequestId.IsValid(firstId), firstId);
        Assert.NotEqual(firstId, secondId);

        var lines = logs.Lines.Where(l => l.Message == "probe handled").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal([firstId], lines[0].RequestIds);
        Assert.Equal([secondId], lines[1].RequestIds);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("quote\"break")]
    [InlineData("{\"RequestId\":\"x\"}")]
    [InlineData("ünïcode")]
    [InlineData("a1234567890123456789012345678901234567890123456789012345678901234")] // 65 characters
    public async Task An_inbound_value_that_is_not_a_plain_token_is_dropped_not_echoed(string inbound)
    {
        var response = await SendAsync("/probe", inbound);

        var id = Assert.Single(response.Headers.GetValues(RequestId.HeaderName));
        Assert.NotEqual(inbound, id);
        Assert.True(RequestId.IsValid(id), id);
        Assert.Equal([id], Assert.Single(logs.Lines, l => l.Message == "probe handled").RequestIds);
    }

    [Fact]
    public async Task Two_inbound_ids_are_ambiguous_and_neither_is_chosen()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.TryAddWithoutValidation(RequestId.HeaderName, ["first-id", "second-id"]);
        var response = await client.SendAsync(request);

        var id = Assert.Single(response.Headers.GetValues(RequestId.HeaderName));
        Assert.DoesNotContain(id, new[] { "first-id", "second-id", "first-id,second-id" });
    }

    [Fact]
    public async Task A_response_cleared_by_its_handler_still_carries_the_id()
    {
        var response = await SendAsync("/cleared", "cleared-req.1:2");

        Assert.Equal(503, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("X-Other"));
        Assert.Equal("cleared-req.1:2", Assert.Single(response.Headers.GetValues(RequestId.HeaderName)));
        Assert.Equal(["cleared-req.1:2"], Assert.Single(logs.Lines, l => l.Message == "cleared").RequestIds);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("0HNABCDEF:00000001", true)]
    [InlineData("A-b_c.d:e", true)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", true)] // exactly 64
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", false)] // 65
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    [InlineData("semi;colon", false)]
    public void The_token_rule(string? value, bool valid) => Assert.Equal(valid, RequestId.IsValid(value));

    private async Task<HttpResponseMessage> SendAsync(string path, string? inbound)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (inbound is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(RequestId.HeaderName, inbound));
        }

        return await client.SendAsync(request);
    }

    private sealed record LogLine(string Message, IReadOnlyList<string> RequestIds);

    /// <summary>Records each line with every <c>RequestId</c> value its scopes carry, as the JSON console would.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogLine> lines = new();
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        public IReadOnlyList<LogLine> Lines => [.. lines];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner.scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var ids = new List<string>();
                owner.scopes.ForEachScope(
                    (scope, found) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            found.AddRange(pairs.Where(p => p.Key == "RequestId").Select(p => p.Value?.ToString() ?? string.Empty));
                        }
                    },
                    ids);
                owner.lines.Enqueue(new LogLine(formatter(state, exception), ids));
            }
        }
    }
}
