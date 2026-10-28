import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The search box's lookup (WS-3 task 10):
 * - the global leg: when the LOCAL lookup resolves with fewer than 3 STRONG matches for a 3+ char
 *   term, the box asks `/typeahead/global` and renders TMDB rows under "Beyond your catalogue" —
 *   lookup, never scored, judged by the conditions server-side (FD-5). Keyboard navigation reaches
 *   them and selecting one navigates like a local hit;
 * - Enter with nothing active opens an EXACT match and never starts an Ask Reel turn;
 * - what the conditions hide is counted on a line;
 * - the people group leads when it matches better than any title;
 * - the predicted-rating badge arrives after the rows, from `/typeahead/scores`.
 */

const LOCAL_TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000aa",
  mediaType: "Movie",
  tmdbId: 2002,
  name: "Jeanne Local Match",
  year: 2019,
  posterPath: null,
  isFullyWatched: false,
  predictedRating: null,
  tier: "word",
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
      whyThis: "Explores a heist crew coming apart, like Heat.", isReleased: true,
    }],
    rows: [], lockedRowCount: 0,
  });
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  // The rest of the Home shell, so no call reaches the tripwire (empty answers; 204 = nothing to show).
  await mockJson(page, "**/api/accuracy", {
    window: "90d", from: "2026-07-27T00:00:00Z", to: "2026-10-25T00:00:00Z", source: "none",
    headline: { withinOnePoint: 0, n: 0, meanAbsoluteError: 0, bias: 0, correlation: null, meanPredicted: 0, meanActual: 0 },
    calibration: [], moments: [],
    conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
    counterfactual: [], learnedThisWeek: null,
  });
  await page.route((u) => u.pathname === "/api/feed/learned", (route) => route.fulfill({ status: 204, body: "" }));
  await page.route((u) => u.pathname === "/api/feed/cross-reference", (route) => route.fulfill({ status: 204, body: "" }));
  await mockJson(page, "**/api/saved/recently-watched", { expectedAbsoluteError: 1.9, items: [] });
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  await mockJson(page, "**/api/feed/genres", []);
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });

  // The local leg is sparse (one strong match); the global leg carries the beyond-catalogue rows.
  await page.route(
    (u) => u.pathname === "/api/search/typeahead",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ titles: [LOCAL_TITLE], people: [], strongMatches: 1, hiddenByConditions: 0 }),
    })
  );
  await page.route(
    (u) => u.pathname === "/api/search/typeahead/global",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ global: GLOBAL_ROWS, hiddenByConditions: 0 }),
    })
  );
  await page.route(
    (u) => u.pathname === "/api/search/typeahead/scores",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ scores: { [LOCAL_TITLE.titleId]: 7.5 } }),
    })
  );

  await page.goto("/home");
}

/** Replaces the local leg's answer for the rest of the test. */
async function localLeg(page: Page, payload: object) {
  await page.unroute((u) => u.pathname === "/api/search/typeahead");
  await page.route(
    (u) => u.pathname === "/api/search/typeahead",
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(payload) }),
  );
}

const INCEPTION = {
  titleId: "00000000-0000-0000-0000-0000000000ic", mediaType: "Movie", tmdbId: 27205, name: "Inception",
  year: 2010, posterPath: null, isFullyWatched: false, predictedRating: null, tier: "exact",
};

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

  test("the badge arrives after the rows, from the scores endpoint", async ({ page }) => {
    await bootHome(page);
    await page.getByTestId("search-input").fill("jeanne");
    await expect(page.getByTestId("typeahead-result")).toHaveCount(1);
    await expect(page.getByTestId("typeahead-result")).toContainText("7.5");
  });
});

