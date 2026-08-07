import { describe, expect, it } from "vitest";
import {
  cycleGenre,
  DEFAULT_FILTERS,
  genreState,
  isInvertedYearRange,
  normalizeFilters,
  normalizeYearRange,
  parseYearInput,
  setGenreState,
  summaryChips,
  yearCeiling,
} from "./pickConditions";
import type { FiltersPayload } from "../store/api";

const base = (over: Partial<FiltersPayload> = {}): FiltersPayload => ({ ...DEFAULT_FILTERS, ...over });

/**
 * The three-state genre rule. Before this, genres were exclude-only: "give me horror" was not
 * expressible at all, and the two lists the backend has always accepted had no UI. The trap the
 * cycle has to avoid is landing a genre on BOTH lists — "include action, exclude action" is
 * unsatisfiable, so the feed empties with nothing on screen explaining why.
 */
describe("genre tri-state", () => {
  it("reads neutral for a genre on neither list", () => {
    expect(genreState(base(), "horror")).toBe("neutral");
  });

  it("reads include and exclude off the two lists", () => {
    expect(genreState(base({ includeGenres: ["horror"] }), "horror")).toBe("include");
    expect(genreState(base({ excludeGenres: ["horror"] }), "horror")).toBe("exclude");
  });

  it("resolves a contradictory payload as exclude — the safer reading, and the visible one", () => {
    expect(genreState(base({ includeGenres: ["horror"], excludeGenres: ["horror"] }), "horror")).toBe("exclude");
  });

  it("cycles neutral → include → exclude → neutral", () => {
    let f = base();
    f = cycleGenre(f, "horror");
    expect(genreState(f, "horror")).toBe("include");
    expect(f.includeGenres).toEqual(["horror"]);

    f = cycleGenre(f, "horror");
    expect(genreState(f, "horror")).toBe("exclude");

    f = cycleGenre(f, "horror");
    expect(genreState(f, "horror")).toBe("neutral");
    expect(f.includeGenres).toEqual([]);
    expect(f.excludeGenres).toEqual([]);
  });

  it("never leaves a genre on both lists as it cycles", () => {
    let f = base();
    for (let i = 0; i < 7; i += 1) {
      f = cycleGenre(f, "horror");
      const onBoth = f.includeGenres.filter((g) => f.excludeGenres.includes(g));
      expect(onBoth).toEqual([]);
    }
  });

  it("moves a genre between lists rather than adding a second entry", () => {
    const f = setGenreState(base({ excludeGenres: ["documentary"] }), "documentary", "include");
    expect(f.excludeGenres).toEqual([]);
    expect(f.includeGenres).toEqual(["documentary"]);
  });

  it("leaves every other genre untouched — one chip is one decision", () => {
    const f = cycleGenre(base({ excludeGenres: ["documentary"], includeGenres: ["crime"] }), "horror");
    expect(f.excludeGenres).toEqual(["documentary"]);
    expect(f.includeGenres).toEqual(["crime", "horror"]);
  });

  it("does not mutate the input", () => {
    const before = base({ excludeGenres: ["documentary"] });
    cycleGenre(before, "documentary");
    expect(before.excludeGenres).toEqual(["documentary"]);
  });
});

/**
 * Year-range validation. The inverted case is the one that matters: `min 2010, max 1990` is a
 * perfectly typeable pair that matches zero titles, so the feed goes blank and the cause is two
 * number boxes the user has already scrolled past.
 */
