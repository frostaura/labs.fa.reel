import { describe, expect, it } from "vitest";
import { resolveSeedKey } from "./feedSeed";

/**
 * Regression for "acting on a card resets the whole list". Rating, saving or dismissing a title
 * queues a server rebuild; the resulting snapshot used to be adopted immediately, which remounted
 * every row, threw away the scrolled pages and bounced the user back to the start — so queueing
 * several titles from one row was impossible. The rule these tests pin: a fresher snapshot is held
 * until the user asks for it.
 */
describe("resolveSeedKey", () => {
  it("adopts the first snapshot — there is nothing to disturb", () => {
    expect(resolveSeedKey({ latest: "t1", adopted: "", adoptOnNext: false })).toBe("t1");
  });

  it("holds a snapshot produced by a reaction, so the row the user is working through survives", () => {
    expect(resolveSeedKey({ latest: "t2", adopted: "t1", adoptOnNext: false })).toBeNull();
  });

  it("keeps holding across repeated rebuilds — queueing several titles must not reset anything", () => {
    expect(resolveSeedKey({ latest: "t2", adopted: "t1", adoptOnNext: false })).toBeNull();
    expect(resolveSeedKey({ latest: "t3", adopted: "t1", adoptOnNext: false })).toBeNull();
    expect(resolveSeedKey({ latest: "t4", adopted: "t1", adoptOnNext: false })).toBeNull();
  });

  it("adopts when the user explicitly asked to refresh", () => {
    expect(resolveSeedKey({ latest: "t2", adopted: "t1", adoptOnNext: true })).toBe("t2");
  });

  it("does nothing before any snapshot has loaded", () => {
    expect(resolveSeedKey({ latest: "", adopted: "", adoptOnNext: false })).toBeNull();
    expect(resolveSeedKey({ latest: "", adopted: "", adoptOnNext: true })).toBeNull();
  });

  it("is a no-op when the snapshot has not changed, even with a refresh pending", () => {
    // Guards the render-time adjustment against a re-entrant loop: an unchanged key must never
    // re-trigger adoption.
    expect(resolveSeedKey({ latest: "t1", adopted: "t1", adoptOnNext: false })).toBeNull();
    expect(resolveSeedKey({ latest: "t1", adopted: "t1", adoptOnNext: true })).toBeNull();
  });
});
