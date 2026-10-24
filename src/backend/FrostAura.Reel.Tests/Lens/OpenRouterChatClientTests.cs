using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The shared chat transport (WS-1 task 2) and the per-purpose options it reads (task 1), driven by a
/// scripted HTTP handler — no network, no key, no spend. Each fact pins one rule the five adapters
/// used to hold separately and inconsistently.
/// </summary>
public class OpenRouterChatClientTests
{
    private static readonly object Schema = new
    {
        type = "json_schema",
        json_schema = new { name = "t", strict = true, schema = new { type = "object" } },
    };

    // ── Task 1: purpose options ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ApiUsagePurpose.AskPlan)]
    [InlineData(ApiUsagePurpose.AskJudge)]
    [InlineData(ApiUsagePurpose.AskAnswer)]
    [InlineData(ApiUsagePurpose.RerankFeed)]
    [InlineData(ApiUsagePurpose.RerankPage)]
    [InlineData(ApiUsagePurpose.CrossReference)]
    [InlineData(ApiUsagePurpose.Lens)]
    [InlineData(ApiUsagePurpose.TasteNarrative)]
    public void The_listed_purposes_run_at_effort_none(string purpose) =>
        Assert.Equal(ReasoningEfforts.None, new LlmPurposeCatalog(Config()).For(purpose).ReasoningEffort);

    [Fact]
    public void Extraction_runs_with_no_reasoning_parameter_and_no_immediate_retries()
    {
        var o = new LlmPurposeCatalog(Config()).For(ApiUsagePurpose.ExtractAttributes);
        Assert.Null(o.ReasoningEffort);
        Assert.Equal(500, o.MaxTokens);
        Assert.Equal(0, o.MaxRetries);
        Assert.False(o.RetryOnLength);
    }

