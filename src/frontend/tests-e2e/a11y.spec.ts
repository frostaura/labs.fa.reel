import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * Accessibility on every route (WS-9 task 10, ADD-19): the FULL axe rule set — not only colour
 * contrast — in Chromium and WebKit, on each page in the state it is mocked into here.
 *
 * Each surface owner fixes its own violations, so the pages are held to a baseline that only
 * shrinks (`a11y-baseline.json`, route → rule → node count): a rule a route does not already break,
 * or more nodes breaking one it does, fails the run. Removing a fixed rule from the baseline is the
 * owner's last step; nothing is ever added to it.
 *
 * Every /api call is answered: specific mocks per page, then 404 for anything else, so a page
 * whose data is not mocked is checked in its error state rather than reaching the tripwire.
 */

type Baseline = Record<string, Record<string, number>>;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const BASELINE: Baseline = JSON.parse(readFileSync(path.join(HERE, "a11y-baseline.json"), "utf8"));

const ACCURACY = {
  window: "90d", from: "2026-06-12T00:00:00Z", to: "2026-09-10T00:00:00Z", source: "exposures",
  headline: { withinOnePoint: 0.26, n: 116, meanAbsoluteError: 1.94, bias: 0.37, correlation: -0.004, meanPredicted: 6.81, meanActual: 5.78 },
  calibration: [
    { bucket: 6, low: 6, high: 7, n: 61, meanPredicted: 6.7, meanActual: 5.9 },
    { bucket: 7, low: 7, high: 8, n: 31, meanPredicted: 7.4, meanActual: 5.2 },
  ],
  moments: [],
  conversion: { shown: 40, saved: 6, watched: 3, rated: 3, shownToSaved: 0.15, savedToWatched: 0.5, shownToRated: 0.075, medianDaysToWatch: 4 },
  counterfactual: [],
  learnedThisWeek: null,
};

const FILTERS = {
  excludeGenres: ["horror"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

const hero = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000001${String(n).padStart(2, "0")}`,
  mediaType: "Movie", tmdbId: 9100 + n, name: `Pick ${n}`, year: 2018, runtimeMinutes: 115,
  posterPath: null, backdropPath: null, genres: ["thriller"], predictedRating: 7.2, crowdRating: 7.1,
  crowdVotes: 120000, whyThis: "Close to the thrillers you rate highest.", hook: "A heist goes wrong in a small harbour town.",
  lensClause: null, isReleased: true, confidence: "confident", floorUsed: 6,
});

const TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000t1", mediaType: "Movie", tmdbId: 1001, name: "Quiet Harbor", year: 2024,
  overview: "A harbormaster keeps one last secret.", whyThis: "Shares the slow-burn dread you love.", tagline: null,
  runtimeMinutes: 112, certification: "PG-13", genres: ["drama"], network: null, status: "Released", traktRating: 7.8,
  traktVotes: 12000, posterPath: null, backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [],
  prediction: null, contributionDetails: [],
  userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
};

const saved = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000002${String(n).padStart(2, "0")}`, tmdbId: 9200 + n, name: `Saved ${n}`,
  mediaType: "Movie", year: 2021, posterPath: null, runtimeMinutes: 100, predictedRating: 7.0, crowdRating: 7.4,
  crowdVotes: 12000, providers: [], availableNow: true, savedAt: "2026-08-01T00:00:00Z", daysSaved: 39,
  fitsEveningBudget: true, onManagedList: true, watchTonight: n === 1, watchTonightReason: null,
});

/** Answers every /api call this file does not mock with a 404, and the shell's own calls with data. */
async function shell(page: Page, session: unknown = SESSION_FOUNDER) {
  await stubEventSource(page);
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  if (session) await mockJson(page, "**/api/auth/me", session);
  else await mockJson(page, "**/api/auth/me", {}, 401);
  await mockJson(page, "**/api/sync/status", syncStatus());
}

async function mockHome(page: Page) {
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [1, 2, 3, 4, 5].map(hero), rows: [], lockedRowCount: 0 });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await mockJson(page, "**/api/saved/recently-watched", { expectedAbsoluteError: null, items: [] });
  await mockJson(page, "**/api/settings/filters", FILTERS);
  await mockJson(page, "**/api/accuracy", ACCURACY);
}

