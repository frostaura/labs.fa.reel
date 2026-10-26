import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * The phone-target acceptance probe for WS-10A's routes (IREV3S-08, O-15): at 390×844 on a touch
 * device, no interactive element inside the page's own content is smaller than 44 × 44.
 *
 * Scoped to `main`: the header pill and the tab bar are the shell's (WS-9), and they are probed on
 * the shell's own routes. An element counts if it is visible and focusable or clickable by role —
 * links, buttons, inputs, selects and anything with a non-negative tabindex. Inline text links
 * inside a sentence are the one exception WCAG 2.5.8 makes, and the one exception here: a link
 * whose parent is a paragraph of running text (`p`) is allowed its line height.
 */

const PHONE = { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true };

async function smallTargets(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const main = document.querySelector("main");
    if (!main) return ["<no main>"];
    const selector = 'a[href], button, input, select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])';
    const out: string[] = [];
    for (const el of Array.from(main.querySelectorAll<HTMLElement>(selector))) {
      const r = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (r.width === 0 || r.height === 0 || style.visibility === "hidden" || style.display === "none") continue;
      if (el.closest('[aria-hidden="true"]')) continue;
      // The inline-link exception: a link set in a run of text, not a control of its own.
      if (el.tagName === "A" && el.parentElement?.tagName === "P" && style.display === "inline") continue;
      // A screen-reader-only overlay link (the hero card's) is covered by the card it fills.
      if (r.width >= 44 && r.height >= 44) continue;
      const label = el.getAttribute("aria-label") ?? el.getAttribute("data-testid") ?? el.textContent?.trim().slice(0, 30) ?? el.tagName;
      out.push(`${el.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}×${Math.round(r.height)}`);
    }
    return out;
  });
}

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
  providers: [{ name: "Netflix", logoPath: null }],
});

const FILTERS = {
  excludeGenres: ["documentary", "horror", "romance"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};

async function boot(page: Page) {
  await stubEventSource(page);
  await mockHomeChrome(page, { filters: FILTERS });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
}

test.describe("phone targets on WS-10A routes", () => {
  test("Home has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/feed", {
      generatedAt: new Date().toISOString(),
      hero: [card(1), card(2)],
      rows: [{ kind: "because-you-loved", anchorTitleId: card(1).titleId, anchorName: "Anchor", title: null, items: [card(3), card(4), card(5)] }],
      lockedRowCount: 0,
    });
    await page.goto("/home");
    await expect(page.getByTestId("rec-card")).toHaveCount(3);
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  test("Saved has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    const item = (n: number) => ({
      titleId: `00000000-0000-0000-0000-0000000001${n}0`, mediaType: "Movie", tmdbId: 2000 + n, name: `Saved ${n}`,
      year: 2001, runtimeMinutes: 100, posterPath: null, predictedRating: 7.4, crowdRating: 7.1, crowdVotes: 500,
      availableNow: true, providers: [{ name: "Netflix", logoPath: null }], savedAt: "2026-10-23T10:00:00Z",
      daysSaved: 3, fitsEveningBudget: true, onManagedList: true, watchTonight: n === 1,
      watchTonightReason: n === 1 ? "Highest-predicted thing in your queue that's streaming and fits tonight." : null,
    });
    await mockJson(page, "**/api/saved", {
      items: [item(1), item(2), item(3)], eveningBudgetMinutes: 150, expectedAbsoluteError: 1.2,
      unscoredRemaining: 0, managedListUrl: null, sort: "tonight",
    });
    await page.goto("/saved");
    await expect(page.getByTestId("saved-card").first()).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });
});
