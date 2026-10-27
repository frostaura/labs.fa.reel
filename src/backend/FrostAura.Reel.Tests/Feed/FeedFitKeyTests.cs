using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// WS-1 CR-3: the feed's fit cache is keyed through <see cref="RerankCacheKey"/> with the agent's salt,
/// so a verdict written by another model, effort, prompt or result format misses instead of serving.
/// </summary>
public class FeedFitKeyTests
{
    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Title = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_verdict_under_another_salt_is_another_key()
    {
        var key = FeedPaginator.FitKey("rr2:m|none:p00000000", Account, Title, "hero");

        Assert.Equal(key, FeedPaginator.FitKey("rr2:m|none:p00000000", Account, Title, "hero"));
        Assert.NotEqual(key, FeedPaginator.FitKey("rr2:m|low:p00000000", Account, Title, "hero"));
        Assert.NotEqual(key, FeedPaginator.FitKey("rr1:m|none:p00000000", Account, Title, "hero"));
        Assert.NotEqual(key, FeedPaginator.FitKey("rr2:m|none:p00000000", Account, Title, "loved:x"));
        Assert.StartsWith("feedfit:rr2:m|none:p00000000:", key, StringComparison.Ordinal);
        Assert.Equal(RerankCacheKey.For("feedfit", "rr2:m|none:p00000000", Account, Title, "hero", []), key);
    }
}
