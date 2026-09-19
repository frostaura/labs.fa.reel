import { configureStore } from "@reduxjs/toolkit";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";
import removedTitles from "./removedTitlesSlice";

/**
 * Dropping a show has to prune it from Tonight's picks.
 *
 * The founder reported the show saying "dropped" and staying in the picks list. The server side of
 * that (dropped titles were never excluded from feed eligibility) is fixed in the backend; this is
 * the client half. The picks rows render from `useInfiniteRow`'s local state, not the RTK cache, so
 * the ONLY thing that makes a card leave them without a refetch is the `removedTitles` set — and
 * `markDropped` was the one card-removing reaction that never touched it.
 *
 * The negative half of each assertion matters as much as the positive one: the fix must NOT be an
 * `invalidatesTags: ["Feed"]`. Invalidating Feed refetches the snapshot, mints a new `generatedAt`,
 * re-keys every row on Home and throws away the pages the user has scrolled.
 */

const TITLE_ID = "5f2b0c1e-0000-4000-8000-000000000001";

function makeStore() {
  return configureStore({
    reducer: { [api.reducerPath]: api.reducer, removedTitles },
    middleware: (gDM) => gDM().concat(api.middleware),
  });
}

function removedKeys(store: ReturnType<typeof makeStore>): string[] {
  return store.getState().removedTitles.keys;
}

/**
 * `fetchBaseQuery` builds a `Request` before calling fetch, and node's `Request` rejects the
 * relative URL a browser would happily resolve against the document origin. The shim restores that
 * one browser behaviour and changes nothing else — the app's own base URL stays "/api".
 */
const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

/** Requests seen by the stubbed transport — this is what proves titleId never reaches the wire. */
let requests: { url: string; method: string }[];

/** 500 with a JSON body, so the failure is a clean error result rather than a parse error. */
const failing = () => vi.fn(() => Promise.resolve(new Response("{}", { status: 500, headers: { "content-type": "application/json" } })));

beforeEach(() => {
  requests = [];
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      requests.push({ url: request.url.replace(ORIGIN, ""), method: request.method });
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("markDropped", () => {
  it("removes the title locally so the card leaves Tonight's picks", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(removedKeys(store)).toEqual([TITLE_ID]);
  });

  it("does it without invalidating the picks snapshot, which would reset every scrolled row", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    // Only the drop POST goes out. A Feed invalidation with an active subscriber would refetch
    // /api/feed; nothing here should ever request it.
    expect(requests.map((r) => r.url)).toEqual(["/api/titles/show/1399/reactions/dropped"]);
    expect(requests[0].method).toBe("POST");
  });

  it("keeps titleId off the wire — it exists only to key the local removal set", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(requests[0].url).not.toContain(TITLE_ID);
  });

  it("leaves the removal set alone when the drop fails, so the card does not vanish on a dead POST", async () => {
    vi.stubGlobal("fetch", failing());
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(removedKeys(store)).toEqual([]);
  });
});

describe("undrop", () => {
  it("restores the title, so undoing brings the card back without a reload", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(removedKeys(store)).toEqual([TITLE_ID]);

    await store.dispatch(api.endpoints.undrop.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(removedKeys(store)).toEqual([]);
  });

  it("restores without refetching the picks snapshot either", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.undrop.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(requests.map((r) => r.url)).toEqual(["/api/titles/show/1399/reactions/dropped"]);
    expect(requests[0].method).toBe("DELETE");
  });

  it("leaves the card hidden when the undrop fails — the server still has it dropped", async () => {
    const store = makeStore();
    await store.dispatch(api.endpoints.markDropped.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    vi.stubGlobal("fetch", failing());
    await store.dispatch(api.endpoints.undrop.initiate({ titleId: TITLE_ID, mediaType: "show", tmdbId: 1399 }));
    expect(removedKeys(store)).toEqual([TITLE_ID]);
  });
});
