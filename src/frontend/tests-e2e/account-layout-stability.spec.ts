import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { FILTER_CHIPS } from "./filterChips.fixture";

/**
 * Settings and Taste DNA hold still while they load (HREV1-D03), and Settings uses the width it is
 * given (HREV1-D07).
 *
 * Measured on the eval stack before the fix: /settings CLS 0.27 at 1440, 0.67 at 834 and 0.84 at
 * 390; /dna 0.22 at 390. Each read drew its own panel the moment it landed — the conditions editor
 * grew from a skeleton into a page-tall form and then twice more as its chips and cost counts
 * arrived, and the taste narrative grew from a 160 px shimmer above the stats — so everything below
 * jumped once per read. WS-10's bar is CLS ≤ 0.05 at p95 at 390, 834 and 1440.
 *
 * The reads answer here in the order and roughly the spacing the eval stack showed (the session and
 * status first, the saved rules, then the chips, then the cost after the meter's 350 ms debounce;
 * the DNA payload before the narrative and the dislikes), with fixtures shaped like the founder's:
 * eight excluded genres, a crowd floor and a year floor all costing titles, three suggestions, a long
 * narrative and a page of dislikes. Layout-shift entries are a Chromium API, so the CLS tests run
 * there; the width tests run in both engines.
 */

const VIEWPORTS = [
  { width: 390, height: 844 },
  { width: 834, height: 1194 },
  { width: 1440, height: 900 },
] as const;
const CLS_BAR = 0.05;

const FILTERS = {
  excludeGenres: ["romance", "music", "animation", "documentary", "comedy", "history", "anime", "western"],
  includeGenres: [],
  excludeKeywords: ["gore"],
  maturityCeiling: null,
  minPredictedRating: 6,
  languages: ["en"],
  minCrowdRating: 6,
  minYear: 2000,
  maxYear: null,
};

const COST = {
  total: 5319,
  eligible: 421,
  byRule: [
    ...FILTERS.excludeGenres.map((slug, i) => ({ kind: "ExcludeGenre", value: slug, marginal: 40 + i * 25 })),
    { kind: "MinCrowdRating", value: "6.0", marginal: 610 },
    { kind: "MinYear", value: "2000", marginal: 380 },
    { kind: "Language", value: "en", marginal: 940 },
  ],
  suggestions: [
    { kind: "Language", value: "en", gain: 940 },
    { kind: "MinCrowdRating", value: "6.0", gain: 610 },
    { kind: "MinYear", value: "2000", gain: 380 },
  ],
};

const SETTINGS = { region: "ZA", onboarded: true, email: "founder@example.com", eveningBudgetMinutes: 150, timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" };

const DNA = {
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
  ],
  eras: [
    { decade: 1990, affinity: 7.0, count: 210 },
    { decade: 2000, affinity: 6.9, count: 520 },
    { decade: 2010, affinity: 6.7, count: 801 },
  ],
  creators: [{ name: "Denis Villeneuve", profilePath: null, affinity: 8.6 }],
  topActors: [],
  histogram: [{ rating: 7, count: 400 }, { rating: 8, count: 300 }],
  drift: [],
  clusters: [],
};

const NARRATIVE = {
  text: "You rate slow, deliberate thrillers and morally grey crime drama well above your own average, and you are harder on broad comedy than the crowd is. Science fiction lands when it is about people rather than spectacle. You finish most of the shows you start, but you drop long-running procedurals early, and you have said no to romance and musicals often enough that Reel keeps them out of the way. ".repeat(2),
  isEdited: false,
  version: 3,
  ratingsCount: 2044,
  generatedAt: "2026-10-20T08:00:00Z",
  isStale: false,
  regenerating: false,
  writerAvailable: true,
};

const DISLIKES = {
  total: 6,
  offset: 0,
  limit: 20,
  hasMore: false,
  items: Array.from({ length: 6 }, (_, i) => ({
    titleId: `00000000-0000-0000-0000-0000000000${10 + i}`,
    mediaType: "Movie",
    tmdbId: 100 + i,
    name: `Dismissed title ${i + 1}`,
    year: 2015 + i,
    posterPath: null,
    kind: "NotInterested",
    reason: "Genre",
    createdAt: "2026-10-10T08:00:00Z",
  })),
  profile: { windowDays: 180, activeCount: 6, genres: [{ genre: "romance", weight: 0.6 }], tones: [] },
};

/** Mocks a GET with a JSON body that answers after `ms`, so the reads land in a realistic order. */
async function mockSlow(page: Page, path: string, body: unknown, ms: number): Promise<void> {
  await page.route(
    (u) => u.pathname === path,
    async (route) => {
      if (route.request().method() !== "GET" && route.request().method() !== "POST") return route.fallback();
      await new Promise((resolve) => setTimeout(resolve, ms));
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    },
  );
}

