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
 * scoring a pick no longer costs a navigation either. Few ratings are made in the app today;
 * most arrive from Trakt hours later, which is why the loop never closes at the moment it is felt.
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
    // The toast's Undo takes focus first (HREV4-PH-02); moving on is done from there, so this spec
    // no longer races the toast's focus frame (REV7-PH-01, which this used to trip intermittently).
    await expect(page.getByTestId("card-undo-toast-undo")).toBeFocused();

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

  // HREV4-PH-02 (WCAG 2.4.3, 2.1.1): a keyboard action takes the focused card off the rail, and focus
  // used to fall to <body> with nothing to take it — Undo was 161 Tabs away on real data. Now a
  // keyboard action moves focus to the toast's Undo (0 Tabs), and closing the toast hands it to the
  // next card, else the previous one, else the rail — never to <body>. A pointer action leaves focus
  // where the pointer put it. Varied: every action, first/middle/last card, the hero's only card.
  test("a keyboard action puts focus on Undo, and the closed toast hands it to the next card, never <body> (HREV4-PH-02)", async ({ page }) => {
    await bootHome(page);
    await page.route(/\/api\/titles\/movie\/\d+\/(reactions\/\w+|rating)$/, (route) => {
      const rated = route.request().url().endsWith("/rating") && route.request().method() === "POST";
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(rated ? { userRating: 7 } : {}) });
    });
    const toast = page.getByTestId("card-undo-toast");
    const undo = page.getByTestId("card-undo-toast-undo");
    const pick = (n: number) => page.getByTestId("rec-card").filter({ hasText: `Pick ${n}` });
    const focusedTestId = () => page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      return el === document.body || !el ? "<body>" : (el.dataset.testid ?? el.closest("[data-testid]")?.getAttribute("data-testid") ?? el.tagName);
    });
    /**
     * Undo holds focus with no Tab at all (the acceptance allows one), and still holds it a few
     * frames later: under the dev server's StrictMode the toast's column was detached and re-attached
     * just after Undo took focus, which dropped it to <body>. Chromium's Tab then happened to land
     * on Undo; WebKit's Tab skips buttons, so it never did.
     */
    const expectUndoFocused = async () => {
      await expect(toast).toBeVisible();
      await expect(undo).toBeFocused();
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 50)))));
      await expect(undo).toBeFocused();
    };

    // Save from the first rail card, Enter on the button: Undo has focus; Undo hands focus to the
    // card that was next (Pick 7), and the saved card comes back.
    await pick(6).getByTestId("quick-save").focus();
    await page.keyboard.press("Enter");
    await expect(pick(6)).toHaveCount(0);
    await expectUndoFocused();
    await page.keyboard.press("Enter");
    await expect(toast).toHaveCount(0);
    await expect(pick(6)).toHaveCount(1);
    await expect(pick(7).getByTestId("rec-card-link")).toBeFocused();

    // Not for me from a middle card, then the toast's Dismiss: focus goes to the next card (Pick 8).
    await pick(7).getByTestId("quick-not-interested").focus();
    await page.keyboard.press("Enter");
    await expect(pick(7)).toHaveCount(0);
    await expectUndoFocused();
    await page.getByTestId("card-undo-toast-dismiss").focus();
    await page.keyboard.press("Enter");
    await expect(toast).toHaveCount(0);
    await expect(pick(8).getByTestId("rec-card-link")).toBeFocused();

    // Rate the LAST card through its picker: Undo has focus, and with no card after it, Dismiss
    // hands focus back to the one before (Pick 8).
    await pick(9).getByTestId("quick-rate").focus();
    await page.keyboard.press("Enter");
    await page.getByTestId("quick-rate-7").focus();
    await page.keyboard.press("Enter");
    await expect(pick(9)).toHaveCount(0);
    await expectUndoFocused();
    await page.getByTestId("card-undo-toast-dismiss").focus();
    await page.keyboard.press("Enter");
    await expect(pick(8).getByTestId("rec-card-link")).toBeFocused();

    // The hero's only card, from the reason picker: its rail leaves with it, so focus goes on to the
    // next card on the page — the first rail card, Pick 6 — rather than to <body>.
    const hero = page.getByTestId("hero-card");
    await hero.getByTestId("quick-not-interested").focus();
    await page.keyboard.press("ArrowDown");
    await page.getByTestId("quick-reason-genre").focus();
    await page.keyboard.press("Enter");
    await expect(hero).toHaveCount(0);
    await expectUndoFocused();
    await page.getByTestId("card-undo-toast-dismiss").focus();
    await page.keyboard.press("Enter");
    await expect(pick(6).getByTestId("rec-card-link")).toBeFocused();
    expect(await focusedTestId()).not.toBe("<body>");

    // A pointer action does not move focus to the toast.
    await pick(8).hover();
    await pick(8).getByTestId("quick-save").click();
    await expect(toast).toBeVisible();
    await expect(undo).not.toBeFocused();
  });
  /**
   * A route that answers only when the test says so: what the card does WHILE the request is out is
   * then asserted directly, with no race against a timer (HREV7-DESK-01).
   */
  async function holdReactions(page: Page, status = 200) {
    const calls: string[] = [];
    const releases: (() => void)[] = [];
    await page.route(/\/api\/titles\/movie\/\d+\/(reactions\/\w+|rating)$/, async (route) => {
      const url = new URL(route.request().url());
      calls.push(`${route.request().method()} ${url.pathname}`);
      await new Promise<void>((resolve) => releases.push(resolve));
      const rated = url.pathname.endsWith("/rating") && route.request().method() === "POST";
      await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(rated ? { userRating: 7 } : {}) });
    });
    return { calls, release: () => releases.splice(0).forEach((r) => r()) };
  }

  // HREV7-DESK-01. Optimistic UI: the card leaves on the tap, not on the server's answer, and comes
  // back if the server says no. Undo is still offered only once the server has said yes. Varied:
  // each action (save, not for me, rate), success and failure.
  for (const action of ["save", "dismiss", "rate"] as const) {
    test(`${action}: the card leaves on the tap, and Undo waits for the server (HREV7-DESK-01)`, async ({ page }) => {
      await bootHome(page);
      const held = await holdReactions(page);
      const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
      const act = async () => {
        await six.hover();
        if (action === "save") await six.getByTestId("quick-save").click();
        else if (action === "dismiss") await six.getByTestId("quick-not-interested").click();
        else {
          await six.getByTestId("quick-rate").click();
          await page.getByTestId("quick-rate-picker").getByTestId("quick-rate-7").click();
        }
      };
      await act();
      // The request is still out: the card is already gone and no Undo is on offer yet.
      await expect(six).toHaveCount(0);
      expect(held.calls).toHaveLength(1);
      await expect(page.getByTestId("card-undo-toast")).toHaveCount(0);
      await expect(page.getByTestId("rec-card")).toHaveCount(3);
      held.release();
      await expect(page.getByTestId("card-undo-toast")).toBeVisible();
      await expect(six).toHaveCount(0);
    });
  }

  test("a failed quick action brings the card back where it was, with no Undo (HREV7-DESK-01)", async ({ page }) => {
    await bootHome(page);
    const held = await holdReactions(page, 500);
    const seven = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
    await seven.hover();
    await seven.getByTestId("quick-not-interested").click();
    await expect(seven).toHaveCount(0);
    held.release();
    await expect(seven).toHaveCount(1);
    // Back in its own place, between Pick 6 and Pick 8.
    await expect(page.getByTestId("rec-card")).toHaveText([/Pick 6/, /Pick 7/, /Pick 8/, /Pick 9/]);
    await expect(page.getByTestId("card-undo-toast")).toHaveCount(0);
    expect(held.calls).toEqual(["POST /api/titles/movie/1007/reactions/not_interested"]);
  });

  test("a failed keyboard action brings the card back with focus on the control that sent it (HREV7-DESK-01)", async ({ page }) => {
    await bootHome(page);
    const held = await holdReactions(page, 500);
    const eight = page.getByTestId("rec-card").filter({ hasText: "Pick 8" });
    await eight.getByTestId("quick-save").focus();
    await page.keyboard.press("Enter");
    await expect(eight).toHaveCount(0);
    held.release();
    await expect(eight).toHaveCount(1);
    await expect(eight.getByTestId("quick-save")).toBeFocused();
    await expect(page.getByTestId("card-undo-toast")).toHaveCount(0);
  });

  // HREV7-DESK-01. A double tap used to post twice. With the card leaving on the first tap, the
  // second lands on whatever slid into its place — the NEXT card's same control — which must not act
  // either. Varied: both one-tap actions, and a gap inside and at the edge of a double tap.
  for (const [testId, gapMs] of [["quick-save", 150], ["quick-not-interested", 150], ["quick-save", 400]] as const) {
    test(`a double tap on ${testId} (${gapMs} ms apart) sends exactly one request (HREV7-DESK-01)`, async ({ page }) => {
      await bootHome(page);
      const held = await holdReactions(page);
      const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
      await six.hover();
      const box = (await six.getByTestId(testId).boundingBox())!;
      const at = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
      await page.mouse.click(at.x, at.y);
      await page.waitForTimeout(gapMs);
      await page.mouse.click(at.x, at.y);
      await page.waitForTimeout(200);
      expect(held.calls).toHaveLength(1);
      expect(held.calls[0]).toContain("/1006/");
      held.release();
      await expect(page.getByTestId("card-undo-toast")).toBeVisible();
      await expect(page.getByTestId("rec-card").filter({ hasText: "Pick 7" })).toHaveCount(1);
      expect(held.calls).toHaveLength(1);
    });
  }

  test("a deliberate tap on the next card, once the double-tap window has passed, still acts (HREV7-DESK-01)", async ({ page }) => {
    await bootHome(page);
    const held = await holdReactions(page);
    const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    await six.hover();
    const box = (await six.getByTestId("quick-save").boundingBox())!;
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(900);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await expect.poll(() => held.calls.length).toBe(2);
    expect(held.calls[1]).toContain("/1007/");
    held.release();
  });

  test("a failed keyboard undo puts focus on Try again, although closing the toast had handed it to the next card (REV7-PH-01)", async ({ page }) => {
    await bootHome(page);
    await page.route("**/api/titles/movie/1006/reactions/save_for_later", (route) =>
      route.fulfill({ status: route.request().method() === "POST" ? 200 : 500, contentType: "application/json", body: "{}" }));
    await page.getByTestId("rec-card").filter({ hasText: "Pick 6" }).getByTestId("quick-save").focus();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("card-undo-toast-undo")).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("card-undo-toast")).toContainText("Couldn’t undo — “Pick 6” is still saved.");
    await expect(page.getByTestId("card-undo-toast-undo")).toBeFocused();
    await expect(page.getByTestId("card-undo-toast-undo")).toHaveText("Try again");
  });

  // REV7-PH-01: a keyboard action hands focus to Undo only if nobody has moved it since. Someone who
  // went on to the next card mid-request kept their place no longer: the toast took focus, and their
  // next Enter undid the action instead of opening the card. Varied: moved by script and by Tab,
  // a slow and an instant server.
  // (Tab is only meaningful mid-request: once an instant answer has put focus on Undo, a Tab moves on
  // from there, as it should.)
  for (const how of ["focus", "tab"] as const) {
    for (const slow of how === "tab" ? [true] : [true, false]) {
      test(`a keyboard action does not take focus back from someone who moved on (${how}, ${slow ? "slow" : "instant"} server) (REV7-PH-01)`, async ({ page }) => {
        await bootHome(page);
        const calls: string[] = [];
        let release: () => void = () => undefined;
        const gate = slow ? new Promise<void>((resolve) => { release = resolve; }) : Promise.resolve();
        await page.route(/\/api\/titles\/movie\/\d+\/reactions\/\w+$/, async (route) => {
          calls.push(route.request().method());
          await gate;
          await route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
        });
        const six = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
        const sevenLink = page.getByTestId("rec-card").filter({ hasText: "Pick 7" }).getByTestId("rec-card-link");
        await six.getByTestId("quick-save").focus();
        await page.keyboard.press("Enter");
        let where: string;
        if (how === "focus") {
          await sevenLink.focus();
          where = "Pick 7";
        } else {
          await page.keyboard.press("Tab");
          where = await page.evaluate(() => {
            const el = document.activeElement;
            return !el || el === document.body ? "<body>" : `${el.getAttribute("data-testid")}|${el.textContent}`;
          });
          expect(where).not.toBe("<body>");
        }
        release();
        await expect(page.getByTestId("card-undo-toast")).toBeVisible();
        await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 50)))));
        await expect(page.getByTestId("card-undo-toast-undo")).not.toBeFocused();
        if (how === "focus") {
          await expect(sevenLink).toBeFocused();
          await page.keyboard.press("Enter");
          await expect(page).toHaveURL(/\/title\/movie\/1007/);
        } else {
          expect(await page.evaluate(() => {
            const el = document.activeElement;
            return !el || el === document.body ? "<body>" : `${el.getAttribute("data-testid")}|${el.textContent}`;
          })).toBe(where);
        }
        expect(calls).toEqual(["POST"]);
      });
    }
  }
});

