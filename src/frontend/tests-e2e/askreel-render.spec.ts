import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubAskReel, stubEventSource } from "./helpers";

/**
 * Ask Reel's rendering, measured rather than described.
 *
 * Round 2's headed-browser pass (F6) filed five defects that no assertion in this suite could
 * have caught, because every one of them is about a BOX rather than about text: a popover whose
 * left edge sat 114 px off-screen at 320 with no way to scroll to it, controls between 25 and
 * 36 px tall against a 44 px minimum, a seam that printed a 1 000-character question as one
 * caption, and the only destructive control in the product being a 26 px tap with nothing between
 * it and a permanent delete.
 *
 * So these tests read `getBoundingClientRect` and `elementFromPoint` and assert numbers. Text
 * assertions belong in `askreel-continuity.spec.ts`; what lives here is geometry, and the reason
 * it is its own file is that geometry needs a viewport, which is a `test.use` and therefore a
 * file-level decision.
 */

// `hasTouch`, because half of what is under test is what a THUMB can reach: `locator.tap()` fires
// real touch events, and a control that only responds to a synthetic click would pass a mouse
// test and fail a person.
test.use({ viewport: { width: 375, height: 812 }, hasTouch: true });

/** WCAG 2.1 AA, success criterion 2.5.5 target size. Not a house style — the published minimum. */
const TOUCH_MIN = 44;

const CONVERSATION = "5c0a1b2c-0000-4000-8000-000000000001";
const OTHER = "5c0a1b2c-0000-4000-8000-000000000002";

/**
 * Thread titles long enough to be cut by a clipped popover. F6-05 read the damage off exactly
 * this: "…drama with supe…", "…e with a clever en…" — the left half of every row gone, and
 * unreachable, because the document does not scroll horizontally.
 */
const CONVERSATION_LIST = [
  {
    id: CONVERSATION,
    title: "a slow-burn detective drama with superb photography",
    turnCount: 4,
    lastTurnAt: new Date().toISOString(),
    lastCardsAt: new Date().toISOString(),
  },
  {
    id: OTHER,
    title: "something tense with a clever ending",
    turnCount: 2,
    lastTurnAt: new Date(Date.now() - 86_400_000).toISOString(),
    lastCardsAt: new Date(Date.now() - 86_400_000).toISOString(),
  },
];

const FILTERS = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

async function boot(page: Page, opts: { conversations?: unknown[] } = {}) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/settings/filters", FILTERS);
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/search/conversations", opts.conversations ?? CONVERSATION_LIST);
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
  await expect(page.getByTestId("askreel-recent")).toBeVisible();
}

/** The box, in viewport coordinates — the only unit any of these findings were stated in. */
async function boxOf(page: Page, testId: string) {
  const box = await page.getByTestId(testId).first().boundingBox();
  expect(box, `${testId} has no box`).not.toBeNull();
  return box!;
}

test.describe("the Recent popover stays inside the viewport", () => {
  test("at 320 the menu is fully on screen and every row is tappable", async ({ page }) => {
    await boot(page);
    // The width F6-05 measured at. The header wraps here, which is what moved `Recent ▾` inward
    // and put a right-anchored 288 px panel at x: -114.
    await page.setViewportSize({ width: 320, height: 568 });
    await page.getByTestId("askreel-recent").tap();
    // The list is fetched on open, so the first frame is "Loading…" and has no rows to measure.
    await expect(page.getByTestId("askreel-recent-row").first()).toBeVisible();

    const menu = await boxOf(page, "askreel-recent-menu");
    expect(menu.x, "the menu's left edge is off-screen").toBeGreaterThanOrEqual(0);
    expect(menu.x + menu.width, "the menu's right edge is off-screen").toBeLessThanOrEqual(320);

    // …and the document still does not scroll sideways, which is what made the clipping
    // unrecoverable rather than merely ugly.
    const scroll = await page.evaluate(() => ({
      w: document.documentElement.scrollWidth,
      c: document.documentElement.clientWidth,
    }));
    expect(scroll.w).toBe(scroll.c);

    // A row that is on screen but underneath something else is no better than one that is off it.
    const hit = await page.evaluate(() => {
      const row = document.querySelector('[data-testid="askreel-recent-open"]');
      if (!row) return "no row";
      const b = row.getBoundingClientRect();
      const top = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2);
      return top && row.contains(top) ? "ok" : (top?.getAttribute("data-testid") ?? top?.tagName ?? "nothing");
    });
    expect(hit).toBe("ok");
  });

  test("at 375 the menu is fully on screen too", async ({ page }) => {
    await boot(page);
    await page.getByTestId("askreel-recent").tap();
    const menu = await boxOf(page, "askreel-recent-menu");
    expect(menu.x).toBeGreaterThanOrEqual(0);
    expect(menu.x + menu.width).toBeLessThanOrEqual(375);
  });

  test("Escape closes it, and so does a tap outside", async ({ page }) => {
    await boot(page);
    const recent = page.getByTestId("askreel-recent");

    // F6-14: the component listened for `pointerdown` only, so the menu was open with focus on a
    // button whose `aria-expanded` said so and Escape did nothing at all.
    await recent.tap();
    await expect(page.getByTestId("askreel-recent-menu")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByTestId("askreel-recent-menu")).toHaveCount(0);
    await expect(recent).toHaveAttribute("aria-expanded", "false");

    await recent.tap();
    await expect(page.getByTestId("askreel-recent-menu")).toBeVisible();
    // A real touch on the page title — not a link, not inside the sheet, and above it at every
    // width, so this is the "tapped somewhere else" gesture and nothing more.
    await page.getByRole("heading", { name: "Ask Reel" }).tap();
    await expect(page.getByTestId("askreel-recent-menu")).toHaveCount(0);
  });
});

test.describe("touch targets", () => {
  /**
   * Every Ask Reel control this fixer owns, at the reference phone width. Foreign components
   * (`BrowseFilters`, the card overlay, the tab bar) are deliberately not asserted here — they are
   * other people's files, and a test that fails on someone else's box is a test that gets deleted.
   */
  async function expectTarget(page: Page, testId: string) {
    const box = await boxOf(page, testId);
    expect(box.height, `${testId} is ${Math.round(box.height)} px tall`).toBeGreaterThanOrEqual(TOUCH_MIN);
    expect(box.width, `${testId} is ${Math.round(box.width)} px wide`).toBeGreaterThanOrEqual(TOUCH_MIN);
  }

  test("the header, the composer and the presets all meet 44 x 44", async ({ page }) => {
    await boot(page);
    for (const id of ["askreel-new", "askreel-recent", "askreel-preset", "askreel-send", "askreel-mic"]) {
      await expectTarget(page, id);
    }
    // The delete control is the smallest of them and the only destructive one — F6-07 measured it
    // at 26 x 26, eight pixels from the row that opens the thread.
    await page.getByTestId("askreel-recent").tap();
    await expectTarget(page, "askreel-recent-delete");
  });

  test("the chips and Stop meet 44 x 44 while a turn runs", async ({ page }) => {
    await boot(page);
    await page.getByTestId("askreel-composer").fill("a heist with a great ensemble");
    await page.getByTestId("askreel-composer").press("Enter");
    await expect(page.getByTestId("askreel-banner")).toBeVisible();

    await expectTarget(page, "askreel-stop");
    await expectTarget(page, "askreel-chip");
  });
});
