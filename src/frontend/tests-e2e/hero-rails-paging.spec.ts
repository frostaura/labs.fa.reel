import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * How Home's rails ask for cards (I-2, F-13, O-7, O-22, F+3).
 *
 * - LAZY. A rail below the fold fetches nothing until the user nears it. Every `/feed/more` page is a
 *   billed curation call and a batch of exposure rows, and a cold Home used to fire one per rail —
 *   eight genre rails nobody scrolled to.
 * - BOUNDED. A failing `/feed/more` used to be retried as fast as `loading` flipped back to false:
 *   4,326 POSTs in ten seconds, with shimmers that never resolved. Now: 1 s, 2 s, 4 s, then a
 *   "Couldn't load — Retry" tile and no shimmer.
 * - DE-DUPLICATED. Every rail tells the server what the rest of the page already shows
 *   (`pageExcludeTitleIds`) under one `pageToken` per page view.
 */

const GENRES = ["action", "comedy", "drama", "horror", "thriller", "romance", "documentary", "animation"];

let seq = 0;
function card(prefix: string, n: number) {
  seq += 1;
  return {
    titleId: `00000000-0000-0000-${prefix.padStart(4, "0")}-${String(n).padStart(12, "0")}`,
    mediaType: "Movie",
    tmdbId: 50_000 + seq,
    name: `${prefix} ${n}`,
    year: 2020,
    runtimeMinutes: 100,
    posterPath: null,
    backdropPath: null,
    genres: ["drama"],
    predictedRating: 7.8,
    crowdRating: 7.2,
    crowdVotes: 5000,
    whyThis: "Because of the films you rate highest.",
    isReleased: true,
    confidence: "confident",
    floorUsed: 7.5,
  };
}

const range = (count: number) => Array.from({ length: count }, (_, i) => i + 1);

const HERO = range(5).map((n) => card("1", n));
const ROWS = ["2", "3", "4"].map((prefix, i) => ({
  kind: i === 0 ? "because-you-loved" : i === 1 ? "deep-cuts" : "new-for-you",
  anchorTitleId: i === 0 ? "00000000-0000-0000-0009-000000000001" : null,
  anchorName: i === 0 ? "Heat" : null,
  title: i === 0 ? null : `Row ${prefix}`,
  items: range(12).map((n) => card(prefix, n)),
}));

interface MoreBody {
  row: string;
  mediaType?: string;
  /** Test-side only: where the asking genre rail sat (top, viewport px) when its request arrived. */
  railTopAtRequest?: number;
  viewportHeight?: number;
  genre?: string | null;
  excludeTitleIds: string[];
  pageExcludeTitleIds?: string[];
  pageToken?: string;
}

/** Which rail a /feed/more POST belongs to: the genre for a genre rail, else the row kind. */
function railOf(body: MoreBody): string {
  return body.row === "genre" ? `genre:${body.genre}` : body.row;
}

const DIGEST = range(3).map((n) => card("5", n));

interface BootOptions {
  /** The snapshot's personalised rows; the default is three full rows of twelve. */
  rows?: typeof ROWS;
  /** The weekly digest rail's cards. */
  digest?: ReturnType<typeof card>[];
  /** Milliseconds before a failing /feed/more answers, so a request can be caught in flight. */
  failDelayMs?: number;
}

async function bootHome(page: Page, more: "ok" | "fail", options: BootOptions = {}): Promise<MoreBody[]> {
  const bodies: MoreBody[] = [];
  const rows = options.rows ?? ROWS;
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/genres", { genres: GENRES.map((slug) => ({ slug, label: slug })) });
  await mockJson(page, "**/api/digest/current", {
    weekStart: options.digest ? "2026-10-19" : null,
    generatedAt: options.digest ? new Date().toISOString() : null,
    items: options.digest ?? [],
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: HERO, rows, lockedRowCount: 0 });
  let page_ = 0;
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    async (route) => {
      const body = route.request().postDataJSON() as MoreBody;
      if (body.row === "genre") {
        const where = await page.evaluate((genre) => {
          const el = document.querySelector(`[data-testid="genre-rail"][data-genre="${genre}"]`);
          return { top: el ? el.getBoundingClientRect().top : Number.NaN, height: window.innerHeight };
        }, body.genre);
        body.railTopAtRequest = where.top;
        body.viewportHeight = where.height;
      }
      bodies.push(body);
      if (more === "fail") {
        if (options.failDelayMs) await new Promise((resolve) => setTimeout(resolve, options.failDelayMs));
        await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "unavailable" }) });
        return;
      }
      page_ += 1;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ items: range(12).map((n) => card(String(100 + page_), n)), exhausted: false }),
      });
    }
  );
  await page.goto("/home");
  await expect(page.getByTestId("hero-card").first()).toBeVisible();
  return bodies;
}

