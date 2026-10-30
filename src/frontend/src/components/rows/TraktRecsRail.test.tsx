// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import TraktRecsRail, { TRAKT_RECS_SKELETON_CARDS } from "./TraktRecsRail";
import { makeStore, renderWithStore } from "../../test/renderWithStore";
import { DEFAULT_BROWSE_FILTER, type BrowseFilter } from "../../lib/browseFilter";
import { titleRemovalStarted } from "../../store/removedTitlesSlice";
import type { TraktRecommendationCard, TraktRecommendations } from "../../store/feedTypes";
import { axeViolations } from "../../test/axe";

/**
 * The rail's states as rendered: loading holds two rows of placeholders, a list renders as one
 * two-row grid scroller, and every empty state says what it is — never a blank rail. The copy
 * itself is pinned in lib/traktRecsCopy.test.ts. jsdom has no IntersectionObserver, so the rail
 * asks at once (useNearViewport's documented fallback).
 */
const card = (n: number, over: Partial<TraktRecommendationCard> = {}): TraktRecommendationCard => ({
  titleId: `00000000-0000-0000-0000-${String(n).padStart(12, "0")}`,
  mediaType: n % 2 === 0 ? "Show" : "Movie",
  tmdbId: 1000 + n,
  name: `Title ${n}`,
  year: 2020,
  runtimeMinutes: 100,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: n === 1 ? 7.4 : null,
  crowdRating: 7,
  crowdVotes: 1000,
  whyThis: "",
  isReleased: true,
  providers: [],
  traktRank: n,
  ...over,
});

const payload = (over: Partial<TraktRecommendations> = {}): TraktRecommendations => ({
  state: "Ok",
  fetchedAt: new Date().toISOString(),
  returnedByTrakt: 6,
  unresolved: 0,
  hiddenAsSeen: 2,
  hiddenAsSetAside: 0,
  hiddenByConditions: 0,
  heldBelowBar: 0,
  bar: null,
  unscoredRemaining: 0,
  items: [card(1), card(2), card(3), card(4)],
  ...over,
});

let responses: Array<() => Response | Promise<Response>>;
const requested: string[] = [];

const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
/** fetchBaseQuery builds a Request from "/api/…"; node's Request needs the origin a browser supplies. */
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

beforeEach(() => {
  responses = [];
  requested.length = 0;
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      // Only the rail's own read is answered; anything else (the session) stays pending.
      if (!request.url.includes("/api/feed/trakt-recommendations")) return new Promise<Response>(() => {});
      requested.push(request.url);
      const next = responses.shift();
      return next ? Promise.resolve(next()) : new Promise<Response>(() => {});
    })
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const json = (body: unknown, status = 200) => () =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function renderRail(filter: BrowseFilter = DEFAULT_BROWSE_FILTER, store = makeStore()) {
  return renderWithStore(
    <MemoryRouter>
      <TraktRecsRail filter={filter} />
    </MemoryRouter>,
    store
  );
}

describe("TraktRecsRail", () => {
  it("holds two rows of placeholders while the first answer is in flight", () => {
    renderRail();
    expect(screen.getByRole("heading", { name: "Trakt recommends" })).toBeTruthy();
    expect(screen.getAllByTestId("rail-skeleton")).toHaveLength(TRAKT_RECS_SKELETON_CARDS);
    const scroller = screen.getAllByTestId("rail-skeleton")[0].parentElement!.parentElement!;
    expect(scroller.className).toContain("grid-rows-2");
    expect(scroller.className).toContain("grid-flow-col");
  });

  it("renders Trakt's list in Trakt's order as one two-row scroller, with what was taken out", async () => {
    responses.push(json(payload()));
    renderRail();

    await waitFor(() => expect(screen.getAllByTestId("rec-card")).toHaveLength(4));
    expect(screen.getAllByTestId("rec-card-link").map((a) => a.textContent)).toEqual(["Title 1", "Title 2", "Title 3", "Title 4"]);
    const scroller = screen.getAllByTestId("rec-card")[0].parentElement!.parentElement!;
    expect(scroller.className).toContain("grid-rows-2");
    expect(screen.getByTestId("rail-caption").textContent).toContain("Not shown: 2 you've already seen.");
    expect(screen.queryByTestId("rail-skeleton")).toBeNull();
    // Reel's number only where the model has one — never a placeholder score.
    expect(screen.getAllByTestId("predicted-rating")).toHaveLength(1);
    expect(requested[0]).toMatch(/\/api\/feed\/trakt-recommendations$/);
  });

  it("drops a card the moment it is acted on, and honours the Movies/TV view", async () => {
    responses.push(json(payload()));
    const store = makeStore();
    renderRail({ media: "movie", availability: "all" }, store);

    await waitFor(() => expect(screen.getAllByTestId("rec-card")).toHaveLength(2));
    act(() => {
      store.dispatch(titleRemovalStarted({ titleId: card(1).titleId }));
    });
    expect(screen.getAllByTestId("rec-card-link").map((a) => a.textContent)).toEqual(["Title 3"]);
  });

  it.each([
    ["Unavailable", /Trakt isn't answering right now/],
    ["NotLinked", /no Trakt link/],
    ["RelinkRequired", /sign in with it again/],
  ] as const)("says %s in words, keeping the heading, instead of an empty rail", async (state, text) => {
    responses.push(json(payload({ state, items: [], returnedByTrakt: 0, hiddenAsSeen: 0 })));
    renderRail();

    const notice = await screen.findByTestId("trakt-recs-notice");
    expect(notice.textContent).toMatch(text);
    expect(notice.getAttribute("data-state")).toBe(state);
    expect(screen.getByRole("heading", { name: "Trakt recommends" })).toBeTruthy();
    expect(screen.queryByTestId("rec-card")).toBeNull();
    expect(screen.queryByTestId("rail-skeleton")).toBeNull();
    expect(Boolean(screen.queryByTestId("trakt-recs-retry"))).toBe(state === "Unavailable");
  });

  it("an empty list after filtering says what was filtered, not that there were none", async () => {
    responses.push(json(payload({ items: [], returnedByTrakt: 3, hiddenAsSeen: 3 })));
    renderRail();

    expect((await screen.findByTestId("trakt-recs-notice")).textContent).toBe("Nothing left to show from Trakt's 3: 3 you've already seen.");
  });

  it("Retry asks Trakt again with retry=true and shows what comes back", async () => {
    responses.push(json(payload({ state: "Unavailable", items: [], returnedByTrakt: 0, hiddenAsSeen: 0 })));
    responses.push(json(payload()));
    renderRail();

    fireEvent.click(await screen.findByTestId("trakt-recs-retry"));

    await waitFor(() => expect(screen.getAllByTestId("rec-card")).toHaveLength(4));
    expect(requested[1]).toMatch(/\/api\/feed\/trakt-recommendations\?retry=true$/);
  });

  it("a read that fails outright is the page's Couldn't-load line, never an empty rail", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      for (let i = 0; i < 4; i++) responses.push(json({ error: "boom" }, 503));
      renderRail();
      // The rails' schedule: three automatic retries (1, 2 and 4 s), then the user's Retry.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(8_000);
      });
      const line = await screen.findByTestId("trakt-recs-failed");
      expect(line.textContent).toContain("Couldn't load Trakt's recommendations");
      expect(screen.queryByTestId("rail-skeleton")).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("breaks no axe rule with a list or with a notice", async () => {
    responses.push(json(payload()));
    const { container } = renderRail();
    await waitFor(() => expect(screen.getAllByTestId("rec-card")).toHaveLength(4));
    expect(await axeViolations(container)).toEqual([]);
  });
});
