import { describe, expect, it } from "vitest";
import {
  LG_BREAKPOINT_PX,
  clampColumn,
  heroActiveColumn,
  heroArtClass,
  heroCardClass,
  heroColumns,
  heroContainerClass,
  heroEagerCount,
  heroProseScale,
  heroRowsInEffect,
  heroScrimClass,
  heroShimmerClass,
  heroTextBlockClass,
  heroTitleClass,
  showHeroDots,
} from "./heroLayout";

/** Every whitespace-separated class token, so assertions test tokens rather than substrings. */
function tokens(className: string): string[] {
  return className.split(/\s+/).filter(Boolean);
}

describe("the card stacks below lg and overlays from lg (F-4, O-1)", () => {
  for (const rows of [1, 2] as const) {
    it(`rows=${rows}: a column with no fixed aspect below lg, so its text can never be cut off the top`, () => {
      const card = tokens(heroCardClass(rows));
      expect(card).toContain("flex");
      expect(card).toContain("flex-col");
      expect(card).toContain("lg:block");
      // The defect: a fixed aspect at every width with the copy pinned to the bottom. Any
      // un-prefixed aspect class brings it back.
      expect(card.filter((t) => t.startsWith("aspect-"))).toEqual([]);
      expect(card.some((t) => t.startsWith("lg:aspect-"))).toBe(true);
    });

    it(`rows=${rows}: the text block is in flow below lg and pinned over the art only from lg`, () => {
      const text = tokens(heroTextBlockClass(rows));
      expect(text).not.toContain("absolute");
      expect(text).not.toContain("bottom-0");
      expect(text).toContain("lg:absolute");
      expect(text).toContain("lg:bottom-0");
    });

    it(`rows=${rows}: the overlay panel is at least 0.85 opaque at its lightest point (O-2)`, () => {
      const text = tokens(heroTextBlockClass(rows));
      const alphas = text
        .map((t) => /^lg:(?:from|via|to|bg)-fa-ink\/(\d+)$/.exec(t))
        .filter((m): m is RegExpExecArray => m !== null)
        .map((m) => Number(m[1]));
      expect(alphas.length).toBeGreaterThan(0);
      expect(Math.min(...alphas)).toBeGreaterThanOrEqual(85);
    });

    it(`rows=${rows}: the title and hook are clamped to two lines`, () => {
      expect(tokens(heroTitleClass(rows))).toContain("line-clamp-2");
      expect(tokens(heroProseScale(rows))).toContain("line-clamp-2");
    });

    it(`rows=${rows}: the shimmer holds a stacked card's space below lg and the overlay aspect from lg`, () => {
      const shimmer = tokens(heroShimmerClass(rows));
      expect(shimmer).toContain("reel-shimmer");
      expect(shimmer.filter((t) => t.startsWith("aspect-"))).toEqual([]);
      expect(shimmer.some((t) => t.startsWith("min-h-[calc("))).toBe(true);
      expect(shimmer).toContain("lg:min-h-0");
      // Same overlay aspect as the card it stands in for, so the card lands in the held space.
      const cardAspects = tokens(heroCardClass(rows)).filter((t) => /^(lg|xl):aspect-/.test(t));
      for (const aspect of cardAspects) expect(shimmer).toContain(aspect);
    });
  }

  it("the art is a 16:9 block in the column below lg and fills the card from lg", () => {
    const art = tokens(heroArtClass());
    expect(art).toContain("aspect-video");
    expect(art).toContain("lg:absolute");
    expect(art).toContain("lg:inset-0");
    expect(art).toContain("lg:aspect-auto");
  });

  it("the gradient scrim only exists where text overlays art", () => {
    const scrim = tokens(heroScrimClass());
    expect(scrim).toContain("hidden");
    expect(scrim).toContain("lg:block");
  });

  it("a half-width two-row card gets a taller overlay aspect until xl", () => {
    const card = tokens(heroCardClass(2));
    expect(card).toContain("lg:aspect-[16/10]");
    expect(card).toContain("xl:aspect-[16/8.5]");
  });

  it("the shape changes at Tailwind's lg breakpoint", () => {
    expect(LG_BREAKPOINT_PX).toBe(1024);
  });
});

describe("single-row container", () => {
  it("is a flex scroller, one card per screen on a phone and two on a tablet", () => {
    expect(tokens(heroContainerClass(1))[0]).toBe("flex");
    const card = tokens(heroCardClass(1));
    expect(card).toContain("w-[min(92%,860px)]");
    expect(card).toContain("md:w-[min(48%,620px)]");
    expect(card).toContain("lg:w-[min(92%,860px)]");
    expect(card).toContain("shrink-0");
  });
});

describe("two-row container", () => {
  it("is one column-flowed grid, so both rows share a single scrollLeft", () => {
    const container = tokens(heroContainerClass(2));
    expect(container).toContain("grid");
    expect(container).toContain("grid-flow-col");
    // One row below lg, two from lg — the phone/tablet collapse lives in CSS, not in an effect.
    expect(container).toContain("grid-rows-1");
    expect(container).toContain("lg:grid-rows-2");
    expect(container).not.toContain("md:grid-rows-2");
    expect(container).toContain("auto-cols-[min(92%,860px)]");
    expect(container).toContain("md:auto-cols-[min(48%,620px)]");
  });

  it("keeps the scroll and snap behaviour of the single row", () => {
    for (const token of ["overflow-x-auto", "snap-x", "snap-mandatory", "scroll-smooth", "gap-4"]) {
      expect(tokens(heroContainerClass(2))).toContain(token);
      expect(tokens(heroContainerClass(1))).toContain(token);
    }
  });

  it("hands card width to the grid rather than the card", () => {
    const card = tokens(heroCardClass(2));
    expect(card.some((t) => t.includes("w-[min("))).toBe(false);
    expect(card).not.toContain("shrink-0");
    // Everything a card needs to still BE a hero card survives.
    expect(card).toContain("snap-center");
    expect(card).toContain("has-[a:focus-visible]:ring-fa-frost/60");
  });

  it("gives the trailing shimmer no width of its own so it lands as one more grid cell", () => {
    expect(tokens(heroShimmerClass(2))).not.toContain("shrink-0");
  });

  it("shrinks the copy to fit a half-width card", () => {
    expect(tokens(heroTextBlockClass(2))).not.toContain("lg:p-7");
    expect(tokens(heroTextBlockClass(1))).toContain("lg:p-7");
    expect(tokens(heroProseScale(2))).not.toContain("sm:text-base");
    expect(tokens(heroProseScale(1))).toContain("sm:text-base");
  });
});

describe("rows in effect", () => {
  it("collapses a two-row request to one row below lg", () => {
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
  it("loads the cards visible at rest: up to two in one row (a tablet shows two), four in two rows", () => {
    expect(heroEagerCount(1)).toBe(2);
    expect(heroEagerCount(2)).toBe(4);
  });
});