/**
 * Press-and-hold on a TOUCH screen (HREV8-PH-02). The mouse hold above never exercised this path:
 * on touch the release fires a compatibility `mousedown` on the button AFTER the picker has opened,
 * which moved focus back to the anchor, and Radix's non-modal popover closed on that focus-outside —
 * so the "only affordance a touch device has spare" opened the picker and shut it again on release.
 *
 * A real touch through the Chrome DevTools Protocol (Playwright has no touch hold of its own), so
 * Chromium only: WebKit cannot be driven this way. Varied over the hero card and a rail card, a
 * short and a long hold, and the reason chosen.
 */
test.describe("card quick actions on a touch screen (HREV8-PH-02)", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  async function touchHold(page: Page, x: number, y: number, ms: number) {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
    await page.waitForTimeout(ms);
    // The hold has matured: the picker is open while the finger is still down.
    await expect(page.getByTestId("quick-reason-picker")).toBeVisible();
    await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
    await cdp.detach();
  }

  const CASES = [
    { where: "a rail card", name: "Pick 8", tmdbId: 1008, holdMs: 700, reason: "tone" },
    { where: "a rail card", name: "Pick 7", tmdbId: 1007, holdMs: 1500, reason: "seen-enough" },
    { where: "the hero", name: "Pick 1", tmdbId: 1001, holdMs: 700, reason: "genre" },
  ] as const;

  for (const { where, name, tmdbId, holdMs, reason } of CASES) {
    test(`a ${holdMs} ms hold on ${where} leaves the picker open after the finger lifts, and the reason tapped is posted`, async ({ page, browserName }) => {
      test.skip(browserName !== "chromium", "a touch hold is driven through CDP, which only Chromium speaks");
      await bootHome(page);

      const reactionCalls: unknown[] = [];
      await page.route(`**/api/titles/movie/${tmdbId}/reactions/not_interested`, (route) => {
        reactionCalls.push(route.request().postDataJSON());
        return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
      });

      const host = where === "the hero"
        ? page.getByTestId("hero-card").filter({ hasText: name })
        : page.getByTestId("rec-card").filter({ hasText: name });
      const notForMe = host.getByTestId("quick-not-interested");
      // Wholly on screen, sideways too: a rail's third card is only partly in a 390 px viewport.
      await host.evaluate((el) => el.scrollIntoView({ block: "center", inline: "center" }));
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const box = await notForMe.boundingBox();
      if (!box) throw new Error("quick-not-interested has no box");

      await touchHold(page, box.x + box.width / 2, box.y + box.height / 2, holdMs);

      const picker = page.getByTestId("quick-reason-picker");
      // Past every timer the release could have started (Radix's close runs on a later task).
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 300)));
      await expect(picker).toBeVisible();
      // The hold's own tap dismissed nothing.
      expect(reactionCalls).toHaveLength(0);
      await expect(host).toHaveCount(1);

      await picker.getByTestId(`quick-reason-${reason}`).tap();
      await expect(host).toHaveCount(0);
      expect(reactionCalls).toEqual([{ reason }]);
    });
  }

  test("a quick tap on touch is still the one-tap dismiss with the default reason", async ({ page, browserName }) => {
    test.skip(browserName !== "chromium", "kept beside the CDP hold specs it guards");
    await bootHome(page);

    const reactionCalls: unknown[] = [];
    await page.route("**/api/titles/movie/1009/reactions/not_interested", (route) => {
      reactionCalls.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const target = page.getByTestId("rec-card").filter({ hasText: "Pick 9" });
    await target.getByTestId("quick-not-interested").tap();
    await expect(target).toHaveCount(0);
    await expect(page.getByTestId("quick-reason-picker")).toHaveCount(0);
    expect(reactionCalls).toEqual([{ reason: "not-for-me" }]);
  });
});
