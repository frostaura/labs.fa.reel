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
/// UNJUDGED: kept, ranked below every judged pick, shown without a model why-line (§4.10). It is
/// never treated as <c>no</c> — a failure is not evidence against a title — and never promoted to
/// <c>yes</c>.</item>
/// </list>
///
/// <para><b>The floor never relaxes</b> (C-7, N+2). A card under the account's For-you bar goes to
/// the tray in every case, whatever is left in the grid — an empty grid with an honest account of
/// why beats a bar that quietly stops meaning anything.</para>
/// </summary>
public static class AskReelGate
{
    /// <summary>Where a card lands. <see cref="Dropped"/> is not a stored tier; nothing is kept.</summary>
    public enum Placement
    {
        /// <summary>A judged <c>yes</c> clearing the floor: the grid.</summary>
        Pick,

        /// <summary>No verdict, clearing the floor: the grid, after every judged pick.</summary>
        UnjudgedPick,

        /// <summary>A <c>partial</c>: the collapsed near-miss tray.</summary>
        NearMiss,

        /// <summary>A <c>yes</c> or unjudged card under the For-you bar: the floor tray.</summary>
        BelowFloor,

        /// <summary>A <c>no</c>: gone.</summary>
        Dropped,
    }

    /// <summary>
    /// Where one card goes. A card with no prediction clears the floor — the account may have no
    /// model at all, and refusing every card for that is refusing to answer (see
    /// <see cref="AskReelFloor"/>); the caller scores the null cards once more before asking.
    /// </summary>
    public static Placement Place(Satisfies? satisfies, decimal? predicted, decimal floor) => satisfies switch
    {
        Satisfies.No => Placement.Dropped,
        Satisfies.Partial => Placement.NearMiss,
        Satisfies.Yes => Clears(predicted, floor) ? Placement.Pick : Placement.BelowFloor,
        _ => Clears(predicted, floor) ? Placement.UnjudgedPick : Placement.BelowFloor,
    };

    /// <summary>The stored tier for a placement; null for <see cref="Placement.Dropped"/>.</summary>
    public static string? TierOf(Placement placement) => placement switch
    {
        Placement.Pick or Placement.UnjudgedPick => SearchTurnCardTier.Pick,
        Placement.NearMiss => SearchTurnCardTier.NearMiss,
        Placement.BelowFloor => SearchTurnCardTier.BelowFloor,
        _ => null,
    };

    /// <summary>
    /// The turn's mode: <see cref="SearchTurnMode.NoneFound"/> exactly when nothing reached the grid.
    /// </summary>
    public static string ModeFor(int picksInGrid) =>
        picksInGrid > 0 ? SearchTurnMode.Picks : SearchTurnMode.NoneFound;

    /// <summary>
    /// The floor test at the precision the badge renders — the same rule as <see cref="AskReelFloor"/>,
    /// so a card badged "6.0" is never placed under a "6.0" bar.
    /// </summary>
    public static bool Clears(decimal? predicted, decimal floor) =>
        floor <= 0m
        || predicted is not { } p
        || decimal.Round(p, 1, MidpointRounding.AwayFromZero) >= floor;
}
