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
