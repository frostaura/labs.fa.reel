import { describe, expect, it } from "vitest";
import {
  enterTarget,
  hasFootprint,
  hiddenLine,
  peopleFirst,
  scoredTitleIds,
  shouldQueryGlobal,
  strongMatchCount,
} from "./globalTypeahead";
import type { TypeaheadPayload, TypeaheadPerson, TypeaheadTitle } from "../store/api/typeahead";

const title = (name: string, tier?: TypeaheadTitle["tier"]): TypeaheadTitle => ({
  titleId: name, mediaType: "Movie", tmdbId: 1, name, year: 2010, posterPath: null,
  isFullyWatched: false, predictedRating: null, tier,
});
const person = (name: string, tier?: TypeaheadPerson["tier"]): TypeaheadPerson => ({
  personId: name, name, knownFor: "Directing", profilePath: null, tier,
});
const payload = (titles: TypeaheadTitle[], people: TypeaheadPerson[] = [], strongMatches?: number): TypeaheadPayload =>
  ({ titles, people, strongMatches });

describe("shouldQueryGlobal", () => {
  it("fires when the STRONG matches are sparse and the term is substantial", () => {
    expect(shouldQueryGlobal("jeanne dielman", 0)).toBe(true);
    expect(shouldQueryGlobal("jeanne dielman", 2)).toBe(true);
  });

  it("stays quiet when the strong matches already answer", () => {
    expect(shouldQueryGlobal("jeanne dielman", 3)).toBe(false);
    expect(shouldQueryGlobal("jeanne dielman", 8)).toBe(false);
  });

  it("requires at least 3 meaningful characters, trimmed", () => {
    expect(shouldQueryGlobal("je", 0)).toBe(false);
    expect(shouldQueryGlobal("  je  ", 0)).toBe(false);
    expect(shouldQueryGlobal("  spy  ", 0)).toBe(true);
  });
});

describe("strongMatchCount", () => {
  it("trusts the server's count, so fuzzy rows never hold the global leg back", () => {
    expect(strongMatchCount(payload([title("Cheaters", "fuzzy"), title("Cheat", "fuzzy"), title("Theater", "fuzzy")], [], 0))).toBe(0);
  });

  it("falls back to every row for a server that sends no count", () => {
    expect(strongMatchCount(payload([title("A"), title("B")]))).toBe(2);
    expect(strongMatchCount(undefined)).toBe(0);
  });
});

describe("peopleFirst", () => {
  it("puts people first when they match better than any title (\"nolan\")", () => {
    expect(peopleFirst(payload([title("Norman", "fuzzy")], [person("Christopher Nolan", "prefix")]))).toBe(true);
  });

  it("keeps titles first on a tie or a better title", () => {
    expect(peopleFirst(payload([title("Keanu", "exact")], [person("Keanu Reeves", "prefix")]))).toBe(false);
    expect(peopleFirst(payload([title("Nolan", "prefix")], [person("Christopher Nolan", "prefix")]))).toBe(false);
  });

  it("puts people first when there are no titles at all", () => {
    expect(peopleFirst(payload([], [person("Christopher Nolan", "prefix")]))).toBe(true);
  });
});

describe("enterTarget", () => {
  it("opens an exact title (\"inception\" + Enter never asks Reel)", () => {
    expect(enterTarget(payload([title("Inception", "exact"), title("Perception", "fuzzy")]))).toEqual({ kind: "title", index: 0 });
  });

  it("opens an exact person", () => {
    expect(enterTarget(payload([title("Norman", "fuzzy")], [person("Christopher Nolan", "exact")]))).toEqual({ kind: "person", index: 0 });
  });

  it("asks Reel for anything less than exact, or with nothing loaded", () => {
    expect(enterTarget(payload([title("Heatwave", "prefix")]))).toEqual({ kind: "ask" });
    expect(enterTarget(payload([title("Untiered")]))).toEqual({ kind: "ask" });
    expect(enterTarget(undefined)).toEqual({ kind: "ask" });
  });
});

describe("hiddenLine", () => {
  it("says how many matches the conditions hide, in words that agree with the number", () => {
    expect(hiddenLine(1)).toBe("1 match hidden by your conditions");
    expect(hiddenLine(17)).toBe("17 matches hidden by your conditions");
  });

  it("says nothing when nothing is hidden", () => {
    expect(hiddenLine(0)).toBeNull();
    expect(hiddenLine(undefined)).toBeNull();
  });
});

describe("footprint and the for-you badge (HREV5-RET-05)", () => {
  const row = (name: string, extra: Partial<TypeaheadTitle>): TypeaheadTitle => ({ ...title(name, "exact"), ...extra });

  it("asks the badge only for rows the account has done nothing with", () => {
    const rows = [
      row("untouched", { footprint: false }),
      row("rated, part-watched", { footprint: true, userRating: 7, inProgress: true }),
      row("saved", { footprint: true }),
      row("seen", { footprint: true, isFullyWatched: true }),
      row("also untouched", { footprint: false }),
    ];
    expect(scoredTitleIds(rows)).toBe("untouched,also untouched");
    expect(scoredTitleIds([])).toBe("");
  });

  it("reads an older server's rows from what they do send", () => {
    expect(hasFootprint(row("old seen", { isFullyWatched: true }))).toBe(true);
    expect(hasFootprint(row("old rated", { userRating: 6 }))).toBe(true);
    expect(hasFootprint(row("old in progress", { inProgress: true }))).toBe(true);
    expect(hasFootprint(row("old untouched", {}))).toBe(false);
  });

  it("trusts the server's flag over the fields it can see", () => {
    // Saved or dropped: no rating, no watch, still a footprint.
    expect(hasFootprint(row("saved", { footprint: true }))).toBe(true);
    expect(hasFootprint(row("someone else's watch", { footprint: false }))).toBe(false);
  });
});
