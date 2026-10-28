// @vitest-environment jsdom
import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SearchBox from "./SearchBox";
import { feedApi } from "../../store/api/feed";
import type { FeedPayload } from "../../store/feedTypes";
import { typeaheadApi, type TypeaheadPayload } from "../../store/api/typeahead";
import { makeStore, renderWithStore } from "../../test/renderWithStore";

/**
 * The typeahead row's "for you" number carries the account's measured band (FD-4 default: every
 * displayed number carries its measured band). HREV1-RET-10: Home showed "7.5 ±1.6 for you" for
 * House while the dropdown showed a bare "7.5 for you".
 */
const HOUSE = "00000000-0000-0000-0000-0000000000a1";

const LOOKUP: TypeaheadPayload = {
  titles: [
    {
      titleId: HOUSE,
      mediaType: "Show",
      tmdbId: 1408,
      name: "House",
      year: 2004,
      posterPath: null,
      isFullyWatched: false,
      predictedRating: null,
      tier: "exact",
    },
  ],
  people: [],
  // Plenty of strong matches, so the global leg is never asked for.
  strongMatches: 6,
  hiddenByConditions: 0,
};

beforeEach(() => {
  // Nothing leaves the test: every read below is served from the seeded cache.
  vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function renderTyped(band: number | null) {
  const store = makeStore();
  await store.dispatch(typeaheadApi.util.upsertQueryData("searchTypeahead", "house", LOOKUP));
  await store.dispatch(typeaheadApi.util.upsertQueryData("typeaheadScores", HOUSE, { scores: { [HOUSE]: 7.49 } }));
  if (band !== null) {
    await store.dispatch(
      feedApi.util.upsertQueryData("getFeed", undefined, { expectedAbsoluteError: band } as unknown as FeedPayload)
    );
  }

  const router = createMemoryRouter([{ path: "/", element: <SearchBox /> }], { initialEntries: ["/"] });
  renderWithStore(<RouterProvider router={router} />, store);
  fireEvent.change(screen.getByTestId("search-input"), { target: { value: "house" } });
  return screen.findByTestId("typeahead-result");
}

describe("SearchBox typeahead score", () => {
  it("prints the measured band beside the predicted number, as every other surface does", async () => {
    const row = await renderTyped(1.6);
    const predicted = within(row).getByTestId("predicted-rating");
    expect(predicted.textContent).toContain("7.5");
    expect(within(predicted).getByTestId("predicted-band").textContent).toBe("±1.6");
    expect(predicted.textContent).toContain("for you");
    // The evidence label travels with it for a screen reader and the tooltip.
    expect(within(row).getByTestId("score-chip").getAttribute("aria-label")).toContain("1.6");
  });

  it("varies with the account's band, and shows the number bare only when no band was measured", async () => {
    const wide = await renderTyped(2.3);
    expect(within(wide).getByTestId("predicted-band").textContent).toBe("±2.3");
    cleanup();

    const unmeasured = await renderTyped(null);
    expect(within(unmeasured).getByTestId("predicted-rating").textContent).toContain("7.5");
    expect(within(unmeasured).queryByTestId("predicted-band")).toBeNull();
  });
});

/**
 * HREV5-RET-05: "the crown" listed The Chosen — rated 7, part-watched — with a "for you" number and
 * no mark at all. HREV5-DESK-06: the panel was a 95% fill over a blur, so the page's badges read
 * through it.
 */
const CHOSEN = "00000000-0000-0000-0000-0000000000c7";
const CROWN = "00000000-0000-0000-0000-0000000000c1";
const SAVED = "00000000-0000-0000-0000-0000000000c5";

const FOOTPRINTED: TypeaheadPayload = {
  titles: [
    { titleId: CROWN, mediaType: "Show", tmdbId: 65494, name: "The Crown", year: 2016, posterPath: null,
      isFullyWatched: false, predictedRating: null, tier: "exact", footprint: false, userRating: null, inProgress: false },
    { titleId: CHOSEN, mediaType: "Show", tmdbId: 103_768, name: "The Chosen", year: 2017, posterPath: null,
      isFullyWatched: false, predictedRating: null, tier: "word", footprint: true, userRating: 7, inProgress: true },
    { titleId: SAVED, mediaType: "Movie", tmdbId: 5, name: "The Crown Saved", year: 2001, posterPath: null,
      isFullyWatched: false, predictedRating: null, tier: "prefix", footprint: true, userRating: null, inProgress: false },
  ],
  people: [],
  strongMatches: 6,
  hiddenByConditions: 0,
};

async function renderFootprinted() {
  const store = makeStore();
  await store.dispatch(typeaheadApi.util.upsertQueryData("searchTypeahead", "the crown", FOOTPRINTED));
  // Only the untouched title's id is ever asked for; an answer under any other key would be a bug.
  await store.dispatch(typeaheadApi.util.upsertQueryData("typeaheadScores", CROWN, { scores: { [CROWN]: 8.1 } }));
  const router = createMemoryRouter([{ path: "/", element: <SearchBox /> }], { initialEntries: ["/"] });
  renderWithStore(<RouterProvider router={router} />, store);
  fireEvent.change(screen.getByTestId("search-input"), { target: { value: "the crown" } });
  await screen.findAllByTestId("typeahead-result");
  return store;
}

describe("SearchBox footprint marks", () => {
  it("shows the user's own rating and progress instead of a prediction on a title they rated", async () => {
    const store = await renderFootprinted();
    const rows = screen.getAllByTestId("typeahead-result");
    const chosen = rows.find((r) => r.textContent?.includes("The Chosen"))!;

    expect(within(chosen).getByTestId("user-rating").textContent).toContain("7");
    expect(within(chosen).getByTestId("user-rating").textContent).toContain("yours");
    expect(within(chosen).queryByTestId("predicted-rating")).toBeNull();
    expect(within(chosen).getByTestId("typeahead-watch-mark").textContent).toContain("in progress");

    // The untouched title still gets its for-you number; the saved one gets neither number nor mark.
    const crown = rows.find((r) => r.textContent?.includes("The Crown") && !r.textContent.includes("Saved"))!;
    expect(within(crown).getByTestId("predicted-rating").textContent).toContain("8.1");
    const saved = rows.find((r) => r.textContent?.includes("The Crown Saved"))!;
    expect(within(saved).queryByTestId("score-chip")).toBeNull();
    expect(within(saved).queryByTestId("typeahead-watch-mark")).toBeNull();

    // The scores request carried the untouched id alone.
    const asked = Object.values(store.getState()[typeaheadApi.reducerPath].queries)
      .filter((q) => q?.endpointName === "typeaheadScores")
      .map((q) => q?.originalArgs);
    expect(asked).toEqual([CROWN]);
  });

  it("draws the panel on an opaque surface, with no blur behind a translucent fill", async () => {
    await renderFootprinted();
    const panel = screen.getByTestId("typeahead-panel");
    const classes = panel.className.split(/\s+/);

    expect(classes).toContain("bg-fa-ink-2");
    expect(classes.some((c) => c.startsWith("bg-") && c.includes("/"))).toBe(false);
    expect(classes.some((c) => c.startsWith("backdrop-blur"))).toBe(false);
  });
});
