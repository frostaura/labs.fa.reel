using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The interim relevance gate: where a judged card goes, decided by what the judge said about the
/// REQUEST and by the account's own bar — never by a fit threshold.
///
/// <para><b>Why not a fit threshold.</b> Fit is a taste-blended score the prompt is told to spread,
/// and on the founder's first "ninja movies" asks 11–16 of 24 non-ninja cards scored ≥ 0.4 (max 0.79)
/// (B-2, M-1). A threshold would have passed the exact failure. The judge's separate request verdict
/// (<see cref="Satisfies"/>) is the gate:</para>
/// <list type="bullet">
/// <item><c>yes</c> → a pick, or the under-your-bar tray when the prediction misses the floor;</item>
/// <item><c>partial</c> → the collapsed "Close, but not …" tray (FD-2b), never a pick, never logged;</item>
/// <item><c>no</c> → dropped: not shown, not stored, not logged;</item>
/// <item>no verdict (a failed batch, the deterministic stub, a model that omitted the field) →
/// UNJUDGED, whatever its score. It is never treated as <c>no</c> — a failure is not evidence against
/// a title — and never promoted to <c>yes</c>. Where it lands depends on the rest of the turn (§4.8,
/// §4.10): below every judged pick when at least one card was judged a match and it clears the bar,
/// and in the "not checked against your request" tray otherwise — sixteen unjudged cards under the
/// model's "here are some ninja films" is exactly the failure this gate exists for (B-1).</item>
/// </list>
///
/// <para><b>An unjudged card is never named as a match under the bar</b> (R1-01): the samurai turn
/// put two unchecked non-samurai films in the floor tray and told the user they were samurai films
/// the bar held back. But the bar is the user's own rule and binds every card (XM-06), so an
/// unjudged card the bar holds back goes to the floor tray all the same (<see cref="UncheckedTier"/>),
/// carrying no verdict; only the unjudged cards that clear the bar go to the unchecked tray, and a
/// NoneFound reply names under the bar only the cards the judge said <c>yes</c> to.</para>
///
/// <para><b>The floor never relaxes</b> (C-7, N+2). A judged match under the account's For-you bar
/// goes to the tray in every case, whatever is left in the grid —
/// an empty grid with an honest account of why beats a bar that quietly stops meaning anything. A
/// card with NO prediction while a bar is set is held back with them (§4.8: "unscored cards with no
/// on-demand score are never picks"): the bar is the user's own rule, and a missing score is not a
/// pass. This is also the definition <c>SupplyReport.ClearingFloor</c> counts by (eligible − under
/// the bar − unscored), so the reply and the grid never disagree about the same title.</para>
/// </summary>
public static class AskReelGate
{
    /// <summary>Where a card lands. <see cref="Dropped"/> is not a stored tier; nothing is kept.</summary>
    public enum Placement
    {
        /// <summary>A judged <c>yes</c> clearing the floor: the grid.</summary>
        Pick,

        /// <summary>
        /// No verdict, whatever the score. The grid after every judged pick when the turn has one and
        /// the card clears the bar; the <see cref="SearchTurnCardTier.Unchecked"/> tray otherwise
        /// (see <see cref="UnjudgedTier"/>).
        /// </summary>
        Unjudged,

        /// <summary>A <c>partial</c>: the collapsed near-miss tray.</summary>
        NearMiss,

        /// <summary>A judged match held back by the For-you bar — under it, or unscored while one is set: the floor tray.</summary>
        BelowFloor,

        /// <summary>A <c>no</c>: gone.</summary>
        Dropped,
    }

    /// <summary>
    /// Where one card goes. Only a judged <c>yes</c> meets the bar here: <c>no</c> and <c>partial</c>
    /// already keep a card out of the grid, and a card with no verdict is <see cref="Placement.Unjudged"/>
    /// whatever its score (R1-01), its grid-or-tray decision taken by <see cref="UnjudgedTier"/> once
    /// the turn is judged. The caller scores the null cards once more before asking, so "unscored"
    /// means "no score could be made", not "not yet asked".
    /// </summary>
    public static Placement Place(Satisfies? satisfies, decimal? predicted, decimal floor) => satisfies switch
    {
        Satisfies.No => Placement.Dropped,
        Satisfies.Partial => Placement.NearMiss,
        Satisfies.Yes when !Clears(predicted, floor) => Placement.BelowFloor,
        Satisfies.Yes => Placement.Pick,
        _ => Placement.Unjudged,
    };

    /// <summary>
    /// The stored tier of an <see cref="Placement.Unjudged"/> card, decided once the whole turn is
    /// judged: the grid (below the judged picks) when at least one card was judged a match AND the card
    /// clears the account's bar, the unchecked tray otherwise — or, when the bar holds it back, the floor
    /// tray (<see cref="UncheckedTier"/>, which callers apply to the non-pick case).
    /// </summary>
    public static string UnjudgedTier(int judgedPicks, bool clearsFloor = true) =>
        judgedPicks > 0 && clearsFloor ? SearchTurnCardTier.Pick : SearchTurnCardTier.Unchecked;

    /// <summary>
    /// The tray an unjudged card goes to when it is not a pick: the floor tray when the account's bar
    /// holds it back (under it, or unscored while one is set), the unchecked tray otherwise.
    ///
    /// <para><b>The bar binds first (XM-06).</b> The unchecked tray used to take every unjudged card,
    /// under the bar or not, so a card the user's own rule rejects reached the screen outside the one
    /// tray labelled with that rule. The floor tray now holds every card the bar held back; its cards
    /// carry <c>satisfies: null</c> where nobody judged them, and the reply's "N held back by your bar"
    /// stays true of all of them. What R1-01 forbade is still forbidden: a NoneFound reply names
    /// as matches under the bar only cards the judge said <c>yes</c> to.</para>
    /// </summary>
    public static string UncheckedTier(decimal? predicted, decimal floor) =>
        Clears(predicted, floor) ? SearchTurnCardTier.Unchecked : SearchTurnCardTier.BelowFloor;

    /// <summary>The stored tier for a placement; null for <see cref="Placement.Dropped"/>.</summary>
    public static string? TierOf(Placement placement) => placement switch
    {
        Placement.Pick => SearchTurnCardTier.Pick,
        Placement.Unjudged => SearchTurnCardTier.Unchecked,
        Placement.NearMiss => SearchTurnCardTier.NearMiss,
        Placement.BelowFloor => SearchTurnCardTier.BelowFloor,
        _ => null,
    };

    /// <summary>
    /// The turn's mode: <see cref="SearchTurnMode.NoneFound"/> exactly when no card was judged a
    /// match. Unjudged cards never make a turn an answer on their own (§4.8: "NoneFound when there
    /// are 0 Yes cards").
    /// </summary>
    public static string ModeFor(int judgedPicks) =>
        judgedPicks > 0 ? SearchTurnMode.Picks : SearchTurnMode.NoneFound;

    /// <summary>
    /// The floor test at the precision the badge renders, so a card badged "6.0" is never placed under
    /// a "6.0" bar. With no bar every card clears; with one, a card with no prediction does not.
    /// </summary>
    public static bool Clears(decimal? predicted, decimal floor) =>
        floor <= 0m
        || (predicted is { } p && decimal.Round(p, 1, MidpointRounding.AwayFromZero) >= floor);
}
