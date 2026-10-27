import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * PH2-02: Recharts 3 draws every chart with `accessibilityLayer` on, which makes its SVG a
 * `role="application"` with `tabindex="0"` and no name. On /dna and /track-record that put three
 * unnamed application regions in the tab order — WebKit's default Tab, which skips buttons and
 * links, still stopped on both /dna charts — and a screen reader entering one switches how keys
 * behave. Axe has no rule for it, so this walks the keyboard path itself.
 *
 * What each chart must be instead: one `role="img"` whose name reads the chart's own numbers, with
 * nothing inside it focusable.
 */

const TASTE = {
  ratingsCount: 2044,
  titlesWatched: 3353,
  hoursWatched: 4210,
  showCompletionRate: 0.62,
  contrarianScore: -0.2,
  userMean: 6.8,
  topGenres: [
    { genre: "drama", affinity: 7.2, count: 980 },
    { genre: "science-fiction", affinity: 6.9, count: 260 },
  ],
  eras: [{ decade: 2010, affinity: 6.7, count: 801 }],
  creators: [],
  topActors: [],
  histogram: [
    { rating: 3, count: 1 },
    { rating: 8, count: 300 },
  ],
  drift: [
    { year: 2019, shares: { drama: 0.3, "science-fiction": 0.1 } },
    { year: 2024, shares: { drama: 0.25, "science-fiction": 0.18 } },
  ],
  clusters: [],
};

const ACCURACY = {
  window: "90d",
  from: "2026-06-12T00:00:00Z",
  to: "2026-09-10T00:00:00Z",
  source: "exposures",
  headline: { withinOnePoint: 0.26, n: 73, meanAbsoluteError: 1.94, bias: 0.37, correlation: -0.004, meanPredicted: 6.81, meanActual: 5.78 },
  calibration: [
    { bucket: 6, low: 6, high: 7, n: 61, meanPredicted: 6.7, meanActual: 5.9 },
    { bucket: 8, low: 8, high: 9, n: 12, meanPredicted: 8.6, meanActual: 4.4 },
    { bucket: 9, low: 9, high: 10, n: 0, meanPredicted: null, meanActual: null },
  ],
  moments: [],
  conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
  counterfactual: [],
  learnedThisWeek: null,
};

async function shell(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
}

async function bootDna(page: Page) {
  await shell(page);
  await mockJson(page, "**/api/taste", TASTE);
  await mockJson(page, "**/api/taste/dislikes", {
    total: 0, offset: 0, limit: 20, hasMore: false, items: [],
    profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
  });
  await mockJson(page, "**/api/taste/narrative", {
    text: null, isEdited: false, version: null, ratingsCount: 0, generatedAt: null,
    isStale: false, regenerating: false, writerAvailable: false,
  });
  await page.goto("/dna");
  // The SVG itself, so the walk below starts after Recharts has drawn (it measures before it paints).
  await expect(page.locator('[data-testid="rating-histogram-chart"] svg').first()).toBeVisible();
  await expect(page.locator('[data-testid="drift-chart"] svg').first()).toBeVisible();
}

async function bootTrackRecord(page: Page) {
  await shell(page);
  await mockJson(page, "**/api/accuracy", ACCURACY);
  await mockJson(page, "**/api/metrics/cross-reference", {
    window: "90d",
    population: { name: "x", selectionNote: "y" },
    crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
  });
  await page.route("**/api/feed/cross-reference", (route) => route.fulfill({ status: 204 }));
  await page.goto("/track-record");
  await expect(page.locator('[data-testid="calibration-chart"] svg').first()).toBeVisible();
}

/** Nothing on the page is an application region, and nothing inside a chart can take focus. */
async function expectNoChartStops(page: Page, charts: string[]) {
  await expect(page.locator('[role="application"]')).toHaveCount(0);
  for (const id of charts) {
    await expect(page.locator(`[data-testid="${id}"] [tabindex]:not([tabindex="-1"])`)).toHaveCount(0);
  }

  // The keyboard path, walked: Tab through the whole page and record every stop inside a chart.
  // Each stop is marked as it is reached, so the walk ends when focus comes back to one it has seen
  // (a position or a selector is not stable: the skip link moves when it takes focus). Bounded.
  await page.locator("body").click({ position: { x: 1, y: 1 } });
  const selector = charts.map((id) => `[data-testid="${id}"]`).join(",");
  const stops: string[] = [];
  for (let i = 0; i < 150; i++) {
    await page.keyboard.press("Tab");
    const here = await page.evaluate((sel) => {
      const el = document.activeElement as HTMLElement | SVGElement | null;
      if (!el || el === document.body) return { seen: false, inChart: null, tag: "body" };
      const seen = el.dataset.kbWalk === "1";
      el.dataset.kbWalk = "1";
      const chart = el.closest(sel);
      return { seen, inChart: chart ? chart.getAttribute("data-testid") : null, tag: el.tagName.toLowerCase() };
    }, selector);
    if (here.seen) break;
    if (here.inChart) stops.push(`${here.inChart}: ${here.tag}`);
  }
  expect(stops).toEqual([]);
}

test.describe("charts on the keyboard path (PH2-02)", () => {
  test("/dna: both charts are named images a Tab never stops in", async ({ page }) => {
    await bootDna(page);

    await expect(page.getByTestId("drift-chart")).toHaveAttribute("role", "img");
    await expect(page.getByRole("img", { name: "Taste drift, 2019 to 2024, as a share of your loved ratings: drama 30% to 25%, science fiction 10% to 18%." })).toBeVisible();
    await expect(page.getByTestId("rating-histogram-chart")).toHaveAttribute("role", "img");
    await expect(page.getByRole("img", { name: "How you rate, ratings given at each score: 3, 1 rating; 8, 300 ratings. Your mean is 6.8." })).toBeVisible();

    await expectNoChartStops(page, ["drift-chart", "rating-histogram-chart"]);
  });

  test("/track-record: the calibration chart is a named image a Tab never stops in", async ({ page }) => {
    await bootTrackRecord(page);

    await expect(page.getByTestId("calibration-chart")).toHaveAttribute("role", "img");
    await expect(
      page.getByRole("img", {
        name:
          "Where the number lands, Reel's average prediction against your average rating in each band: " +
          "6 to 7, Reel said 6.7, you said 5.9, from 61 ratings; 8 to 9, Reel said 8.6, you said 4.4, from 12 ratings.",
      })
    ).toBeVisible();

    await expectNoChartStops(page, ["calibration-chart"]);
  });
});
