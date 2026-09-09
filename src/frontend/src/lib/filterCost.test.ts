import { describe, expect, it } from "vitest";
import {
  applySuggestion,
  costLine,
  formatCount,
  formatMarginal,
  languageRuleValue,
  marginalFor,
  marginalNote,
  ratingRuleValue,
  suggestionLabel,
} from "./filterCost";
import { DEFAULT_FILTERS } from "./pickConditions";
import type { FilterCostReport } from "../store/api";

const report: FilterCostReport = {
  total: 5319,
  eligible: 421,
  byRule: [
    { kind: "ExcludeGenre", value: "comedy", marginal: 180 },
    { kind: "ExcludeGenre", value: "documentary", marginal: 0 },
    { kind: "Language", value: "en", marginal: 640 },
    { kind: "MinCrowdRating", value: "6.0", marginal: 97 },
    { kind: "MinYear", value: "2000", marginal: 1 },
  ],
  suggestions: [
    { kind: "Language", value: "en", gain: 640 },
    { kind: "ExcludeGenre", value: "comedy", gain: 180 },
    { kind: "MinCrowdRating", value: "6.0", gain: 97 },
  ],
};

describe("costLine", () => {
  it("names both numbers in the founder's own framing", () => {
    expect(costLine(report)).toBe("These rules leave 421 of 5,319 titles");
  });

  it("renders nothing at all while the meter is in flight, rather than a zero", () => {
    // A "0 of 0 titles" flash would read as an empty catalogue, which is a lie about the app.
    expect(costLine(undefined)).toBeNull();
  });

  it("still reads as a sentence when the rules leave nothing", () => {
    expect(costLine({ ...report, eligible: 0 })).toBe("These rules leave 0 of 5,319 titles");
  });
});

describe("formatCount", () => {
  it("groups thousands so 5319 is legible at a glance", () => {
    expect(formatCount(5319)).toBe("5,319");
    expect(formatCount(421)).toBe("421");
    expect(formatCount(1000000)).toBe("1,000,000");
  });

  it("never renders a negative — relaxing a rule cannot cost titles", () => {
    expect(formatCount(-12)).toBe("0");
  });
});

describe("formatMarginal", () => {
  it("badges a real gain with its sign", () => {
    expect(formatMarginal(180)).toBe("+180");
    expect(formatMarginal(1200)).toBe("+1,200");
  });

  it("is absent for a rule that is costing nothing, so no chip carries a +0", () => {
    expect(formatMarginal(0)).toBeNull();
    expect(formatMarginal(null)).toBeNull();
    expect(formatMarginal(undefined)).toBeNull();
  });
});

describe("marginalNote", () => {
  it("says what the number buys and what it would cost to get it", () => {
    expect(marginalNote(640, "allow every language")).toBe("640 more titles if you allow every language");
  });

  it("keeps the singular honest", () => {
    expect(marginalNote(1, "allow titles before 2000")).toBe("1 more title if you allow titles before 2000");
  });

  it("says nothing when relaxing the rule would return nothing", () => {
    expect(marginalNote(0, "allow every language")).toBeNull();
    expect(marginalNote(null, "allow every language")).toBeNull();
  });
});

describe("marginalFor", () => {
  it("finds a rule by kind AND value — the meter reports per chip, not per kind", () => {
    expect(marginalFor(report, "ExcludeGenre", "comedy")).toBe(180);
    expect(marginalFor(report, "ExcludeGenre", "documentary")).toBe(0);
  });

  it("distinguishes 'costing nothing' from 'not measured yet'", () => {
    // 0 means the server counted and the answer was zero; null means there is no number to show.
    expect(marginalFor(report, "ExcludeGenre", "documentary")).toBe(0);
    expect(marginalFor(report, "ExcludeGenre", "horror")).toBeNull();
    expect(marginalFor(undefined, "ExcludeGenre", "comedy")).toBeNull();
  });
});