async function bootShell(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockSlow(page, "/api/sync/status", syncStatus({ lastDeltaSyncAt: "2026-10-27T06:00:00Z" }), 80);
  await page.addInitScript(() => {
    const w = window as unknown as { __cls: number };
    w.__cls = 0;
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
        if (!entry.hadRecentInput) w.__cls += entry.value;
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
}

async function bootSettings(page: Page) {
  await bootShell(page);
  await mockSlow(page, "/api/billing/status", { tier: "Founder", configured: false }, 90);
  await mockSlow(page, "/api/preferences/tags", [{ id: "00000000-0000-0000-0000-0000000000a1", text: "slow burn" }], 150);
  await mockSlow(page, "/api/settings", SETTINGS, 120);
  await mockSlow(page, "/api/settings/filters", FILTERS, 300);
  await mockSlow(page, "/api/search/filter-chips", FILTER_CHIPS, 450);
  await mockSlow(page, "/api/settings/filters/cost", COST, 250);
}

async function bootDna(page: Page) {
  await bootShell(page);
  await mockSlow(page, "/api/taste", DNA, 400);
  await mockSlow(page, "/api/taste/narrative", NARRATIVE, 700);
  await mockSlow(page, "/api/taste/dislikes", DISLIKES, 900);
}

async function settledCls(page: Page): Promise<number> {
  // Past the last read (cost at ≈1.1 s after its debounce) with room to spare for a late paint.
  await page.waitForTimeout(2500);
  return page.evaluate(() => (window as unknown as { __cls: number }).__cls);
}

test.describe("layout stability while loading (CLS ≤ 0.05)", () => {
  for (const viewport of VIEWPORTS) {
    test(`/settings holds still at ${viewport.width}×${viewport.height}`, async ({ page, browserName }) => {
      test.skip(browserName !== "chromium", "layout-shift entries are a Chromium API");
      await page.setViewportSize(viewport);
      await bootSettings(page);
      await page.goto("/settings");
      await expect(page.getByTestId("filter-cost-line")).toHaveText(/These rules leave 421 of 5,319 titles/);
      await expect(page.getByTestId("settings-trakt")).toBeVisible();
      expect(await settledCls(page)).toBeLessThanOrEqual(CLS_BAR);
    });

    test(`/dna holds still at ${viewport.width}×${viewport.height}`, async ({ page, browserName }) => {
      test.skip(browserName !== "chromium", "layout-shift entries are a Chromium API");
      await page.setViewportSize(viewport);
      await bootDna(page);
      await page.goto("/dna");
      await expect(page.getByTestId("narrative-text")).toBeVisible();
      await expect(page.getByTestId("genre-fingerprint")).toBeVisible();
      expect(await settledCls(page)).toBeLessThanOrEqual(CLS_BAR);
    });
  }

  test("a cost read that never answers does not hold the conditions back", async ({ page }) => {
    await bootShell(page);
    await mockSlow(page, "/api/billing/status", { tier: "Founder", configured: false }, 0);
    await mockSlow(page, "/api/preferences/tags", [], 0);
    await mockSlow(page, "/api/settings", SETTINGS, 0);
    await mockSlow(page, "/api/settings/filters", FILTERS, 0);
    await mockSlow(page, "/api/search/filter-chips", FILTER_CHIPS, 0);
    // The meter's request is left hanging for good.
    await page.route((u) => u.pathname === "/api/settings/filters/cost", () => undefined);
    await page.goto("/settings");
    // Drawn at the cap, without the counts, and the rest of the page with it.
    await expect(page.getByTestId("prefs-section")).toBeVisible({ timeout: 4000 });
    await expect(page.getByTestId("filter-cost-line")).toHaveCount(0);
    await expect(page.getByTestId("settings-region")).toBeVisible();
  });
});

test.describe("Settings uses the width (HREV1-D07)", () => {
  test("at 1440 the page spans the shell like Home does, with the conditions beside the account cards", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await bootSettings(page);
    await page.goto("/settings");
    await expect(page.getByTestId("settings-trakt")).toBeVisible();

    const main = await page.locator("main").evaluate((m) => {
      const style = getComputedStyle(m);
      const box = m.getBoundingClientRect();
      return { left: box.left + parseFloat(style.paddingLeft), right: box.right - parseFloat(style.paddingRight) };
    });
    const editor = (await page.getByTestId("prefs-section").boundingBox())!;
    const trakt = (await page.getByTestId("settings-trakt").boundingBox())!;
    const region = (await page.getByTestId("settings-region").boundingBox())!;
    // The content reaches the shell's right edge (it stopped at 1056 of 1408 before).
    expect(Math.max(editor.x + editor.width, trakt.x + trakt.width)).toBeGreaterThanOrEqual(main.right - 1);
    expect(editor.x).toBeLessThanOrEqual(main.left + 1);
    // Side by side: the account cards sit to the right of the editor, starting at its top.
    expect(trakt.x).toBeGreaterThanOrEqual(editor.x + editor.width);
    expect(Math.abs(trakt.y - editor.y)).toBeLessThanOrEqual(1);
    expect(region.x).toBe(trakt.x);
  });

  test("at 834 the conditions card fills the content width", async ({ page }) => {
    await page.setViewportSize({ width: 834, height: 1194 });
    await bootSettings(page);
    await page.goto("/settings");
    await expect(page.getByTestId("settings-trakt")).toBeVisible();
    const main = await page.locator("main").evaluate((m) => {
      const style = getComputedStyle(m);
      const box = m.getBoundingClientRect();
      return { left: box.left + parseFloat(style.paddingLeft), right: box.right - parseFloat(style.paddingRight) };
    });
    for (const id of ["prefs-section", "settings-trakt", "settings-region"]) {
      const box = (await page.getByTestId(id).boundingBox())!;
      expect(box.x, id).toBeLessThanOrEqual(main.left + 1);
      expect(box.x + box.width, id).toBeGreaterThanOrEqual(main.right - 1);
    }
  });
});
