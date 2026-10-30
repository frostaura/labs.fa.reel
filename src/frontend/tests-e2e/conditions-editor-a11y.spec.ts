import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, axeScan, mockJson, stubEventSource } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The conditions editor passes the FULL axe rule set when it opens from Home with the genre chips
 * carrying their annotations (HREV8-A11Y-01).
 *
 * An excluded chip's marginal count ("+410") sat at opacity 0.8 over the chip's 15 % danger tint on
 * the dialog's ink-2 surface: #F08484 painted at 4.05–4.24:1 at 11 px, under AA, in both browsers
 * and both viewports. The same chips passed on /settings, whose surface is different, and the repo's
 * a11y gate never opened this dialog with a marginal on an excluded genre, so nothing saw it.
 *
 * The cases vary what the chips carry, because a contrast rule only judges text that renders:
 * - many excluded genres, each with a marginal (the reported shape);
 * - excluded genres under the catalogue bar, which add the "(matches nothing…)" / "(only …)" note
 *   — at opacity 0.7 on the danger tint that note was 3.44:1, the same defect one span over;
 * - included genres, one of them under the bar, beside an excluded one (the success tint).
 * The whole dialog is scanned with no exclusion, in Chromium and WebKit (the two projects), at a
 * desktop and a phone viewport, with reduced motion so axe samples colours as finally painted.
 */

const SYNC = {
  pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
  lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
};

const BASE = {
  excludeGenres: [] as string[], includeGenres: [] as string[], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

type Filters = typeof BASE;

const FIXTURES: Record<string, { filters: Filters; marked: string[] }> = {
  "eight excluded genres, each with a marginal count": {
    filters: {
      ...BASE,
      excludeGenres: ["animation", "anime", "comedy", "documentary", "history", "music", "musical", "romance"],
    },
    marked: ["genre-marginal-animation", "genre-marginal-comedy", "genre-marginal-romance"],
  },
  "excluded genres under the catalogue bar, with their notes and marginals": {
    filters: { ...BASE, excludeGenres: ["sports", "talk-show", "horror"] },
    marked: ["genre-below-bar-sports", "genre-below-bar-talk-show", "genre-marginal-talk-show", "genre-marginal-horror"],
  },
  "included genres, one under the bar, beside an excluded one": {
    filters: { ...BASE, includeGenres: ["crime", "news"], excludeGenres: ["western"] },
    marked: ["genre-below-bar-news", "genre-marginal-western"],
  },
};

const VIEWPORTS = {
  desktop: { viewport: { width: 1440, height: 900 } },
  phone: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true },
} as const;

/** A positive marginal for every excluded genre, so every excluded chip shows its count. */
function costFor(filters: Filters) {
  return {
    total: 5000,
    eligible: 400,
    byRule: filters.excludeGenres.map((slug, i) => ({ kind: "ExcludeGenre", value: slug, marginal: 410 + i * 37 })),
    suggestions: [],
  };
}

async function bootHome(page: Page, filters: Filters) {
  await stubEventSource(page);
  // Registered first, so every specific mock below wins: nothing reaches the dev proxy.
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC);
  await mockJson(page, "**/api/settings/filters", filters);
  await mockJson(page, "**/api/settings/filters/cost", costFor(filters));
  await mockFilterChips(page);
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
}

function summarise(result: Awaited<ReturnType<ReturnType<typeof axeScan>["analyze"]>>) {
  return result.violations.map((v) => ({
    rule: v.id,
    nodes: v.nodes.map((n) => `${n.target.join(" ")} — ${n.failureSummary ?? ""}`),
  }));
}

for (const [viewportName, viewportOptions] of Object.entries(VIEWPORTS)) {
  test.describe(`${viewportName}`, () => {
    test.use({ ...viewportOptions, contextOptions: { reducedMotion: "reduce" } });

    for (const [label, { filters, marked }] of Object.entries(FIXTURES)) {
      test(`the conditions editor opened from a Home chip passes full axe (${label})`, async ({ page }) => {
        await bootHome(page, filters);
        await page.getByTestId("condition-chip").first().click();
        const dialog = page.getByRole("dialog");
        await expect(dialog).toBeVisible();
        // The annotations under test rendered: the scan judges the chips as a person reads them.
        for (const id of marked) await expect(dialog.getByTestId(id)).toBeVisible();
        // Let any remaining transition settle: axe samples colours as they are painted.
        await page.waitForTimeout(400);
        const violations = summarise(await axeScan(page).include('[role="dialog"]').analyze());
        // axe leaves text scrolled out of the dialog's viewport "incomplete", not judged: on a phone
        // the later chips sit below the fold. Scroll each annotated chip in and judge it there too.
        for (const id of marked) {
          const chip = dialog.locator(`[role="group"]:has([data-testid="${id}"])`);
          await chip.scrollIntoViewIfNeeded();
          const slug = await chip.getAttribute("data-testid");
          violations.push(...summarise(await axeScan(page).include(`[role="dialog"] [data-testid="${slug}"]`).analyze()));
        }
        expect(violations).toEqual([]);
      });
    }
  });
}
