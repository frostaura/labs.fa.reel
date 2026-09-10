import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubAskReel, stubEventSource } from "./helpers";

/**
 * Ask Reel on a phone.
 *
 * The composer is `fixed` and so is the mobile tab bar, and for months they occupied the same
 * strip of screen: the tab bar sits at `z-40`, so on every phone width the Send button and the
 * text input were *behind* it — a tap on Send navigated to /dna instead. A visibility assertion
 * cannot see this (the composer is perfectly visible, just covered), so these tests hit-test
 * instead: `document.elementFromPoint` at the centre of each control must resolve back INSIDE
 * that control. Three widths, because the offending overlap is a function of the tab bar's
 * height plus the safe-area inset, not of the layout breakpoint.
 *
 * Also locks the transcript's `log` landmark: Ask Reel's replies arrive asynchronously, so a
 * screen-reader user hears them only if the region announces itself.
 */

// The reference phone. `test.use` per file rather than a second Playwright project, so CI's
// matrix stays at one browser (the precedent is `mobile-nav.spec.ts`).
test.use({ viewport: { width: 375, height: 812 } });

async function bootSearch(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  // A cold /search resumes the latest conversation; 404 = there is none, so the page stays fresh.
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
}

/**
 * Hit-tests one element: is the topmost node at its centre point the element itself (or something
 * inside it)? Returns what was actually hit so a failure names the covering element rather than
 * just reporting `false`.
 */
async function hitTest(page: Page, testId: string): Promise<{ ok: boolean; hit: string }> {
  return page.evaluate((id) => {
    const el = document.querySelector(`[data-testid="${id}"]`);
    if (!el) return { ok: false, hit: `no element with data-testid="${id}"` };
    const box = el.getBoundingClientRect();
    const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    if (!top) return { ok: false, hit: "nothing (point is outside the viewport)" };
    const named = top.closest("[data-testid]")?.getAttribute("data-testid");
    const href = top.closest("a")?.getAttribute("href");
    return {
      ok: el.contains(top) || el === top,
      hit: named ?? href ?? top.tagName.toLowerCase(),
    };
  }, testId);
}

/** Both composer controls must be reachable by a real tap at whatever width is current. */
async function expectComposerReachable(page: Page, label: string) {
  await expect(page.getByTestId("askreel-composer")).toBeVisible();
  const send = await hitTest(page, "askreel-send");
  expect(send.ok, `${label}: a tap on Send lands on "${send.hit}"`).toBe(true);
  const input = await hitTest(page, "askreel-composer");
  expect(input.ok, `${label}: a tap on the input lands on "${input.hit}"`).toBe(true);
}

test.describe("Ask Reel on a phone", () => {
  test("the composer is tappable above the mobile tab bar", async ({ page }) => {
    await bootSearch(page);
    // Typed, so Send is enabled — a disabled button is still hit-tested, but the assertion should
    // describe the control the user actually reaches for.
    await page.getByTestId("askreel-composer").fill("something tense");

    await expect(page.getByTestId("mobile-tabbar")).toBeVisible();
    await expectComposerReachable(page, "375×812");
  });

  test("the composer stays tappable on a taller phone and on the smallest one", async ({ page }) => {
    await bootSearch(page);
    await page.getByTestId("askreel-composer").fill("something tense");

    // A modern large phone: more safe-area inset at the bottom.
    await page.setViewportSize({ width: 390, height: 844 });
    await expectComposerReachable(page, "390×844");

    // The smallest width the app claims to support — the tab bar's four labels are tightest here.
    await page.setViewportSize({ width: 320, height: 568 });
    await expectComposerReachable(page, "320×568");
  });

  test("transcript is a log landmark with speaker labels", async ({ page }) => {
    await bootSearch(page);

    const transcript = page.getByTestId("askreel-transcript");
    await expect(transcript).toHaveAttribute("role", "log");
    await expect(transcript).toHaveAttribute("aria-live", "polite");

    await page.getByTestId("askreel-composer").fill("tense small-town thriller");
    await page.getByTestId("askreel-composer").press("Enter");

    // The user's own turn, and Reel's reply, are each announced with who is speaking — without
    // the labels a screen reader reads one undifferentiated run of sentences.
    await expect(transcript.getByText("You said", { exact: true })).toHaveCount(1);
    await expect(transcript.getByText("tense small-town thriller")).toBeVisible();
  });
});
