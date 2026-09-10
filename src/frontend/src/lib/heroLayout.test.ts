import { describe, expect, it } from "vitest";
import {
  clampColumn,
  heroActiveColumn,
  heroCardClass,
  heroColumns,
  heroContainerClass,
  heroEagerCount,
  heroProseScale,
  heroRowsInEffect,
  heroShimmerClass,
  heroTextBlockClass,
  heroTitleClass,
  showHeroDots,
} from "./heroLayout";

/**
 * The single-row strings as the hero rendered them before two-row mode existed, copied out of the
 * component at the commit that introduced `rows`. They are duplicated here on purpose: two rows is
 * an addition, and the whole safety argument for shipping it is that a phone and the free tier get
 * back exactly the row they had. A diff in any of these is that argument breaking, and it is the
 * kind of break that no screenshot catches and no e2e spec asserts.
 */
const LEGACY = {
  container: "flex gap-4 overflow-x-auto snap-x snap-mandatory scroll-smooth pb-1 -mx-1 px-1",
  card:
    "relative w-[min(92%,860px)] shrink-0 snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] group focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60",
  shimmer:
    "relative w-[min(92%,860px)] shrink-0 snap-center overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 aspect-[16/8.5] reel-shimmer",
  textBlock: "absolute inset-x-0 bottom-0 p-5 sm:p-7 space-y-2",
  title: "text-xl sm:text-3xl font-light text-fa-frost-bright drop-shadow",
  prose: "fa-body sm:text-base",
};

describe("single row is byte-identical to what the hero always rendered", () => {
  it("keeps the container class exactly", () => {
    expect(heroContainerClass(1)).toBe(LEGACY.container);
  });

  it("keeps the card, shimmer and text classes exactly", () => {
    expect(heroCardClass(1)).toBe(LEGACY.card);
    expect(heroShimmerClass(1)).toBe(LEGACY.shimmer);
    expect(heroTextBlockClass(1)).toBe(LEGACY.textBlock);
    expect(heroTitleClass(1)).toBe(LEGACY.title);
    expect(heroProseScale(1)).toBe(LEGACY.prose);
  });
});

describe("two-row container", () => {
  it("is one column-flowed grid, so both rows share a single scrollLeft", () => {
    const container = heroContainerClass(2);
    expect(container).toContain("grid");
    expect(container).toContain("grid-flow-col");
    // One row on phones, two from `md` — the free/phone collapse lives in CSS, not in an effect.
    expect(container).toContain("grid-rows-1");
    expect(container).toContain("md:grid-rows-2");
    expect(container).toContain("auto-cols-[min(92%,860px)]");
    expect(container).toContain("md:auto-cols-[min(48%,620px)]");
  });

  it("keeps the scroll and snap behaviour of the single row", () => {
    for (const token of ["overflow-x-auto", "snap-x", "snap-mandatory", "scroll-smooth", "gap-4"]) {
      expect(heroContainerClass(2)).toContain(token);
    }
  });

  it("hands card width to the grid rather than the card", () => {
    expect(heroCardClass(2)).not.toContain("w-[min(92%,860px)]");
    expect(heroCardClass(2)).not.toContain("shrink-0");
    // Everything a card needs to still BE a hero card survives.
    expect(heroCardClass(2)).toContain("snap-center");
    expect(heroCardClass(2)).toContain("aspect-[16/8.5]");
    expect(heroCardClass(2)).toContain("focus-visible:ring-fa-frost/60");
  });

  it("gives the trailing shimmer the same metrics so it lands as one more grid cell", () => {
    expect(heroShimmerClass(2)).not.toContain("shrink-0");
    expect(heroShimmerClass(2)).toContain("aspect-[16/8.5]");
    expect(heroShimmerClass(2)).toContain("reel-shimmer");
  });

  it("shrinks the copy to fit a half-width card", () => {
    expect(heroTextBlockClass(2)).toBe("absolute inset-x-0 bottom-0 p-5 space-y-2");
    expect(heroTitleClass(2)).toBe("text-xl md:text-2xl font-light text-fa-frost-bright drop-shadow");
    expect(heroProseScale(2)).toBe("fa-body");
  });
});

describe("rows in effect", () => {
  it("collapses a two-row request to one row below md", () => {
    expect(heroRowsInEffect(2, false)).toBe(1);
    expect(heroRowsInEffect(2, true)).toBe(2);
  });

  it("never promotes a one-row request", () => {
    expect(heroRowsInEffect(1, true)).toBe(1);
    expect(heroRowsInEffect(1, false)).toBe(1);
  });
});

describe("columns", () => {
  it("counts one column per card in a single row", () => {
    expect(heroColumns(5, 1)).toBe(5);
    expect(heroColumns(3, 1)).toBe(3);
  });

  it("counts half as many, rounded up, in two rows", () => {
    // The odd case is the live one: HeroSize is 5, so the last column carries a single card.
    expect(heroColumns(5, 2)).toBe(3);
    expect(heroColumns(4, 2)).toBe(2);
    expect(heroColumns(1, 2)).toBe(1);
    expect(heroColumns(17, 2)).toBe(9);
  });

  it("is zero for an empty hero", () => {
    expect(heroColumns(0, 1)).toBe(0);
    expect(heroColumns(0, 2)).toBe(0);
  });
});

describe("dot indicator", () => {
  it("draws nothing at one column and nothing past eight", () => {
    expect(showHeroDots(0)).toBe(false);
    expect(showHeroDots(1)).toBe(false);
    expect(showHeroDots(2)).toBe(true);
    expect(showHeroDots(8)).toBe(true);
    expect(showHeroDots(9)).toBe(false);
  });

  it("draws a dot per COLUMN, not per card — five two-row picks are three dots", () => {
    const columns = heroColumns(5, 2);
    expect(columns).toBe(3);
    expect(showHeroDots(columns)).toBe(true);
  });

  it("lights the first dot at rest and the last at the end of the scroll", () => {
    expect(heroActiveColumn(0, 3000, 1000, 3)).toBe(0);
    expect(heroActiveColumn(2000, 3000, 1000, 3)).toBe(2);
    expect(heroActiveColumn(1000, 3000, 1000, 3)).toBe(1);
  });

  it("never divides by a zero scroll span", () => {
    // A hero narrower than its container: scrollWidth === clientWidth, nothing to scroll.
    expect(heroActiveColumn(0, 1000, 1000, 1)).toBe(0);
    expect(Number.isNaN(heroActiveColumn(0, 1000, 1000, 3))).toBe(false);
  });

  it("stays in range under an over-scroll and a single column", () => {
    // Elastic over-scroll on macOS reports a scrollLeft past the span.
    expect(heroActiveColumn(9999, 3000, 1000, 3)).toBe(2);
    expect(heroActiveColumn(-50, 3000, 1000, 3)).toBe(0);
    expect(heroActiveColumn(500, 3000, 1000, 1)).toBe(0);
  });

  it("clamps a remembered index when the viewport changes the column count", () => {
    // Scrolled to column 4 at one row, then a resize makes it a two-row grid of 3 columns.
    expect(clampColumn(4, 3)).toBe(2);
    expect(clampColumn(0, 0)).toBe(0);
    expect(clampColumn(-1, 3)).toBe(0);
  });
});

describe("eager image loading", () => {
  it("loads the first visible column in one row and the first two in two", () => {
    expect(heroEagerCount(1)).toBe(1);
    expect(heroEagerCount(2)).toBe(4);
  });
});
