using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// IR2S-02: "Just landed on your services" was false twice — "just landed" was a cache refresh and
/// Reel has no idea which services are "yours". An arrival now names a provider, only one the card
/// shows, and an arrival the card cannot back is not rendered at all.
/// </summary>
public class DigestArrivalCopyTests
{
    private static readonly Guid TitleId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static FeedEndpoints.CardProvider Shown(string name) => new(name, $"/{name}.png");

    [Theory]
    [InlineData("Netflix", "Streaming on Netflix")]
    [InlineData("  Hulu ", "Streaming on Hulu")]
    [InlineData(null, "Streaming now")]
    [InlineData("   ", "Streaming now")]
    public void An_arrival_names_its_provider_and_never_claims_your_services(string? provider, string expected)
    {
        var copy = DigestReasons.CopyFor(DigestReasons.NewOnProvider, provider);
        Assert.Equal(expected, copy);
        Assert.DoesNotContain("your", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("landed", copy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_release_is_new_this_week_whatever_provider_rides_along() =>
        Assert.Equal("New this week", DigestReasons.CopyFor(DigestReasons.NewRelease, "Netflix"));

    [Fact]
    public void The_card_names_the_provider_it_arrived_on_when_its_strip_shows_it()
    {
        // Second in the strip, not first: the arrival outranks display order.
        var item = new DigestItem(TitleId, 7.6m, DigestReasons.NewOnProvider, Provider: "Hulu");
        Assert.Equal("Streaming on Hulu", DigestEndpoints.ReasonCopy(item, [Shown("Netflix"), Shown("Hulu")]));

        // Matched case-insensitively, and spelled the way the strip spells it.
        var lower = item with { Provider = "hulu " };
        Assert.Equal("Streaming on Hulu", DigestEndpoints.ReasonCopy(lower, [Shown("Netflix"), Shown("Hulu")]));
    }

    [Fact]
    public void The_card_never_names_a_provider_its_strip_does_not_show()
    {
        // Arrived on Peacock, but Peacock fell out of the top-3 strip: the line names what the
        // reader can actually see rather than something they cannot.
        var item = new DigestItem(TitleId, 7.6m, DigestReasons.NewOnProvider, Provider: "Peacock");
        Assert.Equal("Streaming on Netflix", DigestEndpoints.ReasonCopy(item, [Shown("Netflix"), Shown("Max")]));
    }

    [Fact]
    public void An_arrival_whose_offer_has_lapsed_is_not_rendered()
    {
        var item = new DigestItem(TitleId, 7.6m, DigestReasons.NewOnProvider, Provider: "Netflix");
        Assert.Null(DigestEndpoints.ReasonCopy(item, []));
    }

    [Fact]
    public void A_pre_fix_arrival_built_off_a_cache_refresh_is_not_rendered_even_while_streaming()
    {
        // The live 2026-10-26 shape: NCIS on Netflix, reason new-on-provider, no provider recorded
        // because the build only knew the row had been re-fetched.
        var legacy = new DigestItem(TitleId, 7.6m, DigestReasons.NewOnProvider);
        Assert.Null(DigestEndpoints.ReasonCopy(legacy, [Shown("Netflix"), Shown("Hulu")]));
    }

    [Fact]
    public void A_release_renders_with_or_without_a_strip()
    {
        var item = new DigestItem(TitleId, 8.1m, DigestReasons.NewRelease);
        Assert.Equal("New this week", DigestEndpoints.ReasonCopy(item, []));
        Assert.Equal("New this week", DigestEndpoints.ReasonCopy(item, [Shown("Netflix")]));
    }

    [Fact]
    public void The_push_body_for_an_arrival_names_the_provider()
    {
        var top = new DigestItem(TitleId, 8.0m, DigestReasons.NewOnProvider, Provider: "Netflix");
        var payload = BuildDigestJobHandler.BuildPushPayload("Southland", "Show", 1, top, 1);
        Assert.Equal("Streaming on Netflix", payload.Body);
    }
}
