import { describe, expect, it } from "vitest";
import { narrativeStaleNote, narrativeStaleReason } from "./narrativeStale";

const TASTE_MOVED = "your taste has moved since";
const EARLIER_VERSION = "written by an earlier version of Reel";

describe("narrativeStaleReason", () => {
  it("a current narrative has no reason", () => {
    expect(narrativeStaleReason({ isStale: false, staleReason: null })).toBeNull();
  });

  it("a moved taste reads as taste-moved", () => {
    expect(narrativeStaleReason({ isStale: true, staleReason: "taste-moved" })).toBe("taste-moved");
  });

  it("an outdated prompt reads as outdated-prompt even though isStale is true", () => {
    expect(narrativeStaleReason({ isStale: true, staleReason: "outdated-prompt" })).toBe("outdated-prompt");
  });

  it("an older server's bare isStale still means the taste moved", () => {
    expect(narrativeStaleReason({ isStale: true })).toBe("taste-moved");
    expect(narrativeStaleReason({ isStale: true, staleReason: null })).toBe("taste-moved");
  });

  it("an older server's bare current narrative has no reason", () => {
    expect(narrativeStaleReason({ isStale: false })).toBeNull();
  });
});

describe("narrativeStaleNote", () => {
  it("taste-moved keeps its wording", () => {
    expect(narrativeStaleNote("taste-moved")).toContain(TASTE_MOVED);
    expect(narrativeStaleNote("taste-moved")).not.toContain(EARLIER_VERSION);
  });

  it("outdated-prompt never claims the taste moved, and says the text is not current", () => {
    const note = narrativeStaleNote(narrativeStaleReason({ isStale: true, staleReason: "outdated-prompt" }));
    expect(note).not.toContain(TASTE_MOVED);
    expect(note).toContain(EARLIER_VERSION);
    expect(note).toContain("may not match the charts below");
  });

  it("a current narrative adds nothing", () => {
    expect(narrativeStaleNote(null)).toBe("");
  });
});
