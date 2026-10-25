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
 * One row and two rows share every card class except width and overlay aspect, and
 * `heroLayout.test.ts` pins the shape both modes depend on: stacked below `lg`, overlaid on a dense
 * panel from `lg`, one row below `lg` whatever the tier asked for.
 */

/** How many rows the hero is asked for. Two is a request, not a promise — see {@link heroRowsInEffect}. */
export type HeroRows = 1 | 2;

/**
 * Tailwind's `lg` breakpoint, in pixels. The hero changes SHAPE here, not just size: below it a
 * card stacks (art above, text below on `ink-2`); from it the text overlays the art on a dense
 * panel. Two rows also start here. It is written down once so the CSS (`lg:` classes below) and the
 * arithmetic (`heroRowsInEffect`) cannot drift apart.
 */
export const LG_BREAKPOINT_PX = 1024;

/*
 * WHY THE CARD STACKS (F-4, O-1, I-10). The card used to be a fixed `aspect-[16/8.5]` box with
 * `overflow-hidden` and every line of copy pinned to its bottom edge, growing upward. At 390 px
 * that box is 175 px tall and the copy is ~240 px, so the title and its badge were cut off the TOP
 * of every phone and tablet card, and the quick actions sat on the text. No scrim value fixes a
 * box that is too small for its content, so below `lg` the card is a column: 16:9 art on top, then
 * the text on solid `ink-2` with as much height as it needs, then the actions in a row of their
 * own. Only from `lg`, where the art is wide enough to carry it, does the text go back over the
 * art — and then on a panel at least 0.85 opaque (O-2: titles were measured at 1.29:1 on bright
 * art under the old 0.4-alpha gradient).
 */

const SCROLLER = "gap-4 overflow-x-auto snap-x snap-mandatory scroll-smooth pb-1 -mx-1 px-1";

const SINGLE_ROW_CONTAINER = `flex ${SCROLLER}`;

/**
 * `grid-rows-1` below `lg` is the load-bearing half: a stacked card is taller than it is wide on a
 * phone, and two rows of them would push everything else off the first screen. Card width comes
 * from `auto-cols`, which is why the cards carry no width class of their own in this mode. From
 * `md` two cards share the width, so a tablet uses its space instead of one card at 92%.
 */
const TWO_ROW_CONTAINER = `grid grid-flow-col grid-rows-1 lg:grid-rows-2 auto-cols-[min(92%,860px)] md:auto-cols-[min(48%,620px)] ${SCROLLER}`;

/** Single-row card width: one card on a phone, two on a tablet, the wide headline card from `lg`. */
const SINGLE_ROW_WIDTH = "w-[min(92%,860px)] md:w-[min(48%,620px)] lg:w-[min(92%,860px)] shrink-0";

const CARD_FRAME =
  "relative flex flex-col snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 lg:block";

/**
 * The overlay aspect. A half-width card at 1024–1279 px is ~470 px wide, and at 16:8.5 that is
 * 250 px for a title, badges, meta, a two-line hook and the reaching note — the same squeeze the
 * phone had. It gets 16:10 until `xl`, where the cards are wide enough for the original shape.
 */
const OVERLAY_ASPECT_SINGLE = "lg:aspect-[16/8.5]";
const OVERLAY_ASPECT_TWO = "lg:aspect-[16/10] xl:aspect-[16/8.5]";

const CARD_FOCUS = "group focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60";

const SINGLE_ROW_CARD = `${CARD_FRAME} ${SINGLE_ROW_WIDTH} ${OVERLAY_ASPECT_SINGLE} ${CARD_FOCUS}`;
const TWO_ROW_CARD = `${CARD_FRAME} ${OVERLAY_ASPECT_TWO} ${CARD_FOCUS}`;

/**
 * The placeholders hold the space a card will take. Below `lg` that is the art (16:9) plus the
 * text block underneath it — about 11rem at the caption scale — so the shimmer is the art's aspect
 * with the text's height padded on, rather than a box that is a head too short and shoves the page
 * when the real card lands.
 */
