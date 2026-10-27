import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The hero honesty label.
 *
 * The build needs a 7.5 predicted rating to lead the hero and backfills the row from the best of
 * what is left when nothing clears it — silently. Live, that backfill fills the whole row: 31 of 470
 * scored titles reach 7.5 and the founder's hero is network procedurals at 6.6–7.0 under the heading
 * "Tonight's picks". These specs pin that the reach is now visible on the card that is reaching, that
 * a pick which cleared the bar carries no caveat, and that when the whole row is a reach it is said
 * ONCE, on Home's status line, and not again on every card (FD-15, O-6).
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
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
  ...extra,
});

async function bootHome(page: Page, hero: unknown[], minPredictedRating = 6, feedStatus = 200) {
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
    minPredictedRating, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  const snapshot = { generatedAt: new Date().toISOString(), hero, rows: [], lockedRowCount: 0 };
  let feedReads = 0;
  await page.route((u) => u.pathname === "/api/feed", (route) => {
    feedReads += 1;
    // `feedStatus` fails the FIRST read only, so a retry can land the snapshot.
    return feedReads === 1 && feedStatus !== 200
      ? route.fulfill({ status: feedStatus, contentType: "application/json", body: "{}" })
      : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(snapshot) });
  });
  await page.goto("/home");
  if (feedStatus === 200) await expect(page.getByTestId("hero-card")).toHaveCount(hero.length);
}

test.describe("hero honesty label", () => {
  test("a 6.6 pick beside one that cleared the bar says it is reaching, and names the bar", async ({ page }) => {
    await bootHome(page, [
      heroCard(1, { predictedRating: 8.2, confidence: "confident", floorUsed: 7.5 }),
      heroCard(2, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 }),
    ]);

    await expect(page.getByTestId("hero-reaching-label")).toHaveText(
      "Reaching a bit — nothing cleared my usual 7.5 bar tonight",
    );
  });

  test("an 8.1 pick carries no caveat at all", async ({ page }) => {
    await bootHome(page, [heroCard(2, { predictedRating: 8.1, confidence: "confident", floorUsed: 7.5 })]);

    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(0);
    await expect(page.getByTestId("hero-reaching-note")).toHaveCount(0);
  });

  test("the whole row reaching is said once, on the status line, and on no card", async ({ page }) => {
    // The live shape: five picks, none of them over the bar, under "Tonight's picks".
    await bootHome(page, [
      heroCard(1, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(2, { predictedRating: 6.7, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(3, { predictedRating: 6.9, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(4, { predictedRating: 7.0, confidence: "reaching", floorUsed: 7.5 }),
      heroCard(5, { predictedRating: 7.0, confidence: "reaching", floorUsed: 7.5 }),
    ]);

    await expect(page.getByTestId("hero-reaching-note")).toHaveText("Nothing cleared my usual 7.5 bar tonight");
    await expect(page.getByTestId("hero-reaching-note")).toHaveCount(1);
    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(0);
    // The status line sits above the picks it is about.
    const note = await page.getByTestId("hero-reaching-note").boundingBox();
    const card = await page.getByTestId("hero-card").first().boundingBox();
    expect(note!.y + note!.height).toBeLessThanOrEqual(card!.y);
  });

  test("the row's note says 'your' only for the account's own bar (HREV1-D01)", async ({ page }) => {
    // The founder's only bar is For you ≥ 6.0; the 7.5 the picks missed is Reel's. The note used
    // to say "your usual 7.5 bar". An account that raised its own minimum to 8.5 does own that bar.
    await bootHome(page, [heroCard(1, { predictedRating: 7.9, confidence: "reaching", floorUsed: 8.5 })], 8.5);
    await expect(page.getByTestId("hero-reaching-note")).toHaveText("Nothing cleared your 8.5 bar tonight");
  });

  test("a lone reaching pick is the whole row: the status line says it, the card does not repeat it", async ({ page }) => {
    await bootHome(page, [heroCard(1, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 })]);

    await expect(page.getByTestId("hero-reaching-note")).toHaveText("Nothing cleared my usual 7.5 bar tonight");
    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(0);
  });

  test("one pick over the bar retires the row-level band but not that pick's neighbours' labels", async ({ page }) => {
    await bootHome(page, [
      heroCard(1, { predictedRating: 8.4, confidence: "confident", floorUsed: 7.5 }),
      heroCard(2, { predictedRating: 6.6, confidence: "reaching", floorUsed: 7.5 }),
    ]);

    await expect(page.getByTestId("hero-reaching-note")).toHaveCount(0);
    await expect(page.getByTestId("hero-reaching-label")).toHaveCount(1);
  });
  test("a failed snapshot read says so above the live picks it falls back to, and Retry lands the snapshot (HREV1-D12)", async ({ page }) => {
    await bootHome(page, [heroCard(1, { predictedRating: 8.2, confidence: "confident", floorUsed: 7.5 })], 6, 500);
    const failed = page.getByTestId("feed-load-failed");
    await expect(failed).toContainText("Couldn't load tonight's picks — showing live picks instead");
    // No stamp claims these were ranked for you tonight.
    await expect(page.getByText(/ranked for you ·/)).toHaveCount(0);

    await page.getByTestId("feed-load-failed-retry").click();
    await expect(failed).toHaveCount(0);
    await expect(page.getByText(/ranked for you ·/)).toBeVisible();
    await expect(page.getByTestId("hero-card")).toHaveCount(1);
  });
});
