import type { AnchoredSupply, RailSupply } from "../store/feedTypes";

/** The line under a rail that came up short or empty, and the label of its way into the editor. */
export interface RailSupplyLine {
  text: string;
  /** The label for the button that opens the conditions editor, or null when no condition is why. */
  seeWhy: string | null;
}

const count = (n: number) => n.toLocaleString("en-US");

/**
 * What a short or empty rail says about itself (HREV1-RET-07, WS-5 task 9: "Rails that come up
 * short show your conditions hide N — see why").
 *
 * A rail used to render nothing at all once it was empty, and nothing extra while short — so a
 * Thriller rail with 251 titles held out by the account's conditions, or a "More like this" with
 * nothing left to offer, vanished without a word, and the founder could not tell a thin catalogue
 * from his own rules. Now:
 *
 * - **Conditions hide some** (the server's `supply`): said with the number, and a "See why" into the
 *   conditions editor — the one control that changes it. What is already seen rides along, so the
 *   count is not mistaken for the whole story.
 * - **Nothing hidden** — an empty rail says what it does know (what is seen); a short one has nothing
 *   to explain and says nothing.
 * - **No count from the server** (every row but genre rails today): an empty rail says it is empty,
 *   in terms true of every rail — they never show what you have seen or what your conditions rule
 *   out — and names no cause it cannot vouch for.
 * - **A tier that pages no rails** (`tierCapped`, Free): its /feed/more is always empty and exhausted
 *   with no count, because of the tier and nothing else, so seen titles and conditions would be a
 *   false cause. No line — the rail vanishes, as it did before RET-07.
 * - **An anchored rail that ran dry** (the server's `anchoredSupply`, "More like this" and "Because
 *   you loved"): its window had already applied the conditions, so what it lost is to the
 *   shared-evidence gate (titles above the bar sharing too little with the anchor) and to the bar.
 *   Said as that (HREV5-RET-01) — the generic line blamed "your conditions" for 146 titles the gate
 *   refused. The gate is not a setting, so it gets no link; the bar is one, so a rail only the bar
 *   emptied links to the editor that holds it.
 */
export function railSupplyLine(
  supply: RailSupply | null | undefined,
  {
    empty,
    hiddenByView = 0,
    tierCapped = false,
    anchored = null,
    anchorName = null,
    bar = null,
  }: {
    empty: boolean;
    hiddenByView?: number;
    tierCapped?: boolean;
    /** What an anchored rail's window lost (see {@link AnchoredSupply}). */
    anchored?: AnchoredSupply | null;
    /** The anchor's name, for the anchored line; "this title" without one. */
    anchorName?: string | null;
    /** The account's for-you bar, when known; the anchored line names it. */
    bar?: number | null;
  }
): RailSupplyLine | null {
  // Cards this rail did load, hidden by the per-device browse toggles (availability is applied in
  // the browser, not by the server): the server's count is not the reason, so it is not quoted.
  if (empty && hiddenByView > 0) {
    return { text: "Nothing here matches the Movies/TV and availability view set on this device", seeWhy: null };
  }
  if (!supply && empty && !tierCapped && anchored && (anchored.gateHeldBack > 0 || anchored.belowBar > 0)) {
    return anchoredLine(anchored, anchorName, bar);
  }
  if (!supply) {
    return empty && !tierCapped ? { text: "Nothing left here that you haven't seen and your conditions allow", seeWhy: "Your conditions" } : null;
  }
  const hidden = supply.hiddenByConditions;
  const seen = supply.alreadySeen > 0 ? ` · you've seen ${count(supply.alreadySeen)}` : "";
  if (hidden > 0) {
    const noun = hidden === 1 ? "title" : "titles";
    return empty
      ? { text: `Nothing left here — your conditions hide ${count(hidden)} ${noun}${seen}`, seeWhy: "See why" }
      : { text: `Your conditions hide ${count(hidden)} more ${noun}${seen}`, seeWhy: "See why" };
  }
  if (!empty) return null;
  return supply.alreadySeen > 0
    ? { text: `Nothing left here — you've seen ${count(supply.alreadySeen)}`, seeWhy: null }
    : { text: "Nothing left here right now", seeWhy: null };
}

/** The line for an anchored rail the gate or the bar emptied; see {@link railSupplyLine}. */
function anchoredLine({ gateHeldBack, belowBar }: AnchoredSupply, anchorName: string | null, bar: number | null): RailSupplyLine {
  const anchor = anchorName?.trim() || "this title";
  const yourBar = bar != null && bar > 0 ? `your ${bar.toFixed(1)} bar` : "your for-you bar";
  if (gateHeldBack > 0) {
    const matches = `${count(gateHeldBack)} near ${gateHeldBack === 1 ? "match" : "matches"}`;
    const below = belowBar > 0 ? ` · ${count(belowBar)} more ${belowBar === 1 ? "is" : "are"} under ${yourBar}` : "";
    return { text: `Nothing else shares a person, keyword or theme with ${anchor} closely enough (${matches})${below}`, seeWhy: null };
  }
  const close = `${count(belowBar)} close ${belowBar === 1 ? "match is" : "matches are"} under it`;
  return { text: `Nothing left like ${anchor} above ${yourBar} — ${close}`, seeWhy: "Your bar" };
}
