import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * The one-tap card overlay (Not for me / Save) on the feed rails: revealed on hover, acts without
 * navigating, and removal flows through removedTitlesSlice — the card leaves the rail while every
 * other card's DOM node stays put (no feed remount, no scroll reset mid-triage).
 *
 * Plus the reason picker behind it. The overlay used to hardcode `not-for-me`, feeding the
 * reason-weighted DislikeSignal its flattest signal from the app's busiest dismissal surface. The
 * contract these specs pin: a plain tap still posts the default INSTANTLY (the fast path is the
 * point of the overlay), and a secondary gesture — hold, or right-click — opens the same four
 * reasons the modal offers and posts whichever one the user chose.
 *
 * Plus the 1–10 rating popover the overlay now carries — the same component the hero mounts, so
 * scoring a pick no longer costs a navigation either. Only 2% of ratings are made in the app today;
 * the rest arrive from Trakt hours later, which is why the loop never closes at the moment it is felt.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 1000 + n,
  name: `Pick ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.1,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
});

async function bootHome(page: Page) {
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [card(1)],
    rows: [{
      kind: "because-you-loved",
      anchorTitleId: "00000000-0000-0000-0000-0000000000aa",
      anchorName: "Anchor Film",
      title: null,
      items: [card(6), card(7), card(8), card(9)],
    }],
    lockedRowCount: 0,
  });
  await page.goto("/home");
  await expect(page.getByTestId("rec-card")).toHaveCount(4);
}

test.describe("card quick actions", () => {
  test("hover reveals Not for me + Save; Not for me fires the reaction and removes only that card, in place", async ({ page }) => {
    await bootHome(page);

    const reactionCalls: { url: string; body: unknown }[] = [];
    await page.route("**/api/titles/movie/1006/reactions/not_interested", (route) => {
      reactionCalls.push({ url: route.request().url(), body: route.request().postDataJSON() });
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    const notForMe = target.getByTestId("quick-not-interested");
    const save = target.getByTestId("quick-save");

    // Hidden at rest (opacity 0 still counts as "visible" to Playwright, so assert the CSS), then
    // revealed by the card's hover group.
    await expect(notForMe).toHaveCSS("opacity", "0");
    await target.hover();
    await expect(notForMe).toHaveCSS("opacity", "1");
    await expect(save).toHaveCSS("opacity", "1");

    // Tag every OTHER card's live DOM node so remount-vs-persist is provable after the removal.
    await page.evaluate(() => {
      document.querySelectorAll('[data-testid="rec-card"]').forEach((el) => {
        if (!el.textContent?.includes("Pick 6")) (el as HTMLElement).dataset.persisted = "yes";
      });
    });

    // The tap acts instead of navigating: no title route, the reaction fires with its reason.
    await notForMe.click();
    await expect(page).toHaveURL(/\/home/);
    await expect(target).toHaveCount(0);
    expect(reactionCalls).toHaveLength(1);
    expect(reactionCalls[0].body).toEqual({ reason: "not-for-me" });

    // The survivors are the SAME nodes — removal filtered the list, it did not remount the rail.
    await expect(page.getByTestId("rec-card")).toHaveCount(3);
    const persisted = await page.evaluate(() =>
      Array.from(document.querySelectorAll('[data-testid="rec-card"]')).map(
        (el) => (el as HTMLElement).dataset.persisted ?? "no"
      )
    );
    expect(persisted).toEqual(["yes", "yes", "yes"]);
  });

  test("right-click opens the four-way picker and posts the reason that was chosen", async ({ page }) => {
    await bootHome(page);

    const reactionCalls: unknown[] = [];
    await page.route("**/api/titles/movie/1007/reactions/not_interested", (route) => {
      reactionCalls.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
    await target.hover();
    await target.getByTestId("quick-not-interested").click({ button: "right" });

    const picker = page.getByTestId("quick-reason-picker");
    await expect(picker).toBeVisible();
    // All four reasons, the same set and copy the detail modal shows.
    await expect(picker.getByRole("button")).toHaveText([
      "Just not for me",
      "Not my genre",
      "Seen enough like this",
      "Wrong tone",
    ]);

    // Opening the picker must not have dismissed anything on its own.
    expect(reactionCalls).toHaveLength(0);

    await picker.getByTestId("quick-reason-genre").click();
    await expect(page).toHaveURL(/\/home/);
    await expect(target).toHaveCount(0);
    expect(reactionCalls).toEqual([{ reason: "genre" }]);
  });

  test("the rating popover posts the score in place and retires the card", async ({ page }) => {
    await bootHome(page);

    const ratings: unknown[] = [];
    await page.route("**/api/titles/movie/1009/rating", (route) => {
      ratings.push(route.request().postDataJSON());
      return route.fulfill({
        status: 200, contentType: "application/json", body: JSON.stringify({ userRating: 9 }),
      });
    });

    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
    await target.hover();
    await target.getByTestId("quick-rate").click();

    const picker = page.getByTestId("quick-rate-picker");
    await expect(picker).toBeVisible();
    await picker.getByTestId("quick-rate-9").click();

    // Rated from a suggestion surface means "I have seen this", so the play is logged — the same
    // call the /rate grid makes, and the opposite of the post-watch strip, where it already exists.
    await expect(page).toHaveURL(/\/home/);
    expect(ratings).toEqual([{ rating: 9, markWatched: true }]);
    await expect(target).toHaveCount(0);
    await expect(page.getByTestId("rec-card")).toHaveCount(3);
  });

  test("press-and-hold opens the picker instead of dismissing, and the hold's own click never fires the default", async ({ page }) => {
    await bootHome(page);

    const reactionCalls: unknown[] = [];
    await page.route("**/api/titles/movie/1008/reactions/not_interested", (route) => {
      reactionCalls.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 8" });
    const notForMe = target.getByTestId("quick-not-interested");
    await target.hover();

    // Hold past LONG_PRESS_MS (450ms), then release — the release's click must be swallowed.
    const box = await notForMe.boundingBox();
    if (!box) throw new Error("quick-not-interested has no box");
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.waitForTimeout(700);
    await page.mouse.up();

    const picker = page.getByTestId("quick-reason-picker");
    await expect(picker).toBeVisible();
    expect(reactionCalls).toHaveLength(0);
    await expect(target).toHaveCount(1);

    await picker.getByTestId("quick-reason-tone").click();
    await expect(target).toHaveCount(0);
    expect(reactionCalls).toEqual([{ reason: "tone" }]);
  });

  test("save, not for me and rate each raise an undo that calls the existing reversal and puts the card back (F-19)", async ({ page }) => {
    await bootHome(page);

    const calls: string[] = [];
    await page.route(/\/api\/titles\/movie\/\d+\/(reactions\/\w+|rating)$/, (route) => {
      const url = new URL(route.request().url());
      calls.push(`${route.request().method()} ${url.pathname}`);
      const body = url.pathname.endsWith("/rating") && route.request().method() === "POST" ? { userRating: 7 } : {};
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    });
    const toast = page.getByTestId("card-undo-toast");

    // Save → "Saved … to your queue" → Undo → DELETE save_for_later, card back on the rail.
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    await six.hover();
    await six.getByTestId("quick-save").click();
    await expect(six).toHaveCount(0);
    await expect(toast).toContainText("Saved “Pick 6” to your queue.");
    await page.getByTestId("card-undo-toast-undo").click();
    await expect(toast).toHaveCount(0);
    await expect(six).toHaveCount(1);

    // Not for me → Undo → DELETE not_interested.
    const seven = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
    await seven.hover();
    await seven.getByTestId("quick-not-interested").click();
    await expect(toast).toContainText("“Pick 7” won’t be picked for you again.");
    await page.getByTestId("card-undo-toast-undo").click();
    await expect(seven).toHaveCount(1);

    // Rate → Undo → DELETE rating (which also clears the play it logged), card back.
    const eight = page.getByTestId("rec-card").filter({ hasText: "Pick 8" });
    await eight.hover();
    await eight.getByTestId("quick-rate").click();
    await page.getByTestId("quick-rate-picker").getByTestId("quick-rate-7").click();
    await expect(toast).toContainText("Rated “Pick 8” 7 — logged as seen.");
    await page.getByTestId("card-undo-toast-undo").click();
    await expect(eight).toHaveCount(1);

    expect(calls).toEqual([
      "POST /api/titles/movie/1006/reactions/save_for_later",
      "DELETE /api/titles/movie/1006/reactions/save_for_later",
      "POST /api/titles/movie/1007/reactions/not_interested",
      "DELETE /api/titles/movie/1007/reactions/not_interested",
      "POST /api/titles/movie/1008/rating",
      "DELETE /api/titles/movie/1008/rating",
    ]);
  });

  test("a failed action raises no undo, and a failed undo says nothing changed", async ({ page }) => {
    await bootHome(page);
    let failUndo = true;
    await page.route("**/api/titles/movie/1009/reactions/save_for_later", (route) =>
      route.request().method() === "POST"
        ? route.fulfill({ status: 200, contentType: "application/json", body: "{}" })
        : failUndo
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: "{}" })
    );
    await page.route("**/api/titles/movie/1008/reactions/save_for_later", (route) =>
      route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
    );

    const eight = page.getByTestId("rec-card").filter({ hasText: "Pick 8" });
    await eight.hover();
    await eight.getByTestId("quick-save").click();
    await expect(eight).toHaveCount(1);
    await expect(page.getByTestId("card-undo-toast")).toHaveCount(0);

    const nine = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
    await nine.hover();
    await nine.getByTestId("quick-save").click();
    await page.getByTestId("card-undo-toast-undo").click();
    await expect(page.getByTestId("card-undo-toast")).toContainText("Couldn’t undo — “Pick 9” is still saved.");
    failUndo = false;
    await page.getByTestId("card-undo-toast-undo").click();
    await expect(nine).toHaveCount(1);
  });

  test("a card is reachable and actionable from the keyboard, with no control nested in its link (O+2)", async ({ page }) => {
    await bootHome(page);
    const saves: string[] = [];
    await page.route("**/api/titles/movie/1006/reactions/save_for_later", (route) => {
      saves.push(route.request().method());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    // No interactive element inside another one, anywhere on a card.
    const nested = await page.evaluate(
      () => document.querySelectorAll('[data-testid="rec-card"] a button, [data-testid="rec-card"] button a, [data-testid="hero-card"] a button').length
    );
    expect(nested).toBe(0);

    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    await six.getByTestId("rec-card-link").focus();
    // Tab order: the title link, then its actions — Not for me, Save, Rate.
    await page.keyboard.press("Tab");
    await expect(six.getByTestId("quick-not-interested")).toBeFocused();
    await expect(six.getByTestId("quick-not-interested")).toHaveCSS("opacity", "1");
    await page.keyboard.press("Tab");
    await expect(six.getByTestId("quick-save")).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(six).toHaveCount(0);
    expect(saves).toEqual(["POST"]);
    await expect(page).toHaveURL(/\/home/);

    // And Enter on a title link opens the title.
    const seven = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
    await seven.getByTestId("rec-card-link").focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/title\/movie\/1007/);
  });

  test("on a coarse pointer every action is a 44 px target and the 2×5 rating picker stays inside a phone", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootHome(page);
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    for (const id of ["quick-not-interested", "quick-save", "quick-rate"]) {
      const box = await six.getByTestId(id).boundingBox();
      expect(box!.width).toBeGreaterThanOrEqual(44);
      expect(box!.height).toBeGreaterThanOrEqual(44);
    }
    await six.getByTestId("quick-rate").tap();
    const picker = page.getByTestId("quick-rate-picker");
    await expect(picker).toBeVisible();
    const pickerBox = (await picker.boundingBox())!;
    expect(pickerBox.x).toBeGreaterThanOrEqual(0);
    expect(pickerBox.x + pickerBox.width).toBeLessThanOrEqual(390);
    expect(pickerBox.y + pickerBox.height).toBeLessThanOrEqual(844);
    const one = (await picker.getByTestId("quick-rate-1").boundingBox())!;
    const six2 = (await picker.getByTestId("quick-rate-6").boundingBox())!;
    expect(one.height).toBeGreaterThanOrEqual(44);
    // Two rows of five: 6 sits directly under 1.
    expect(Math.abs(six2.x - one.x)).toBeLessThan(1);
    expect(six2.y).toBeGreaterThan(one.y);
    await context.close();
  });
});
