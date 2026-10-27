using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's card on its way to the screen: the gate's placement, the wire projection, the stored row. v1 only;
/// deleted with the v1 pipeline at cutover (§4.13).
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// A card on its way to one of the answer's places, with everything every reader of it needs.
    /// </summary>
    private sealed record GatedCard(
        Title Title,
        double Sim,
        decimal? Predicted,
        RerankResult? Verdict,
        double Blend,
        AskReelGate.Placement Placement,
        bool Outside,
        string? Why)
    {
        /// <summary>The fit, only where the MODEL produced it — a fallback number is never shown or stored.</summary>
        public double? ModelFit => Verdict is { FitSource: ResultSource.Llm } v ? v.Fit : null;

        /// <summary>The model's request verdict, persisted form; null when unjudged.</summary>
        public string? SatisfiesWire =>
            Verdict is { FitSource: ResultSource.Llm, Satisfies: { } s } ? LlmResultText.Wire(s) : null;
    }

    /// <summary>
    /// Builds a gated card. The why-line is the model's ONLY when the model wrote it: a fallback
    /// sentence ("A action pick aligned with your taste.") is never shown, never stored, never
    /// logged as model output (A-8, B-6). The blend uses the fit only when it is the model's; an
    /// unjudged card is ordered without one rather than with an invented one.
    /// </summary>
    private static GatedCard Gate(
        Title title, double sim, decimal? predicted, RerankResult? verdict, double tagBoost, AskReelGate.Placement placement)
    {
        var modelFit = verdict is { FitSource: ResultSource.Llm } v ? v.Fit : (double?)null;
        var blend = (sim * 0.32) + ((double)(predicted ?? 6m) / 10d * 0.23) + ((modelFit ?? 0d) * 0.35) + (tagBoost * 0.10);
        var why = verdict is { WhySource: ResultSource.Llm, Why: { Length: > 0 } w } ? AskReelText.StripMarkdown(w) : null;
        return new GatedCard(title, sim, predicted, verdict, blend, placement, Outside: false, why);
    }

    /// <summary>The wire card for a gated card.</summary>
    private static object CardOf(GatedCard c) =>
        Card(c.Title, c.Sim, c.Predicted, c.ModelFit, c.Why, outsideRequest: c.Outside, blend: c.Blend,
            satisfies: c.SatisfiesWire);

    /// <summary>Emits one card and records it as shown, so an abort persists it.</summary>
    /// <summary>
    /// Unjudged cards split by the account's bar (XM-06): the ones it holds back (under it, or unscored
    /// while it is set) for the floor tray, the rest for the unchecked tray — each half in its order.
    /// </summary>
    private static (List<GatedCard> Held, List<GatedCard> Clearing) SplitByBar(IEnumerable<GatedCard> cards, decimal floor)
    {
        var held = new List<GatedCard>();
        var clearing = new List<GatedCard>();
        foreach (var card in cards)
        {
            (AskReelGate.Clears(card.Predicted, floor) ? clearing : held).Add(card);
        }

        return (held, clearing);
    }

    private static async Task EmitCardAsync(GatedCard card, AskTurnProgress progress, Emit emit)
    {
        progress.Shown(Row(card, rank: 0, SearchTurnCardTier.Pick));
        await emit("candidate", CardOf(card));
    }

    /// <summary>
    /// The stored rows, in the order the screen renders them: picks and outside cards first (ranks
    /// 0..n-1), then the floor tray, then the near-miss tray, then the unchecked tray — so a tray card's
    /// stored rank is its position on screen, which is what its handle ("the second one") counts.
    /// </summary>
    private static List<TurnCard> Stored(
        IReadOnlyList<GatedCard> final, IReadOnlyList<GatedCard> nearMiss, IReadOnlyList<GatedCard> belowFloor,
        IReadOnlyList<GatedCard> uncheckedTray)
    {
        var rows = new List<TurnCard>(final.Count + nearMiss.Count + belowFloor.Count + uncheckedTray.Count);
        rows.AddRange(final.Select((c, i) => Row(c, i, SearchTurnCardTier.Pick)));
        var offset = rows.Count;
        rows.AddRange(belowFloor.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.BelowFloor)));
        offset = rows.Count;
        rows.AddRange(nearMiss.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.NearMiss)));
        offset = rows.Count;
        rows.AddRange(uncheckedTray.Select((c, i) => Row(c, offset + i, SearchTurnCardTier.Unchecked)));
        return rows;
    }

    /// <summary>One stored row for a gated card.</summary>
    private static TurnCard Row(GatedCard c, int rank, string tier) =>
        new(
            c.Title.Id, c.Title.TmdbId, rank, c.Outside,
            Math.Round(c.Sim, 2),
            c.Predicted is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null,
            c.ModelFit is { } fit ? Math.Round((decimal)fit, 3, MidpointRounding.AwayFromZero) : null,
            Math.Round((decimal)c.Blend, 4, MidpointRounding.AwayFromZero),
            c.Why,
            ExposureWhySourceOf(c),
            BelowFloor: tier != SearchTurnCardTier.Pick,
            Tier: tier,
            Satisfies: c.SatisfiesWire,
            FitSource: c.Verdict is { } v ? LlmResultText.Name(v.FitSource) : null);

    /// <summary>
    /// The typed source of a card's why-line, assigned by the code that produced it — never inferred
    /// from "the sentence is non-empty", which is how thousands of stub sentences were logged as model
    /// output (B-6). An outside card's caption is the deterministic template; a model sentence is
    /// <c>Llm</c>; a card with no why-line says so (<c>None</c>).
    /// </summary>
    private static ExposureWhySource ExposureWhySourceOf(GatedCard c) =>
        c.Outside ? ExposureWhySource.Templater
        : c.Why is { Length: > 0 } ? ExposureWhySource.Llm
        : ExposureWhySource.None;
}
