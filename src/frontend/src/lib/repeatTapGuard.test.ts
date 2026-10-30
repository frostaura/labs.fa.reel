import { describe, expect, it } from "vitest";
import { REPEAT_RADIUS_PX, REPEAT_WINDOW_MS, createRepeatTapGuard } from "./repeatTapGuard";

describe("createRepeatTapGuard (HREV7-DESK-01)", () => {
  it("lets the first press act and swallows a second one on the same spot a moment later", () => {
    const allow = createRepeatTapGuard();
    expect(allow({ at: 1000, x: 100, y: 100 })).toBe(true);
    expect(allow({ at: 1150, x: 101, y: 99 })).toBe(false);
  });

  it("swallows every press of a triple tap after the first", () => {
    const allow = createRepeatTapGuard();
    expect(allow({ at: 0, x: 50, y: 50 })).toBe(true);
    expect(allow({ at: 200, x: 50, y: 50 })).toBe(false);
    expect(allow({ at: 400, x: 50, y: 50 })).toBe(false);
  });

  it("acts on a press that comes after the double-tap interval", () => {
    const allow = createRepeatTapGuard();
    expect(allow({ at: 0, x: 50, y: 50 })).toBe(true);
    expect(allow({ at: REPEAT_WINDOW_MS, x: 50, y: 50 })).toBe(true);
    expect(allow({ at: REPEAT_WINDOW_MS + REPEAT_WINDOW_MS - 1, x: 50, y: 50 })).toBe(false);
  });

  it("acts on a quick press somewhere else — a different control, chosen on purpose", () => {
    const allow = createRepeatTapGuard();
    expect(allow({ at: 0, x: 50, y: 50 })).toBe(true);
    expect(allow({ at: 100, x: 50 + REPEAT_RADIUS_PX + 1, y: 50 })).toBe(true);
    expect(allow({ at: 150, x: 50 + REPEAT_RADIUS_PX + 1, y: 50 + REPEAT_RADIUS_PX })).toBe(false);
  });

  it("does not treat a clock that went backwards as a repeat", () => {
    const allow = createRepeatTapGuard();
    expect(allow({ at: 1000, x: 50, y: 50 })).toBe(true);
    expect(allow({ at: 900, x: 50, y: 50 })).toBe(true);
  });

  it("keeps each guard's history its own", () => {
    const a = createRepeatTapGuard();
    const b = createRepeatTapGuard();
    expect(a({ at: 0, x: 0, y: 0 })).toBe(true);
    expect(b({ at: 10, x: 0, y: 0 })).toBe(true);
  });
});