test.describe("tiered lookup", () => {
  test("Enter on an exact match opens the title and starts no Ask Reel turn", async ({ page }) => {
    await bootHome(page);
    await localLeg(page, { titles: [INCEPTION], people: [], strongMatches: 1, hiddenByConditions: 0 });
    await mockJson(page, "**/api/titles/movie/27205", {
      titleId: INCEPTION.titleId, mediaType: "Movie", tmdbId: 27205, name: "Inception", year: 2010,
      overview: null, tagline: null, runtimeMinutes: 148, certification: null, genres: ["science-fiction"],
      network: null, status: "Released", traktRating: null, traktVotes: 0, posterPath: null, backdropPath: null,
      trailerUrl: null, cast: [], directors: [], writers: [], prediction: null, contributionDetails: [],
      userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
    });
    await mockJson(page, "**/api/titles/movie/27205/providers", {
      region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/27205/watch", providers: [],
    });
    const asks: string[] = [];
    page.on("request", (r) => {
      if (new URL(r.url()).pathname === "/api/search/ask") asks.push(r.url());
    });

    // Typed and entered faster than the debounce: Enter waits for the lookup of exactly this text.
    const input = page.getByTestId("search-input");
    await input.pressSequentially("inception", { delay: 10 });
    await input.press("Enter");

    await expect(page).toHaveURL(/\/title\/movie\/27205/);
    expect(asks).toHaveLength(0);
  });

  test("Enter on anything less than an exact match still asks Reel", async ({ page }) => {
    await bootHome(page);
    await localLeg(page, { titles: [{ ...INCEPTION, name: "Inception Point", tier: "prefix" }], people: [], strongMatches: 1, hiddenByConditions: 0 });
    const input = page.getByTestId("search-input");
    await input.fill("inception");
    await expect(page.getByTestId("typeahead-result")).toHaveCount(1);
    await input.press("Enter");
    await expect(page).toHaveURL(/\/search/);
  });

  test("what the conditions hide is counted on a line", async ({ page }) => {
    await bootHome(page);
    await localLeg(page, { titles: [], people: [], strongMatches: 0, hiddenByConditions: 3 });
    await page.unroute((u) => u.pathname === "/api/search/typeahead/global");
    await page.route(
      (u) => u.pathname === "/api/search/typeahead/global",
      (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ global: [], hiddenByConditions: 1 }) }),
    );
    await page.getByTestId("search-input").fill("the notebook");
    await expect(page.getByTestId("typeahead-hidden")).toHaveText("4 matches hidden by your conditions");
    await expect(page.getByTestId("typeahead-empty")).toContainText("that your conditions allow");
  });

  test("the people group leads when it matches better than any title", async ({ page }) => {
    await bootHome(page);
    await localLeg(page, {
      titles: [{ ...INCEPTION, name: "Norman", tmdbId: 1, tier: "fuzzy" }],
      people: [{ personId: "00000000-0000-0000-0000-0000000000pp", name: "Christopher Nolan", knownFor: "Directing", profilePath: null, tier: "prefix" }],
      strongMatches: 0,
      hiddenByConditions: 0,
    });
    const input = page.getByTestId("search-input");
    await input.fill("nolan");
    await expect(page.getByTestId("typeahead-person")).toBeVisible();
    await input.press("ArrowDown");
    await expect(page.getByTestId("typeahead-person")).toHaveAttribute("aria-selected", "true");
  });
});

test.describe("what the account did with a title (HREV5-RET-05, HREV5-DESK-06)", () => {
  const CHOSEN = {
    ...INCEPTION, titleId: "00000000-0000-0000-0000-0000000000c7", tmdbId: 103768, name: "The Chosen",
    mediaType: "Show", tier: "word", footprint: true, userRating: 7, inProgress: true,
  };
  const CROWN = {
    ...INCEPTION, titleId: "00000000-0000-0000-0000-0000000000c1", tmdbId: 65494, name: "The Crown",
    mediaType: "Show", tier: "exact", footprint: false, userRating: null, inProgress: false,
  };

  test("a rated, half-watched title shows the user's rating and progress, and is never scored", async ({ page }) => {
    await bootHome(page);
    await localLeg(page, { titles: [CROWN, CHOSEN], people: [], strongMatches: 6, hiddenByConditions: 0 });
    const scored: string[] = [];
    page.on("request", (r) => {
      const url = new URL(r.url());
      if (url.pathname === "/api/search/typeahead/scores") scored.push(url.searchParams.get("ids") ?? "");
    });

    await page.getByTestId("search-input").fill("the crown");
    const chosen = page.getByTestId("typeahead-result").filter({ hasText: "The Chosen" });
    await expect(chosen.getByTestId("user-rating")).toContainText("7");
    await expect(chosen.getByTestId("typeahead-watch-mark")).toContainText("in progress");
    await expect(chosen.getByTestId("predicted-rating")).toHaveCount(0);
    await expect.poll(() => scored).toEqual([CROWN.titleId]);
  });

  test("the panel is opaque over the hero grid", async ({ page }) => {
    await bootHome(page);
    await page.getByTestId("search-input").fill("jeanne");
    const panel = page.getByTestId("typeahead-panel");
    await expect(panel).toBeVisible();
    const surface = await panel.evaluate((el) => {
      const style = getComputedStyle(el);
      return { background: style.backgroundColor, backdrop: style.backdropFilter };
    });
    // rgb(...) is opaque; rgba(..., a) with a < 1 is not.
    expect(surface.background).toMatch(/^rgb\(/);
    expect(surface.backdrop === "none" || surface.backdrop === "").toBe(true);
  });
});
