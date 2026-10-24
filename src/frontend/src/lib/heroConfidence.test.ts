import { describe, expect, it } from "vitest";
import { formatBar, heroReachingNote, isReaching, reachingLabel } from "./heroConfidence";

const confident = { confidence: "confident" as const, floorUsed: 7.5 };
const reaching = { confidence: "reaching" as const, floorUsed: 7.5 };
/** A hero card paged in after the snapshot, or any non-hero card: never measured against the bar. */
const unmeasured = {};

describe("isReaching", () => {
  it("is true only for a pick the server measured and found short", () => {
    expect(isReaching(reaching)).toBe(true);
    expect(isReaching(confident)).toBe(false);
  });

  it("treats an unmeasured card as silence, not as a reach", () => {
    // Guessing would put a caveat on cards that were never held to the hero bar at all.
    expect(isReaching(unmeasured)).toBe(false);
  });
});

describe("reachingLabel", () => {
  it("names the bar the pick fell short of", () => {
    expect(reachingLabel(reaching)).toBe("Reaching a bit — nothing cleared my usual 7.5 bar tonight");
  });

  it("still says the honest thing when the server sent no bar", () => {
    expect(reachingLabel({ confidence: "reaching" })).toBe("Reaching a bit — nothing cleared my usual bar tonight");
  });

  it("says nothing at all about a pick that cleared it", () => {
    expect(reachingLabel(confident)).toBeNull();
    expect(reachingLabel(unmeasured)).toBeNull();
  });

  it("carries an account's raised bar rather than the product default", () => {
    expect(reachingLabel({ confidence: "reaching", floorUsed: 8.5 })).toContain("8.5");
  });
});

describe("heroReachingNote", () => {
  it("speaks up when every measured pick in the row is a reach", () => {
    expect(heroReachingNote([reaching, reaching, reaching])).toBe("Nothing cleared your usual 7.5 bar tonight");
  });

  it("stays quiet when even one pick cleared the bar — that card's own label is enough", () => {
    expect(heroReachingNote([reaching, reaching, confident])).toBeNull();
  });

  it("stays quiet on a row nothing was measured on", () => {
    expect(heroReachingNote([])).toBeNull();
    expect(heroReachingNote([unmeasured, unmeasured])).toBeNull();
  });

  it("is not retracted by unmeasured cards paging in behind the snapshot", () => {
    // The hero scrolls forever; a paged card has no bar, and its arrival must not quietly withdraw a
    // true statement about the five picks the build actually chose.
    expect(heroReachingNote([reaching, reaching, unmeasured])).not.toBeNull();
  });
});

describe("formatBar", () => {
  it("speaks the bar to one decimal", () => {
    expect(formatBar(7.5)).toBe("7.5");
    expect(formatBar(8)).toBe("8.0");
  });
});
