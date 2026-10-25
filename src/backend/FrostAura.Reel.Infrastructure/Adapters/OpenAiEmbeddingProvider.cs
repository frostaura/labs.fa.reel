using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// OpenAI-compatible /embeddings adapter (api.openai.com by default; EMBEDDINGS_BASE_URL
/// overridable). Returns L2-normalized vectors so pgvector cosine and dot products agree.
/// </summary>
public class OpenAiEmbeddingProvider(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    // Optional by design: the typed HttpClient is transient and is constructed inside a scope in
    // every real code path, but the adapter must still be `new`-able in tests without a scope.
    IAccountContext? accountContext = null,
    // The OpenRouter breaker, DI-supplied; null in a hand-built adapter. Consulted only when the
    // embeddings ride the OpenRouter KEY — the same key is the same credit, so a 402 on chat is
    // a 402 here too, and an embedding sent behind it is a refusal paid for in latency.
    Application.Llm.IProviderHealth? health = null)
    : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // Key resolution, in priority order: an explicit embeddings key, a dedicated OpenAI key, or
    // the OpenRouter key. OpenRouter serves openai/text-embedding-3-small at the SAME 1536 dims
    // the schema expects, so the one OpenRouter key drives both attribute extraction and
    // embeddings — no separate OpenAI account needed (verified 2026-06-17).
    private string? ApiKey =>
        Resolve("EMBEDDINGS_API_KEY") ?? Resolve("OPENAI_API_KEY") ?? Resolve("OPENROUTER_API_KEY");

    private string? Resolve(string name) => configuration[name] is { Length: > 0 } key ? key : null;

    public bool IsAvailable => ApiKey is not null;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not configured — embeddings unavailable.");
        }

        if (texts.Count == 0)
        {
            return [];
        }

        var model = configuration["EMBEDDINGS_MODEL"] ?? "text-embedding-3-small";
        var sharesOpenRouterKey = Resolve("EMBEDDINGS_API_KEY") is null && Resolve("OPENAI_API_KEY") is null;
        var breakers = sharesOpenRouterKey ? health : null;

        // Admitted through the same door as every chat call — including the half-open probe, so an
        // account whose only OpenRouter traffic is embeddings still notices the credit coming back.
        var probe = false;
        if (breakers is not null && !breakers.TryAdmit(ApiProvider.OpenRouter, ApiUsagePurpose.Embed, out var open, out probe))
        {
            throw new InvalidOperationException(
                $"Embeddings not sent: the OpenRouter breaker is open ({open?.ReasonCode ?? "unknown"}).");
        }

        var reported = false;
        EmbeddingsResponse dto;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings")
            {
                Content = JsonContent.Create(new
                {
                    model,
                    input = texts,
                }, options: JsonOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await httpClient.SendAsync(request, ct);
            if (breakers is not null)
            {
                // Any HTTP answer says whether the key works: a refusal opens (or re-opens) the
                // breaker, anything else closes it.
                if ((int)response.StatusCode is 401 or 402 or 403)
                {
                    breakers.ReportRefused(ApiProvider.OpenRouter, (int)response.StatusCode);
                }
                else
                {
                    breakers.ReportAnswered(ApiProvider.OpenRouter);
                }

                reported = true;
            }

            response.EnsureSuccessStatusCode();
            dto = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(JsonOptions, ct)
                ?? throw new InvalidOperationException("Empty embeddings response.");
        }
        finally
        {
            if (probe && !reported)
            {
                // The probe ended without an HTTP answer (a dropped connection, a cancellation):
                // hand the half-open slot back so the next call can probe.
                breakers!.ReportInconclusive(ApiProvider.OpenRouter);
            }
        }

        // Recorded after the response, still on the unified LLM-spend bucket but now carrying its own
        // model and purpose so embeddings stop being indistinguishable from chat. The embeddings API
        // bills prompt tokens only — there is no completion side, and reporting one would be a lie.
        usageRecorder.Record(
            ApiProvider.OpenRouter,
            ApiUsagePurpose.Embed,
            model,
            accountContext?.AccountId,
            dto.Usage?.PromptTokens);

        return dto.Data
            .OrderBy(d => d.Index)
            .Select(d => Normalize(d.Embedding))
            .ToArray();
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm <= 0)
        {
            return vector;
        }

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / norm);
        }

        return result;
    }

    private sealed record EmbeddingsResponse(EmbeddingDatum[] Data, OpenRouterUsage? Usage);

    private sealed record EmbeddingDatum(int Index, [property: JsonPropertyName("embedding")] float[] Embedding);
}
