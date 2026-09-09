using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The weekly push copy. It used to be two hardcoded strings and a link to "/" attached to a
/// digest that is entirely personalised, so the notification could not say what it had found and
/// the tap landed nowhere near it. These pin the four things the copy must do: name the top pick,
/// quote its number, say honestly why it is there, and deep-link to it.
/// </summary>
public class DigestPushCopyTests
{
    private static DigestItem Item(decimal rating = 8.2m, string reason = DigestReasons.NewRelease, string? degraded = null) =>
        new(Guid.NewGuid(), rating, reason, degraded);

    [Fact]
    public void The_title_names_the_top_pick_its_rating_and_how_many_others_wait()
    {
        var payload = BuildDigestJobHandler.BuildPushPayload(
            "Sicario: Day of the Soldado", "Movie", 400535, Item(), itemCount: 10);

        Assert.Equal("Sicario: Day of the Soldado — 8.2 for you, plus 9 more this week", payload.Title);
    }

    /// <summary>Also pins the display rounding: stored 2dp, shown 1dp, like every other served rating.</summary>
    [Fact]
    public void A_digest_of_one_drops_the_plus_clause_rather_than_saying_plus_zero()
    {
        var payload = BuildDigestJobHandler.BuildPushPayload(
            "Quiet Harbor", "Movie", 1001, Item(rating: 7.06m), itemCount: 1);

        Assert.Equal("Quiet Harbor — 7.1 for you", payload.Title);
    }

    [Fact]
    public void The_body_is_the_top_items_own_why()
    {
        Assert.Equal(
            "New this week",
            BuildDigestJobHandler.BuildPushPayload("A", "Movie", 1, Item(reason: DigestReasons.NewRelease), 3).Body);
        Assert.Equal(
            "Just landed on your services",
            BuildDigestJobHandler.BuildPushPayload("A", "Movie", 1, Item(reason: DigestReasons.NewOnProvider), 3).Body);
    }

    [Fact]
    public void A_relaxed_floor_says_so_instead_of_overselling_the_pick()
    {
        var degraded = "Only 2 of this week's arrivals cleared the 7 “high for you” bar, so it was relaxed to 6 for this digest.";
        var payload = BuildDigestJobHandler.BuildPushPayload("A", "Movie", 1, Item(degraded: degraded), 5);

        Assert.Equal(degraded, payload.Body);
    }

    [Theory]
    [InlineData("Movie", "/title/movie/400535")]
    [InlineData("Show", "/title/show/400535")]
    public void The_url_deep_links_to_the_top_pick(string mediaType, string expected)
    {
        var payload = BuildDigestJobHandler.BuildPushPayload("A", mediaType, 400535, Item(), 4);

        Assert.Equal(expected, payload.Url);
    }

    [Fact]
    public void A_title_row_that_cannot_be_deep_linked_falls_back_to_home_and_a_countable_headline()
    {
        // The only case the /title/:mediaType/:tmdbId route cannot express: no title row, or a
        // catalogue row with no TMDB id. The push still goes out — it just cannot name the pick.
        var missing = BuildDigestJobHandler.BuildPushPayload(null, null, null, Item(), itemCount: 6);
        Assert.Equal("/", missing.Url);
        Assert.Equal("Your week in Reel: 6 new high-for-you titles", missing.Title);

        var noTmdbId = BuildDigestJobHandler.BuildPushPayload("Quiet Harbor", "Movie", null, Item(), itemCount: 6);
        Assert.Equal("/", noTmdbId.Url);
        Assert.StartsWith("Quiet Harbor", noTmdbId.Title);
    }

    [Fact]
    public void The_serialized_payload_keeps_the_lowercase_keys_the_service_worker_reads()
    {
        var json = JsonSerializer.Serialize(
            BuildDigestJobHandler.BuildPushPayload("Quiet Harbor", "Movie", 1001, Item(), 3));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
        Assert.Equal("Quiet Harbor — 8.2 for you, plus 2 more this week", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("New this week", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("/title/movie/1001", doc.RootElement.GetProperty("url").GetString());
    }
}
