import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { CONDITIONS_SCOPE_COPY } from "../src/lib/conditionsCopy";

/**
 * The conditions dialog on Home says what the conditions govern, and no more (R1-27).
 *
 * It used to promise they applied to "all search" (R1-27), then that search applied them "except the
 * “for you” minimum" (IR1S-06) — but the typeahead's global leg applies none of them, so The
 * Notebook surfaces for an account excluding romance. The sentence is the shared
 * `CONDITIONS_SCOPE_COPY`; this pins that the dialog the founder actually opens renders exactly it,
 * whatever the saved conditions are (the copy must not vary with them).
 */

const FIXTURES = {
  // A floor plus a genre exclusion — the shape of the founder's own conditions.
  "a floor and a genre exclusion": {
    excludeGenres: ["documentary"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  },
  // No floor at all, but the exact rules IR1S-06 showed the global lookup ignoring.
  "no floor; romance, pre-2000 and non-English excluded": {
    excludeGenres: ["romance"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: 2000, maxYear: null,
  },
};

for (const [label, FILTERS] of Object.entries(FIXTURES)) {
  test(`the conditions dialog says top-bar search can show excluded titles (${label})`, async ({ page }) => {
    await stubEventSource(page);
    // Registered first, so every specific mock below wins: nothing reaches the dev proxy.
    await page.route(
      (u) => u.pathname.startsWith("/api/"),
      (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
    );
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/settings/filters", FILTERS);
    await mockJson(page, "**/api/feed", {
      generatedAt: new Date().toISOString(),
      hero: [{
        titleId: "00000000-0000-0000-0000-000000000c01", mediaType: "Movie", tmdbId: 7001, name: "Heat", year: 1995,
        runtimeMinutes: 170, posterPath: null, backdropPath: null, genres: ["crime"], predictedRating: 8.2,
        crowdRating: 8.3, crowdVotes: 90000, whyThis: "Because of the films you rate highest.", isReleased: true,
        confidence: "confident", floorUsed: 7.5,
      }],
      rows: [],
      lockedRowCount: 0,
    });
    await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
    await page.goto("/home");

    await page.getByTestId("open-conditions").click();
    const scope = page.getByTestId("conditions-scope");
    await expect(scope).toHaveText(`${CONDITIONS_SCOPE_COPY} Saving re-cuts the feed straight away.`);
    await expect(scope).toContainText("can show titles your conditions exclude");
    await expect(scope).not.toContainText("all search");
    await expect(scope).not.toContainText("Search applies them too");
  });
}
