import { configureStore } from "@reduxjs/toolkit";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";

/**
 * POST /api/feed/more keeps the server's `supply` (HREV1-RET-07). The endpoint's transform rebuilt
 * the page as `{ items, exhausted }`, so the count behind a short rail — "your conditions hide 251"
 * — was dropped in the store before any component could read it, and no rail ever said why.
 */

const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

function answer(body: unknown) {
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } })))
  );
}

const makeStore = () =>
  configureStore({ reducer: { [api.reducerPath]: api.reducer }, middleware: (gDM) => gDM().concat(api.middleware) });

beforeEach(() => vi.unstubAllGlobals());
afterEach(() => vi.unstubAllGlobals());

describe("loadMoreFeed", () => {
  it("keeps the supply the server counted behind a short page", async () => {
    answer({ items: [], exhausted: true, supply: { hiddenByConditions: 251, alreadySeen: 48, clearingFloor: 44 } });
    const page = await makeStore().dispatch(api.endpoints.loadMoreFeed.initiate({ row: "genre", genre: "thriller", excludeTitleIds: [] })).unwrap();
    expect(page).toEqual({ items: [], exhausted: true, supply: { hiddenByConditions: 251, alreadySeen: 48, clearingFloor: 44 } });
  });

  it("reads an absent supply (every non-genre row today) as null, and an absent exhausted as dry", async () => {
    answer({ items: [] });
    const page = await makeStore().dispatch(api.endpoints.loadMoreFeed.initiate({ row: "similar", excludeTitleIds: [] })).unwrap();
    expect(page).toEqual({ items: [], exhausted: true, supply: null });
  });
});
