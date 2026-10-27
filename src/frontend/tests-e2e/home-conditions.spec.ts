import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";
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
  // No floor at all: the exact rules IR1S-06 showed the global lookup ignoring, which it now hides (FD-5).
  "no floor; romance, pre-2000 and non-English excluded": {
    excludeGenres: ["romance"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: 2000, maxYear: null,
  },
};

for (const [label, FILTERS] of Object.entries(FIXTURES)) {
  test(`the conditions dialog says top-bar search hides and counts what they rule out (${label})`, async ({ page }) => {
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
    await expect(scope).toContainText("counted, not shown");
    await expect(scope).not.toContainText("can show titles your conditions exclude");
    await expect(scope).not.toContainText("all search");
    await expect(scope).not.toContainText("Search applies them too");
  });
}

/** The founder's real shape: eight exclusions and three floors — more chips than the row shows. */
const MANY = {
  excludeGenres: ["documentary", "horror", "romance", "music", "reality", "talk", "news", "soap"],
  includeGenres: ["crime"], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};

async function bootHome(page: import("@playwright/test").Page, filters: unknown, hero: unknown[] = [HEAT]) {
  await stubEventSource(page);
  await mockHomeChrome(page, { filters });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero, rows: [], lockedRowCount: 0 });
  await page.goto("/home");
}

const HEAT = {
  titleId: "00000000-0000-0000-0000-000000000c01", mediaType: "Movie", tmdbId: 7001, name: "Heat", year: 1995,
  runtimeMinutes: 170, posterPath: null, backdropPath: null, genres: ["crime"], predictedRating: 8.2,
  crowdRating: 8.3, crowdVotes: 90000, whyThis: "Because of the films you rate highest.", isReleased: true,
  confidence: "confident", floorUsed: 7.5,
};

test.describe("Home's condition chips (H-20, H-9, O-11)", () => {
  test("are neutral, signed with ± rather than tinted, and each one opens the editor", async ({ page }) => {
    await bootHome(page, MANY);
    const chips = page.getByTestId("condition-chip");
    await expect(chips).toHaveCount(6);
    // One neutral style: no success/danger tint on any chip.
    for (const chip of await chips.all()) {
      const cls = await chip.locator("span").first().getAttribute("class");
      expect(cls).not.toMatch(/fa-success|fa-danger/);
    }
    await expect(chips.first()).toHaveAttribute("data-sign", "include");
    await expect(chips.first()).toHaveAccessibleName("Has crime — change conditions");
    await expect(chips.nth(1)).toHaveAttribute("data-sign", "exclude");
    await expect(chips.nth(1)).toHaveAccessibleName("No documentary — change conditions");

    await chips.nth(1).click();
    await expect(page.getByTestId("conditions-scope")).toBeVisible();
  });

  test("“+N more” expands to every condition and collapses again", async ({ page }) => {
    await bootHome(page, MANY);
    const more = page.getByTestId("conditions-more");
    // 9 genre rules + crowd + for-you + years + English = 13 conditions; 6 shown.
    await expect(more).toHaveText("+7 more");
    await more.click();
    await expect(page.getByTestId("condition-chip")).toHaveCount(13);
    await expect(more).toHaveText("Show fewer");
    await expect(more).toHaveAttribute("aria-expanded", "true");
    await more.click();
    await expect(page.getByTestId("condition-chip")).toHaveCount(6);
  });

  test("the browse toggles render even when there are no picks to show", async ({ page }) => {
    await bootHome(page, MANY, []);
    await expect(page.getByTestId("browse-filters")).toBeVisible();
    await expect(page.getByTestId("browse-movie")).toBeVisible();
  });

  test("on a phone the chips are the conditions control: 44 px each, in the one control line", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootHome(page, MANY);
    await expect(page.getByTestId("open-conditions")).toHaveCount(0);
    const chips = page.getByTestId("condition-chip");
    await expect(chips).toHaveCount(2);
    await expect(page.getByTestId("conditions-more")).toHaveText("+11 more");
    for (const target of [...(await chips.all()), page.getByTestId("conditions-more")]) {
      const box = (await target.boundingBox())!;
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
    // Same line as the browse toggles.
    const chipTop = (await chips.first().boundingBox())!.y;
    const browseTop = (await page.getByTestId("browse-filters").boundingBox())!.y;
    expect(Math.abs(chipTop - browseTop)).toBeLessThan(4);
    await chips.first().tap();
    await expect(page.getByTestId("conditions-scope")).toBeVisible();
    await context.close();
  });

  test("on a phone with no conditions saved, the Conditions button stands in", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootHome(page, { languages: ["*"] });
    await expect(page.getByTestId("condition-chip")).toHaveCount(0);
    await expect(page.getByTestId("open-conditions")).toBeVisible();
    await context.close();
  });
});
