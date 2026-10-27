import { describe, expect, it } from "vitest";
import { personHiddenLine } from "./personCopy";

describe("personHiddenLine (R1-41: a person's credits the conditions hold out are counted)", () => {
  // PersonEndpoints holds every content rule and the maturity ceiling out of the filmography and
  // reports hiddenByConditions; the page used to drop that count, so titles vanished unexplained.
  it("says nothing when nothing is hidden, or when an older server sends no count", () => {
    expect(personHiddenLine(0, 5)).toBeNull();
    expect(personHiddenLine(undefined, 5)).toBeNull();
    expect(personHiddenLine(null, 0)).toBeNull();
  });

  it("counts what is hidden beside what is shown, singular and plural", () => {
    expect(personHiddenLine(1, 4)).toBe("1 more of their titles is hidden by your conditions.");
    expect(personHiddenLine(3, 4)).toBe("3 more of their titles are hidden by your conditions.");
  });

  it("says ALL of them when the conditions leave nothing to list, so the page never reads as empty", () => {
    expect(personHiddenLine(1, 0)).toBe("Their 1 title in Reel is hidden by your conditions.");
    expect(personHiddenLine(6, 0)).toBe("All 6 of their titles in Reel are hidden by your conditions.");
  });
});
