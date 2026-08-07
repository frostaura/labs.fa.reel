import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  DEFAULT_BROWSE_FILTER,
  isFilterActive,
  matchesBrowseFilter,
  readBrowseFilter,
  writeBrowseFilter,
} from "./browseFilter";

/**
 * A minimal in-memory Storage. The suite runs in node with no DOM (no jsdom or happy-dom is
 * installed, deliberately — every other test here is pure logic), so a stub is what lets these
 * exercise the real parse/validate/catch paths without pulling a DOM implementation into the
 * project just to read two string keys.
 */
function installStorage(options: { throwOnRead?: boolean; throwOnWrite?: boolean } = {}) {
  const map = new Map<string, string>();
  const storage = {
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
  (globalThis as { localStorage?: unknown }).localStorage = storage;
  return map;
}

let store: Map<string, string>;

beforeEach(() => {
  store = installStorage();
});

afterEach(() => {
  delete (globalThis as { localStorage?: unknown }).localStorage;
});

describe("browse filter persistence", () => {
  it("remembers a filter across a reload", () => {
    writeBrowseFilter("home", { media: "movie", availability: "coming" });

    // A fresh read is what a reload does — nothing is carried in memory.
    expect(readBrowseFilter("home")).toEqual({ media: "movie", availability: "coming" });
  });

  it("keeps home and search apart", () => {
    // "Browsing movies tonight" must not silently re-scope an unrelated search.
    writeBrowseFilter("home", { media: "movie", availability: "all" });
    writeBrowseFilter("search", { media: "show", availability: "all" });

    expect(readBrowseFilter("home").media).toBe("movie");
    expect(readBrowseFilter("search").media).toBe("show");
  });

  it("defaults when nothing has been stored", () => {
    expect(readBrowseFilter("home")).toEqual(DEFAULT_BROWSE_FILTER);
  });

  it.each([
    ["corrupt json", "{not json"],
    ["a bare string", '"movie"'],
    ["null", "null"],
    ["an array", "[]"],
  ])("degrades to the default for %s", (_label, stored) => {
    store.set("reel.browseFilter.home", stored);
    expect(readBrowseFilter("home")).toEqual(DEFAULT_BROWSE_FILTER);
  });

  it("drops an enum value no predicate recognises", () => {
    // The real hazard: localStorage survives deploys, so a value from an older build can outlive
    // the code that understood it. matchesBrowseFilter would then match nothing and the feed would
    // render empty with no explanation.
    store.set("reel.browseFilter.home", JSON.stringify({ media: "documentary", availability: "someday" }));

    const restored = readBrowseFilter("home");
    expect(restored).toEqual(DEFAULT_BROWSE_FILTER);
    expect(matchesBrowseFilter({ mediaType: "Movie", isReleased: true }, restored)).toBe(true);
  });

  it("keeps the half of a partially-valid value that is still recognised", () => {
    store.set("reel.browseFilter.home", JSON.stringify({ media: "show", availability: "nonsense" }));

    expect(readBrowseFilter("home")).toEqual({ media: "show", availability: "all" });
  });

  it("survives storage being unavailable", () => {
    // Safari private mode throws on access rather than returning null. A remembered filter is not
    // worth a blank page.
    installStorage({ throwOnRead: true, throwOnWrite: true });

    expect(readBrowseFilter("home")).toEqual(DEFAULT_BROWSE_FILTER);
    expect(() => writeBrowseFilter("home", { media: "movie", availability: "all" })).not.toThrow();
  });

  it("reports whether a restored filter is actually narrowing anything", () => {
    writeBrowseFilter("home", DEFAULT_BROWSE_FILTER);
    expect(isFilterActive(readBrowseFilter("home"))).toBe(false);

    writeBrowseFilter("home", { media: "show", availability: "all" });
    expect(isFilterActive(readBrowseFilter("home"))).toBe(true);
  });
});
