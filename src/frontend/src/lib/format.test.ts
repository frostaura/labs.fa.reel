import { describe, expect, it } from "vitest";
import { formatEpisodeRef, formatRuntime, formatVotes } from "./format";

/**
 * The crowd badge's footnote. A community score means nothing without the size of the community
 * behind it — 9.1 from 12 votes is a rounding error, 9.1 from 40,000 is a fact — but it has to fit
 * in a handful of characters beside the number.
 */
describe("formatVotes", () => {
  it("shows small counts exactly — 12 votes is the interesting case, not a rounding target", () => {
    expect(formatVotes(1)).toBe("1");
    expect(formatVotes(12)).toBe("12");
    expect(formatVotes(999)).toBe("999");
  });

  it("abbreviates thousands, keeping one decimal only where it distinguishes", () => {
    expect(formatVotes(1000)).toBe("1k");
    expect(formatVotes(1240)).toBe("1.2k");
    expect(formatVotes(9500)).toBe("9.5k");
    // Past 10k the decimal stops carrying information at badge size, so it is dropped.
    expect(formatVotes(12_400)).toBe("12k");
    expect(formatVotes(40_000)).toBe("40k");
    expect(formatVotes(999_400)).toBe("999k");
  });

  it("abbreviates millions", () => {
    expect(formatVotes(1_000_000)).toBe("1m");
    expect(formatVotes(2_400_000)).toBe("2.4m");
  });

  it("says nothing at all when there is no crowd — the badge then shows only the score", () => {
    expect(formatVotes(null)).toBeNull();
    expect(formatVotes(undefined)).toBeNull();
    expect(formatVotes(0)).toBeNull();
    expect(formatVotes(-5)).toBeNull();
    expect(formatVotes(Number.NaN)).toBeNull();
  });
});

describe("formatRuntime", () => {
  it("formats hours and minutes, dropping an empty half", () => {
    expect(formatRuntime(45)).toBe("45m");
    expect(formatRuntime(120)).toBe("2h");
    expect(formatRuntime(143)).toBe("2h 23m");
  });

  it("is null for a missing or nonsensical runtime", () => {
    expect(formatRuntime(null)).toBeNull();
    expect(formatRuntime(0)).toBeNull();
  });
});

describe("formatEpisodeRef", () => {
  it("needs both halves", () => {
    expect(formatEpisodeRef(2, 5)).toBe("S2 E5");
    expect(formatEpisodeRef(2, null)).toBeNull();
    expect(formatEpisodeRef(null, 5)).toBeNull();
  });
});
