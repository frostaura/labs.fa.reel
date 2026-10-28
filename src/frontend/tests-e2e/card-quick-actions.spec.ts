import AxeBuilder from "@axe-core/playwright";
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

/**
 * Radix runs a popover's close auto-focus in a `setTimeout(0)` after the content unmounts, so a
 * `not.toBeFocused()` straight after the close passes before any focus could have been stolen.
 * A later timer runs after Radix's, so the assertion that follows sees where focus finally rests.
 */
async function settleCloseAutoFocus(page: Page) {
  await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 50)));
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
    // It sits in the shared toast column, with no transformed wrapper of its own (R1-42: the
    // provider's "-translate-y-28" lift over the verdict died when the stack landed).
    await expect(page.getByTestId("toast-stack").getByTestId("card-undo-toast")).toHaveCount(1);
    expect(
      await page.evaluate(() =>
        Array.from(document.querySelectorAll<HTMLElement>("#root *")).filter(
          (el) => getComputedStyle(el).position === "fixed" && getComputedStyle(el).zIndex === "60"
        ).length
      )
    ).toBe(0);
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
    // Plain Tab in BOTH engines (R1-42): WebKit leaves links and buttons out of the Tab order unless
    // they carry an explicit tabindex, which the card's link and actions now do. From the last
    // action of one card, Tab lands on the next card's title link.
    await six.getByTestId("quick-rate").focus();
    await page.keyboard.press("Tab");
    await expect(page.getByTestId("rec-card").filter({ hasText: "Pick 7" }).getByTestId("rec-card-link")).toBeFocused();
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

  test("closing the rating picker from the keyboard puts focus back on its trigger (H3REV-PH-01)", async ({ page }) => {
    // The trigger used to be a PopoverAnchor with no PopoverTrigger, so Radix had nothing to return
    // focus to: Escape closed the picker and left document.activeElement on <body>, and on desktop
    // the actions are opacity 0 until hovered or focused, so there was no visible place to resume.
    await bootHome(page);
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    const rate = six.getByTestId("quick-rate");
    await rate.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("quick-rate-1")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.getByTestId("quick-rate-picker")).toHaveCount(0);
    await expect(rate).toBeFocused();
    await expect(rate).toHaveCSS("opacity", "1");
    await expect(rate).toHaveAttribute("aria-expanded", "false");
    // And the trigger still works from where focus came back to.
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("quick-rate-1")).toBeFocused();
  });

  test("closing the reason picker from the keyboard puts focus back on Not for me, which still dismisses (H3REV-PH-01)", async ({ page }) => {
    await bootHome(page);
    const reasons: unknown[] = [];
    await page.route("**/api/titles/movie/1006/reactions/not_interested", (route) => {
      reasons.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    const notForMe = six.getByTestId("quick-not-interested");

    // Alt+Enter and ArrowDown both open it; both must hand focus back.
    for (const opener of ["Alt+Enter", "ArrowDown"]) {
      await notForMe.focus();
      await page.keyboard.press(opener);
      await expect(page.getByTestId("quick-reason-picker")).toBeVisible();
      await expect.poll(() => page.evaluate(() =>
        !!document.querySelector('[data-testid="quick-reason-picker"]')?.contains(document.activeElement))).toBe(true);
      await page.keyboard.press("Escape");
      await expect(page.getByTestId("quick-reason-picker")).toHaveCount(0);
      await expect(notForMe).toBeFocused();
      await expect(notForMe).toHaveCSS("opacity", "1");
    }
    expect(reasons).toEqual([]);

    // A keyboard open is not a hold: no click follows it, so it must not swallow the NEXT press —
    // Enter from the restored focus is the fast path and dismisses with the default reason at once.
    await page.keyboard.press("Enter");
    await expect(six).toHaveCount(0);
    expect(reasons).toHaveLength(1);
  });

  test("on a phone, tap-open then Escape returns focus to the rate and reason triggers (H3REV-PH-01)", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootHome(page);
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });

    const rate = six.getByTestId("quick-rate");
    await rate.tap();
    await expect(page.getByTestId("quick-rate-picker")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByTestId("quick-rate-picker")).toHaveCount(0);
    await expect(rate).toBeFocused();

    const notForMe = six.getByTestId("quick-not-interested");
    await notForMe.click({ button: "right" });
    await expect(page.getByTestId("quick-reason-picker")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByTestId("quick-reason-picker")).toHaveCount(0);
    await expect(notForMe).toBeFocused();
    await context.close();
  });

  test("a second press on the rate trigger closes its picker, and a click elsewhere keeps focus where it landed (H3REV-PH-01)", async ({ page }) => {
    await bootHome(page);
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    const rate = six.getByTestId("quick-rate");
    await six.hover();
    await rate.click();
    await expect(page.getByTestId("quick-rate-picker")).toBeVisible();
    // The press lands on the trigger, which sits OUTSIDE the content: as an anchor it was an outside
    // interaction that dismissed the picker and the same click's toggle then reopened it.
    await rate.click();
    await expect(page.getByTestId("quick-rate-picker")).toHaveCount(0);
    await expect(rate).toHaveAttribute("aria-expanded", "false");

    // Dismissing by pointing somewhere else must not drag focus back to the trigger.
    await rate.click();
    await expect(page.getByTestId("quick-rate-picker")).toBeVisible();
    const seven = page.getByTestId("rec-card").filter({ hasText: "Pick 7" }).getByTestId("quick-save");
    await page.getByTestId("rec-card").filter({ hasText: "Pick 7" }).hover();
    await page.route("**/api/titles/movie/1007/reactions/save_for_later", (route) =>
      route.fulfill({ status: 200, contentType: "application/json", body: "{}" }));
    await seven.click();
    await expect(page.getByTestId("quick-rate-picker")).toHaveCount(0);
    await settleCloseAutoFocus(page);
    await expect(rate).not.toBeFocused();

    // The same for the reason picker, whose focus return is by hand rather than Radix's.
    const notForMe = six.getByTestId("quick-not-interested");
    await six.hover();
    await notForMe.click({ button: "right" });
    await expect(page.getByTestId("quick-reason-picker")).toBeVisible();
    // The picker opens to the RIGHT, over the neighbouring card's corner (WebKit lays Pick 8 under
    // it), so click the first card's Save that the picker does not cover.
    const pickerBox = (await page.getByTestId("quick-reason-picker").boundingBox())!;
    let elsewhere = null;
    for (const n of [8, 9]) {
      const candidate = page.getByTestId("rec-card").filter({ hasText: `Pick ${n}` });
      const box = (await candidate.getByTestId("quick-save").boundingBox())!;
      const overlaps = box.x < pickerBox.x + pickerBox.width && pickerBox.x < box.x + box.width
        && box.y < pickerBox.y + pickerBox.height && pickerBox.y < box.y + box.height;
      if (!overlaps) { elsewhere = { card: candidate, n }; break; }
    }
    expect(elsewhere, "a Save outside the reason picker to click").not.toBeNull();
    await page.route(`**/api/titles/movie/${1000 + elsewhere!.n}/reactions/save_for_later`, (route) =>
      route.fulfill({ status: 200, contentType: "application/json", body: "{}" }));
    await elsewhere!.card.hover();
    await elsewhere!.card.getByTestId("quick-save").click();
    await expect(page.getByTestId("quick-reason-picker")).toHaveCount(0);
    await settleCloseAutoFocus(page);
    await expect(notForMe).not.toBeFocused();
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
  test("both pickers are named dialogs behind triggers that say they open one (PH-04)", async ({ browser }) => {
    // axe flagged the score and reason popovers as unnamed dialogs (aria-dialog-name, serious) on a
    // phone, behind a trigger whose aria-haspopup promised a menu.
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootHome(page);
    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });

    const rate = target.getByTestId("quick-rate");
    await expect(rate).toHaveAttribute("aria-haspopup", "dialog");
    await rate.tap();
    const ratePicker = page.getByRole("dialog", { name: "Rate Pick 6 out of 10" });
    await expect(ratePicker).toBeVisible();
    const rateScan = await new AxeBuilder({ page }).include('[data-testid="quick-rate-picker"]').analyze();
    expect(rateScan.violations.map((v) => v.id)).toEqual([]);
    await page.keyboard.press("Escape");
    await expect(ratePicker).toHaveCount(0);

    const notForMe = target.getByTestId("quick-not-interested");
    await expect(notForMe).toHaveAttribute("aria-haspopup", "dialog");
    await notForMe.click({ button: "right" });
    const reasonPicker = page.getByRole("dialog", { name: "Why not Pick 6?" });
    await expect(reasonPicker).toBeVisible();
    const reasonScan = await new AxeBuilder({ page }).include('[data-testid="quick-reason-picker"]').analyze();
    expect(reasonScan.violations.map((v) => v.id)).toEqual([]);
    await context.close();
  });
});
