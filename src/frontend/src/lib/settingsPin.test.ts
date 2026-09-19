import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The unlock store behind the settings PIN. The security property pinned here: the store holds a
 * server-issued unlock TOKEN and nothing else — the raw PIN must never touch sessionStorage, and
 * the legacy `{pin: ...}` shape (which DID cache the raw PIN, the QA finding this store replaced)
 * is scrubbed on first load rather than honoured.
 *
 * The module hydrates at import time, so every test re-imports it fresh via `vi.resetModules()`
 * against a stubbed sessionStorage (vitest runs in a node environment).
 */

const UNLOCK_KEY = "reel.pinUnlock";

function fakeSessionStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (key: string) => map.get(key) ?? null,
    key: (index: number) => [...map.keys()][index] ?? null,
    removeItem: (key: string) => {
      map.delete(key);
    },
    setItem: (key: string, value: string) => {
      map.set(key, value);
    },
  };
}

async function loadStore() {
  vi.resetModules();
  return await import("./settingsPin");
}

describe("settingsPin unlock store", () => {
  beforeEach(() => {
    vi.stubGlobal("sessionStorage", fakeSessionStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("scrubs the legacy raw-PIN shape on load instead of honouring it", async () => {
    sessionStorage.setItem(UNLOCK_KEY, JSON.stringify({ pin: "2468", until: Date.now() + 60_000 }));

    const store = await loadStore();

    expect(store.getUnlockToken()).toBeNull();
    expect(sessionStorage.getItem(UNLOCK_KEY)).toBeNull();
  });

  it("discards a stored shape that carries a pin alongside a token", async () => {
    sessionStorage.setItem(
      UNLOCK_KEY,
      JSON.stringify({ token: "tok", pin: "2468", until: Date.now() + 60_000 })
    );

    const store = await loadStore();

    expect(store.getUnlockToken()).toBeNull();
    expect(sessionStorage.getItem(UNLOCK_KEY)).toBeNull();
  });

  it("rehydrates a live token so a reload inside the TTL stays unlocked", async () => {
    const until = Date.now() + 60_000;
    sessionStorage.setItem(UNLOCK_KEY, JSON.stringify({ token: "tok-123", until }));

    const store = await loadStore();

    expect(store.getUnlockToken()).toEqual({ token: "tok-123", until });
  });

  it("discards an expired token on load", async () => {
    sessionStorage.setItem(UNLOCK_KEY, JSON.stringify({ token: "tok-123", until: Date.now() - 1 }));

    const store = await loadStore();

    expect(store.getUnlockToken()).toBeNull();
    expect(sessionStorage.getItem(UNLOCK_KEY)).toBeNull();
  });

  it("stores the token with the server's expiry, and never any PIN", async () => {
    const store = await loadStore();
    const expiresAt = new Date(Date.now() + 15 * 60 * 1000).toISOString();

    store.storeUnlockToken("tok-abc", expiresAt);

    expect(store.getUnlockToken()).toEqual({ token: "tok-abc", until: Date.parse(expiresAt) });
    const persisted = JSON.parse(sessionStorage.getItem(UNLOCK_KEY)!) as Record<string, unknown>;
    expect(persisted).toEqual({ token: "tok-abc", until: Date.parse(expiresAt) });
    expect("pin" in persisted).toBe(false);
  });

  it("falls back to a 15-minute lifetime when expiresAt is unparsable", async () => {
    const store = await loadStore();
    const before = Date.now();

    store.storeUnlockToken("tok-abc", "not-a-date");

    const unlock = store.getUnlockToken();
    expect(unlock?.token).toBe("tok-abc");
    expect(unlock!.until).toBeGreaterThanOrEqual(before + 14 * 60 * 1000);
    expect(unlock!.until).toBeLessThanOrEqual(Date.now() + 16 * 60 * 1000);
  });

  it("clears both memory and storage on clearUnlockToken", async () => {
    const store = await loadStore();
    store.storeUnlockToken("tok-abc", new Date(Date.now() + 60_000).toISOString());

    store.clearUnlockToken();

    expect(store.getUnlockToken()).toBeNull();
    expect(sessionStorage.getItem(UNLOCK_KEY)).toBeNull();
  });

  it("re-locks by itself when the token's expiry passes", async () => {
    vi.useFakeTimers();
    const store = await loadStore();
    store.storeUnlockToken("tok-abc", new Date(Date.now() + 1_000).toISOString());
    expect(store.getUnlockToken()).not.toBeNull();

    vi.advanceTimersByTime(1_500);

    expect(store.getUnlockToken()).toBeNull();
    expect(sessionStorage.getItem(UNLOCK_KEY)).toBeNull();
  });
});
