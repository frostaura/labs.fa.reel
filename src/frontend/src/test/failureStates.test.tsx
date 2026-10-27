// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CardUndoProvider } from "../components/rec/CardUndo";
import { useCardUndo, type CardAction } from "../lib/cardUndo";
import { titleRemoved } from "../store/removedTitlesSlice";
import { makeStore, renderWithStore, type TestStore } from "./renderWithStore";

/**
 * Claims family failure-states-change-nothing (R1-46), card half: a failed undo says the card
 * action still stands, and the store agrees. The copy alone was unit-tested (cardUndo.test.ts);
 * this pins the state behind it. A card the user dismissed, saved or rated is in `removedTitles`
 * (that set is what keeps it off every rail); an undo that fails must leave it there, because the
 * toast then says it is "still hidden", "still saved" or that the rating "still stands". The
 * passing undo is the control: the same fixture with a 204 restores the card, so the failing case
 * cannot pass by never restoring anything.
 */

const TITLE_ID = "5f2b0c1e-0000-4000-8000-0000000000aa";
const BASE = { titleId: TITLE_ID, mediaType: "movie", tmdbId: 603, name: "The Matrix" } as const;

const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
/** fetchBaseQuery builds a Request from "/api/…"; node's Request needs an origin a browser would supply. */
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

let requests: { method: string; path: string }[];
let status: number;

beforeEach(() => {
  requests = [];
  status = 500;
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      requests.push({ method: request.method, path: new URL(request.url).pathname });
      return Promise.resolve(
        status === 204 ? new Response(null, { status }) : new Response("{}", { status, headers: { "content-type": "application/json" } })
      );
    })
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

/** Raises the toast for `action` once, as a card does after its action succeeded. */
function Announcer({ action }: { action: CardAction }) {
  const announce = useCardUndo();
  useEffect(() => announce?.(action), [announce, action]);
  return null;
}

function mount(action: CardAction): TestStore {
  const store = makeStore();
  // The card's own action already landed: the title is off the rails.
  store.dispatch(titleRemoved({ titleId: TITLE_ID }));
  renderWithStore(
    <CardUndoProvider>
      <Announcer action={action} />
    </CardUndoProvider>,
    store
  );
  return store;
}

const removed = (store: TestStore) => store.getState().removedTitles.keys;

describe("a failed card undo changes nothing, and says so", () => {
  it.each([
    { kind: "dismiss", action: { ...BASE, kind: "dismiss" } as CardAction, path: "/api/titles/movie/603/reactions/not_interested", still: "“The Matrix” is still hidden from your picks" },
    { kind: "save", action: { ...BASE, kind: "save" } as CardAction, path: "/api/titles/movie/603/reactions/save_for_later", still: "“The Matrix” is still saved" },
    { kind: "rate", action: { ...BASE, kind: "rate", rating: 8 } as CardAction, path: "/api/titles/movie/603/rating", still: "Your 8 for “The Matrix” still stands" },
  ])("$kind: the undo is sent, fails, and the card stays off the rails", async ({ action, path, still }) => {
    const store = mount(action);
    const undo = await screen.findByTestId("card-undo-toast-undo");
    await act(async () => fireEvent.click(undo));

    await screen.findByText(`Couldn’t undo — ${still}.`);
    expect(requests).toEqual([{ method: "DELETE", path }]);
    expect(removed(store)).toEqual([TITLE_ID]);
    expect(screen.getByTestId("card-undo-toast-undo").textContent).toContain("Try again");
  });

  it.each([
    { ...BASE, kind: "dismiss" } as CardAction,
    { ...BASE, kind: "save" } as CardAction,
    { ...BASE, kind: "rate", rating: 8 } as CardAction,
  ])("control — the same undo that succeeds restores the card ($kind)", async (action) => {
    status = 204;
    const store = mount(action);
    const undo = await screen.findByTestId("card-undo-toast-undo");
    await act(async () => fireEvent.click(undo));

    await waitFor(() => expect(removed(store)).toEqual([]));
    expect(screen.queryByText(/Couldn’t undo/)).toBeNull();
  });
});
