import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The hero honesty label.
 *
 * The build needs a 7.5 predicted rating to lead the hero and backfills the row from the best of
 * what is left when nothing clears it — silently. Live, that backfill fills the whole row: 31 of 470
 * scored titles reach 7.5 and the founder's hero is network procedurals at 6.6–7.0 under the heading
 * "Tonight's picks". These specs pin that the reach is now visible on the card that is reaching, that
 * a pick which cleared the bar carries no caveat, and that the row-level band appears only when the
 * whole row is a reach.
 */

const heroCard = (n: number, extra: Record<string, unknown>) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 3000 + n,
  name: `Hero ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Strong fit with your taste profile.",
  isReleased: true,
  ...extra,
});

async function bootHome(page: Page, hero: unknown[]) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero,
    rows: [],
    lockedRowCount: 0,
  });
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(hero.length);
}

test.describe("hero honesty label", () => {
  test("a 6.6 pick says it is reaching, and names the bar it fell short of", async ({ page }) => {
    await bootHome(page, [heroCard(1, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 })]);

    await expect(page.getByTestId("hero-reaching-label")).toHaveText(
      "Reaching a bit — nothing cleared my usual 7.5 bar tonight",
    );
  });

  test("an 8.1 pick carries no caveat at all", async ({ page }) => {
    await bootHome(page, [heroCard(2, { predictedRating: 8.1, confidence: "confident", floorUsed: 7.5 })]);

    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(0);
    await expect(page.getByTestId("hero-reaching-note")).toHaveCount(0);
  });

  test("the whole row reaching is said once, above the row, as well as on each card", async ({ page }) => {
    // The live shape: five picks, none of them over the bar, under "Tonight's picks".
    await bootHome(page, [
      heroCard(1, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(2, { predictedRating: 6.7, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(3, { predictedRating: 6.9, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(4, { predictedRating: 7.0, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(5, { predictedRating: 7.0, confidence: "reaching", floorUsed: 7.5 }),
    ]);

    await expect(page.getByTestId("hero-reaching-note")).toHaveText(
      "Nothing cleared your usual 7.5 bar tonight — these are the best of what's left, ranked honestly.",
    );
    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(5);
  });

  test("one pick over the bar retires the row-level band but not that pick's neighbours' labels", async ({ page }) => {
    await bootHome(page, [
      heroCard(1, { predictedRating: 8.4, confidence: "confident", floorUsed: 7.5 }),
      heroCard(2, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 }),
    ]);

    await expect(page.getByTestId("hero-reaching-note")).toHaveCount(0);
    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(1);
  });
});
