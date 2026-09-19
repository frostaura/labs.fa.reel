/**
 * Layout arithmetic for the tonight's-picks hero, kept out of the component so the class strings
 * and the dot-indicator maths can be asserted without a DOM.
 *
 * The hero renders one row or two. Two rows is not two carousels: it is ONE scroll container laid
 * out as a column-flowed grid, so both rows share `scrollLeft` by construction. There is no JS
 * syncing two scrollers — that is the failure mode this shape exists to avoid, and the reason the
 * container class rather than an effect is where the decision lives.
 *
 * Column-major placement is intended: rank 1 is top-left, rank 2 sits directly under it, rank 3
 * opens the next column. DOM order is unchanged, so rank order, DOM order and Tab order stay the
 * same thing — which is what lets the existing hero specs keep asserting order the way they do.
 *
 * The single-row strings here are byte-identical to what the hero has always rendered, and
 * `heroLayout.test.ts` pins them against an independent copy. Two rows is an addition, not a
 * redesign; a phone and the free tier still see exactly the row they saw before.
 */

/** How many rows the hero is asked for. Two is a request, not a promise — see {@link heroRowsInEffect}. */
export type HeroRows = 1 | 2;

const SINGLE_ROW_CONTAINER = "flex gap-4 overflow-x-auto snap-x snap-mandatory scroll-smooth pb-1 -mx-1 px-1";

/**
 * `grid-rows-1` below `md` is the load-bearing half: phones have no room for two rows of 16:8.5
 * art, and the free tier's three cards cannot fill two rows or page for more. Card width comes from
 * `auto-cols`, which is why the cards drop their own `w-[…] shrink-0` in this mode.
 */
const TWO_ROW_CONTAINER =
  "grid grid-flow-col grid-rows-1 md:grid-rows-2 auto-cols-[min(92%,860px)] md:auto-cols-[min(48%,620px)] gap-4 overflow-x-auto snap-x snap-mandatory scroll-smooth pb-1 -mx-1 px-1";

const SINGLE_ROW_CARD =
  "relative w-[min(92%,860px)] shrink-0 snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] group focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60";

const TWO_ROW_CARD =
  "relative snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] group focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60";

const SINGLE_ROW_SHIMMER =
  "relative w-[min(92%,860px)] shrink-0 snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] reel-shimmer";

const TWO_ROW_SHIMMER =
  "relative snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] reel-shimmer";

/** The scroll container. One element in both modes — the rows are grid rows, not siblings. */
export function heroContainerClass(rows: HeroRows): string {
  return rows === 2 ? TWO_ROW_CONTAINER : SINGLE_ROW_CONTAINER;
}

/** One pick. Sized by the grid in two-row mode, by its own width class in one-row mode. */
export function heroCardClass(rows: HeroRows): string {
  return rows === 2 ? TWO_ROW_CARD : SINGLE_ROW_CARD;
}

/** The trailing "more coming" placeholder. Same card metrics, so it lands as one more grid cell. */
export function heroShimmerClass(rows: HeroRows): string {
  return rows === 2 ? TWO_ROW_SHIMMER : SINGLE_ROW_SHIMMER;
}

/** A half-width card gets less padding and a smaller headline; the caption scale is unchanged. */
export function heroTextBlockClass(rows: HeroRows): string {
  return rows === 2 ? "absolute inset-x-0 bottom-0 p-5 space-y-2" : "absolute inset-x-0 bottom-0 p-5 sm:p-7 space-y-2";
}

export function heroTitleClass(rows: HeroRows): string {
  return rows === 2
    ? "text-xl md:text-2xl font-light text-fa-frost-bright drop-shadow"
    : "text-xl sm:text-3xl font-light text-fa-frost-bright drop-shadow";
}

/**
 * The prose scale for the hook / why-line. Two rows drop the `sm:` bump: at ~620px the hero copy is
 * competing with the art for a much smaller card, and the hook is the line that has to stay
 * readable.
 */
export function heroProseScale(rows: HeroRows): string {
  return rows === 2 ? "fa-body" : "fa-body sm:text-base";
}

/**
 * What the viewport will actually give us. `rows` is what the tier asked for; below `md` the grid
 * collapses to one row whatever was asked, so every count derived from it has to collapse too.
 */
export function heroRowsInEffect(rows: HeroRows, isMdUp: boolean): HeroRows {
  return rows === 2 && isMdUp ? 2 : 1;
}

/**
 * Columns, not cards, are what the user scrolls past — so columns are what the dots count. At two
 * rows a five-card hero is three columns, and drawing five dots for three scroll positions is a
 * lie the eye catches immediately.
 */
export function heroColumns(cardCount: number, rowsInEffect: HeroRows): number {
  return Math.ceil(cardCount / rowsInEffect);
}

/** Dots are an at-a-glance count, not a scrollbar: pointless at one column, noise past eight. */
export function showHeroDots(columns: number): boolean {
  return columns > 1 && columns <= 8;
}

/**
 * Which dot is lit, from the scroll position. Derived from the fraction scrolled rather than from a
 * card index because the container scrolls continuously and the cards are snap targets, not pages.
 */
export function heroActiveColumn(
  scrollLeft: number,
  scrollWidth: number,
  clientWidth: number,
  columns: number
): number {
  const span = Math.max(1, scrollWidth - clientWidth);
  const fraction = Math.min(1, Math.max(0, scrollLeft / span));
  return clampColumn(Math.round(fraction * (columns - 1)), columns);
}

/** Keeps a remembered index legal after the viewport (and therefore the column count) changes. */
export function clampColumn(index: number, columns: number): number {
  return Math.min(Math.max(0, index), Math.max(0, columns - 1));
}

/**
 * How many backdrops load eagerly. One row shows one card, two rows show the first two columns —
 * four cards — and lazy-loading art that is already on screen is a visible flash of empty card.
 */
export function heroEagerCount(rowsInEffect: HeroRows): number {
  return rowsInEffect === 2 ? 4 : 1;
}
