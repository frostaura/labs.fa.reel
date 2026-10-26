import { expect, test, type Locator, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * Two toasts at once stack; they never cover each other (integrator CR to WS-9: "UndoToast
 * stacking"). Rating a card from Home raises both the after-rating verdict and the card undo in
 * the same moment — the commonest pair — so this rates one and measures where both land, on a
 * desktop and on a phone, where the column must also clear the bottom tab bar.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie", tmdbId: 1000 + n, name: `Pick ${n}`, year: 2026, runtimeMinutes: 110,
  posterPath: null, backdropPath: null, genres: ["drama"], predictedRating: 8.1, crowdRating: 7.5,
  crowdVotes: 9000, whyThis: "Explores a heist crew coming apart, like Heat.", isReleased: true,
});

async function bootHome(page: Page) {
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/learned", null);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [card(1)],
    rows: [{ kind: "because-you-loved", anchorTitleId: "00000000-0000-0000-0000-0000000000aa", anchorName: "Anchor Film", title: null, items: [card(6), card(7), card(8), card(9)] }],
    lockedRowCount: 0,
  });
  await mockJson(page, "**/api/titles/movie/1009/rating", {
    userRating: 3,
    verdict: { predictedBefore: 8.1, surface: "feed", shownAt: new Date().toISOString(), delta: 5.1 },
  });
  await page.goto("/home");
  await expect(page.getByTestId("rec-card")).toHaveCount(4);
}

async function box(locator: Locator) {
  const b = await locator.boundingBox();
  if (!b) throw new Error("not laid out");
  return b;
}

async function rateAndMeasure(page: Page, open: () => Promise<void>) {
  await open();
  await page.getByTestId("quick-rate-picker").getByTestId("quick-rate-3").click();
  const verdict = page.getByTestId("verdict-toast");
  const undo = page.getByTestId("card-undo-toast");
  await expect(verdict).toBeVisible();
  await expect(undo).toBeVisible();
  const [a, b] = [await box(verdict), await box(undo)];
  // One above the other, with no shared pixel row.
  expect(a.y >= b.y + b.height || b.y >= a.y + a.height, `toasts overlap: ${JSON.stringify([a, b])}`).toBe(true);
  // Both live in the one column.
  await expect(page.getByTestId("toast-stack").locator('[data-testid="verdict-toast"], [data-testid="card-undo-toast"]')).toHaveCount(2);
  return [a, b];
}

test("the verdict and the card undo stack instead of covering each other", async ({ page }) => {
  await bootHome(page);
  const target = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
  await rateAndMeasure(page, async () => {
    await target.hover();
    await target.getByTestId("quick-rate").click();
  });
});

test("on a phone the stacked toasts clear the bottom tab bar", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
  const page = await context.newPage();
  await bootHome(page);
  const target = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
  const toasts = await rateAndMeasure(page, () => target.getByTestId("quick-rate").tap());
  const bar = await box(page.getByTestId("mobile-tabbar"));
  for (const t of toasts) expect(t.y + t.height).toBeLessThanOrEqual(bar.y);
  await context.close();
});
