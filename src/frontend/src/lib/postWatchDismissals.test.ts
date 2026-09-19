import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { dismiss, readDismissed } from "./postWatchDismissals";

/**
 * A minimal in-memory Storage, matching the pattern the browse-filter suite established: the tests
 * run in node with no DOM, and a stub is what lets these exercise the real parse/validate/catch
 * paths without pulling in a DOM implementation to read one string key.
 */
function installStorage(options: { throwOnRead?: boolean; throwOnWrite?: boolean } = {}) {
  const map = new Map<string, string>();
  (globalThis as { localStorage?: unknown }).localStorage = {
    getItem(key: string) {
      if (options.throwOnRead) throw new Error("SecurityError");
      return map.get(key) ?? null;
    },
    setItem(key: string, value: string) {
      if (options.throwOnWrite) throw new Error("QuotaExceededError");
      map.set(key, value);
    },
    removeItem: (key: string) => void map.delete(key),
    clear: () => map.clear(),
    key: (i: number) => [...map.keys()][i] ?? null,
    get length() {
      return map.size;
    },
  };
  return map;
}

beforeEach(() => installStorage());
afterEach(() => {
  delete (globalThis as { localStorage?: unknown }).localStorage;
});

describe("post-watch prompt dismissals", () => {
  it("remembers a dismissal per title across a reload", () => {
    dismiss("title-a");
    dismiss("title-b");

    const stored = readDismissed();
    expect(stored.has("title-a")).toBe(true);
    expect(stored.has("title-b")).toBe(true);
    expect(stored.has("title-c")).toBe(false);
  });

  it("re-validates stored content rather than trusting it", () => {
    // localStorage survives deploys, so a value from an older build can outlive the code that
    // wrote it. None of these may throw or leak a non-string into the set.
    const store = installStorage();
    store.set("reel.postwatch.dismissed", "not json at all");
    expect(readDismissed().size).toBe(0);

    store.set("reel.postwatch.dismissed", JSON.stringify({ a: 1 }));
    expect(readDismissed().size).toBe(0);

    store.set("reel.postwatch.dismissed", JSON.stringify(["ok", 7, null, { x: 1 }]));
    expect([...readDismissed()]).toEqual(["ok"]);
  });

  it("survives storage that throws — a dismissed prompt is not worth a blank page", () => {
    installStorage({ throwOnRead: true });
    expect(readDismissed().size).toBe(0);

    installStorage({ throwOnWrite: true });
    // The dismissal still holds for this render; it simply will not survive a reload.
    expect(dismiss("title-a").has("title-a")).toBe(true);

    delete (globalThis as { localStorage?: unknown }).localStorage;
    expect(readDismissed().size).toBe(0);
    expect(dismiss("title-a").has("title-a")).toBe(true);
  });
});