/** Scrolls the whole page a screen at a time so every rail comes into range once. */
async function scrollThrough(page: Page): Promise<void> {
  for (let i = 0; i < 30; i++) {
    const done = await page.evaluate(() => {
      window.scrollBy(0, window.innerHeight * 0.8);
      return window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2;
    });
    await page.waitForTimeout(120);
    if (done) break;
  }
}

test.describe("lazy rails", () => {
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    test(`a cold Home at ${viewport.width}×${viewport.height} asks for at most 2 pages and no genre rail`, async ({ page }) => {
      await page.setViewportSize(viewport);
      const bodies = await bootHome(page, "ok");
      await page.waitForTimeout(3000);
      expect(bodies.length, JSON.stringify(bodies.map(railOf))).toBeLessThanOrEqual(2);
      expect(bodies.filter((b) => b.row === "genre")).toHaveLength(0);
    });
  }

  /**
   * The margin is a promise about distance, not about how many rails a feed has: a rail fetches
   * when it is within 200px of the viewport and not before. With three full rows above them no
   * genre rail is that close (the test above); with fewer rows the first genre rails ARE on screen
   * and must fill, while the ones further down still wait. Every case asserts both halves: each
   * rail that asked was inside the margin when it asked, and each rail still inside it now has
   * asked. (A rail that has loaded is taller than its placeholder, so a rail that was inside the
   * margin when it asked can sit outside it a moment later — hence "when it asked".)
   */
  for (const [label, rowCount] of [["no personalised rows", 0], ["one personalised row", 1]] as const) {
    for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
      test(`with ${label} at ${viewport.width}×${viewport.height}, exactly the genre rails near the fold fetch`, async ({ page }) => {
        await page.setViewportSize(viewport);
        const bodies = await bootHome(page, "ok", { rows: ROWS.slice(0, rowCount) });
        await page.waitForTimeout(3000);
        const asked = bodies.filter((b) => b.row === "genre");
        for (const body of asked) {
          expect(body.railTopAtRequest, `genre:${body.genre} asked from ${body.railTopAtRequest}`).toBeLessThanOrEqual(
            body.viewportHeight! + 200 + 1
          );
        }
        const fetched = new Set(asked.map((b) => b.genre));
        const nearNow = await page.evaluate(() =>
          Array.from(document.querySelectorAll<HTMLElement>('[data-testid="genre-rail"]'))
            .filter((el) => el.getBoundingClientRect().top <= window.innerHeight + 200)
            .map((el) => el.dataset.genre!)
        );
        // With no rows the first genre rail is on screen and must have filled; with one row none is
        // within reach, and none asked.
        if (rowCount === 0) expect(nearNow.length).toBeGreaterThanOrEqual(1);
        else expect(fetched.size).toBe(0);
        for (const genre of nearNow) expect(fetched.has(genre), `genre:${genre} is on screen but never asked`).toBe(true);
        // Never the whole page: the rails further down wait for the user.
        expect(fetched.size).toBeLessThan(GENRES.length);
      });
    }
  }

  test("a genre rail fills once the user scrolls to it", async ({ page }) => {
    const bodies = await bootHome(page, "ok");
    await scrollThrough(page);
    await expect.poll(() => new Set(bodies.filter((b) => b.row === "genre").map((b) => b.genre)).size).toBe(GENRES.length);
    // And each rail asked for its first page once, not in a burst.
    for (const genre of GENRES) {
      expect(bodies.filter((b) => b.row === "genre" && b.genre === genre).length).toBeLessThanOrEqual(2);
    }
  });
});

test.describe("page-wide exclusions", () => {
  test("every rail sends what the rest of the page shows, under one page token", async ({ page }) => {
    const bodies = await bootHome(page, "ok", { digest: DIGEST });
    await expect(page.getByText("New & high for you this week")).toBeVisible();
    await scrollThrough(page);
    await expect.poll(() => bodies.filter((b) => b.row === "genre").length).toBeGreaterThanOrEqual(GENRES.length);

    const tokens = new Set(bodies.map((b) => b.pageToken));
    expect(tokens.size).toBe(1);
    expect([...tokens][0]).toMatch(/^[A-Za-z0-9]{1,64}$/);

    const firstGenre = bodies.find((b) => b.row === "genre")!;
    // The hero, every seeded row and the weekly digest are on the page, so the genre rail excludes
    // all of them...
    for (const id of [...HERO, ...ROWS.flatMap((r) => r.items), ...DIGEST].map((c) => c.titleId)) {
      expect(firstGenre.pageExcludeTitleIds).toContain(id);
    }
    // ...while its own depth stays in excludeTitleIds, and is not double-counted.
    for (const id of firstGenre.excludeTitleIds) {
      expect(firstGenre.pageExcludeTitleIds).not.toContain(id);
    }
  });

  test("the hero's first top-up already excludes the rows below it", async ({ page }) => {
    // The hero's seed of five is under the top-up line, so it asks at once — before the rows below
    // it have run a single effect. The registry is seeded from the snapshot when it is created.
    const bodies = await bootHome(page, "ok");
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBeGreaterThanOrEqual(1);
    const firstHero = bodies.find((b) => b.row === "hero")!;
    for (const id of ROWS.flatMap((r) => r.items).map((c) => c.titleId)) {
      expect(firstHero.pageExcludeTitleIds).toContain(id);
    }
  });
});

