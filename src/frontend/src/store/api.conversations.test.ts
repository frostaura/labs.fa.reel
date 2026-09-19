import { configureStore } from "@reduxjs/toolkit";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";

/**
 * The conversation read routes.
 *
 * Ask Reel's `/ask` stream stays a raw fetch — RTK Query cannot hold a `text/event-stream` body —
 * but everything AROUND the stream is ordinary cached data: the thread list behind `Recent`, one
 * conversation resumed by id, a turn asked what became of it, and the delete. Putting them here
 * rather than in four more hand-rolled fetches is what makes a delete refresh the list without the
 * page having to know it should.
 *
 * The two things worth pinning are the URLs (a thread resumed from the WRONG id is the multi-device
 * steal all over again) and the tag wiring (a delete that does not invalidate leaves the deleted
 * thread in the menu until a reload).
 */

const ORIGIN = "http://localhost";
const CONVERSATION = "6a3e9e9c-0000-4000-8000-000000000001";

/**
 * `fetchBaseQuery` builds a `Request` before calling fetch, and node's `Request` rejects the
 * relative URL a browser resolves against the document origin. Same shim as `api.dropped.test.ts`.
 */
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

let requests: { url: string; method: string }[];

function makeStore() {
  return configureStore({
    reducer: { [api.reducerPath]: api.reducer },
    middleware: (gDM) => gDM().concat(api.middleware),
  });
}

/** A resume payload with an answer in it — the shape Phase 1 added. */
const RESUMED = {
  id: CONVERSATION,
  title: "a tense heist",
  lastCardsAt: "2026-09-11T09:00:00Z",
  turns: [{ sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" }],
  cards: [],
  reason: "3 picks ranked for you, from 40 pulled.",
  removed: 0,
  lastQuery: "a tense heist",
  mediaTypes: ["movie"],
  shown: [603],
};

beforeEach(() => {
  requests = [];
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      requests.push({ url: request.url.replace(ORIGIN, ""), method: request.method });
      if (request.method === "DELETE") return Promise.resolve(new Response(null, { status: 204 }));
      const body = request.url.includes("/conversations/") ? RESUMED : [];
      return Promise.resolve(
        new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } }),
      );
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getConversation", () => {
  it("asks for the id it was given, and only falls back to the deprecated alias by name", async () => {
    // Fixture variation: a real id, and the literal "latest". One endpoint serves both because the
    // route's `{id:guid}` constraint cannot match the word — which is what lets the page express
    // "this tab's thread, or the account's newest if this browser has never asked" as one query.
    const store = makeStore();
    await store.dispatch(api.endpoints.getConversation.initiate(CONVERSATION));
    await store.dispatch(api.endpoints.getConversation.initiate("latest"));
    expect(requests.map((r) => r.url)).toEqual([
      `/api/search/conversations/${CONVERSATION}`,
      "/api/search/conversations/latest",
    ]);
  });

  it("returns the answer, not just the transcript", async () => {
    const store = makeStore();
    const result = await store.dispatch(api.endpoints.getConversation.initiate(CONVERSATION));
    expect(result.data?.lastCardsAt).toBe("2026-09-11T09:00:00Z");
    expect(result.data?.lastQuery).toBe("a tense heist");
    expect(result.data?.mediaTypes).toEqual(["movie"]);
  });

  it("caches per id, so switching threads and switching back does not refetch", async () => {
    // The `Recent` menu jumps between threads; each one is its own cache entry keyed by its id,
    // which is the difference between a menu that feels instant and one that re-fetches on a
    // pointer-move.
    const store = makeStore();
    await store.dispatch(api.endpoints.getConversation.initiate(CONVERSATION));
    await store.dispatch(api.endpoints.getConversation.initiate(CONVERSATION));
    expect(requests).toHaveLength(1);
  });
});

describe("listConversations", () => {
  it("asks for a bounded list", async () => {
    // Fixture variation: the default limit, and an explicit one. The menu shows a handful; an
    // unbounded list would grow with the account for a control nobody scrolls.
    const store = makeStore();
    await store.dispatch(api.endpoints.listConversations.initiate(undefined));
    await store.dispatch(api.endpoints.listConversations.initiate(5));
    expect(requests.map((r) => r.url)).toEqual([
      "/api/search/conversations?limit=20",
      "/api/search/conversations?limit=5",
    ]);
  });
});

describe("getTurn", () => {
  it("asks the turn what became of it", async () => {
    // This is what replaces Last-Event-ID replay: the events are gone, but the turn is a row, and
    // a row answers the only question a disconnected client has.
    const store = makeStore();
    await store.dispatch(api.endpoints.getTurn.initiate("7b3e9e9c-0000-4000-8000-000000000002"));
    expect(requests[0].url).toBe("/api/search/turns/7b3e9e9c-0000-4000-8000-000000000002");
  });
});

describe("deleteConversation", () => {
  it("deletes by id and refreshes the list behind Recent", async () => {
    const store = makeStore();
    // A live subscriber, so the invalidation has something to refetch — without one, RTK has no
    // reason to re-request and the test would pass against an endpoint with no tags at all.
    const list = store.dispatch(api.endpoints.listConversations.initiate(undefined));
    await list;
    await store.dispatch(api.endpoints.deleteConversation.initiate(CONVERSATION));

    expect(requests.map((r) => `${r.method} ${r.url}`)).toEqual([
      "GET /api/search/conversations?limit=20",
      `DELETE /api/search/conversations/${CONVERSATION}`,
      // The refetch. Without it the deleted thread stays in the menu until the page is reloaded,
      // and clicking it 404s — a delete that visibly did nothing.
      "GET /api/search/conversations?limit=20",
    ]);
    list.unsubscribe();
  });

  it("drops that conversation's own cache entry too", async () => {
    // Fixture variation: the deleted thread is also the one currently open. Leaving its entry
    // cached means the page can keep rendering a conversation the server no longer has.
    const store = makeStore();
    const open = store.dispatch(api.endpoints.getConversation.initiate(CONVERSATION));
    await open;
    requests = [];
    await store.dispatch(api.endpoints.deleteConversation.initiate(CONVERSATION));
    expect(requests.map((r) => `${r.method} ${r.url}`)).toContain(`GET /api/search/conversations/${CONVERSATION}`);
    open.unsubscribe();
  });
});