describe("normalizeYearRange", () => {
  const bounds = { floor: 1900, ceiling: 2028 };

  it("passes a sane range through", () => {
    expect(normalizeYearRange({ minYear: 1990, maxYear: 2005 }, bounds)).toEqual({ minYear: 1990, maxYear: 2005 });
  });

  it("swaps inverted bounds instead of returning a range that matches nothing", () => {
    expect(normalizeYearRange({ minYear: 2010, maxYear: 1990 }, bounds)).toEqual({ minYear: 1990, maxYear: 2010 });
  });

  it("keeps an equal pair as a single-year range", () => {
    expect(normalizeYearRange({ minYear: 1999, maxYear: 1999 }, bounds)).toEqual({ minYear: 1999, maxYear: 1999 });
  });

  it("treats an absent bound as open-ended, on either side", () => {
    expect(normalizeYearRange({ minYear: 1990, maxYear: null }, bounds)).toEqual({ minYear: 1990, maxYear: null });
    expect(normalizeYearRange({ minYear: null, maxYear: 2005 }, bounds)).toEqual({ minYear: null, maxYear: 2005 });
    expect(normalizeYearRange({}, bounds)).toEqual({ minYear: null, maxYear: null });
  });

  it("clamps out-of-range years into the offered span", () => {
    expect(normalizeYearRange({ minYear: 1200, maxYear: 9999 }, bounds)).toEqual({ minYear: 1900, maxYear: 2028 });
  });

  it("drops NaN — an empty or half-typed number box parses to it", () => {
    expect(normalizeYearRange({ minYear: Number.NaN, maxYear: 2005 }, bounds)).toEqual({ minYear: null, maxYear: 2005 });
    expect(normalizeYearRange({ minYear: 1990, maxYear: Number.POSITIVE_INFINITY }, bounds)).toEqual({ minYear: 1990, maxYear: null });
  });

  it("rounds a fractional year", () => {
    expect(normalizeYearRange({ minYear: 1990.6, maxYear: 2005.2 }, bounds)).toEqual({ minYear: 1991, maxYear: 2005 });
  });

  it("clamps before swapping, so a clamped pair cannot come back inverted", () => {
    expect(normalizeYearRange({ minYear: 9999, maxYear: 1200 }, bounds)).toEqual({ minYear: 1900, maxYear: 2028 });
  });

  it("defaults its ceiling ahead of today, so announced titles stay reachable", () => {
    expect(yearCeiling(new Date("2026-08-07T00:00:00Z"))).toBe(2028);
  });

  it("only accepts a full four-digit year, so typing 1990 does not clamp at '1'", () => {
    expect(parseYearInput("1")).toBeNull();
    expect(parseYearInput("19")).toBeNull();
    expect(parseYearInput("199")).toBeNull();
    expect(parseYearInput("1990")).toBe(1990);
    expect(parseYearInput(" 1990 ")).toBe(1990);
    expect(parseYearInput("")).toBeNull();
    expect(parseYearInput("19900")).toBeNull();
    expect(parseYearInput("nineteen")).toBeNull();
  });

  it("flags an inverted pair for the UI without correcting it", () => {
    expect(isInvertedYearRange({ minYear: 2010, maxYear: 1990 })).toBe(true);
    expect(isInvertedYearRange({ minYear: 1990, maxYear: 2010 })).toBe(false);
    expect(isInvertedYearRange({ minYear: 2010, maxYear: null })).toBe(false);
    expect(isInvertedYearRange({ minYear: Number.NaN, maxYear: 1990 })).toBe(false);
  });
});

/**
 * `normalizeFilters` exists because the crowd-rating and year fields are newer than some of the
 * servers that answer /settings/filters. An absent field must read as "off", not as `undefined`
 * landing in a slider's `value` and flipping it to an uncontrolled input mid-session.
 */
describe("normalizeFilters", () => {
  it("fills the new fields when the backend has not shipped them yet", () => {
    const f = normalizeFilters({ excludeGenres: ["documentary"], languages: ["en"] });
    expect(f.minCrowdRating).toBeNull();
    expect(f.minYear).toBeNull();
    expect(f.maxYear).toBeNull();
    expect(f.includeGenres).toEqual([]);
    expect(f.excludeKeywords).toEqual([]);
  });

  it("falls back to English rather than an empty allow-list, which would read as no titles", () => {
    expect(normalizeFilters({ languages: [] }).languages).toEqual(["en"]);
    expect(normalizeFilters(null).languages).toEqual(["en"]);
  });

  it("sanitises an inverted stored range on the way in", () => {
    const f = normalizeFilters({ minYear: 2010, maxYear: 1990 });
    expect(f.minYear).toBeLessThanOrEqual(f.maxYear!);
  });
});