test.describe("leaving a rail while its request is out", () => {
  test("navigating away mid-request schedules nothing more", async ({ page }) => {
    test.setTimeout(30_000);
    await mockJson(page, "**/api/saved", []);
    const bodies = await bootHome(page, "fail", { failDelayMs: 800 });
    // The hero tops up at once and the answer is 800 ms away: leave Home inside that window.
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBe(1);
    await page.getByRole("link", { name: /saved/i }).first().click();
    await expect(page).toHaveURL(/\/saved/);
    const atExit = bodies.length;
    // Long enough for the in-flight 503 to land and the 1 s and 2 s retries to have fired.
    await page.waitForTimeout(5000);
    expect(bodies.length - atExit, JSON.stringify(bodies.slice(atExit).map(railOf))).toBe(0);
  });

  test("switching the medium mid-request retires the old hero; only the new one pages", async ({ page }) => {
    test.setTimeout(30_000);
    const bodies = await bootHome(page, "fail", { failDelayMs: 800 });
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBe(1);
    await page.getByTestId("browse-filters").getByRole("button", { name: "Movies" }).click();
    await page.waitForTimeout(10_000);
    const oldHero = bodies.filter((b) => b.row === "hero" && !b.mediaType);
    const newHero = bodies.filter((b) => b.row === "hero" && b.mediaType === "movie");
    // The old hero made the one request that was already out, and nothing after it.
    expect(oldHero).toHaveLength(1);
    // The new hero is one rail with its own bounded backoff.
    expect(newHero.length).toBeGreaterThanOrEqual(1);
    expect(newHero.length).toBeLessThanOrEqual(4);
  });
});

test.describe("a failing /feed/more", () => {
  test("backs off, stops at four requests per rail, and ends in a Retry tile with no shimmer", async ({ page }) => {
    test.setTimeout(60_000);
    const bodies = await bootHome(page, "fail");
    await scrollThrough(page);
    const started = Date.now();
    // The 1 s + 2 s + 4 s backoff plus request time; sample at the ten-second mark.
    await page.waitForTimeout(Math.max(0, 10_000 - (Date.now() - started)));
    await scrollThrough(page);
    await page.waitForTimeout(1000);

    const perRail = new Map<string, number>();
    for (const body of bodies) perRail.set(railOf(body), (perRail.get(railOf(body)) ?? 0) + 1);
    // Every genre rail and the hero (seed of five, below the top-up line) tried.
    expect(perRail.get("hero")).toBeDefined();
    for (const genre of GENRES) expect(perRail.get(`genre:${genre}`), `genre:${genre}`).toBeDefined();
    for (const [rail, count] of perRail) expect(count, rail).toBeLessThanOrEqual(4);

    const failedRails = [...perRail.values()].filter((count) => count === 4).length;
    await expect(page.getByTestId("rail-retry")).toHaveCount(failedRails);
    await expect(page.locator(".reel-shimmer")).toHaveCount(0);

    // The seeded rows never needed a page, so they never failed and still show their cards.
    await expect(page.getByText("Row 3", { exact: true })).toBeVisible();
  });

  test("Retry is one more request for that rail only", async ({ page }) => {
    test.setTimeout(60_000);
    const bodies = await bootHome(page, "fail");
    await scrollThrough(page);
    const action = page.locator('[data-testid="genre-rail"][data-genre="action"]');
    // Every rail (eight genres and the hero) has spent its retries, so nothing else is scheduled.
    await expect(page.getByTestId("rail-retry")).toHaveCount(GENRES.length + 1, { timeout: 20_000 });
    const before = bodies.length;
    const actionBefore = bodies.filter((b) => b.genre === "action").length;

    await action.getByTestId("rail-retry-button").click();
    await expect.poll(() => bodies.filter((b) => b.genre === "action").length).toBe(actionBefore + 1);
    await page.waitForTimeout(2500);
    // Straight back to the tile — no fresh round of automatic retries — and no other rail moved.
    expect(bodies.filter((b) => b.genre === "action").length).toBe(actionBefore + 1);
    expect(bodies.length - before).toBe(1);
    await expect(action.getByTestId("rail-retry")).toBeVisible();
  });
});
