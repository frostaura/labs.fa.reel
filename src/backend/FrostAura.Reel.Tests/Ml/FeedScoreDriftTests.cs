using System.Text.Json;
using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The rule that decides whether a Preserve build's recalibration reaches the screen.
///
/// <para>Before this existed, a build that changed every prediction and no item wrote nothing at
/// all: the no-op comparison is on (Row, Anchor, Rank, TitleId) and the numbers were discarded one
/// line before the snapshot. These tests pin the two halves of the split — the threshold that keeps
/// last-digit flicker off the page, and the payload the client patches from.</para>
/// </summary>
public class FeedScoreDriftTests
{
    [Theory]
    [InlineData(6.9, 5.6, true)]   // the founder's case: hero stale at 6.9, everything else fresh at 5.6
    [InlineData(7.0, 7.3, true)]   // exactly the threshold — inclusive, so a 0.3 move is worth showing
    [InlineData(7.0, 6.7, true)]   // symmetric downward
    [InlineData(7.0, 7.2, false)]  // below the threshold: two last-digit steps, reads as instability
    [InlineData(7.0, 7.0, false)]
    public void A_rating_move_is_visible_only_at_or_past_the_threshold(double previous, double current, bool expected) =>
        Assert.Equal(expected, FeedScoreDrift.RatingMoved((decimal)previous, (decimal)current));

    [Fact]
    public void The_threshold_is_three_tenths_of_a_displayed_point() =>
        Assert.Equal(0.3m, FeedScoreDrift.Threshold);

    [Fact]
    public void A_contributions_change_counts_even_when_the_number_holds()
    {
        const string before = """[{"feature":"directorAffinity","value":0.41}]""";
        const string after = """[{"feature":"genreAffinity","value":0.41}]""";

        Assert.True(FeedScoreDrift.ExplanationChanged(before, after));
        // The number did not move, but the card is now justified differently — that is a write.
        Assert.True(FeedScoreDrift.Changed(7.0m, 7.0m, before, after));
        Assert.False(FeedScoreDrift.Changed(7.0m, 7.0m, before, before));
    }

    [Fact]
    public void A_null_explanation_and_an_empty_one_are_the_same_absence() =>
        Assert.False(FeedScoreDrift.ExplanationChanged(null, string.Empty));

    [Fact]
    public void The_payload_carries_the_unchanged_generated_at_and_only_the_moved_cards()
    {
        var generatedAt = new DateTime(2026, 9, 9, 18, 30, 0, DateTimeKind.Utc);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var payload = FeedScoreDrift.Payload(
            generatedAt,
            [new FeedScoreUpdate(first, 6.4m), new FeedScoreUpdate(second, 8.1m)]);

        Assert.Equal(generatedAt, Assert.IsType<DateTime>(payload["generatedAt"]));
        var changed = Assert.IsType<List<Dictionary<string, object?>>>(payload["changed"]);
        Assert.Equal(2, changed.Count);
        Assert.Equal(first, changed[0]["titleId"]);
        Assert.Equal(6.4m, changed[0]["predictedRating"]);
        Assert.Equal(second, changed[1]["titleId"]);
        Assert.Equal(8.1m, changed[1]["predictedRating"]);
    }

    [Fact]
    public void The_payload_serialises_to_the_frame_the_client_parses()
    {
        var titleId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var json = JsonSerializer.Serialize(
            FeedScoreDrift.Payload(
                new DateTime(2026, 9, 9, 18, 30, 0, DateTimeKind.Utc),
                [new FeedScoreUpdate(titleId, 6.4m)]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Keys are asserted verbatim: RealtimeSync.tsx reads them by name off the SSE frame, and a
        // rename here is invisible to the compiler on both sides.
        Assert.Contains("\"generatedAt\":\"2026-09-09T18:30:00Z\"", json);
        Assert.Contains("\"changed\":[", json);
        Assert.Contains("\"titleId\":\"11111111-2222-3333-4444-555555555555\"", json);
        Assert.Contains("\"predictedRating\":6.4", json);
    }

    [Fact]
    public void An_empty_change_set_still_produces_a_well_formed_payload()
    {
        var payload = FeedScoreDrift.Payload(DateTime.UtcNow, []);

        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(payload["changed"]));
    }
}
