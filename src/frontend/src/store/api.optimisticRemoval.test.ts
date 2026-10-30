import { configureStore } from "@reduxjs/toolkit";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";
import removedTitles, { selectHiddenKeys, selectRemovedKeys, titleRemoved } from "./removedTitlesSlice";

/**
 * HREV7-DESK-01. Save, Not for me and Rate take the card off the live rows on the TAP: the hidden
 * set moves as the request starts, the confirmed set only when the server agrees, and a failure
 * takes back exactly the hold it put down. Fixtures vary the action, the outcome, and whether the
 * title was already hidden by an earlier action (which a failure must never un-hide).
 */

const TITLE_ID = "5f2b0c1e-0000-4000-8000-000000000042";
const REF = { titleId: TITLE_ID, mediaType: "movie", tmdbId: 603 };

function makeStore() {
  return configureStore({
    reducer: { [api.reducerPath]: api.reducer, removedTitles },
    middleware: (gDM) => gDM().concat(api.middleware),
  });
}
type Store = ReturnType<typeof makeStore>;
// The slice selectors are typed against the app's RootState; these stores hold the same slice.
const hidden = (s: Store) => selectHiddenKeys(s.getState() as never);
const confirmed = (s: Store) => selectRemovedKeys(s.getState() as never);

const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

/** The server's answer, held until the test lets it go. */
let answer: (status: number) => void;

beforeEach(() => {
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn(
      (request: Request) =>
        new Promise<Response>((resolve) => {
          answer = (status) =>
            resolve(
              new Response(request.url.endsWith("/rating") && status === 200 ? '{"userRating":7}' : "{}", {
                status,
                headers: { "content-type": "application/json" },
              })
            );
        })
    )
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const actions = {
  save: (s: Store) => s.dispatch(api.endpoints.saveForLater.initiate(REF)),
  dismiss: (s: Store) => s.dispatch(api.endpoints.markNotInterested.initiate({ ...REF, reason: "not-for-me" })),
  rate: (s: Store) => s.dispatch(api.endpoints.rateTitle.initiate({ ...REF, rating: 7, markWatched: true })),
};

/** Lets the mutation reach fetch (RTK Query starts it a few microtasks after the dispatch). */
const sent = () => vi.waitFor(() => expect(fetch).toHaveBeenCalled());

describe.each(Object.entries(actions))("%s", (_name, act) => {
  it("hides the title the moment it is sent, and confirms it only when the server agrees", async () => {
    const store = makeStore();
    const request = act(store);
    expect(hidden(store)).toEqual([TITLE_ID]);
    expect(confirmed(store)).toEqual([]);
    await sent();
    answer(200);
    await request;
    expect(hidden(store)).toEqual([TITLE_ID]);
    expect(confirmed(store)).toEqual([TITLE_ID]);
    expect(store.getState().removedTitles.pending).toEqual([]);
  });

  it("shows the title again when the server refuses", async () => {
    const store = makeStore();
    const request = act(store);
    expect(hidden(store)).toEqual([TITLE_ID]);
    await sent();
    answer(500);
    await request;
    expect(hidden(store)).toEqual([]);
    expect(confirmed(store)).toEqual([]);
  });

  it("never un-hides a title an earlier action had already removed", async () => {
    const store = makeStore();
    store.dispatch(titleRemoved({ titleId: TITLE_ID }));
    const request = act(store);
    await sent();
    answer(500);
    await request;
    expect(hidden(store)).toEqual([TITLE_ID]);
    expect(confirmed(store)).toEqual([TITLE_ID]);
  });
});
