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
using LlmCallMetrics = FrostAura.Reel.Infrastructure.Llm.LlmCallMetrics;
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
        Assert.Equal(100 + (370 * 6), judge.MaxTokensFor(6));

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
        Assert.Equal([100, 200], handler.Bodies.Select(MaxTokensOf));
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
        Assert.Equal(700, result.MaxTokens);
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
        // A refusal generated nothing: its tokens are a KNOWN zero, so the ledger never shows NULL for it.
        Assert.Equal(0, refused.CompletionTokens);
        Assert.Equal(0, refused.PromptTokens);
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
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var isProbe));
        Assert.True(isProbe);
        Assert.False(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.AskPlan, out _, out _));
        health.ReportInconclusive(ApiProvider.OpenRouter); // hand the slot back for the real probe

        var probe = await client.CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.True(probe.Succeeded);
        Assert.Equal(ProviderHealthState.Healthy, health.Current().State);
        Assert.True((await client.CompleteAsync(Request(ApiUsagePurpose.AskPlan), CancellationToken.None)).Succeeded);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task A_cancelled_probe_hands_the_slot_back_so_the_breaker_can_still_close()
    {
        // The reviewers' lock: a caller that aborts mid-probe (an Ask turn the user walked away
        // from) used to leave the slot taken for ever — every purpose refused until a restart.
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        await Client(new Script(new Canned(HttpStatusCode.PaymentRequired, "{}")), health: health)
            .CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));
        var client = Client(new Script(Ok("{}")) { Delay = TimeSpan.FromSeconds(5) }, health: health);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.CompleteAsync(Request(ApiUsagePurpose.AskPlan), cts.Token));
        }

        // The next caller is the probe, it is answered, and the breaker closes.
        var next = Client(new Script(Ok("{}")), health: health);
        Assert.True((await next.CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None)).Succeeded);
        Assert.Equal(ProviderHealthState.Healthy, health.Current().State);
    }

    [Fact]
    public async Task A_probe_whose_gate_throws_or_refuses_hands_the_slot_back()
    {
        foreach (ILlmCallGate gate in new ILlmCallGate[] { new ThrowingGate(), new RefusingGate() })
        {
            var clock = new ManualClock();
            var health = new ProviderHealthService(clock: clock);
            await Client(new Script(new Canned(HttpStatusCode.PaymentRequired, "{}")), health: health)
                .CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
            clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));

            var gated = Client(new Script(Ok("{}")), health: health, gate: gate);
            if (gate is ThrowingGate)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => gated.CompleteAsync(Request(ApiUsagePurpose.AskJudge), CancellationToken.None));
            }
            else
            {
                Assert.Equal(LlmCallFailure.BudgetSpent,
                    (await gated.CompleteAsync(Request(ApiUsagePurpose.AskJudge), CancellationToken.None)).Failure);
            }

            Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var probe));
            Assert.True(probe);
        }
    }

    [Fact]
    public async Task A_timed_out_probe_releases_the_slot_and_a_non_probe_timeout_never_releases_someone_elses()
    {
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        await Client(new Script(new Canned(HttpStatusCode.PaymentRequired, "{}")), health: health)
            .CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));

        // Someone else holds the probe; this caller is refused before it sends anything, and its
        // refusal must not hand back a slot it never held.
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var held));
        Assert.True(held);
        var refused = await Client(new Script(Ok("{}")), health: health).CompleteAsync(Request(ApiUsagePurpose.AskPlan), CancellationToken.None);
        Assert.Equal(LlmCallFailure.ProviderUnavailable, refused.Failure);
        Assert.False(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out _));

        // The holder's probe times out: the slot is free again.
        health.ReportInconclusive(ApiProvider.OpenRouter);
        var slow = new Script(Ok("{}")) { Delay = TimeSpan.FromSeconds(5) };
        var timedOut = await Client(slow, health: health, config: new() { ["LLM_LENS_ATTEMPT_TIMEOUT_SECONDS"] = "0.2" })
            .CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.TimedOut, timedOut.Failure);
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var again));
        Assert.True(again);
    }

    [Fact]
    public void A_probe_slot_nobody_returns_lapses_after_its_lease()
    {
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));

        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var probe));
        Assert.True(probe);
        Assert.False(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out _));
        Assert.NotNull(health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.Lens));

        clock.Advance(ProviderHealthService.ProbeLease);
        Assert.Null(health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.Lens));
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var second));
        Assert.True(second);
    }

    [Fact]
    public void Background_work_sees_a_half_open_breaker_as_open_only_while_a_probe_is_out()
    {
        // Background jobs ask OpenBreakerFor before they start. It used to say "open" for ever after
        // a 402, so an account whose only traffic was background work was never probed.
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        Assert.Equal(ProviderBreakerReasons.PaymentRequired, health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.Lens)?.ReasonCode);

        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));
        Assert.Null(health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.TasteNarrative));

        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out _));
        Assert.NotNull(health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.TasteNarrative));

        // A purpose breaker is still honoured on its own terms.
        health.OpenPurpose(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference, ProviderBreakerReasons.TruncationBreaker,
            clock.GetUtcNow().UtcDateTime.AddHours(1));
        health.ReportAnswered(ApiProvider.OpenRouter);
        Assert.Null(health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.Lens));
        Assert.Equal(ProviderBreakerReasons.TruncationBreaker,
            health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference)?.ReasonCode);
    }

    [Fact]
    public async Task An_unreadable_200_is_transient_retried_and_ends_as_a_provider_error()
    {
        // A gateway's HTML error page served with a 200: nothing about the request was wrong.
        var html = new Canned(HttpStatusCode.OK, "<html><body>Bad gateway</body></html>");
        var recovered = new Script(html, Ok("{}"));
        Assert.True((await Client(recovered).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None)).Succeeded);
        Assert.Equal(2, recovered.Bodies.Count);

        var persistent = new Script(html);
        var result = await Client(persistent).CompleteAsync(Request(ApiUsagePurpose.Lens), CancellationToken.None);
        Assert.Equal(LlmCallFailure.ProviderError, result.Failure);
        Assert.Equal(2, persistent.Bodies.Count); // lens allows one transient retry
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
    public async Task A_server_error_leaves_its_tokens_unknown_rather_than_zero()
    {
        var result = await Client(new Script(new Canned(HttpStatusCode.BadGateway, "{}")))
            .CompleteAsync(Request(ApiUsagePurpose.ExtractAttributes), CancellationToken.None);
        Assert.Equal(LlmCallFailure.ProviderError, result.Failure);
        Assert.Null(result.CompletionTokens);
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

    [Fact]
    public async Task Embeddings_on_the_openrouter_key_share_its_breaker()
    {
        var health = new ProviderHealthService();
        var handler = new Script(new Canned(HttpStatusCode.PaymentRequired, "{}"), Ok("{}"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        var embeddings = new OpenAiEmbeddingProvider(http, Recorder(), Config(), health: health);

        await Assert.ThrowsAsync<HttpRequestException>(() => embeddings.EmbedAsync(["a"]));
        Assert.Equal(ProviderHealthState.Down, health.Current().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embeddings.EmbedAsync(["b"]));
        Assert.Single(handler.Bodies); // the second embedding was never sent

        // A separate embeddings key is a separate credit: the OpenRouter breaker does not apply.
        var own = new OpenAiEmbeddingProvider(
            new HttpClient(new Script(new Canned(HttpStatusCode.PaymentRequired, "{}"))) { BaseAddress = new Uri("https://api.invalid/v1/") },
            Recorder(), Config(new() { ["EMBEDDINGS_API_KEY"] = "other-test-key" }), health: health);
        await Assert.ThrowsAsync<HttpRequestException>(() => own.EmbedAsync(["c"]));
    }

    [Fact]
    public async Task Embeddings_probe_a_half_open_breaker_and_close_it_on_an_answer()
    {
        // Embeddings used to read the breaker but never report back, so an embedding-only day
        // could never close it. They now take the probe like any chat call.
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        var handler = new Script(new Canned(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[3,4]}],"usage":{"prompt_tokens":2}}"""));
        var embeddings = new OpenAiEmbeddingProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") }, Recorder(), Config(), health: health);

        await Assert.ThrowsAsync<InvalidOperationException>(() => embeddings.EmbedAsync(["inside the cool-off"]));
        Assert.Empty(handler.Bodies);

        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));
        var vectors = await embeddings.EmbedAsync(["the probe"]);
        Assert.Single(vectors);
        Assert.Equal(ProviderHealthState.Healthy, health.Current().State);
    }

    [Fact]
    public async Task An_embedding_probe_that_dies_hands_the_slot_back()
    {
        var clock = new ManualClock();
        var health = new ProviderHealthService(clock: clock);
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        clock.Advance(ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));
        var embeddings = new OpenAiEmbeddingProvider(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            Recorder(), Config(), health: health);

        await Assert.ThrowsAsync<HttpRequestException>(() => embeddings.EmbedAsync(["x"]));
        Assert.True(health.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Lens, out _, out var probe));
        Assert.True(probe);
    }

    [Theory]
    [InlineData(null, "openai/gpt-6-luna|none")]
    [InlineData("low", "openai/gpt-6-luna|low")]
    [InlineData("omit", "openai/gpt-6-luna|-")]
    public void The_lens_cache_salt_carries_the_model_and_the_effort_actually_sent(string? effort, string expected)
    {
        var settings = new Dictionary<string, string?>();
        if (effort is not null)
        {
            settings["LLM_LENS_EFFORT"] = effort;
        }

        var writer = new OpenRouterLensWriter(
            new HttpClient { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") }, Recorder(), Config(settings),
            NullLogger<OpenRouterLensWriter>.Instance);

        Assert.Equal(expected, writer.LensCacheSalt);
        Assert.Equal("openai/gpt-6-luna|none", writer.NarrativeCacheSalt); // its own purpose, its own override
    }

    // ── Task 8: per-purpose counters and the 2%-over-50 alarm ──────────────────────────────

    [Fact]
    public async Task Every_sent_call_is_counted_with_its_length_finishes_retries_and_fallback()
    {
        var metrics = new LlmCallMetrics();

        // A length finish answered by its 2x retry: a success, one length finish, one retry.
        await Client(new Script(Ok("{\"a\":", finish: "length"), Ok("""{"a":1}""")), metrics: metrics)
            .CompleteAsync(Request(ApiUsagePurpose.AskJudge, Schema), default);
        // Two length finishes, then the typed failure: a fallback.
        await Client(new Script(Ok("{\"a\":", finish: "length"), Ok("{\"a\":", finish: "length")), metrics: metrics)
            .CompleteAsync(Request(ApiUsagePurpose.AskJudge, Schema), default);
        // A clean answer.
        await Client(new Script(Ok("""{"a":1}""")), metrics: metrics)
            .CompleteAsync(Request(ApiUsagePurpose.AskJudge, Schema), default);

        var judge = Assert.Single(metrics.Snapshot(), h => h.Purpose == ApiUsagePurpose.AskJudge);
        Assert.Equal(3, judge.Calls);
        Assert.Equal(2, judge.LengthCalls);
        Assert.Equal(1, judge.Fallbacks);
        Assert.Equal(2, judge.Retries);
        Assert.Equal(3, judge.WindowCalls);
        // Under 50 calls the alarm cannot judge a rate.
        Assert.False(judge.Alarming);
    }

    [Fact]
    public async Task A_call_refused_before_sending_is_not_a_call()
    {
        // A spent allowance is a stop, not a model failure: counting it would raise the fallback
        // alarm every evening the budget runs out.
        var metrics = new LlmCallMetrics();

        await Client(new Script(Ok("{}")), gate: new RefusingGate(), metrics: metrics)
            .CompleteAsync(Request(ApiUsagePurpose.AskPlan, Schema), default);

        Assert.DoesNotContain(metrics.Snapshot(), h => h.Purpose == ApiUsagePurpose.AskPlan);
    }

    [Theory]
    [InlineData(49, 2, false)] // not yet 50 calls: no verdict
    [InlineData(50, 1, false)] // exactly 2% is not above it
    [InlineData(50, 2, true)]  // 4%
    [InlineData(200, 2, false)] // the window is the LAST 50: two old failures have aged out
    public void The_alarm_is_a_rate_above_two_percent_over_the_last_fifty_calls(int calls, int failuresFirst, bool alarming)
    {
        var metrics = new LlmCallMetrics();
        for (var i = 0; i < calls; i++)
        {
            metrics.Record(ApiUsagePurpose.RerankFeed, lengthFinishes: 0, attempts: 1, fallback: i < failuresFirst);
        }

        var feed = Assert.Single(metrics.Snapshot());
        Assert.Equal(Math.Min(calls, LlmCallMetrics.Window), feed.WindowCalls);
        Assert.Equal(alarming, feed.Alarming);
    }

    [Fact]
    public void A_length_rate_above_two_percent_alarms_on_its_own()
    {
        var metrics = new LlmCallMetrics();
        for (var i = 0; i < LlmCallMetrics.Window; i++)
        {
            metrics.Record(ApiUsagePurpose.Lens, lengthFinishes: i < 2 ? 1 : 0, attempts: i < 2 ? 2 : 1, fallback: false);
        }

        var lens = Assert.Single(metrics.Snapshot());
        Assert.Equal(0.04, lens.WindowLengthRate, 3);
        Assert.Equal(0.0, lens.WindowFallbackRate, 3);
        Assert.True(lens.Alarming);
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
        Dictionary<string, string?>? config = null,
        LlmCallMetrics? metrics = null)
    {
        var configuration = Config(config);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterChatClient(
            http, recorder ?? Recorder(), configuration, NullLogger.Instance,
            purposes: new LlmPurposeCatalog(configuration), capabilities: capabilities, health: health, gate: gate,
            metrics: metrics);
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

    private sealed class ThrowingGate : ILlmCallGate
    {
        public Task<LlmAdmission> TryReserveAsync(Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct) =>
            throw new InvalidOperationException("the ledger is unreachable");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection reset");
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
