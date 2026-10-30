/**
 * The rail scroller's shape, stated once so a one-row rail and a two-row rail can never drift apart
 * on anything but the rows (central primitive; no rail writes its own scroller class string).
 *
 * Two rows is not two scrollers: it is ONE scroll container laid out as a column-flowed grid, so
 * both rows share `scrollLeft` by construction and no script keeps two rails in step — the same
 * shape the tonight's-picks hero uses (`heroLayout.ts`). Column-major placement is intended: item 1
 * is top-left, item 2 directly under it, item 3 opens the next column, so DOM order, reading order
 * and Tab order stay one order. The columns are as wide as the cards (`auto-cols-max` over cards
 * that carry the shared `w-card` width), so a wide screen shows more columns rather than wider cards.
 */
export type RailRows = 1 | 2;

const SCROLLER = "gap-4 overflow-x-auto snap-x pb-2 [scrollbar-width:thin]";

export function railScrollerClass(rows: RailRows = 1): string {
  return rows === 2 ? `grid grid-flow-col grid-rows-2 auto-cols-max ${SCROLLER}` : `flex ${SCROLLER}`;
}

/**
 * A rail's inline text action ("Retry" after a line of copy): a link-weight button at caption size,
 * a 44 px target only on a touch-first pointer. The same look `ReadFailedLine` and the supply line
 * use, kept here so new rail copy takes it from one place.
 */
export const RAIL_INLINE_ACTION = "h-auto p-0 text-xs font-normal text-fa-frost underline underline-offset-2 coarse:min-h-11";
