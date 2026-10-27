import type { RailSupply } from "../store/feedTypes";

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
 */
export function railSupplyLine(
  supply: RailSupply | null | undefined,
  { empty, hiddenByView = 0 }: { empty: boolean; hiddenByView?: number }
): RailSupplyLine | null {
  // Cards this rail did load, hidden by the per-device browse toggles (availability is applied in
  // the browser, not by the server): the server's count is not the reason, so it is not quoted.
  if (empty && hiddenByView > 0) {
    return { text: "Nothing here matches the Movies/TV and availability view set on this device", seeWhy: null };
  }
  if (!supply) {
    return empty ? { text: "Nothing left here that you haven't seen and your conditions allow", seeWhy: "Your conditions" } : null;
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
