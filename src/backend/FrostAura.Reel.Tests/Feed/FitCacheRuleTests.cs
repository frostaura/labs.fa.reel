using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// R1-23 (WS-1 task 4 leftover in WS-5's files): a fit verdict enters the shared fit cache only when
/// the model gave it, through one helper the paginator and the feed build both use. A fallback fit
/// is the stub's predicted/10; cached, one failed batch replayed for six hours as model output.
/// </summary>
public class FitCacheRuleTests
{
    [Theory]
    [InlineData(ResultSource.Llm, ResultSource.Llm, true)]
    // The voice guard refused only the sentence: the fit is still the model's, so it is kept.
    [InlineData(ResultSource.Llm, ResultSource.Fallback, true)]
    [InlineData(ResultSource.Fallback, ResultSource.Fallback, false)]
    [InlineData(ResultSource.Fallback, ResultSource.Llm, false)]
    public void Only_a_model_fit_is_ever_cached(ResultSource fitSource, ResultSource whySource, bool cached)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = new RerankResult(Guid.NewGuid(), 0.42d, fitSource, whySource == ResultSource.Llm ? "You keep coming back to this." : string.Empty, whySource);

        Assert.Equal(cached, FeedPaginator.CacheFit(cache, "fit-key", result));
        Assert.Equal(cached, cache.TryGetValue("fit-key", out _));
    }

    [Theory]
    [InlineData(ResultSource.Llm, 0.42d)]
    [InlineData(ResultSource.Fallback, null)]
    public void A_fallbacks_fit_reads_as_no_fit(ResultSource fitSource, double? expected)
    {
        var result = new RerankResult(Guid.NewGuid(), 0.42d, fitSource, string.Empty, ResultSource.Fallback);

        Assert.Equal(expected, result.ModelFit);
    }
}
