using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// WS-1 task 6, the transport half: the judge's budget at effort none, and the breaker refusing the
/// purpose before anything is sent — the "0 cross-reference calls for the rest of that UTC day" the
/// acceptance reads off the ledger.
/// </summary>
public class CrossReferenceTransportTests
{
    [Fact]
    public async Task The_ceiling_is_a_fixed_allowance_plus_a_per_candidate_share_and_effort_is_none()
    {
        var handler = new Script();
        var judge = Judge(handler);

        await judge.JudgeAsync(Candidates(20), Persona(), CrossReferenceRound.First, ApiUsagePurpose.CrossReference);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var options = new LlmPurposeCatalog(Config()).For(ApiUsagePurpose.CrossReference);
        Assert.Equal(options.MaxTokens + (options.PerItemTokens * 20), doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("none", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task A_truncated_batch_reports_why_after_the_one_2x_retry()
    {
        var handler = new Script { FinishReason = "length" };
        var result = await Judge(handler).JudgeAsync(Candidates(3), Persona(), CrossReferenceRound.First, ApiUsagePurpose.CrossReference);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentFailureKind.Truncated, result.Failure);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Behind_an_open_purpose_breaker_nothing_is_sent_and_nothing_is_billed()
    {
        var handler = new Script();
        var health = new ProviderHealthService();
        var recorder = Recorder();
        health.OpenPurpose(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference, ProviderBreakerReasons.TruncationBreaker,
            DateTime.UtcNow.Date.AddDays(1));
        var judge = Judge(handler, health, recorder);

        for (var run = 0; run < 3; run++)
        {
            var result = await judge.JudgeAsync(Candidates(5), Persona(), CrossReferenceRound.First, ApiUsagePurpose.CrossReference);
            Assert.False(result.Succeeded);
            Assert.Equal(AgentFailureKind.ProviderUnavailable, result.Failure);
        }

        Assert.Empty(handler.Bodies);
        Assert.Equal(0, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.CrossReference).Calls);

        // The breaker is the purpose's own: the backtest (cross-reference-eval) and every other
        // purpose still run.
        var eval = await judge.JudgeAsync(Candidates(5), Persona(), CrossReferenceRound.First, ApiUsagePurpose.CrossReferenceEval);
        Assert.True(eval.Succeeded);
    }

    [Fact]
    public void The_run_key_carries_the_model_and_the_effort_actually_sent()
    {
        Assert.Equal("openai/gpt-6-luna|none", Judge(new Script()).RunKey);
        Assert.Equal("openai/gpt-6-luna|low", Judge(new Script(), config: new() { ["LLM_CROSS_REFERENCE_EFFORT"] = "low" }).RunKey);
    }

    private static ICrossReferenceJudge Judge(
        Script handler, ProviderHealthService? health = null, ApiUsageRecorder? recorder = null,
        Dictionary<string, string?>? config = null)
    {
        var configuration = Config(config);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterCrossReferenceJudge(
            http, recorder ?? Recorder(), configuration, NullLogger<OpenRouterCrossReferenceJudge>.Instance,
            health: health);
    }

    private static IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one" };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ApiUsageRecorder Recorder()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
    }

    private static LensPersona Persona() => new("Likes thrillers.", null, []);

    private static List<CrossReferenceCandidate> Candidates(int n) => Enumerable.Range(0, n)
        .Select(i => new CrossReferenceCandidate(
            Guid.NewGuid(), $"Title {i}", 2000 + i, "Movie", ["thriller"], 7.1m, null, null, [],
            null, null, null, null, null, null, null, null, null, [],
            [new CrossReferenceNeighbour("Heat", 1995, 9, 0.8, Guid.NewGuid())], "An overview."))
        .ToList();

    /// <summary>Answers every request with an empty verdict list, keeping each body.</summary>
    private sealed class Script : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public string FinishReason { get; init; } = "stop";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = """{"results":[]}""" }, finish_reason = FinishReason } },
                usage = new { prompt_tokens = 1000, completion_tokens = 40 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
