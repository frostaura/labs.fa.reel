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

async function bootHome(page: Page, options: { clock?: boolean } = {}) {
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
  if (options.clock) await page.clock.install();
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

/**
 * HREV4-PH-02 (WCAG 2.2.1): the undo window holds while someone is on the toast. Before, a keyboard
 * user who reached "Undo" after the six seconds found the action already committed, and the clock
 * ran on under a resting pointer. The page's clock is driven, so "long after the window" is exact.
 */
const UNDO_WINDOW_MS = 6000; // UndoToast's UNDO_WINDOW_MS; a change there turns the expiry check below red.

async function notForMeByKeyboard(page: Page, calls: string[]) {
  await page.route("**/api/titles/movie/1007/reactions/not_interested", (route) => {
    calls.push(route.request().method());
    return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });
  const seven = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
  await seven.getByTestId("quick-not-interested").focus();
  await page.keyboard.press("Enter");
  await expect(seven).toHaveCount(0);
  await expect(page.getByTestId("card-undo-toast")).toContainText("“Pick 7” won’t be picked for you again.");
  return seven;
}

test("a keyboard user on the card undo keeps it past the window, and Enter undoes", async ({ page }) => {
  await bootHome(page, { clock: true });
  const calls: string[] = [];
  const seven = await notForMeByKeyboard(page, calls);
  // Focus arriving on Undo — the keyboard or screen-reader user has got there.
  await page.getByTestId("card-undo-toast-undo").focus();
  await page.clock.runFor(UNDO_WINDOW_MS * 3);
  await expect(page.getByTestId("card-undo-toast")).toBeVisible();
  await expect(page.getByTestId("card-undo-toast-undo")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByTestId("card-undo-toast")).toHaveCount(0);
  await expect(seven).toHaveCount(1);
  expect(calls).toEqual(["POST", "DELETE"]);
});

test("Tab from Undo to Dismiss still holds the window; tabbing out gives a fresh full one", async ({ page, browserName }) => {
  await bootHome(page, { clock: true });
  await notForMeByKeyboard(page, []);
  const toast = page.getByTestId("card-undo-toast");
  await page.getByTestId("card-undo-toast-undo").focus();
  // WebKit on macOS moves Tab between buttons only with Option held (a11y.spec's tabKey).
  await page.keyboard.press(browserName === "webkit" ? "Alt+Tab" : "Tab");
  await expect(page.getByTestId("card-undo-toast-dismiss")).toBeFocused();
  await page.clock.runFor(UNDO_WINDOW_MS * 2);
  await expect(toast).toBeVisible();
  // Out of the toast, to the page.
  await page.getByTestId("rec-card").first().getByTestId("quick-not-interested").focus();
  await page.clock.runFor(UNDO_WINDOW_MS - 500);
  await expect(toast).toBeVisible();
  await page.clock.runFor(1000);
  await expect(toast).toHaveCount(0);
});

test("a pointer resting on the card undo holds the window until it leaves", async ({ page }) => {
  await bootHome(page, { clock: true });
  await notForMeByKeyboard(page, []);
  const toast = page.getByTestId("card-undo-toast");
  // A keyboard-raised undo takes focus (HREV4-PH-02), and focus holds the clock by design; move it
  // off the toast so only the pointer is holding the window.
  await expect(page.getByTestId("card-undo-toast-undo")).toBeFocused();
  await page.getByTestId("rec-card").first().getByTestId("quick-not-interested").focus();
  await toast.hover();
  await page.clock.runFor(UNDO_WINDOW_MS * 2);
  await expect(toast).toBeVisible();
  await page.mouse.move(5, 5);
  await page.clock.runFor(UNDO_WINDOW_MS - 500);
  await expect(toast).toBeVisible();
  await page.clock.runFor(1000);
  await expect(toast).toHaveCount(0);
});

/**
 * The verdict toast follows the same rule (HREV5 follow-up to HREV4-PH-02). It holds two focusable
 * controls — Dismiss and "See the track record" — and rises with the card undo on every rating, so
 * a keyboard user passing through the toast column must not have it vanish on its nine-second clock.
 */
const VERDICT_WINDOW_MS = 9000; // VerdictToast's DISMISS_AFTER_MS; a change there turns the expiry checks below red.

async function rateNine(page: Page) {
  const target = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
  await target.hover();
  await target.getByTestId("quick-rate").click();
  await page.getByTestId("quick-rate-picker").getByTestId("quick-rate-3").click();
  const verdict = page.getByTestId("verdict-toast");
  await expect(verdict).toBeVisible();
  return verdict;
}

test("with nobody on it, the verdict goes on its own clock", async ({ page }) => {
  await bootHome(page, { clock: true });
  const verdict = await rateNine(page);
  await page.mouse.move(5, 5);
  await page.clock.runFor(VERDICT_WINDOW_MS - 500);
  await expect(verdict).toBeVisible();
  await page.clock.runFor(1000);
  await expect(verdict).toHaveCount(0);
});

test("a keyboard user on the verdict keeps it, Tab between its controls holds, tabbing out gives a fresh full window", async ({ page, browserName }) => {
  await bootHome(page, { clock: true });
  const verdict = await rateNine(page);
  await page.mouse.move(5, 5);
  await verdict.getByRole("button", { name: "Dismiss" }).focus();
  await page.keyboard.press(browserName === "webkit" ? "Alt+Tab" : "Tab");
  await expect(verdict.getByRole("link", { name: "See the track record" })).toBeFocused();
  await page.clock.runFor(VERDICT_WINDOW_MS * 3);
  await expect(verdict).toBeVisible();
  await page.getByTestId("rec-card").first().getByTestId("quick-not-interested").focus();
  await page.clock.runFor(VERDICT_WINDOW_MS - 500);
  await expect(verdict).toBeVisible();
  await page.clock.runFor(1000);
  await expect(verdict).toHaveCount(0);
});

test("a pointer resting on the verdict holds it until it leaves", async ({ page }) => {
  await bootHome(page, { clock: true });
  const verdict = await rateNine(page);
  await verdict.hover();
  await page.clock.runFor(VERDICT_WINDOW_MS * 3);
  await expect(verdict).toBeVisible();
  await page.mouse.move(5, 5);
  await page.clock.runFor(VERDICT_WINDOW_MS - 500);
  await expect(verdict).toBeVisible();
  await page.clock.runFor(1000);
  await expect(verdict).toHaveCount(0);
});
