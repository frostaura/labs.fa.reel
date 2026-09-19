import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The global typeahead leg: when the LOCAL shortlist resolves sparse (<3 titles for a 3+ char
 * term), the box re-asks with `global=1` and renders raw TMDB rows under "Beyond your catalogue" —
 * lookup, not suggestion (never scored, never eligibility-filtered). Keyboard navigation must
 * reach those rows, and selecting one navigates to the title route like a local hit.
 */

const LOCAL_TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000aa",
  mediaType: "Movie",
  tmdbId: 2002,
  name: "Jeanne Local Match",
  year: 2019,
  posterPath: null,
  isFullyWatched: false,
  predictedRating: 7.5,
};

const GLOBAL_ROWS = [
  {
    source: "tmdb", mediaType: "movie", tmdbId: 777, name: "Jeanne Dielman",
    year: 1975, posterPath: null, crowdRating: 7.9, predictedRating: null,
  },
  {
    source: "tmdb", mediaType: "show", tmdbId: 888, name: "Jeanne of the Coast",
    year: 2021, posterPath: null, crowdRating: 6.8, predictedRating: null,
  },
];

async function bootHome(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [{
      titleId: "00000000-0000-0000-0000-000000000001", mediaType: "Movie", tmdbId: 1001,
      name: "Hero Pick", year: 2026, runtimeMinutes: 110, posterPath: null, backdropPath: null,
      genres: ["drama"], predictedRating: 8.4, crowdRating: null, crowdVotes: null,
      whyThis: "Strong fit with your taste profile.", isReleased: true,
    }],
    rows: [], lockedRowCount: 0,
  });
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });

  // Same pathname, two answers: the local leg (no global=1) is sparse, the global leg carries the
  // beyond-catalogue rows. Discriminated on the query param, which mockJson's pathname matching
  // deliberately ignores — so these use URL-predicate routes directly.
  await page.route(
    (u) => u.pathname === "/api/search/typeahead" && u.searchParams.get("global") !== "1",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ titles: [LOCAL_TITLE], people: [] }),
    })
  );
  await page.route(
    (u) => u.pathname === "/api/search/typeahead" && u.searchParams.get("global") === "1",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ titles: [LOCAL_TITLE], people: [], global: GLOBAL_ROWS }),
    })
  );

  await page.goto("/home");
}

test.describe("global typeahead — beyond your catalogue", () => {
  test("sparse local results grow the flagged TMDB group, reachable by keyboard", async ({ page }) => {
    await bootHome(page);
    // The selected global row lands on the title route; the dialog hydrates it on demand, so the
    // detail fetch needs a real (if minimal) payload rather than a 401.
    await mockJson(page, "**/api/titles/movie/777", {
      titleId: "00000000-0000-0000-0000-0000000000gg", mediaType: "Movie", tmdbId: 777,
      name: "Jeanne Dielman", year: 1975, overview: null, tagline: null, runtimeMinutes: 201,
      certification: null, genres: ["drama"], network: null, status: "Released",
      traktRating: null, traktVotes: 0, posterPath: null, backdropPath: null, trailerUrl: null,
      cast: [], directors: [], writers: [], prediction: null, contributionDetails: [],
      userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
    });
    await mockJson(page, "**/api/titles/movie/777/providers", {
      region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/777/watch", providers: [],
    });

    const input = page.getByTestId("search-input");
    await input.fill("jeanne");

    // Local row first, then the fenced global group with its provenance label.
    await expect(page.getByTestId("typeahead-result")).toHaveCount(1);
    await expect(page.getByText("Beyond your catalogue")).toBeVisible();
    // Scoped to the box: the footer's TMDB attribution also contains "the TMDB" text.
    await expect(page.getByTestId("search-box").getByText("via TMDB")).toBeVisible();
    await expect(page.getByTestId("typeahead-global-result")).toHaveCount(2);
    await expect(page.getByTestId("typeahead-global-result").first()).toContainText("Jeanne Dielman");

    // Keyboard: Down past the single local row reaches the first global row (options run
    // titles → people → global, and there are no people here).
    await input.press("ArrowDown");
    await expect(page.getByTestId("typeahead-result")).toHaveAttribute("aria-selected", "true");
    await input.press("ArrowDown");
    await expect(page.getByTestId("typeahead-global-result").first()).toHaveAttribute("aria-selected", "true");

    // Enter takes the active global option and navigates to its title route.
    await input.press("Enter");
    await expect(page).toHaveURL(/\/title\/movie\/777/);
  });
});