interface RouteCase {
  name: string;
  path: string;
  /** What the page needs beyond the shell. */
  mock?: (page: Page) => Promise<void>;
  /** Proof the page rendered its content, so axe checks the page and not an error state. */
  ready?: (page: Page) => Promise<void>;
  session?: unknown;
}

const ROUTES: RouteCase[] = [
  {
    name: "landing",
    path: "/",
    session: null,
    ready: (page) => expect(page.getByRole("button", { name: /sign in with trakt/i })).toBeVisible(),
  },
  { name: "home", path: "/home", ready: (page) => expect(page.getByTestId("hero-card")).toHaveCount(5), mock: mockHome },
  {
    name: "dna",
    path: "/dna",
    ready: (page) => expect(page.getByText("Your taste DNA")).toBeVisible(),
    mock: async (page) => {
      await mockJson(page, "**/api/taste", {
        userMean: 6.7, contrarianScore: 0.2, ratingsCount: 2044, hoursWatched: 4000, titlesWatched: 3353, showCompletionRate: 0.7,
        topGenres: [], eras: [], creators: [], topActors: [], histogram: [{ rating: 7, count: 10 }], drift: [],
      });
      await mockJson(page, "**/api/taste/dislikes", { total: 0, offset: 0, limit: 20, hasMore: false, items: [], profile: null });
    },
  },
  {
    name: "title",
    path: "/title/movie/1001",
    ready: (page) => expect(page.getByTestId("title-detail")).toBeVisible(),
    mock: async (page) => {
      await mockJson(page, "**/api/titles/movie/1001", TITLE);
      await mockJson(page, "**/api/titles/movie/1001/providers", {
        region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch", providers: [],
      });
      await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
    },
  },
  {
    name: "settings",
    path: "/settings",
    ready: (page) => expect(page.getByTestId("prefs-section")).toBeVisible(),
    mock: async (page) => {
      await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
      await mockJson(page, "**/api/preferences/tags", []);
      await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
      await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
      await mockJson(page, "**/api/settings/filters", FILTERS);
    },
  },
  {
    name: "saved",
    path: "/saved",
    ready: (page) => expect(page.getByText("Saved 1")).toBeVisible(),
    mock: async (page) => {
      await mockJson(page, "**/api/saved", {
        managedListUrl: "https://trakt.tv/lists/42", sort: "tonight", eveningBudgetMinutes: 150,
        expectedAbsoluteError: 1.9, unscoredRemaining: 0, items: [1, 2, 3].map(saved),
      });
      await mockJson(page, "**/api/saved/recently-watched", { expectedAbsoluteError: null, items: [] });
    },
  },
  { name: "search", path: "/search", mock: async (page) => mockJson(page, "**/api/settings/filters", FILTERS) },
  {
    name: "track-record",
    path: "/track-record",
    mock: async (page) => mockJson(page, "**/api/accuracy", ACCURACY),
    ready: (page) => expect(page.getByTestId("track-record")).toBeVisible(),
  },
  { name: "lab", path: "/lab" },
  { name: "rate", path: "/rate", mock: async (page) => mockJson(page, "**/api/titles/rate-suggestions", []) },
  { name: "onboarding", path: "/onboarding", session: { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Ingesting" } },
  { name: "person", path: "/person/31" },
];

/** Violations as route-baseline counts: rule id → number of failing nodes. */
async function violations(page: Page) {
  const result = await new AxeBuilder({ page }).analyze();
  return result.violations;
}

for (const route of ROUTES) {
  test(`axe: ${route.name} (${route.path}) breaks no rule beyond its baseline`, async ({ page }) => {
    await shell(page, route.session === undefined ? SESSION_FOUNDER : route.session);
    await route.mock?.(page);
    await page.goto(route.path);
    await route.ready?.(page);
    // Settled: the page fallback is gone and the network is quiet.
    await expect(page.locator("#root")).not.toBeEmpty();
    await expect(page.getByTestId("page-fallback")).toHaveCount(0);
    await page.waitForLoadState("networkidle");

    const found = await violations(page);
    if (process.env.A11Y_RECORD) {
      // Survey mode for an owner checking progress: what this route breaks now, per browser.
      const dir = path.join(test.info().project.outputDir, "a11y-survey");
      mkdirSync(dir, { recursive: true });
      const counts = Object.fromEntries(found.map((v) => [v.id, v.nodes.length]));
      writeFileSync(path.join(dir, `${route.name}.${test.info().project.name}.json`), JSON.stringify(counts));
    }
    const allowed = BASELINE[route.name] ?? {};
    const over = found
      .filter((v) => v.nodes.length > (allowed[v.id] ?? 0))
      .map((v) => ({
        rule: v.id,
        impact: v.impact,
        nodes: v.nodes.length,
        baseline: allowed[v.id] ?? 0,
        help: v.help,
        targets: v.nodes.slice(0, 5).map((n) => n.target.join(" ")),
      }));
    expect(over, `new accessibility violations on ${route.path}`).toEqual([]);
  });
}

// ── Keyboard paths (task 10): navigation, cards and the title modal, in Chromium and WebKit ──────

/** WebKit, like Safari, only Tabs to links with Option held. */
const tabKey = (browserName: string) => (browserName === "webkit" ? "Alt+Tab" : "Tab");

/** Presses Tab until the focused element matches `selector`; fails after `max` presses. */
async function tabTo(page: Page, key: string, selector: string, max = 40): Promise<void> {
  for (let i = 0; i < max; i++) {
    await page.keyboard.press(key);
    if (await page.evaluate((sel) => document.activeElement?.matches(sel) === true, selector)) return;
  }
  throw new Error(`focus never reached ${selector} in ${max} presses`);
}

test.describe("keyboard paths", () => {
  test("the first Tab is a skip link to the page, and the header navigates by keyboard", async ({ page, browserName }) => {
    const key = tabKey(browserName);
    await shell(page);
    await mockJson(page, "**/api/saved", { managedListUrl: null, sort: "tonight", eveningBudgetMinutes: 150, expectedAbsoluteError: 1.9, unscoredRemaining: 0, items: [] });
    await mockJson(page, "**/api/taste", { userMean: 6.7, contrarianScore: 0.2, ratingsCount: 10, hoursWatched: 10, titlesWatched: 10, showCompletionRate: 0.7, topGenres: [], eras: [], creators: [], topActors: [], histogram: [], drift: [] });
    await page.goto("/saved");
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();

    await page.keyboard.press(key);
    const skip = page.getByRole("link", { name: "Skip to content" });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press("Enter");
    await expect(page.locator("main#main")).toBeFocused();

    await page.locator("body").focus();
    await tabTo(page, key, 'header a[aria-label="Taste DNA"]');
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/dna$/);
  });

  test("a card opens the title modal by keyboard; focus stays inside it, and Escape returns to the card", async ({ page, browserName }) => {
    const key = tabKey(browserName);
    await shell(page);
    await mockHome(page);
    await mockJson(page, "**/api/titles/movie/9101", { ...TITLE, tmdbId: 9101, name: "Pick 1" });
    await mockJson(page, "**/api/titles/movie/9101/providers", { region: "US", attribution: "JustWatch", tmdbWatchPage: null, providers: [] });
    await page.goto("/home");
    await expect(page.getByTestId("hero-card")).toHaveCount(5);

    await tabTo(page, key, '[data-testid="hero-card"]');
    const card = page.getByTestId("hero-card").first();
    await expect(card).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/title\/movie\/9101$/);
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest("[role=dialog]"))).toBe(true);

    for (let i = 0; i < 25; i++) {
      await page.keyboard.press(key);
      expect(await page.evaluate(() => !!document.activeElement?.closest("[role=dialog]")), `Tab ${i + 1} left the dialog`).toBe(true);
    }

    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(/\/home$/);
    await expect(dialog).toHaveCount(0);
    await expect(card).toBeFocused();
  });
});

test("with reduced motion asked for, no CSS animation keeps running on Home", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await shell(page);
  await mockHome(page);
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(5);
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          document.getAnimations().filter((a) => {
            const duration = Number(a.effect?.getComputedTiming().duration ?? 0);
            return a.playState === "running" && duration > 1;
          }).length
      )
    )
    .toBe(0);
});
