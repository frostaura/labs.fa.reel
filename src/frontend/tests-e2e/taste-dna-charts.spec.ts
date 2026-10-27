import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The Taste DNA charts carry information (P1, G-12). On live every genre sat within ±0.4 of a 6.8
 * mean, and a 0–10 fill drew all eight at 65–72% of the track: a chart that said nothing. The page
 * now diverges from the mean on a scale fitted to the spread, so the largest gap reaches the edge.
 * The fixture is that live shape.
 */
const FOUNDER_SHAPED = {
  ratingsCount: 2044,
  titlesWatched: 3353,
  hoursWatched: 4210,
  showCompletionRate: 0.62,
  contrarianScore: -0.2,
  userMean: 6.8,
  topGenres: [
    { genre: "drama", affinity: 7.2, count: 980 },
    { genre: "thriller", affinity: 7.0, count: 512 },
    { genre: "crime", affinity: 7.1, count: 403 },
    { genre: "science-fiction", affinity: 6.9, count: 260 },
    { genre: "comedy", affinity: 6.5, count: 455 },
    { genre: "action", affinity: 6.6, count: 391 },
    { genre: "horror", affinity: 6.7, count: 120 },
    { genre: "romance", affinity: 6.8, count: 98 },
  ],
  eras: [
    { decade: 1990, affinity: 7.0, count: 210 },
    { decade: 2000, affinity: 6.9, count: 520 },
    { decade: 2010, affinity: 6.7, count: 801 },
  ],
  creators: [],
  topActors: [],
  histogram: [{ rating: 8, count: 300 }],
  drift: [],
  clusters: [],
};

async function bootCharts(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/taste", FOUNDER_SHAPED);
  await mockJson(page, "**/api/taste/dislikes", {
    total: 0, offset: 0, limit: 20, hasMore: false, items: [],
    profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
  });
  await mockJson(page, "**/api/taste/narrative", {
    text: null, isEdited: false, version: null, ratingsCount: 0, generatedAt: null,
    isStale: false, regenerating: false, writerAvailable: false,
  });
  await page.goto("/dna");
  await expect(page.getByTestId("genre-fingerprint")).toBeVisible();
}

test.describe("taste DNA charts", () => {
  test("the largest genre bar spans at least 40% of the chart, and bars leave the mean both ways", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await bootCharts(page);

    const tracks = page.getByTestId("genre-fingerprint-track");
    await expect(tracks).toHaveCount(8);
    const trackBox = await tracks.first().boundingBox();
    const widths = await page
      .getByTestId("genre-fingerprint-bar")
      .evaluateAll((bars) => bars.map((b) => b.getBoundingClientRect().width));
    expect(Math.max(...widths) / trackBox!.width).toBeGreaterThanOrEqual(0.4);

    // Drama (+0.4) goes right of the mean, comedy (−0.3) left of it; romance sits on the mean.
    await expect(page.locator('[data-testid="genre-fingerprint-bar"][data-side="above"]')).toHaveCount(4);
    await expect(page.locator('[data-testid="genre-fingerprint-bar"][data-side="below"]')).toHaveCount(3);
    await expect(page.getByTestId("genre-fingerprint-scale")).toContainText("0.40 points");
    // Counts are whole numbers.
    await expect(page.getByTestId("genre-fingerprint-row").first()).toContainText("980 rated");
    await expect(page.getByTestId("genre-fingerprint-row").first()).not.toContainText("980.0");
    await expect(page.getByTestId("era-fingerprint-row")).toHaveCount(3);
  });

  test("on a phone each label sits above its bar, and nothing overflows", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await bootCharts(page);

    // Measured inside a poll (WS-9 CR-7): one measurement taken while the chart was still laying
    // out failed once in a full run and never alone.
    const row = page.getByTestId("genre-fingerprint-row").first();
    await expect
      .poll(async () => {
        const label = await row.locator("span").first().boundingBox();
        const track = await row.getByTestId("genre-fingerprint-track").boundingBox();
        if (!label || !track) return "not laid out";
        if (label.y + label.height > track.y) return `label bottom ${label.y + label.height} below track top ${track.y}`;
        // The bar gets most of the row rather than what a fixed label column leaves.
        if (track.width <= 150) return `track ${track.width} px wide`;
        return "ok";
      })
      .toBe("ok");

    // Against the layout width, not innerWidth (WS-9 CR-9, as landing.spec): WebKit draws a classic
    // vertical scrollbar, so its content box is narrower than the window (380 of 390) and scrollWidth
    // matches that, while Chromium's overlay scrollbar leaves the two equal.
    const { scrollWidth, clientWidth, innerWidth } = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
      innerWidth: window.innerWidth,
    }));
    expect(scrollWidth).toBe(clientWidth);
    expect(clientWidth).toBeLessThanOrEqual(innerWidth);
  });
});
