using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>One chat message.</summary>
public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);

    public static LlmMessage User(string content) => new("user", content);
}

/// <summary>
/// One chat completion to make. <paramref name="ResponseFormat"/> is the strict
/// <c>{type: json_schema, …}</c> object where the caller has a schema, or null for plain
/// <c>json_object</c>. <paramref name="Items"/> is the batch size a per-item token share scales
/// with. <paramref name="MaxTokens"/> overrides the purpose's ceiling for this call only — the
/// extractor's compact repair pass is the one caller that needs it.
/// </summary>
public sealed record LlmChatRequest(
    string Purpose,
    IReadOnlyList<LlmMessage> Messages,
    object? ResponseFormat = null,
    int Items = 0,
    int? MaxTokens = null);

/// <summary>Why a chat call produced no usable content. Every value is a failure the caller can act on.</summary>
public enum LlmCallFailure
{
    /// <summary>The completion hit its ceiling (<c>finish_reason=length</c>), after the 2× retry where the purpose allows one.</summary>
    Truncated,

    /// <summary>The provider answered with no content.</summary>
    EmptyAnswer,

    /// <summary>A transport failure, a 429/5xx, or an unreadable envelope, after the purpose's retries.</summary>
    ProviderError,

    /// <summary>The provider rejected the request itself (a 4xx other than 401/402/403). Never retried.</summary>
    BadRequest,

    /// <summary>401, 402 or 403. Never retried, and it opens the provider breaker.</summary>
    ProviderRefused,

    /// <summary>The attempt or total time budget ran out.</summary>
    TimedOut,

    /// <summary>An open breaker refused the call before it was sent.</summary>
    ProviderUnavailable,

    /// <summary>The purpose's daily allowance could not admit the call; nothing was sent.</summary>
    BudgetSpent,
}

/// <summary>
/// What one chat call came to. <see cref="Content"/> is the raw content of the LAST attempt — set
/// even on <see cref="LlmCallFailure.Truncated"/>, where a caller may want to tell an empty cut-off
/// from a partial one — and must never be treated as an answer unless <see cref="Failure"/> is null.
/// Token figures are the last attempt's; every attempt is on the ledger regardless.
/// </summary>
public sealed record LlmChatResult(
    string? Content,
    LlmCallFailure? Failure,
    int? HttpStatus,
    string? FinishReason,
    int HttpAttempts,
    int MaxTokens,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    string Model,
    string? Effort,
    string? BreakerReason,
    TimeSpan Elapsed)
{
    public bool Succeeded => Failure is null;

    /// <summary>The agent-facing name of the failure, for turn records and counters.</summary>
    public AgentFailureKind? AgentFailure => Failure switch
    {
        null => null,
        LlmCallFailure.Truncated => AgentFailureKind.Truncated,
        LlmCallFailure.EmptyAnswer => AgentFailureKind.EmptyAnswer,
        LlmCallFailure.ProviderRefused => AgentFailureKind.ProviderRefused,
        LlmCallFailure.TimedOut => AgentFailureKind.TimedOut,
        LlmCallFailure.ProviderUnavailable => AgentFailureKind.ProviderUnavailable,
        LlmCallFailure.BudgetSpent => AgentFailureKind.BudgetSpent,
        _ => AgentFailureKind.ProviderError,
    };

    /// <summary>The status-API reason code for the failure (null on success).</summary>
    public string? ReasonCode => AgentFailure is { } kind ? LlmFailureCodes.ReasonCode(kind, HttpStatus, BreakerReason) : null;
}