const SHIMMER_FRAME = "relative snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 reel-shimmer";
const SINGLE_ROW_SHIMMER = `${SHIMMER_FRAME} ${SINGLE_ROW_WIDTH} min-h-[calc((min(92vw,860px)*9/16)+11rem)] md:min-h-[calc((min(46vw,620px)*9/16)+11rem)] lg:min-h-0 ${OVERLAY_ASPECT_SINGLE}`;
const TWO_ROW_SHIMMER = `${SHIMMER_FRAME} min-h-[calc((min(92vw,860px)*9/16)+11rem)] md:min-h-[calc((min(46vw,620px)*9/16)+11rem)] lg:min-h-0 ${OVERLAY_ASPECT_TWO}`;

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

/**
 * The artwork frame: a 16:9 block in the card's column below `lg`, the whole card behind the text
 * from `lg`. The `<img>` inside fills it either way.
 */
export function heroArtClass(): string {
  return "relative aspect-video w-full shrink-0 overflow-hidden bg-fa-ink-3 lg:absolute lg:inset-0 lg:aspect-auto lg:h-full";
}

/**
 * The gradient that eases the art into the text panel. Only where text overlays art: a stacked
 * card's art carries no text, and darkening it would only hide the picture.
 */
export function heroScrimClass(): string {
  return "absolute inset-0 hidden lg:block reel-scrim";
}

/**
 * The text block. Below `lg` it is in flow on the card's own `ink-2` and takes the height it needs.
 * From `lg` it is pinned to the bottom of the art on a panel whose LIGHTEST point is 0.85 opaque
 * ink, so no artwork can bring the title under 4.5:1 (frost-bright over 0.85 ink on pure white is
 * still ~9:1). A half-width card gets less padding.
 */
export function heroTextBlockClass(rows: HeroRows): string {
  const overlay = "lg:absolute lg:inset-x-0 lg:bottom-0 lg:bg-gradient-to-t lg:from-fa-ink/95 lg:to-fa-ink/85";
  return rows === 2
    ? `relative flex-1 p-4 sm:p-5 space-y-2 ${overlay}`
    : `relative flex-1 p-4 sm:p-5 space-y-2 ${overlay} lg:p-7`;
}

/**
 * The headline. Two lines at most: a long title wraps once and then ellipsises, it never grows the
 * card past its art. A half-width overlaid card (two rows, from `lg`) holds it to one line — its
 * panel shares a fixed-aspect card with the action row in the top corner.
 */
export function heroTitleClass(rows: HeroRows): string {
  return rows === 2
    ? "text-xl xl:text-2xl font-light text-fa-frost-bright line-clamp-2 lg:line-clamp-1"
    : "text-xl sm:text-2xl lg:text-3xl font-light text-fa-frost-bright line-clamp-2";
}

/**
 * The prose scale for the hook / why-line, clamped to two lines (the task's hook limit; the lens
 * clause lives in the detail view, not on the card). Two rows drop the size bump: at ~620px the
 * copy competes with the art for a much smaller card.
 */
export function heroProseScale(rows: HeroRows): string {
  return rows === 2 ? "fa-body line-clamp-2" : "fa-body sm:text-base line-clamp-2";
}

/**
 * What the viewport will actually give us. `rows` is what the tier asked for; below `lg` the grid
 * collapses to one row whatever was asked, so every count derived from it has to collapse too.
 */
export function heroRowsInEffect(rows: HeroRows, isLgUp: boolean): HeroRows {
  return rows === 2 && isLgUp ? 2 : 1;
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
 * How many backdrops load eagerly. One row shows one card on a phone and two on a tablet; two rows
 * show the first two columns — four cards — and lazy-loading art that is already on screen is a
 * visible flash of empty card.
 */
export function heroEagerCount(rowsInEffect: HeroRows): number {
  return rowsInEffect === 2 ? 4 : 2;
}
