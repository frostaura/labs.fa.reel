import { describe, expect, it, vi } from "vitest";
import { createInvalidationGate } from "./invalidationGate";

function gate() {
  let hidden = false;
  const apply = vi.fn<(tags: string[]) => void>();
  const g = createInvalidationGate<string>(() => hidden, apply);
  return { g, apply, setHidden: (h: boolean) => (hidden = h) };
}

describe("createInvalidationGate", () => {
  it("applies at once while the tab is visible", () => {
    const { g, apply } = gate();
    g.invalidate(["Sync", "Feed"]);
    expect(apply).toHaveBeenCalledExactlyOnceWith(["Sync", "Feed"]);
    expect(g.pending()).toBe(false);
  });

  it("holds every invalidation while hidden and applies them once, de-duplicated, on show", () => {
    const { g, apply, setHidden } = gate();
    setHidden(true);
    // Ten minutes of a busy stream: completions, a failure, a reconnect.
    for (let i = 0; i < 10; i += 1) g.invalidate(["Sync", "Feed", "TasteDna"]);
    g.invalidate(["Sync"]);
    g.invalidate(["Feed", "Sync"]);
    expect(apply).not.toHaveBeenCalled();
    expect(g.pending()).toBe(true);

    setHidden(false);
    g.flush();
    expect(apply).toHaveBeenCalledExactlyOnceWith(["Sync", "Feed", "TasteDna"]);
    expect(g.pending()).toBe(false);
  });

  it("does nothing on a flush with nothing held, or while still hidden", () => {
    const { g, apply, setHidden } = gate();
    g.flush();
    setHidden(true);
    g.invalidate(["Sync"]);
    g.flush();
    expect(apply).not.toHaveBeenCalled();
    setHidden(false);
    g.flush();
    g.flush();
    expect(apply).toHaveBeenCalledTimes(1);
  });
});
