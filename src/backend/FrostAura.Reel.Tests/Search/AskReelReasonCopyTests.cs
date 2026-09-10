using System.Text.Json;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The line under the results, and the numbers on the cards above it.
///
/// <para>Both are small and both are load-bearing. "1 picks" shipped, and a reader who notices it
/// stops trusting the counts beside it. A cosine printed to seventeen significant figures claims a
/// precision the number does not have. And the reason line used to NEST its conditions, so the
/// rating-floor clause was reachable in only one of its two branches: a turn that removed four
/// cards for failing the user's own bar said nothing whatsoever about them, which is exactly what
/// "it only showed me six" looks like from the outside.</para>
/// </summary>
public class AskReelReasonCopyTests
{
    [Fact]
    public void One_pick_is_singular()
    {
        Assert.StartsWith(
            "1 pick ranked for you",
            AskReelReason.Build(picks: 1, outsideShown: 0, droppedByFloor: 0, discovered: 30, widened: false, floorRelaxed: false, floor: 0m),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "2 picks ranked for you",
            AskReelReason.Build(picks: 2, outsideShown: 0, droppedByFloor: 0, discovered: 30, widened: false, floorRelaxed: false, floor: 0m),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "1 pick matched everything you asked",
            AskReelReason.Build(picks: 1, outsideShown: 3, droppedByFloor: 0, discovered: 30, widened: false, floorRelaxed: false, floor: 0m),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_turn_that_found_nothing_says_so_and_says_nothing_else()
    {
        Assert.Equal(
            AskReelReason.Empty,
            AskReelReason.Build(picks: 0, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: true, floorRelaxed: true, floor: 6m));
    }

    /// <summary>
    /// The defect the clause list exists for: the floor speaks in BOTH branches. Neither of these
    /// sentences could be produced by the old nested string.
    /// </summary>
    [Fact]
    public void The_floor_clause_fires_whether_it_bound_or_was_relaxed()
    {
        Assert.Equal(
            "6 picks ranked for you, from 40 pulled · 4 under your 6.0 bar — shown below",
            AskReelReason.Build(picks: 6, outsideShown: 0, droppedByFloor: 4, discovered: 40, widened: false, floorRelaxed: false, floor: 6m));

        Assert.Equal(
            "10 picks ranked for you, from 40 pulled · nothing cleared your 6.0 bar, so it was relaxed",
            AskReelReason.Build(picks: 10, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true, floor: 6m));
    }

    [Fact]
    public void Every_true_fact_about_a_turn_gets_its_own_clause()
    {
        Assert.Equal(
            "5 picks matched everything you asked · 7 closest-but-outside shown separately · widened to find enough "
            + "· 3 under your 7.5 bar — shown below",
            AskReelReason.Build(picks: 5, outsideShown: 7, droppedByFloor: 3, discovered: 62, widened: true, floorRelaxed: false, floor: 7.5m));
    }

    /// <summary>
    /// The floor is rendered to one decimal, invariantly, at every site. A comma decimal separator
    /// on a differently-configured host would be a wrong answer here rather than a rendering, and
    /// "6" for a 6.0 bar reads as a different setting from the one the user typed.
    /// </summary>
    [Theory]
    [InlineData(6, "6.0")]
    [InlineData(7.5, "7.5")]
    [InlineData(8.25, "8.3")]
    public void The_bar_is_one_invariant_decimal(double floor, string rendered)
    {
        Assert.Contains(
            $"your {rendered} bar",
            AskReelReason.Build(picks: 6, outsideShown: 0, droppedByFloor: 1, discovered: 40, widened: false, floorRelaxed: false, floor: (decimal)floor),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Similarity and fit are rendered numbers, so they go on the wire rounded. Blend rides along at
    /// four places to explain the order without inviting a client to recompute it — the array order
    /// carries a tiebreak this number cannot express.
    /// </summary>
    [Fact]
    public void Fit_is_two_decimal_places_on_the_wire()
    {
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktSlug = string.Empty,
            Name = "A Title",
            Genres = ["science-fiction"],
            CreatedAt = DateTime.UtcNow,
        };

        var card = JsonSerializer.SerializeToElement(
            LiveSearchExpansionService.Card(
                title, similarity: 0.8271839203d, predicted: 7.25m, fit: 0.6666666666d, why: "Because.",
                blend: 0.5123456789d),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(0.83d, card.GetProperty("similarity").GetDouble());
        Assert.Equal(0.67d, card.GetProperty("fit").GetDouble());
        Assert.Equal(0.5123d, card.GetProperty("blend").GetDouble());
    }

    /// <summary>An absent number stays absent — rounding null into 0.0 would invent a measurement.</summary>
    [Fact]
    public void An_absent_similarity_or_fit_stays_null()
    {
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktSlug = string.Empty,
            Name = "A Title",
            Genres = [],
            CreatedAt = DateTime.UtcNow,
        };

        var card = JsonSerializer.SerializeToElement(
            LiveSearchExpansionService.Card(title, similarity: null, predicted: null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(JsonValueKind.Null, card.GetProperty("similarity").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("fit").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("blend").ValueKind);
    }
}