    [Fact]
    public void Every_field_is_overridable_per_purpose_and_a_blank_or_invalid_value_is_ignored()
    {
        var catalog = new LlmPurposeCatalog(Config(new()
        {
            ["LLM_ASK_PLAN_MODEL"] = "openai/pinned",
            ["LLM_ASK_PLAN_EFFORT"] = "LOW",
            ["LLM_ASK_PLAN_MAX_TOKENS"] = "333",
            ["LLM_ASK_PLAN_MAX_RETRIES"] = "0",
            ["LLM_ASK_PLAN_ATTEMPT_TIMEOUT_SECONDS"] = "2.5",
            ["LLM_ASK_PLAN_TOTAL_TIMEOUT_SECONDS"] = "7",
            ["LLM_ASK_JUDGE_EFFORT"] = "galaxy-brain",
            ["LLM_ASK_JUDGE_MAX_TOKENS"] = "   ",
            ["LLM_ASK_ANSWER_EFFORT"] = "omit",
        }));

        var plan = catalog.For(ApiUsagePurpose.AskPlan);
        Assert.Equal("openai/pinned", plan.Model);
        Assert.Equal(ReasoningEfforts.Low, plan.ReasoningEffort);
        Assert.Equal(333, plan.MaxTokens);
        Assert.Equal(0, plan.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2.5), plan.AttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7), plan.TotalTimeout);

        var judge = catalog.For(ApiUsagePurpose.AskJudge);
        Assert.Equal(ReasoningEfforts.None, judge.ReasoningEffort);
        Assert.Equal(900, judge.MaxTokens);

        Assert.Null(catalog.For(ApiUsagePurpose.AskAnswer).ReasoningEffort);
    }

    [Theory]
    // The search family's old rule: REEL_SEARCH_MODEL, then OPENROUTER_MODEL, then luna; blank is absent.
    [InlineData(ApiUsagePurpose.SearchAgent, "s", "o", "s")]
    [InlineData(ApiUsagePurpose.RerankFeed, "", "o", "o")]
    [InlineData(ApiUsagePurpose.AskJudge, null, null, "openai/gpt-6-luna")]
    // The background writers' old rule: OPENROUTER_MODEL then luna — REEL_SEARCH_MODEL never applies.
    [InlineData(ApiUsagePurpose.CrossReference, "s", "o", "o")]
    [InlineData(ApiUsagePurpose.Lens, "s", "", "openai/gpt-6-luna")]
    public void Model_resolution_is_each_adapters_old_rule(string purpose, string? search, string? general, string expected)
    {
        var o = new LlmPurposeCatalog(Config(new() { ["REEL_SEARCH_MODEL"] = search, ["OPENROUTER_MODEL"] = general }))
            .For(purpose);
        Assert.Equal(expected, o.Model);
    }

    [Fact]
    public void A_batch_ceiling_is_a_fixed_allowance_plus_a_per_candidate_share()
    {
        var o = new LlmPurposeCatalog(Config(new()
        {
            ["LLM_CROSS_REFERENCE_MAX_TOKENS"] = "1000",
            ["LLM_CROSS_REFERENCE_TOKENS_PER_CANDIDATE"] = "50",
        })).For(ApiUsagePurpose.CrossReference);
        Assert.Equal(1000 + (50 * 60), o.MaxTokensFor(60));
        Assert.Equal(1000 + 50, o.MaxTokensFor(1));
    }

    [Fact]
    public void A_probe_runs_at_the_options_of_the_purpose_it_measures()
    {
        var catalog = new LlmPurposeCatalog(Config(new() { ["LLM_ASK_PLAN_MAX_TOKENS"] = "444" }));
        Assert.Equal(catalog.For(ApiUsagePurpose.AskPlan), catalog.For(ApiUsagePurpose.Probe(ApiUsagePurpose.AskPlan)));
    }

    // ── Task 2: the request ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Effort_rides_the_request_as_reasoning_effort_after_the_messages()
    {
        var handler = new Script(Ok("{}"));
        await Client(handler).CompleteAsync(Request(ApiUsagePurpose.AskPlan), CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("none", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(600, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(
            ["model", "temperature", "max_tokens", "response_format", "messages", "reasoning"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_model_that_rejects_effort_none_falls_back_to_low_and_remembers_it()
    {
        var capabilities = new ModelCapabilities();
        var handler = new Script(
            new Canned(HttpStatusCode.BadRequest, """{"error":{"message":"Reasoning effort 'none' is not supported for this model"}}"""),
            Ok("{}"),
            Ok("{}"));
        var client = Client(handler, capabilities: capabilities);

        var first = await client.CompleteAsync(Request(ApiUsagePurpose.AskJudge), CancellationToken.None);
        var second = await client.CompleteAsync(Request(ApiUsagePurpose.AskJudge), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal("low", first.Effort);
        Assert.Equal(["none", "low", "low"], handler.Bodies.Select(EffortOf));
        Assert.True(second.Succeeded);
        // The cache salt follows the effort actually sent, so a verdict written at "low" is a
        // different key from one written at "none".
        Assert.EndsWith("|low", client.CacheSalt(ApiUsagePurpose.AskJudge), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strict_schema_rejection_resends_as_json_object_for_that_model()
    {
        var capabilities = new ModelCapabilities();
        var handler = new Script(
            new Canned(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok("{}"), Ok("{}"));
        var client = Client(handler, capabilities: capabilities);

        await client.CompleteAsync(Request(ApiUsagePurpose.Lens, Schema), CancellationToken.None);
        await client.CompleteAsync(Request(ApiUsagePurpose.Lens, Schema), CancellationToken.None);

        Assert.Equal(["json_schema", "json_object", "json_object"], handler.Bodies.Select(FormatOf));
    }

    // ── Task 2: failures ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_length_finish_retries_once_at_twice_the_ceiling_then_fails_typed_and_both_are_billed()
    {
        var handler = new Script(Ok("{\"a\":", finish: "length"), Ok("{\"a\":", finish: "length"));
        var recorder = Recorder();

        var result = await Client(handler, recorder).CompleteAsync(Request(ApiUsagePurpose.RerankFeed), CancellationToken.None);

        Assert.Equal(LlmCallFailure.Truncated, result.Failure);
        Assert.Equal(AgentFailureKind.Truncated, result.AgentFailure);
        Assert.Equal(JobFailureReason.NoAnswer, result.ReasonCode);
        Assert.Equal([900, 1800], handler.Bodies.Select(MaxTokensOf));
        var (calls, tokens) = recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.RerankFeed);
        Assert.Equal(2, calls);
        Assert.Equal(2 * (100 + 900), tokens); // the truncated attempts' tokens are recorded, not dropped
    }

    [Fact]
    public async Task A_length_finish_answered_by_the_retry_is_a_success()
    {
        var handler = new Script(Ok("{\"a\":", finish: "length"), Ok("""{"a":1}"""));
        var result = await Client(handler).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("""{"a":1}""", result.Content);
        Assert.Equal(1400, result.MaxTokens);
    }

    [Fact]
    public async Task A_purpose_that_does_not_retry_on_length_fails_on_the_first_truncation()
    {
        var handler = new Script(Ok("{\"a\":", finish: "length"), Ok("{}"));
        var result = await Client(handler).CompleteAsync(Request(ApiUsagePurpose.ExtractAttributes), CancellationToken.None);
        Assert.Equal(LlmCallFailure.Truncated, result.Failure);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, JobFailureReason.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired, JobFailureReason.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden, JobFailureReason.Forbidden)]
    public async Task A_refusal_is_one_billed_call_zero_retries_and_opens_the_breaker_for_every_purpose(
        HttpStatusCode status, string reason)
    {
        var health = new ProviderHealthService();
        var recorder = Recorder();
        var handler = new Script(new Canned(status, """{"error":{"message":"no"}}"""), Ok("{}"));

        var refused = await Client(handler, recorder, health: health).CompleteAsync(Request(ApiUsagePurpose.AskPlan), CancellationToken.None);
        var next = await Client(handler, recorder, health: health).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);

        Assert.Equal(LlmCallFailure.ProviderRefused, refused.Failure);
        Assert.Equal(reason, refused.ReasonCode);
        Assert.Single(handler.Bodies); // the second call was never sent
        Assert.Equal(LlmCallFailure.ProviderUnavailable, next.Failure);
        Assert.Equal(reason, next.ReasonCode);
        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.AskPlan).Calls);
        Assert.Equal(0, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.Lens).Calls);

        // Visible to the status API on the very next poll.
        var snapshot = health.Current();
        Assert.Equal(ProviderHealthState.Down, snapshot.State);
        var breaker = Assert.Single(snapshot.Breakers);
        Assert.Null(breaker.Purpose);
        Assert.Equal(reason, breaker.ReasonCode);
    }

    [Fact]
    public async Task A_refusal_inside_a_200_envelope_is_a_refusal()
    {
        var health = new ProviderHealthService();
        var handler = new Script(new Canned(HttpStatusCode.OK, """{"error":{"code":402,"message":"Insufficient credits"}}"""));
        var result = await Client(handler, health: health).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.ProviderRefused, result.Failure);
        Assert.Equal(ProviderHealthState.Down, health.Current().State);
    }

    [Fact]
    public async Task After_the_cool_off_exactly_one_probe_goes_out_and_an_answer_closes_the_breaker()
    {
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        var handler = new Script(new Canned(HttpStatusCode.PaymentRequired, "{}"), Ok("{}"), Ok("{}"));
        var client = Client(handler, health: health);

        await client.CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));

        // The half-open slot is taken by the first caller; a concurrent second caller is refused.
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _));
        Assert.False(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.AskPlan, out _));
        health.ReportInconclusive(ApiProvider.OpenRouter); // hand the slot back for the real probe

        var probe = await client.CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.True(probe.Succeeded);
        Assert.Equal(ProviderHealthState.Healthy, health.Current().State);
        Assert.True((await client.CompleteAsync(Request(ApiUsagePurpose.AskPlan), CancellationToken.None)).Succeeded);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_up_to_the_purposes_limit_and_every_attempt_is_billed()
    {
        var recorder = Recorder();
        var handler = new Script(
            new Canned(HttpStatusCode.ServiceUnavailable, "{}"),
            new Canned(HttpStatusCode.TooManyRequests, "{}"),
            Ok("{}"));

        var result = await Client(handler, recorder).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);

        // Lens allows one transient retry: 503 then 429 is a failure, and the third response is never asked for.
        Assert.Equal(LlmCallFailure.ProviderError, result.Failure);
        Assert.Equal(JobFailureReason.RateLimited, result.ReasonCode);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.Lens).Calls);
    }

    [Fact]
    public async Task A_body_that_drops_after_a_200_is_retried_once_even_where_transient_retries_are_off()
    {
        var recorder = Recorder();
        var handler = new Script(new Canned(HttpStatusCode.OK, Body: null, DropBody: true), Ok("{}"));

        var result = await Client(handler, recorder)
            .CompleteAsync(Request(ApiUsagePurpose.ExtractAttributes), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.ExtractAttributes).Calls);
    }

    [Fact]
    public async Task A_second_dropped_body_is_a_provider_error()
    {
        var handler = new Script(new Canned(HttpStatusCode.OK, null, DropBody: true));
        var result = await Client(handler).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.ProviderError, result.Failure);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task An_attempt_that_outlives_its_timeout_fails_as_timed_out_and_is_not_retried()
    {
        var handler = new Script(Ok("{}")) { Delay = TimeSpan.FromSeconds(5) };
        var client = Client(handler, config: new() { ["LLM_LENS_ATTEMPT_TIMEOUT_SECONDS"] = "0.2" });

        var result = await client.CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);

        Assert.Equal(LlmCallFailure.TimedOut, result.Failure);
        Assert.Equal(JobFailureReason.TimedOut, result.ReasonCode);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_rather_than_reading_as_a_timeout()
    {
        var handler = new Script(Ok("{}")) { Delay = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).CompleteAsync(Request(ApiUsagePurpose.Lens), cts.Token));
    }

    [Fact]
    public async Task A_deterministic_bad_request_is_not_retried()
    {
        var handler = new Script(new Canned(HttpStatusCode.BadRequest, """{"error":{"message":"bad messages"}}"""), Ok("{}"));
        var result = await Client(handler).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.BadRequest, result.Failure);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task An_empty_answer_is_a_typed_failure()
    {
        var result = await Client(new Script(Ok(""))).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.EmptyAnswer, result.Failure);
    }

    [Fact]
    public async Task A_refused_budget_sends_nothing_and_bills_nothing()
    {
        var recorder = Recorder();
        var handler = new Script(Ok("{}"));
        var client = Client(handler, recorder, gate: new RefusingGate());

        var result = await client.CompleteAsync(Request(ApiUsagePurpose.AskJudge), CancellationToken.None);

        Assert.Equal(LlmCallFailure.BudgetSpent, result.Failure);
        Assert.Equal(JobFailureReason.BudgetSpent, result.ReasonCode);
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, recorder.PendingToday(ApiProvider.OpenRouter, null, ApiUsagePurpose.AskJudge).Calls);
    }

    [Fact]
    public async Task Reasoning_tokens_are_read_from_the_usage_details()
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{}" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 10, completion_tokens = 400, completion_tokens_details = new { reasoning_tokens = 380 } },
        });
        var result = await Client(new Script(new Canned(HttpStatusCode.OK, body))).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(380, result.ReasoningTokens);
        Assert.Equal(400, result.CompletionTokens);
    }

    [Fact]
    public async Task A_hostile_envelope_never_throws()
    {
        foreach (var body in new[] { "not json", "[]", """{"choices":"x"}""", """{"choices":[{"message":{"content":7}}]}""" })
        {
            var result = await Client(new Script(new Canned(HttpStatusCode.OK, body))).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
            Assert.False(result.Succeeded);
        }
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────

    private static LlmChatRequest Request(string purpose, object? schema = null) =>
        new(purpose, [LlmMessage.System("s"), LlmMessage.User("u")], schema);

    private static IConfiguration Config(Dictionary<string, string?>? values = null)
    {
        var all = new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one" };
        foreach (var (k, v) in values ?? [])
        {
            all[k] = v;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private static ApiUsageRecorder Recorder()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
    }

    private static OpenRouterChatClient Client(
        Script handler,
        ApiUsageRecorder? recorder = null,
        ModelCapabilities? capabilities = null,
        IProviderHealth? health = null,
        ILlmCallGate? gate = null,
        Dictionary<string, string?>? config = null)
    {
        var configuration = Config(config);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterChatClient(
            http, recorder ?? Recorder(), configuration, NullLogger.Instance,
            purposes: new LlmPurposeCatalog(configuration), capabilities: capabilities, health: health, gate: gate);
    }

    private static Canned Ok(string content, string finish = "stop") =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } },
            usage = new { prompt_tokens = 100, completion_tokens = 900 },
        }));

    private static string? EffortOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetProperty("effort").GetString() : null;
    }

    private static string FormatOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("response_format").GetProperty("type").GetString()!;
    }

    private static int MaxTokensOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("max_tokens").GetInt32();
    }

    private sealed record Canned(HttpStatusCode Status, string? Body, bool DropBody = false);

    private sealed class Script(params Canned[] script) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            var canned = script[Math.Min(_index++, script.Length - 1)];
            return new HttpResponseMessage(canned.Status)
            {
                Content = canned.DropBody
                    ? new DroppingContent()
                    : new StringContent(canned.Body ?? string.Empty, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>A body whose stream dies mid-read — the HttpIOException behind E-6.</summary>
    private sealed class DroppingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RefusingGate : ILlmCallGate
    {
        public Task<LlmAdmission> TryReserveAsync(Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct) =>
            Task.FromResult(LlmAdmission.Refuse("test"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
