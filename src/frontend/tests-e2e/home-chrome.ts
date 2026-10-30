import type { Page } from "@playwright/test";
import { mockJson } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The "Trakt recommends" rail's "nothing to say" answer: Trakt answered and had nothing. Exported so
 * a spec that boots Home with its own mocks can answer the rail the same way.
 */
export const TRAKT_RECS_NONE = {
  state: "Ok",
  fetchedAt: "2026-10-30T10:00:00Z",
  returnedByTrakt: 0,
  unresolved: 0,
  hiddenAsSeen: 0,
  hiddenAsSetAside: 0,
  hiddenByConditions: 0,
  heldBelowBar: 0,
  bar: null,
  unscoredRemaining: 0,
  items: [],
};

/**
 * Every call Home makes around its picks, answered with the account's "nothing to say" shape, so a
 * WS-10A spec that boots Home makes no unmocked call (R1-21) and states only what it is about.
 *
 * Registered BEFORE a spec's own mocks: Playwright answers with the most recently registered
 * matching route, so anything the spec mocks afterwards wins.
 */
export async function mockHomeChrome(
  page: Page,
  overrides: { filters?: unknown; accuracy?: unknown; recentlyWatched?: unknown } = {}
): Promise<void> {
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [], degradedReason: null });
  await mockJson(page, "**/api/feed/trakt-recommendations", TRAKT_RECS_NONE);
  await mockJson(page, "**/api/saved/recently-watched", overrides.recentlyWatched ?? { expectedAbsoluteError: null, items: [] });
  await mockJson(page, "**/api/settings/filters", overrides.filters ?? { languages: ["*"] });
  // The conditions editor (opened from the Home chips) builds its genre chips from the catalogue.
  await mockFilterChips(page);
  await mockJson(
    page,
    "**/api/accuracy",
    overrides.accuracy ?? {
      window: "365d",
      from: null,
      to: "2026-10-26T00:00:00Z",
      source: "none",
      headline: { withinOnePoint: 0, n: 0, meanAbsoluteError: 0, bias: 0, correlation: null, meanPredicted: 0, meanActual: 0 },
      calibration: [],
      moments: [],
      conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
      counterfactual: [],
      learnedThisWeek: null,
      crossReference: { minimumOutcomes: 20, online: [], backtests: [] },
    }
  );
  for (const path of ["/api/feed/learned", "/api/feed/cross-reference"]) {
    await page.route(
      (url) => url.pathname === path,
      (route) => route.fulfill({ status: 204, body: "" })
    );
  }
}
