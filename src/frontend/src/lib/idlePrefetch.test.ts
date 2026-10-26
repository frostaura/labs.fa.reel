import { describe, expect, it, vi } from "vitest";
import { FALLBACK_DELAY_MS, IDLE_TIMEOUT_MS, prefetchWhenIdle, type IdleWindow } from "./idlePrefetch";

function fakeWindow(overrides: Partial<IdleWindow> = {}): IdleWindow & { idle: (() => void)[]; timers: (() => void)[] } {
  const idle: (() => void)[] = [];
  const timers: (() => void)[] = [];
  return {
    idle,
    timers,
    requestIdleCallback: vi.fn((cb: () => void) => idle.push(cb)),
    cancelIdleCallback: vi.fn(),
    setTimeout: vi.fn((cb: () => void) => timers.push(cb)),
    clearTimeout: vi.fn(),
    ...overrides,
  };
}

describe("prefetchWhenIdle", () => {
  it("loads the chunk in an idle period, with a deadline", () => {
    const win = fakeWindow();
    const load = vi.fn(() => Promise.resolve());
    prefetchWhenIdle(load, win);
    expect(load).not.toHaveBeenCalled();
    expect(win.requestIdleCallback).toHaveBeenCalledWith(expect.any(Function), { timeout: IDLE_TIMEOUT_MS });
    win.idle[0]();
    expect(load).toHaveBeenCalledTimes(1);
  });

  it("falls back to a timer where there is no requestIdleCallback (Safari)", () => {
    const win = fakeWindow({ requestIdleCallback: undefined, cancelIdleCallback: undefined });
    const load = vi.fn(() => Promise.resolve());
    const cancel = prefetchWhenIdle(load, win);
    expect(win.setTimeout).toHaveBeenCalledWith(expect.any(Function), FALLBACK_DELAY_MS);
    cancel();
    expect(win.clearTimeout).toHaveBeenCalled();
  });

  it("does nothing when the user asked to save data", () => {
    const win = fakeWindow({ navigator: { connection: { saveData: true } } });
    const load = vi.fn(() => Promise.resolve());
    prefetchWhenIdle(load, win);
    expect(win.requestIdleCallback).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
  });

  it("swallows a failed prefetch", async () => {
    const win = fakeWindow();
    const load = vi.fn(() => Promise.reject(new Error("offline")));
    prefetchWhenIdle(load, win);
    expect(() => win.idle[0]()).not.toThrow();
    await Promise.resolve();
  });
});
