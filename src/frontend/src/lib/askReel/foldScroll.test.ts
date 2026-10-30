import { describe, expect, it } from "vitest";
import { SCROLL_QUIET_MS, SETTLE_MS, foldScroll, isSettling } from "./foldScroll";

/**
 * HREV6-PH-01 — what the page does when the earlier answer folds above the live turn. Each branch
 * is pinned against the nearest input that takes a different one (one field away).
 */
describe("foldScroll", () => {
  const base = { before: 231, after: -719, viewportHeight: 844, settling: false };

  it("holds the anchor still when it was on screen and the fold moved it (WebKit, no anchoring)", () => {
    expect(foldScroll(base)).toEqual({ kind: "hold", by: -950 });
  });

  it("holds an anchor the reader had scrolled past (above the viewport) too", () => {
    expect(foldScroll({ ...base, before: -300, after: -1250 })).toEqual({ kind: "hold", by: -950 });
  });

  it("does nothing when the browser already anchored (Chromium) or the shift is sub-pixel", () => {
    expect(foldScroll({ ...base, after: 231 })).toEqual({ kind: "none" });
    expect(foldScroll({ ...base, after: 231.6 })).toEqual({ kind: "none" });
    expect(foldScroll({ ...base, after: 232 })).toEqual({ kind: "hold", by: 1 });
  });

  it("retargets when the anchor was below the viewport — the reader was in the answer that folded", () => {
    expect(foldScroll({ ...base, before: 844, after: -106 })).toEqual({ kind: "retarget" });
    // One pixel higher and it was on screen: held.
    expect(foldScroll({ ...base, before: 843, after: -107 })).toEqual({ kind: "hold", by: -950 });
  });

  it("an anchor below the viewport that did not move is left alone", () => {
    expect(foldScroll({ ...base, before: 1500, after: 1500 })).toEqual({ kind: "none" });
  });

  it("retargets while the turn-start scroll is still settling, even with no shift (Chromium's anchoring)", () => {
    expect(foldScroll({ ...base, settling: true })).toEqual({ kind: "retarget" });
    expect(foldScroll({ ...base, after: 231, settling: true })).toEqual({ kind: "retarget" });
  });
});

describe("isSettling", () => {
  const t0 = 10_000;
  const quiet = { turnStart: t0, lastScroll: -Infinity, lastInput: -Infinity };

  it("is settling inside the window after a turn starts", () => {
    expect(isSettling({ ...quiet, now: t0 + SETTLE_MS - 1 })).toBe(true);
    expect(isSettling({ ...quiet, now: t0 + SETTLE_MS })).toBe(false);
  });

  it("stays settling past the window while the page is still visibly scrolling", () => {
    const now = t0 + SETTLE_MS + 500;
    expect(isSettling({ ...quiet, now, lastScroll: now - SCROLL_QUIET_MS + 1 })).toBe(true);
    expect(isSettling({ ...quiet, now, lastScroll: now - SCROLL_QUIET_MS })).toBe(false);
  });

  it("a scroll from before the turn does not count", () => {
    expect(isSettling({ ...quiet, turnStart: t0, now: t0 + SETTLE_MS + 10, lastScroll: t0 - 1 })).toBe(false);
  });

  it("is never settling once the reader has touched the page since the turn began", () => {
    expect(isSettling({ ...quiet, now: t0 + 100, lastInput: t0 + 50 })).toBe(false);
    expect(isSettling({ ...quiet, now: t0 + 100, lastInput: t0 })).toBe(false);
    // The keypress that SENT the turn came before it started, and is not a takeover.
    expect(isSettling({ ...quiet, now: t0 + 100, lastInput: t0 - 1 })).toBe(true);
  });

  it("is never settling with no turn running", () => {
    expect(isSettling({ ...quiet, turnStart: null, now: t0 + 100 })).toBe(false);
  });
});
