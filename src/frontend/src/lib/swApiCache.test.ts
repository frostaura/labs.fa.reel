import { describe, expect, it } from "vitest";
import { createSignOutGuard, markFromCache, shouldTakeOverOnInstall } from "./swApiCache";
import { FROM_CACHE_HEADER } from "./swContract";

describe("markFromCache", () => {
  it("marks a cached answer and keeps its body, status and the server's Date", async () => {
    const cached = new Response(JSON.stringify({ items: [1] }), {
      status: 200,
      headers: { "Content-Type": "application/json", Date: "Sat, 24 Oct 2026 10:00:00 GMT" },
    });
    const marked = await markFromCache(cached);
    expect(marked?.headers.get(FROM_CACHE_HEADER)).toBe("1");
    expect(marked?.headers.get("Date")).toBe("Sat, 24 Oct 2026 10:00:00 GMT");
    expect(marked?.status).toBe(200);
    expect(await marked?.json()).toEqual({ items: [1] });
  });

  it("passes a cache miss through untouched", async () => {
    expect(await markFromCache(undefined)).toBeUndefined();
    expect(await markFromCache(null)).toBeNull();
  });
});

describe("createSignOutGuard", () => {
  const request = new Request("https://reel.test/api/feed");

  it("caches an answer to a request that started after the last sign-out", () => {
    const guard = createSignOutGuard();
    const state = {};
    guard.requestWillFetch({ request, state });
    const response = new Response("{}");
    expect(guard.cacheWillUpdate({ response, state })).toBe(response);
  });

  it("refuses an answer to a request that was in flight when the user signed out (I-11)", () => {
    const guard = createSignOutGuard();
    const accountA = {};
    guard.requestWillFetch({ request, state: accountA });
    guard.signedOut();
    expect(guard.cacheWillUpdate({ response: new Response("{\"of\":\"A\"}"), state: accountA })).toBeNull();

    // The next account's requests cache normally.
    const accountB = {};
    guard.requestWillFetch({ request, state: accountB });
    const response = new Response("{}");
    expect(guard.cacheWillUpdate({ response, state: accountB })).toBe(response);
  });

  it("caches when workbox supplies no per-request state", () => {
    const guard = createSignOutGuard();
    guard.signedOut();
    const response = new Response("{}");
    expect(guard.cacheWillUpdate({ response, state: undefined })).toBe(response);
  });
});

describe("shouldTakeOverOnInstall", () => {
  it("takes over once from a worker that predates the update prompt", () => {
    expect(shouldTakeOverOnInstall({ hasActiveWorker: true, promptAwareMarker: false })).toBe(true);
  });

  it("waits for the prompt when the active worker already shows one", () => {
    expect(shouldTakeOverOnInstall({ hasActiveWorker: true, promptAwareMarker: true })).toBe(false);
  });

  it("has nothing to take over on a first install", () => {
    expect(shouldTakeOverOnInstall({ hasActiveWorker: false, promptAwareMarker: false })).toBe(false);
    expect(shouldTakeOverOnInstall({ hasActiveWorker: false, promptAwareMarker: true })).toBe(false);
  });
});
