import { test, expect, type Browser } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * Home does not shove its picks down once they are on screen (HREV1-D04).
 *
 * At 834×1194 every cold load scored a CLS of 0.133: the conditions chips arrive with the saved
 * conditions, AFTER the first paint, and were inserted above the controls and the hero — one shift
 * of 73 px at ~392 ms. The filters read here is held back on purpose so it lands after the feed,
 * the order that produced it, and the cumulative layout shift is read from the browser's own
 * `layout-shift` entries (the CLS definition: shifts without recent input).
 */

const FILTERS = {
  excludeGenres: ["documentary", "horror", "romance"], includeGenres: [], excludeKeywords: ["zombie"], maturityCeiling: "R",
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};

const hero = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`, mediaType: "Movie", tmdbId: 1000 + n, name: `Pick ${n}`, year: 2026,
  runtimeMinutes: 110, posterPath: null, backdropPath: null, genres: ["drama"], predictedRating: 8.1, crowdRating: 7.5,
  crowdVotes: 9000, whyThis: "Explores a heist crew coming apart, like Heat.", isReleased: true, confidence: "confident", floorUsed: 7.5,
});

async function homeCls(
  browser: Browser,
  context: Parameters<Browser["newContext"]>[0],
  filters: unknown
): Promise<{ cls: number; heroMovedPx: number; heroBeforeFilters: boolean }> {
  const ctx = await browser.newContext(context);
  const page = await ctx.newPage();
  await page.addInitScript(() => {
    (window as unknown as { __cls: number }).__cls = 0;
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
        if (!entry.hadRecentInput) (window as unknown as { __cls: number }).__cls += entry.value;
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [1, 2, 3, 4, 5].map(hero), rows: [], lockedRowCount: 0 });
  let filtersAnswered = false;
  await page.route(
    (u) => u.pathname === "/api/settings/filters",
    async (route) => {
      // Lands well after the feed has painted, as the live read did.
      await new Promise((r) => setTimeout(r, 600));
      filtersAnswered = true;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(filters) });
    }
  );
  await page.goto("/home");
  const first = page.getByTestId("hero-card").first();
  await expect(first).toBeVisible();
  const heroBeforeFilters = !filtersAnswered;
  const before = (await first.boundingBox())!.y;
  await expect.poll(() => filtersAnswered).toBe(true);
  // The chips (or, with no conditions, nothing) have rendered and any shift they cause is recorded.
  await page.waitForTimeout(500);
  const after = (await first.boundingBox())!.y;
  const cls = await page.evaluate(() => (window as unknown as { __cls: number }).__cls);
  await ctx.close();
  return { cls, heroMovedPx: Math.abs(after - before), heroBeforeFilters };
}

const VIEWPORTS = [
  { name: "834 (iPad, touch)", context: { viewport: { width: 834, height: 1194 }, hasTouch: true } },
  { name: "834 (mouse)", context: { viewport: { width: 834, height: 1194 } } },
  { name: "1440", context: { viewport: { width: 1440, height: 900 } } },
  { name: "390 (phone)", context: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true } },
];

test.describe("Home holds its layout while the conditions load (HREV1-D04)", () => {
  for (const { name, context } of VIEWPORTS) {
    for (const [label, filters] of [["the founder's conditions", FILTERS], ["no conditions", { languages: ["*"] }]] as const) {
      test(`CLS stays under 0.02 at ${name} with ${label}`, async ({ browser }) => {
        const { cls, heroMovedPx, heroBeforeFilters } = await homeCls(browser, context, filters);
        // The order that produced the defect: picks on screen first, conditions after.
        expect(heroBeforeFilters).toBe(true);
        // The direct measure: the first pick does not move when the conditions land (it moved 73 px)…
        expect(heroMovedPx, `the first pick moved ${heroMovedPx}px`).toBeLessThanOrEqual(1);
        // …and the browser's own score agrees.
        expect(cls, `cumulative layout shift ${cls.toFixed(3)}`).toBeLessThan(0.02);
      });
    }
  }
});