/// <summary>
/// The one OpenRouter chat-completions transport every LLM adapter calls through.
///
/// <para><b>Why one.</b> Five adapters each carried their own copy of the request, the strict-schema
/// downgrade, the length check and the usage recording, and they had drifted: the interpreter sent
/// no <c>max_tokens</c> at all, the search agent recorded nothing for a call that threw, no adapter
/// set a reasoning effort, and every one of them retried on the transport's 10-second defaults —
/// killing slow-but-alive completions and paying for them again. Every rule below is now written
/// once.</para>
///
/// <list type="bullet">
/// <item><b>Per-purpose bounds.</b> Model, reasoning effort, completion ceiling, retries and the
/// attempt and total timeouts come from <see cref="ILlmPurposeCatalog"/>. The effort is sent as
/// <c>reasoning.effort</c>; a model that rejects a value falls back (none → low → no parameter)
/// and is remembered in <see cref="ModelCapabilities"/>. A null effort sends no parameter at all,
/// which is how extraction's request stays byte-identical.</item>
/// <item><b>Strict schema</b> wherever the caller has one and the model accepts it; one rejection
/// retires it for that model and the call is re-issued as <c>json_object</c>.</item>
/// <item><b>A <c>length</c> finish</b> gets ONE retry at twice the ceiling (where the purpose
/// allows it), then a typed <see cref="LlmCallFailure.Truncated"/> — never a partial object read
/// as a whole one.</item>
/// <item><b>401/402/403 are never retried</b> and open the provider breaker; while it is open every
/// purpose is refused before anything is sent.</item>
/// <item><b>Transient failures</b> (connection, 429, 5xx) are retried up to the purpose's
/// <c>MaxRetries</c>; a <b>body read that drops</b> after a 200 (the <c>HttpIOException</c> behind
/// 2,038 failed extractions) gets one retry of its own, because a 200 whose body never arrived is a
/// paid answer nobody received. Timeouts are not retried: a retry doubles the wait.</item>
/// <item><b>Every HTTP attempt is on the ledger</b>, a failed one included — with its tokens where
/// the provider reported them and NULL ("unknown", never "free") where it did not.</item>
/// <item><b>Budget is reserved before each attempt</b> through <see cref="ILlmCallGate"/>, so a
/// ceiling is a limit rather than a forecast.</item>
/// </list>
///
/// <para>Constructed by each adapter around its own typed <see cref="HttpClient"/> rather than
/// resolved from DI, so every existing <c>new OpenRouterXxx(httpClient, recorder, config, logger)</c>
/// keeps working. The process-wide pieces — the capability table, the breaker, the gate — are DI
/// singletons passed in; an adapter built by hand gets private ones, so tests never share state.</para>
/// </summary>
public sealed class OpenRouterChatClient(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger logger,
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null,
    LlmCallMetrics? metrics = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The pause before a transient retry. Short: the purpose's total timeout is the real bound.</summary>
    private static readonly TimeSpan TransientBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>A hard stop on attempts of any kind — downgrades included — so no response can loop the client.</summary>
    private const int MaxAttempts = 8;

    /// <summary>Body-drop retries, separate from the purpose's transient retries (see the type note).</summary>
    private const int MaxBodyDropRetries = 1;

    private readonly ILlmPurposeCatalog _purposes = purposes ?? new LlmPurposeCatalog(configuration);
    private readonly ModelCapabilities _capabilities = capabilities ?? new ModelCapabilities(configuration);
    private readonly IProviderHealth _health = health ?? new ProviderHealthService();

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    /// <summary>The bounds a purpose runs at — exposed so adapters can report the model they bill.</summary>
    public LlmPurposeOptions Options(string purpose) => _purposes.For(purpose);

    /// <summary>The model-and-effort salt a cache key built from this purpose's output must carry.</summary>
    public string CacheSalt(string purpose)
    {
        var options = _purposes.For(purpose);
        return _capabilities.CacheSalt(options.Model, options.ReasoningEffort);
    }

    public async Task<LlmChatResult> CompleteAsync(LlmChatRequest request, CancellationToken ct)
    {
        var lengthFinishes = new StrongBox<int>();
        var result = await CompleteCoreAsync(request, lengthFinishes, ct);
        // Task 8's counters. A call refused before any attempt was sent is a stop and is not
        // counted (Record ignores zero attempts); a sent call that did not answer usably is a
        // fallback for its caller.
        metrics?.Record(request.Purpose, lengthFinishes.Value, result.HttpAttempts, fallback: !result.Succeeded);
        return result;
    }

    private async Task<LlmChatResult> CompleteCoreAsync(LlmChatRequest request, StrongBox<int> lengthFinishes, CancellationToken ct)
    {
        var options = _purposes.For(request.Purpose);
        var model = options.Model;
        var maxTokens = request.MaxTokens ?? options.MaxTokensFor(request.Items);
        var clock = Stopwatch.StartNew();
        var accountId = accountContext?.AccountId;

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(options.TotalTimeout);

        var attempts = 0;
        var transientRetries = 0;
        var bodyDropRetries = 0;
        var lengthRetried = false;
        Attempt? last = null;

        while (attempts < MaxAttempts)
        {
            var effort = _capabilities.EffectiveEffort(model, options.ReasoningEffort);
            var strict = request.ResponseFormat is not null && _capabilities.SupportsStrictSchema(model);

            if (!_health.TryAdmit(ApiProvider.OpenRouter, request.Purpose, out var breaker, out var probe))
            {
                logger.LogWarning(
                    "{Purpose} call not sent: the OpenRouter breaker is open ({Reason}).", request.Purpose, breaker?.ReasonCode);
                return Result(last, LlmCallFailure.ProviderUnavailable, attempts, maxTokens, model, effort, clock, breaker?.ReasonCode);
            }

            // Whether this attempt told the breaker how the key is. A probe that leaves by any other
            // door — a budget refusal, a timeout, the caller's cancellation, an exception — hands
            // the half-open slot back in the finally below; a slot nobody returns would refuse every
            // purpose on the provider until the lease lapsed.
            var reported = false;
            try
            {
                using var admission = gate is null
                    ? LlmAdmission.Unmetered
                    : await gate.TryReserveAsync(accountId, request.Purpose, EstimateTokens(request.Messages, maxTokens), ct);
                if (!admission.Admitted)
                {
                    return Result(last, LlmCallFailure.BudgetSpent, attempts, maxTokens, model, effort, clock, null);
                }

                attempts++;
                last = await AttemptAsync(request, model, effort, strict, maxTokens, accountId, total, ct);

                switch (last.Outcome)
                {
                    case Outcome.Answered:
                        Answered();
                        if (string.Equals(last.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                        {
                            lengthFinishes.Value++;
                            if (options.RetryOnLength && !lengthRetried)
                            {
                                lengthRetried = true;
                                logger.LogWarning(
                                    "{Purpose} completion hit its {MaxTokens}-token ceiling on {Model} (effort {Effort}); retrying once at {Retry}.",
                                    request.Purpose, maxTokens, model, effort ?? "-", maxTokens * 2);
                                maxTokens *= 2;
                                continue;
                            }

                            logger.LogWarning(
                                "{Purpose} completion hit its {MaxTokens}-token ceiling on {Model} (effort {Effort}); the truncated "
                                + "answer is discarded rather than parsed as a whole one.",
                                request.Purpose, maxTokens, model, effort ?? "-");
                            return Result(last, LlmCallFailure.Truncated, attempts, maxTokens, model, effort, clock, null);
                        }

                        return string.IsNullOrWhiteSpace(last.Content)
                            ? Result(last, LlmCallFailure.EmptyAnswer, attempts, maxTokens, model, effort, clock, null)
                            : Result(last, null, attempts, maxTokens, model, effort, clock, null);

                    case Outcome.Refused:
                        _health.ReportRefused(ApiProvider.OpenRouter, last.Status!.Value);
                        reported = true;
                        return Result(last, LlmCallFailure.ProviderRefused, attempts, maxTokens, model, effort, clock, null);

                    case Outcome.StrictRejected:
                        Answered();
                        _capabilities.MarkStrictSchemaUnsupported(model);
                        logger.LogWarning(
                            "OpenRouter rejected strict json_schema for {Model} (HTTP {Status}); falling back to json_object "
                            + "for this model for the rest of the process. Field validation now rests on the parser.",
                            model, last.Status);
                        continue;

                    case Outcome.EffortRejected:
                        Answered();
                        _capabilities.MarkEffortUnsupported(model, effort!);
                        logger.LogWarning(
                            "{Model} rejected reasoning effort '{Effort}' (HTTP {Status}); falling back to '{Fallback}' for this model.",
                            model, effort, last.Status, ReasoningEfforts.FallbackFor(effort!) ?? "no reasoning parameter");
                        continue;

                    case Outcome.BadRequest:
                        Answered();
                        return Result(last, LlmCallFailure.BadRequest, attempts, maxTokens, model, effort, clock, null);

                    case Outcome.TimedOut:
                        // Inconclusive: the finally hands a probe slot back.
                        return Result(last, LlmCallFailure.TimedOut, attempts, maxTokens, model, effort, clock, null);

                    case Outcome.BodyDropped when bodyDropRetries < MaxBodyDropRetries:
                        bodyDropRetries++;
                        logger.LogWarning(
                            "{Purpose} response body dropped after HTTP 200 on {Model}; retrying once.", request.Purpose, model);
                        continue;

                    case Outcome.Transient or Outcome.BodyDropped:
                        if (last.Status is not null)
                        {
                            // An HTTP answer that was not a refusal: the key works, whatever went wrong.
                            Answered();
                        }

                        if (last.Outcome == Outcome.Transient && transientRetries < options.MaxRetries)
                        {
                            transientRetries++;
                            logger.LogWarning(
                                "{Purpose} call failed transiently (HTTP {Status}) on {Model}; retry {Retry} of {Max}.",
                                request.Purpose, last.Status, model, transientRetries, options.MaxRetries);
                            try
                            {
                                await Task.Delay(TransientBackoff, total.Token);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                return Result(last, LlmCallFailure.TimedOut, attempts, maxTokens, model, effort, clock, null);
                            }

                            continue;
                        }

                        return Result(last, LlmCallFailure.ProviderError, attempts, maxTokens, model, effort, clock, null);
                }
            }
            finally
            {
                if (probe && !reported)
                {
                    _health.ReportInconclusive(ApiProvider.OpenRouter);
                }
            }

            void Answered()
            {
                _health.ReportAnswered(ApiProvider.OpenRouter);
                reported = true;
            }
        }

        return Result(last, LlmCallFailure.ProviderError, attempts, maxTokens, model,
            _capabilities.EffectiveEffort(model, options.ReasoningEffort), clock, null);
    }

    /// <summary>
    /// The serialised request body, exactly as it is sent. Internal so the byte-identity of the
    /// extractor's request can be pinned: property order is model, temperature, max_tokens,
    /// response_format, messages — the order the adapters' anonymous objects always used — and
    /// <c>reasoning</c> is appended only when an effort is sent.
    /// </summary>
    internal static JsonObject BuildBody(
        string model, int maxTokens, object responseFormat, IReadOnlyList<LlmMessage> messages, string? effort)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["response_format"] = JsonSerializer.SerializeToNode(responseFormat, JsonOptions),
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };
        if (effort is not null)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>The content the client sends for a body — the same serializer settings the adapters used.</summary>
    internal static HttpContent Content(JsonObject body) => JsonContent.Create(body, options: JsonOptions);

    private static long EstimateTokens(IReadOnlyList<LlmMessage> messages, int maxTokens) =>
        (messages.Sum(m => (long)m.Content.Length) / 4) + maxTokens;

    private enum Outcome
    {
        Answered,
        Refused,
        StrictRejected,
        EffortRejected,
        BadRequest,
        Transient,
        BodyDropped,
        TimedOut,
    }

    private sealed record Attempt(
        Outcome Outcome, int? Status, string? Content, string? FinishReason,
        long? PromptTokens, long? CompletionTokens, long? ReasoningTokens);

    private async Task<Attempt> AttemptAsync(
        LlmChatRequest request, string model, string? effort, bool strict, int maxTokens, Guid? accountId,
        CancellationTokenSource total, CancellationToken ct)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        attemptCts.CancelAfter(_purposes.For(request.Purpose).AttemptTimeout);
        var token = attemptCts.Token;

        long? prompt = null, completion = null;
        try
        {
            var responseFormat = strict ? request.ResponseFormat! : new { type = "json_object" };
            using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = Content(BuildBody(model, maxTokens, responseFormat, request.Messages, effort)),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Purpose} call failed before a response on {Model}.", request.Purpose, model);
                return new Attempt(Outcome.Transient, (int?)ex.StatusCode, null, null, null, null, null);
            }

            using (response)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(token);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "{Purpose} response body dropped on {Model} (HTTP {Status}).",
                        request.Purpose, model, (int)response.StatusCode);
                    return response.IsSuccessStatusCode
                        ? new Attempt(Outcome.BodyDropped, (int)response.StatusCode, null, null, null, null, null)
                        : new Attempt(Outcome.Transient, (int)response.StatusCode, null, null, null, null, null);
                }

                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    if (status is >= 400 and < 500)
                    {
                        // A 4xx (a refusal, a rejected request, a rate limit) is the provider
                        // declining to generate: zero tokens is KNOWN, not unknown, so the ledger
                        // says 0 rather than NULL. A 5xx may have generated before failing — unknown.
                        prompt = 0;
                        completion = 0;
                    }

                    return new Attempt(Classify(response.StatusCode, body, strict, effort), status, null, null, prompt, completion, null);
                }

                var envelope = ReadEnvelope(body);
                prompt = envelope.PromptTokens;
                completion = envelope.CompletionTokens;
                if (envelope.ErrorCode is { } errorCode)
                {
                    // OpenRouter can report a provider-side error inside a 200 envelope.
                    return new Attempt(
                        errorCode is 401 or 402 or 403 ? Outcome.Refused : Outcome.Transient,
                        errorCode is 401 or 402 or 403 ? errorCode : status,
                        null, null, prompt, completion, envelope.ReasoningTokens);
                }

                if (!envelope.Parsed)
                {
                    // A 200 whose body is not an envelope at all — a gateway's HTML error page, a
                    // proxy's truncated JSON. Nothing about OUR request was wrong, so it is transient
                    // and earns the purpose's retry; it ends as a ProviderError, never a BadRequest.
                    logger.LogWarning("{Purpose} response on {Model} was not a chat-completion envelope.", request.Purpose, model);
                    return new Attempt(Outcome.Transient, status, null, null, null, null, null);
                }

                return new Attempt(
                    Outcome.Answered, status, envelope.Content, envelope.FinishReason,
                    prompt, completion, envelope.ReasoningTokens);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("{Purpose} call on {Model} ran out of time.", request.Purpose, model);
            return new Attempt(Outcome.TimedOut, null, null, null, null, null, null);
        }
        finally
        {
            // AFTER the response, in a finally: the counts live in the body, and an attempt that died
            // before one still reads as a call with unknown tokens rather than as free.
            usageRecorder.Record(ApiProvider.OpenRouter, request.Purpose, model, accountId, prompt, completion);
        }
    }

    private static Outcome Classify(HttpStatusCode status, string body, bool strict, string? effort)
    {
        var code = (int)status;
        if (code is 401 or 402 or 403)
        {
            return Outcome.Refused;
        }

        if (code is 429 or 408 || code >= 500)
        {
            return code == 501 && strict && MentionsSchema(body) ? Outcome.StrictRejected : Outcome.Transient;
        }

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound)
        {
            if (effort is not null && MentionsReasoning(body) && !MentionsSchema(body))
            {
                return Outcome.EffortRejected;
            }

            if (strict && MentionsSchema(body))
            {
                return Outcome.StrictRejected;
            }
        }

        return Outcome.BadRequest;
    }

    /// <summary>The same predicate every adapter used: a broader reading would retire strict mode on an unrelated 4xx.</summary>
    private static bool MentionsSchema(string body) =>
        body.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
        || body.Contains("response_format", StringComparison.OrdinalIgnoreCase)
        || body.Contains("structured output", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsReasoning(string body) =>
        body.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
        || body.Contains("effort", StringComparison.OrdinalIgnoreCase);

    private sealed record Envelope(
        bool Parsed, string? Content, string? FinishReason, long? PromptTokens, long? CompletionTokens,
        long? ReasoningTokens, int? ErrorCode);

    /// <summary>Reads the envelope by hand, kind-checking every accessor: nothing here may throw on a hostile body.</summary>
    private static Envelope ReadEnvelope(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Envelope(false, null, null, null, null, null, null);
            }

            long? prompt = null, completion = null, reasoning = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                prompt = Long(usage, "prompt_tokens");
                completion = Long(usage, "completion_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    reasoning = Long(details, "reasoning_tokens");
                }
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n)
                    ? n
                    : 500;
                return new Envelope(true, null, null, prompt, completion, reasoning, code);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return new Envelope(true, null, null, prompt, completion, reasoning, null);
            }

            var choice = choices[0];
            string? content = null, finish = null;
            if (choice.ValueKind == JsonValueKind.Object)
            {
                if (choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var ce) && ce.ValueKind == JsonValueKind.String)
                {
                    content = ce.GetString();
                }

                if (choice.TryGetProperty("finish_reason", out var fe) && fe.ValueKind == JsonValueKind.String)
                {
                    finish = fe.GetString();
                }
            }

            return new Envelope(true, content, finish, prompt, completion, reasoning, null);
        }
        catch (JsonException)
        {
            return new Envelope(false, null, null, null, null, null, null);
        }
    }

    private static long? Long(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    private static LlmChatResult Result(
        Attempt? last, LlmCallFailure? failure, int attempts, int maxTokens, string model, string? effort,
        Stopwatch clock, string? breakerReason) =>
        new(
            last?.Content,
            failure,
            last?.Status,
            last?.FinishReason,
            attempts,
            maxTokens,
            last?.PromptTokens,
            last?.CompletionTokens,
            last?.ReasoningTokens,
            model,
            effort,
            breakerReason,
            clock.Elapsed);
}
