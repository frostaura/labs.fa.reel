/**
 * What the page does about the earlier answer folding above the live turn (HREV6-PH-01) — the
 * decision {@link HoldLiveTurn} carries out, kept pure so every branch is pinned without a browser.
 *
 * - `hold` — scroll by the shift, so the anchor (just above the newest question) stays exactly
 *   where the reader sees it. The browser's own scroll anchoring does this in Chromium; WebKit has
 *   none, and without it a phone was left showing only the tail of the new grid.
 * - `retarget` — finish the turn's own move instead: the anchor to the top, as
 *   `scrollTargetFor("streaming")` meant. Two cases. The page is still SETTLING — its smooth scroll
 *   to the new question is in flight, aimed at where the anchor WAS, and would overshoot by the
 *   whole fold in either engine (anchoring corrects the offset, not the animation's target). Or
 *   the anchor was BELOW the viewport: the reader had scrolled back up into the very answer that
 *   just folded away, so what they were reading is gone.
 * - `none` — nothing moved (sub-pixel rounding is not a jump), or the browser already anchored.
 */
export type FoldScroll = { kind: "none" } | { kind: "hold"; by: number } | { kind: "retarget" };

export function foldScroll(input: {
  /** The anchor's viewport top before the fold's commit. */
  before: number;
  /** …and after it, before anything scrolled. */
  after: number;
  viewportHeight: number;
  /** The turn-start scroll is still in flight and the reader has not touched the page since. */
  settling: boolean;
}): FoldScroll {
  if (input.settling) return { kind: "retarget" };
  const shift = input.after - input.before;
  if (Math.abs(shift) < 1) return { kind: "none" };
  if (input.before >= input.viewportHeight) return { kind: "retarget" };
  return { kind: "hold", by: shift };
}

/**
 * How long after a turn starts its smooth scroll may still be running. Both engines finish a
 * programmatic smooth scroll well inside this; a scroll event inside {@link SCROLL_QUIET_MS} keeps
 * the window open while the page is still visibly moving.
 */
export const SETTLE_MS = 1000;
export const SCROLL_QUIET_MS = 120;

/** Whether the turn-start scroll counts as still in flight at `now` (all times `performance.now()`). */
export function isSettling(t: { now: number; turnStart: number | null; lastScroll: number; lastInput: number }): boolean {
  if (t.turnStart == null) return false;
  // The reader took over: from here on, where the page is is where THEY put it.
  if (t.lastInput >= t.turnStart) return false;
  return t.now - t.turnStart < SETTLE_MS || (t.lastScroll >= t.turnStart && t.now - t.lastScroll < SCROLL_QUIET_MS);
}