/**
 * The summary chips. Their whole job is answering "why does my feed look like this" at a glance,
 * so a chip that describes a non-condition ("all languages", a floor of zero) is worse than no chip
 * — it teaches the eye to skip the row.
 */
describe("summaryChips", () => {
  it("says nothing when nothing is narrowing the feed", () => {
    expect(summaryChips(base({ languages: ["*"] }))).toEqual([]);
  });

  it("distinguishes include from exclude in the words, not only the colour", () => {
    const chips = summaryChips(base({ includeGenres: ["horror"], excludeGenres: ["documentary"], languages: ["*"] }));
    expect(chips.map((c) => [c.label, c.tone])).toEqual([
      ["has horror", "include"],
      ["no documentary", "exclude"],
    ]);
  });

  it("spells a slug as words", () => {
    expect(summaryChips(base({ includeGenres: ["science-fiction"], languages: ["*"] }))[0].label).toBe("has science fiction");
  });

  it("labels the two rating floors differently — they are not the same number", () => {
    const chips = summaryChips(base({ minCrowdRating: 7, minPredictedRating: 8.5, languages: ["*"] }));
    expect(chips.map((c) => c.label)).toEqual(["crowd ≥ 7.0", "for you ≥ 8.5"]);
  });

  it("omits a rating floor of zero — that is 'off', not a condition", () => {
    expect(summaryChips(base({ minCrowdRating: 0, minPredictedRating: 0, languages: ["*"] }))).toEqual([]);
  });

  it("describes each shape of year range", () => {
    const label = (over: Partial<FiltersPayload>) => summaryChips(base({ languages: ["*"], ...over }))[0]?.label;
    expect(label({ minYear: 1990, maxYear: 2005 })).toBe("1990–2005");
    expect(label({ minYear: 1990 })).toBe("1990 onwards");
    expect(label({ maxYear: 2005 })).toBe("up to 2005");
    expect(label({})).toBeUndefined();
  });

  it("shows the language allow-list but never 'all languages'", () => {
    expect(summaryChips(base({ languages: ["en"] })).map((c) => c.label)).toEqual(["English only"]);
    expect(summaryChips(base({ languages: ["en", "ja"] })).map((c) => c.label)).toEqual(["English, Japanese"]);
    expect(summaryChips(base({ languages: ["*"] }))).toEqual([]);
  });

  it("names an unknown language code rather than dropping it", () => {
    expect(summaryChips(base({ languages: ["sv"] })).map((c) => c.label)).toEqual(["SV only"]);
  });

  it("covers maturity and keywords too", () => {
    const chips = summaryChips(base({ maturityCeiling: "PG-13", excludeKeywords: ["gore"], languages: ["*"] }));
    expect(chips.map((c) => c.label)).toEqual(["up to PG-13", "no “gore”"]);
  });

  it("keys every chip uniquely, so React can render the row", () => {
    const chips = summaryChips(
      base({
        includeGenres: ["horror", "crime"],
        excludeGenres: ["documentary"],
        excludeKeywords: ["gore", "clown"],
        minCrowdRating: 7,
        minPredictedRating: 8,
        minYear: 1990,
        maturityCeiling: "R",
      }),
    );
    expect(new Set(chips.map((c) => c.key)).size).toBe(chips.length);
  });

  it("survives a payload from a server that has not shipped the new fields", () => {
    expect(() => summaryChips({ excludeGenres: ["documentary"] })).not.toThrow();
    expect(summaryChips({ excludeGenres: ["documentary"] }).map((c) => c.label)).toEqual([
      "no documentary",
      "English only",
    ]);
  });
});
