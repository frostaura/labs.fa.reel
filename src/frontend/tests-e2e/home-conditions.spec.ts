import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The conditions dialog on Home says what the conditions govern, and no more (R1-27).
 *
 * It used to promise they applied to "all search"; typeahead does not apply the "for you" minimum
 * (ireview_surface defect 13). The sentence is the shared `CONDITIONS_SCOPE_COPY`; this pins that
 * the dialog the founder actually opens renders it.
 */

const FILTERS = {
  excludeGenres: ["documentary"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

test("the conditions dialog states the search exception instead of claiming all search", async ({ page }) => {
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
  await expect(scope).toContainText("except the “for you” minimum");
  await expect(scope).toContainText("hero and every row");
  await expect(scope).not.toContainText("all search");
});
