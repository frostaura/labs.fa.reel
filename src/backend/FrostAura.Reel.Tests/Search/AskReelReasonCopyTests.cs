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
            "10 picks ranked for you, from 40 pulled · nothing cleared your 6.0 bar, so it was relaxed "
            + "· 10 picks on screen under your 6.0 bar",
            AskReelReason.Build(
                picks: 10, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 0));
    }

    /// <summary>
    /// THE DEFECT three independent finders reported, one round apart, from three different
    /// browsers. Relaxation triggers on a THRESHOLD (fewer than six cards clear), and the copy
    /// asserted ZERO. On six of seven billed turns the user was shown cards badged 6.2 under a
    /// sentence saying nothing had cleared their 6.0 bar. The predicate and the sentence were never
    /// the same claim; now the sentence is built from the count rather than inferred from the flag.
    /// </summary>
    [Fact]
    public void A_relaxed_turn_says_how_many_actually_cleared()
    {
        Assert.Equal(
            "24 picks ranked for you, from 80 pulled · only 2 picks cleared your 6.0 bar, so it was relaxed "
            + "· 22 picks on screen under your 6.0 bar",
            AskReelReason.Build(
                picks: 24, outsideShown: 0, droppedByFloor: 0, discovered: 80, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 2));
    }

    /// <summary>
    /// Zero really is zero, and it still has to account for the grid: the old line stopped at
    /// "so it was relaxed" and left every sub-floor card on screen unexplained, because on a
    /// relaxed turn <c>droppedByFloor</c> is 0 and the tray is empty.
    /// </summary>
    [Fact]
    public void A_relaxed_turn_where_nothing_cleared_still_counts_the_grid()
    {
        Assert.Equal(
            "9 picks ranked for you, from 40 pulled · nothing cleared your 6.0 bar, so it was relaxed "
            + "· 9 picks on screen under your 6.0 bar",
            AskReelReason.Build(
                picks: 9, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 0));
    }

    /// <summary>
    /// The third branch, and the one that reads worst if the copy is written from the flag: a thin
    /// turn where every card DID clear still relaxes, because the threshold is about how much is
    /// left on the screen. "So it was relaxed" would be true and useless — the relaxation changed
    /// nothing — and it would send the reader hunting for below-bar cards that do not exist.
    /// </summary>
    [Fact]
    public void A_relaxed_turn_where_every_pick_cleared_does_not_claim_a_drop()
    {
        Assert.Equal(
            "3 picks ranked for you, from 40 pulled · every pick cleared your 6.0 bar",
            AskReelReason.Build(
                picks: 3, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 3));
    }

    /// <summary>
    /// Both new counts pluralise. "1 picks cleared" beside a real number is the same trust leak as
    /// the "1 picks" that shipped once already, and both sides of this clause pair can be 1.
    /// </summary>
    [Fact]
    public void The_relaxed_clauses_pluralise_on_both_sides()
    {
        Assert.Equal(
            "2 picks ranked for you, from 40 pulled · only 1 pick cleared your 6.0 bar, so it was relaxed "
            + "· 1 pick on screen under your 6.0 bar",
            AskReelReason.Build(
                picks: 2, outsideShown: 0, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 1));
    }

    /// <summary>
    /// The outside channel counts towards both halves — it is on the screen, so it is under the bar
    /// or over it like anything else, and the relaxed clause pair must not quietly ignore it.
    ///
    /// <para><b>But it is not counted as PICKS.</b> These clauses span both channels, and calling
    /// the total "picks" folded cards the very same sentence had just described as not matching the
    /// request into the pick count — "21 picks so far" sat against "0 picks matched everything you
    /// asked" on one measured screen. The noun becomes "cards" the moment an outside channel
    /// exists.</para>
    /// </summary>
    [Fact]
    public void The_outside_channel_is_counted_by_the_relaxed_clauses()
    {
        Assert.Equal(
            "4 picks matched everything you asked · 3 closest-but-outside shown separately "
            + "· only 2 cards cleared your 6.0 bar, so it was relaxed · 5 cards on screen under your 6.0 bar",
            AskReelReason.Build(
                picks: 4, outsideShown: 3, droppedByFloor: 0, discovered: 40, widened: false, floorRelaxed: true,
                floor: 6m, aboveFloor: 2));
    }

    /// <summary>
    /// A caller that has not wired the count through yet gets a clause that is true of every
    /// branch. It states nothing it cannot know — which is the whole point of the fix, and is why
    /// the sentinel is not allowed to fall back to the old absolute.
    /// </summary>
    [Fact]
    public void An_unwired_caller_gets_a_clause_that_claims_no_count()
    {
        Assert.Equal(
            "24 picks ranked for you, from 80 pulled · your 6.0 bar was relaxed to fill the screen",
            AskReelReason.Build(
                picks: 24, outsideShown: 0, droppedByFloor: 0, discovered: 80, widened: false, floorRelaxed: true,
                floor: 6m));
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
