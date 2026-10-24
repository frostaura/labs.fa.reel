import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RELOAD_GUARD_MS, isChunkLoadError, reloadOnceForNewBuild } from "./deployReload";

class MemoryStorage {
  private map = new Map<string, string>();
  getItem(k: string) {
    return this.map.get(k) ?? null;
  }
  setItem(k: string, v: string) {
    this.map.set(k, v);
  }
}

beforeEach(() => vi.stubGlobal("sessionStorage", new MemoryStorage()));
afterEach(() => vi.unstubAllGlobals());

describe("isChunkLoadError", () => {
  it.each([
    "Failed to fetch dynamically imported module: http://x/assets/Saved-abc.js",
    "Importing a module script failed.",
    "error loading dynamically imported module: http://x/a.js",
    "Unable to preload CSS for /assets/Home-1.css",
  ])("recognises %s", (message) => {
    expect(isChunkLoadError(new TypeError(message))).toBe(true);
  });

  it("does not mistake an ordinary render error for a deploy", () => {
    expect(isChunkLoadError(new TypeError("items.map is not a function"))).toBe(false);
    expect(isChunkLoadError(null)).toBe(false);
  });
});

describe("reloadOnceForNewBuild", () => {
  it("reloads once, then refuses inside the guard window, then reloads again after it", () => {
    const reload = vi.fn();
    expect(reloadOnceForNewBuild(1_000, reload)).toBe(true);
    expect(reloadOnceForNewBuild(1_000 + RELOAD_GUARD_MS - 1, reload)).toBe(false);
    expect(reloadOnceForNewBuild(1_000 + RELOAD_GUARD_MS, reload)).toBe(true);
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it("fails closed when storage is unavailable — an unguarded reload could loop", () => {
    vi.stubGlobal("sessionStorage", {
      getItem() {
        throw new Error("SecurityError");
      },
    });
    const reload = vi.fn();
    expect(reloadOnceForNewBuild(1_000, reload)).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });
});
