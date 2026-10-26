using System.Collections.Concurrent;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>
/// Embeddings for short concept texts ("ninja", "survival at sea"), shared by the concept resolver's
/// nearest-label fallback and the hybrid retriever's semantic arm, so one text costs at most one
/// paid call per <see cref="Ttl"/> however many arms ask for it.
///
/// <para><b>Bounded wait, unbounded call.</b> An uncached text takes 1.2–2.7 s to embed (§4.7), so a
/// caller waits at most its budget and proceeds without the vector — but the call itself is never
/// abandoned: it finishes in the background and is cached, so the next turn that says the same thing
/// gets it for free. Concurrent askers share one in-flight call. A failure is logged and returns
/// null; it is never cached, so an outage does not poison the text.</para>
/// </summary>
public sealed class ConceptEmbeddings(IEmbeddingProvider provider, IMemoryCache cache, ILogger<ConceptEmbeddings> logger)
{
    /// <summary>How long an embedded text is reused. Texts are account-independent, so the key is the text and the model.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, Task<float[]?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>True when an embedding provider is configured.</summary>
    public bool IsAvailable => provider.IsAvailable;

    /// <summary>
    /// The vector for <paramref name="text"/>, waiting at most <paramref name="budget"/>; null when the
    /// text is empty, no provider is configured, the budget ran out or the call failed.
    /// </summary>
    public async Task<float[]?> EmbedAsync(string text, TimeSpan budget, CancellationToken ct)
    {
        var normalised = text.Trim().ToLowerInvariant();
        if (normalised.Length == 0 || !provider.IsAvailable)
        {
            return null;
        }

        var key = $"concept-embedding:{EmbeddingText.Model}:{normalised}";
        if (cache.TryGetValue<float[]>(key, out var cached) && cached is { Length: > 0 })
        {
            return cached;
        }

        var call = InFlight.GetOrAdd(key, _ => CallAsync(key, normalised));
        var finished = await Task.WhenAny(call, Task.Delay(budget, ct));
        ct.ThrowIfCancellationRequested();
        return finished == call ? await call : null;
    }

    private async Task<float[]?> CallAsync(string key, string text)
    {
        try
        {
            // Not tied to any caller's token: a caller that stops waiting must not cancel the call
            // the next turn would otherwise have to pay for again.
            var vectors = await provider.EmbedAsync([text], CancellationToken.None);
            if (vectors is [{ Length: > 0 } vector, ..])
            {
                cache.Set(key, vector, Ttl);
                return vector;
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Concept embedding failed; the arms that need it proceed without it.");
            return null;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }
}
