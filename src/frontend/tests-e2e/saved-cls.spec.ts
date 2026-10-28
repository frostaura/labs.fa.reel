import { test, expect, type Browser } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";
import { SEIZED_US_PROVIDERS } from "../src/lib/whereToWatch.fixtures";

/**
 * Saved does not shove its queue down once it is on screen (HREV5-DESK-01).
 *
 * Live, every cold load of /saved scored a CLS of 0.168 at 834×1194 and 0.053 at 1440×900 (the bar
 * is 0.05 on every route). Measured here from the browser's own `layout-shift` entries, the one big
 * shift is the pinned pick's where-to-watch: the shelf was drawn the moment /saved answered, with
 * four shimmer tiles where the providers go, and when the providers landed the tiles grew into a
 * wall of chips and pushed the "Started watching" row, the held-out line and the whole queue down.
 * The Trakt list button arriving in the header nudged the title row as well.
 *
 * Each read is held back on purpose so it lands after the one before it — the order that produced
 * the defect. The fixtures vary what can arrive late: the pin's providers (the Seized ladder, a
 * dozen chips), a post-watch prompt above the shelf, and a shelf with no pin at all.
 */

const item = (n: number, extra: Record<string, unknown> = {}) => ({
  titleId: `00000000-0000-0000-0000-0000000002${String(n).padStart(2, "0")}`, mediaType: "Movie", tmdbId: 5000 + n,
  name: `Saved ${n}`, year: 2001, runtimeMinutes: 100, posterPath: null, predictedRating: 7.4 - n / 10, crowdRating: 7.1,
  crowdVotes: 500, availableNow: true, providers: [{ name: "Netflix", logoPath: null }], savedAt: "2026-10-23T10:00:00Z",
  daysSaved: 3, fitsEveningBudget: true, onManagedList: true, watchTonight: false, watchTonightReason: null, ...extra,
});

const PINNED = item(1, {
  watchTonight: true,
  watchTonightReason: "Highest-predicted thing in your queue that's streaming and fits tonight — 7.3 for you (±1.2), 1h 40m against your 2h 30m.",
});

const RECENT = {
  titleId: "00000000-0000-0000-0000-0000000000b1", mediaType: "Movie", tmdbId: 2001, name: "Sicario", year: 2015,
  posterPath: null, predictedRating: 8.4, watchedAt: "2026-10-27T00:00:00Z",
};

interface Fixture {
  pinned: boolean;
  recentlyWatched: unknown[];
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function savedCls(browser: Browser, context: Parameters<Browser["newContext"]>[0], fixture: Fixture) {
  const ctx = await browser.newContext(context);
  const page = await ctx.newPage();
  await page.addInitScript(() => {
    const w = window as unknown as { __cls: number; __shifts: string[] };
    w.__cls = 0;
    w.__shifts = [];
    new PerformanceObserver((list) => {
      type Source = { node: Node | null; previousRect: DOMRectReadOnly; currentRect: DOMRectReadOnly };
      for (const e of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean; sources?: Source[] })[]) {
        if (e.hadRecentInput) continue;
        w.__cls += e.value;
        const nodes = (e.sources ?? []).map((s) => {
          const el = s.node as HTMLElement | null;
          const name = el?.getAttribute?.("data-testid") ?? `${el?.tagName ?? "?"}.${String(el?.className ?? "").slice(0, 32)}`;
          return `${name} y${Math.round(s.previousRect.y)}→${Math.round(s.currentRect.y)} h${Math.round(s.previousRect.height)}→${Math.round(s.currentRect.height)}`;
        });
        w.__shifts.push(`${Math.round(e.startTime)} ms ${e.value.toFixed(4)} [${nodes.join(", ")}]`);
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  const items = fixture.pinned
    ? [PINNED, ...Array.from({ length: 11 }, (_, i) => item(i + 2))]
    : Array.from({ length: 12 }, (_, i) => item(i + 2, { availableNow: false }));
  let providersAnswered = !fixture.pinned;
  await page.route(
    (u) => u.pathname === "/api/saved",
    async (route) => {
      await delay(250);
      await route.fulfill({
        status: 200, contentType: "application/json",
        body: JSON.stringify({
          items, eveningBudgetMinutes: 150, expectedAbsoluteError: 1.2, unscoredRemaining: 0,
          managedListUrl: "https://trakt.tv/users/founder/lists/reel-up-next", sort: "tonight", heldOutByRules: 4,
        }),
      });
    }
  );
  await page.route(
    (u) => u.pathname === "/api/saved/recently-watched",
    async (route) => {
      await delay(450);
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ expectedAbsoluteError: 1.9, items: fixture.recentlyWatched }) });
    }
  );
  await page.route(
    (u) => u.pathname === `/api/titles/movie/${PINNED.tmdbId}/providers`,
    async (route) => {
      // Lands after the shelf's own read, as the live read did.
      await delay(400);
      providersAnswered = true;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(SEIZED_US_PROVIDERS) });
    }
  );
  await page.goto("/saved");
  const firstQueued = page.getByTestId("saved-card").first();
  await expect(firstQueued).toBeVisible();
  const before = (await firstQueued.boundingBox())!.y;
  await expect.poll(() => providersAnswered).toBe(true);
  if (fixture.pinned) await expect(page.getByTestId("provider-chip").first()).toBeVisible();
  if (fixture.recentlyWatched.length > 0) await expect(page.getByTestId("post-watch-strip")).toBeVisible();
  // Anything the late reads would shift has rendered, and the shift is recorded.
  await page.waitForTimeout(600);
  const after = (await firstQueued.boundingBox())!.y;
  const { cls, shifts } = await page.evaluate(() => {
    const w = window as unknown as { __cls: number; __shifts: string[] };
    return { cls: w.__cls, shifts: w.__shifts };
  });
  await ctx.close();
  return { cls, shifts, queueMovedPx: Math.abs(after - before) };
}

const VIEWPORTS = [
  { name: "834 (iPad, touch)", context: { viewport: { width: 834, height: 1194 }, hasTouch: true } },
  { name: "1440", context: { viewport: { width: 1440, height: 900 } } },
  { name: "390 (phone)", context: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true } },
];

const FIXTURES: [string, Fixture][] = [
  ["a pinned Watch tonight pick", { pinned: true, recentlyWatched: [] }],
  ["a pin and a post-watch prompt", { pinned: true, recentlyWatched: [RECENT] }],
  ["no pin", { pinned: false, recentlyWatched: [] }],
];

test.describe("Saved holds its layout while its reads land (HREV5-DESK-01)", () => {
  for (const { name, context } of VIEWPORTS) {
    for (const [label, fixture] of FIXTURES) {
      test(`CLS stays under 0.02 at ${name} with ${label}`, async ({ browser }) => {
        const { cls, shifts, queueMovedPx } = await savedCls(browser, context, fixture);
        // The direct measure: the queue does not move once it is on screen (it moved 224 px)…
        expect(queueMovedPx, `the first queued card moved ${queueMovedPx}px`).toBeLessThanOrEqual(1);
        // …and the browser's own score agrees.
        expect(cls, `cumulative layout shift ${cls.toFixed(4)}: ${shifts.join(" | ")}`).toBeLessThan(0.02);
      });
    }
  }
});