describe("rule value keys", () => {
  it("reports the language rule under its effective allow-list, joined", () => {
    expect(languageRuleValue(["en", "fr"])).toBe("en,fr");
  });

  it("reads an empty selection as the English-only default, exactly as the server does", () => {
    expect(languageRuleValue([])).toBe("en");
  });

  it("formats a rating floor to one decimal, matching ContentFilterRules", () => {
    expect(ratingRuleValue(6)).toBe("6.0");
    expect(ratingRuleValue(7.5)).toBe("7.5");
  });
});

describe("suggestionLabel", () => {
  it("says what the tap does, in the second person, without telling anyone what to want", () => {
    expect(suggestionLabel({ kind: "ExcludeGenre", value: "science-fiction", gain: 1 })).toBe("Allow science fiction");
    expect(suggestionLabel({ kind: "IncludeGenre", value: "horror", gain: 1 })).toBe("Stop requiring horror");
    expect(suggestionLabel({ kind: "Language", value: "en", gain: 1 })).toBe("Allow every language");
    expect(suggestionLabel({ kind: "MinCrowdRating", value: "6.0", gain: 1 })).toBe("Drop the 6.0 crowd floor");
    expect(suggestionLabel({ kind: "MinYear", value: "2000", gain: 1 })).toBe("Allow titles before 2000");
    expect(suggestionLabel({ kind: "MaxYear", value: "2020", gain: 1 })).toBe("Allow titles after 2020");
  });

  it("falls back to something honest for a kind this bundle has not shipped yet", () => {
    expect(suggestionLabel({ kind: "SomethingNew", value: "x", gain: 1 })).toBe("Relax this rule");
  });
});

describe("applySuggestion", () => {
  const draft = {
    ...DEFAULT_FILTERS,
    excludeGenres: ["comedy", "documentary"],
    includeGenres: ["horror"],
    excludeKeywords: ["zombie"],
    languages: ["en"],
    minCrowdRating: 6,
    minYear: 2000,
    maxYear: 2020,
  };

  it("drops exactly the one genre the chip named", () => {
    expect(applySuggestion(draft, { kind: "ExcludeGenre", value: "comedy", gain: 1 }).excludeGenres).toEqual([
      "documentary",
    ]);
  });

  it("relaxes the language rule to every language, not to an empty list", () => {
    // An empty list means English-only again on both sides, so clearing it would deliver none of
    // the titles the number beside the chip promised.
    expect(applySuggestion(draft, { kind: "Language", value: "en", gain: 1 }).languages).toEqual(["*"]);
  });

  it("turns off the floors and bounds it is offered for", () => {
    expect(applySuggestion(draft, { kind: "MinCrowdRating", value: "6.0", gain: 1 }).minCrowdRating).toBeNull();
    expect(applySuggestion(draft, { kind: "MinYear", value: "2000", gain: 1 }).minYear).toBeNull();
    expect(applySuggestion(draft, { kind: "MaxYear", value: "2020", gain: 1 }).maxYear).toBeNull();
    expect(applySuggestion(draft, { kind: "IncludeGenre", value: "horror", gain: 1 }).includeGenres).toEqual([]);
    expect(applySuggestion(draft, { kind: "ExcludeKeyword", value: "zombie", gain: 1 }).excludeKeywords).toEqual([]);
  });

  it("changes nothing else, and never mutates the draft it was handed", () => {
    const next = applySuggestion(draft, { kind: "ExcludeGenre", value: "comedy", gain: 1 });
    expect(draft.excludeGenres).toEqual(["comedy", "documentary"]);
    expect(next.minYear).toBe(2000);
    expect(next.includeGenres).toEqual(["horror"]);
    expect(next.languages).toEqual(["en"]);
  });

  it("leaves an unknown kind alone rather than guessing at a relaxation", () => {
    expect(applySuggestion(draft, { kind: "SomethingNew", value: "x", gain: 1 })).toEqual(draft);
  });
});
