using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// A deterministic, offline <see cref="IEmbeddingProvider"/>: 1536 dimensions derived from the
/// text's LENGTH and L2-normalised, so two texts of the same length embed identically and texts of
/// different lengths land at stable, reproducible angles. That is enough for a cosine-similarity
/// path to be exercised end to end, and it costs nothing.
///
/// <para>It exists because the embeddings adapter has no stub mode of its own — unlike the
/// OpenRouter adapters, which answer deterministically when keyless or under
/// <c>TestHelpers:ModelStubMode</c>. Left in place, the real provider would reach for
/// <c>EMBEDDINGS_API_KEY</c> and bill a live account from a test run. Promoted from the private
/// copy in <c>LiveSearchExpansionTests</c> so the WAF harness can swap it in for every
/// endpoint test rather than each test re-declaring one.</para>
/// </summary>
internal sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public bool IsAvailable => true;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = texts.Select(t =>
        {
            var v = new float[1536];
            var seed = t.Length;
            for (var i = 0; i < v.Length; i++)
            {
                v[i] = (float)Math.Sin(seed + i);
            }

            var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }

            return v;
        }).ToArray();
        return Task.FromResult(vectors);
    }
}
