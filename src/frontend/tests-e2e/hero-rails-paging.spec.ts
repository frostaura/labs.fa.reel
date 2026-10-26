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
 *
 * Timing (R1-23). The backoff cases used to sleep through the real 1 s + 2 s + 4 s ladder, and two
 * of them hit their 30 s / 60 s test timeouts in an isolated run, where the first test also pays
 * Vite's cold compile. Now: the compile is paid once in `beforeAll` under its own budget; the
 * backoff runs on Playwright's clock, advanced explicitly, so no case waits on wall time for a
 * timer; every `/api` request the page makes is answered by a fixture here (an unanswered one is a
 * test failure, not a proxy error); and "nothing more happened" is checked after a round trip
 * through the page rather than after a sleep.
 *
 * Round 2 of R1-23 (13 whole-spec runs, 3 red) found three more clocks the spec was racing:
 * - Leaving Home is not the URL changing. Saved is a lazy chunk, and the router keeps Home mounted
 *   until it loads — long enough for an 800 ms 503 to land on a still-mounted Home, whose retry is
 *   then legitimate. Saved is now warmed with Home; the in-flight 503 is HELD until Home has
 *   actually unmounted, so it lands on a departed rail every time; and the page's clock is paused
 *   from then on (`clock.install()` alone keeps time running on its own).
 * - A rail's position at its `fetch` is a React render after the observer latched it, and a rail
 *   loading above it can move it a pixel or two in between (1046 against a limit of 1045). The
 *   margin is now checked where it is decided: the observer's own entry for that rail.
 * - "Wait 3 s and count" is gone: the lazy cases wait until the page is quiet — every request
 *   answered, two frames painted, a probe round-tripped — twice in a row.
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
  /** Install Playwright's clock before the page loads, so the backoff can be advanced explicitly. */
  clock?: boolean;
  /** Hold the first failing /feed/more unanswered until `release()` — a request caught in flight. */
  holdFirst?: boolean;
}

interface Booted {
  /** Every /feed/more POST, in arrival order. */
  bodies: MoreBody[];
  /** How many /feed/more requests have been answered — a request in flight is `bodies.length - answered()`. */
  answered: () => number;
  /** Lets a held request (`holdFirst`) answer. */
  release: () => void;
}

/** Paths the page asked for that no fixture answers, per test; asserted empty after each one. */
let unmocked: string[] = [];
/** Probe requests seen, by id — see `settle`. */
let probes = new Set<string>();

test.afterEach(() => {
  expect(unmocked, "every /api request the page makes is answered by a fixture").toEqual([]);
});

/**
 * The rest of the page's /api surface, answered so nothing falls through to the dev proxy: an
 * unanswered request used to be a proxy error against a port that might one day be the founder's
 * live API, and a widget that silently did not render. A catch-all is registered first, so every
 * specific mock wins over it; what it catches is recorded and fails the test in `afterEach`.
 */
async function answerTheShell(page: Page): Promise<void> {
  unmocked = [];
  probes = new Set();
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path.startsWith("/api/__probe/")) probes.add(path.slice("/api/__probe/".length));
      else unmocked.push(`${route.request().method()} ${path}`);
      return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
    }
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  await mockJson(page, "**/api/accuracy", {
    window: "365d", from: "2025-10-24T00:00:00Z", to: "2026-10-24T00:00:00Z", source: "exposures",
    headline: { withinOnePoint: 0, n: 0, meanAbsoluteError: null, bias: null, correlation: null, meanPredicted: null, meanActual: null },
    calibration: [], moments: [],
    conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
    counterfactual: [], learnedThisWeek: null,
  });
  await mockJson(page, "**/api/saved/recently-watched", { expectedAbsoluteError: null, items: [] });
  await mockJson(page, "**/api/saved", {
    managedListUrl: null, sort: "tonight", eveningBudgetMinutes: null, expectedAbsoluteError: null, unscoredRemaining: 0, items: [],
  });
  // Nothing honest to say: both bands answer 204 and render nothing.
  for (const path of ["/api/feed/learned", "/api/feed/cross-reference"]) {
    await page.route((u) => u.pathname === path, (route) => route.fulfill({ status: 204, body: "" }));
  }
}

/**
 * Lets every request the page has already started reach its route handler, then returns — the
 * replacement for "sleep a bit and see if anything else arrived". A probe request goes out from
 * the page after whatever the last clock advance fired, and the catch-all records it; anything those
 * timers started was issued before it.
 */
async function settle(page: Page): Promise<void> {
  const id = Math.random().toString(36).slice(2);
  await page.evaluate((probe) => void fetch(`/api/__probe/${probe}`).catch(() => undefined), id);
  await expect.poll(() => probes.has(id)).toBe(true);
}

/**
 * Advances the page's clock in one-second steps until `done` holds — the backoff ladder replayed at
 * the speed of the test, not of wall time. Stepping (rather than one big jump) gives each 503 time
 * to land and schedule its next retry before the clock moves past it.
 */
async function advanceUntil(page: Page, done: () => Promise<boolean> | boolean, timeout = 20_000): Promise<void> {
  await expect
    .poll(
      async () => {
        if (await done()) return true;
        await page.clock.runFor(1000);
        return done();
      },
      { timeout, intervals: [50] }
    )
    .toBe(true);
}

async function bootHome(page: Page, more: "ok" | "fail", options: BootOptions = {}): Promise<Booted> {
  const bodies: MoreBody[] = [];
  let answered = 0;
  const rows = options.rows ?? ROWS;
  await stubEventSource(page);
  await answerTheShell(page);
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/genres", { genres: GENRES.map((slug) => ({ slug, label: slug })) });
  await mockJson(page, "**/api/digest/current", {
    weekStart: options.digest ? "2026-10-19" : null,
    generatedAt: options.digest ? new Date().toISOString() : null,
    items: options.digest ?? [],
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: HERO, rows, lockedRowCount: 0 });
  let page_ = 0;
  let release: () => void = () => undefined;
  const held = options.holdFirst ? new Promise<void>((resolve) => (release = resolve)) : null;
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    async (route) => {
      const body = route.request().postDataJSON() as MoreBody;
      // Recorded on arrival, before anything is awaited, so a count read straight after `settle`
      // includes it.
      bodies.push(body);
      if (more === "fail") {
        if (held && bodies.length === 1) await held;
        await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "unavailable" }) });
        answered += 1;
        return;
      }
      page_ += 1;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ items: range(12).map((n) => card(String(100 + page_), n)), exhausted: false }),
      });
      answered += 1;
    }
  );
  await page.addInitScript(recordObserverAndRequests);
  if (options.clock) await page.clock.install();
  await page.goto("/home");
  await expect(page.getByTestId("hero-card").first()).toBeVisible();
  return { bodies, answered: () => answered, release: () => release() };
}

interface RailLog {
  /** Each genre rail's observer entries that reported it in range, in order. */
  near: Record<string, { seq: number; top: number; viewport: number }[]>;
  /** The sequence number of each genre rail's /feed/more requests, in order. */
  asked: Record<string, number[]>;
}

/**
 * Records, in the page, when the prefetch observer reported each genre rail in range (and where the
 * rail sat in that entry), and when each genre rail's `/feed/more` went out, on one shared sequence.
 *
 * The margin is decided by the observer's entry, so that is where it is measured. Reading the rail
 * at its request (as this spec did in round 1) reads it a React render later, and a rail above it
 * that has just loaded can push it a pixel or two in between — 1046 against a limit of 1045 in one
 * whole-spec run. Reading it in the route handler was later still: a full round trip.
 */
function recordObserverAndRequests(): void {
  const w = window as unknown as { __railLog: RailLog };
  const log: RailLog = { near: {}, asked: {} };
  w.__railLog = log;
  let seq = 0;
  const Native = window.IntersectionObserver;
  if (Native) {
    window.IntersectionObserver = class extends Native {
      constructor(callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
        super((entries, observer) => {
          for (const entry of entries) {
            const genre = (entry.target as HTMLElement).dataset?.genre;
            if (genre && entry.isIntersecting) {
              (log.near[genre] ??= []).push({ seq: ++seq, top: entry.boundingClientRect.top, viewport: window.innerHeight });
            }
          }
          callback(entries, observer);
        }, options);
      }
    };
  }
  const original = window.fetch.bind(window);
  window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : String(input);
    if (url.includes("/api/feed/more")) {
      const at = ++seq;
      const body = input instanceof Request ? input.clone().text() : Promise.resolve(String(init?.body ?? ""));
      void body.then((text) => {
        const genre = (JSON.parse(text) as { genre?: string | null }).genre;
        if (genre) (log.asked[genre] ??= []).push(at);
      });
    }
    return original(input, init);
  };
}

/** Two painted frames: layout has run, and the observers have delivered what it changed. */
async function frames(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

/**
 * Waits until Home has stopped asking: every /feed/more answered, two frames painted (so a rail a
 * loaded rail pushed into or out of range has been observed) and a probe round-tripped (so a request
 * that observation started has reached its handler) — with no new request, twice running. The
 * replacement for "wait three seconds and count".
 */
async function quiesce(page: Page, booted: Booted): Promise<void> {
  let quiet = 0;
  for (let round = 0; round < 40 && quiet < 2; round++) {
    const before = booted.bodies.length;
    await expect.poll(() => booted.answered() === booted.bodies.length).toBe(true);
    await frames(page);
    await settle(page);
    quiet = booted.bodies.length === before && booted.answered() === before ? quiet + 1 : 0;
  }
  expect(quiet, "Home stopped asking for pages").toBe(2);
}

/**
 * Vite compiles the app on the first page load, and on a loaded machine that alone can take most
 * of a 30 s test budget — which is how two of these cases timed out when run on their own and
 * passed in the full suite (R1-23). The compile is paid here, once, under its own budget, so every
 * test's budget is spent on the behaviour it asserts.
 */
test.beforeAll(async ({ browser }, testInfo) => {
  testInfo.setTimeout(120_000);
  const page = await browser.newPage();
  try {
    await bootHome(page, "ok");
    // Saved too: the navigation case leaves Home for it, and an uncompiled chunk keeps Home mounted.
    await page.getByRole("link", { name: /saved/i }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: "Saved" })).toBeVisible();
    expect(unmocked).toEqual([]);
  } finally {
    await page.close();
  }
});

/** Scrolls the whole page a screen at a time so every rail comes into range once. */
async function scrollThrough(page: Page): Promise<void> {
  for (let i = 0; i < 30; i++) {
    const done = await page.evaluate(() => {
      window.scrollBy(0, window.innerHeight * 0.8);
      return window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2;
    });
    // Two frames: the scroll is painted and the observers have run before the next step.
    await frames(page);
    if (done) break;
  }
}

test.describe("lazy rails", () => {
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    test(`a cold Home at ${viewport.width}×${viewport.height} asks for at most 2 pages and no genre rail`, async ({ page }) => {
      await page.setViewportSize(viewport);
      const booted = await bootHome(page, "ok");
      const { bodies } = booted;
      // The hero's seed of five is under its top-up line, so it always asks; then Home goes quiet.
      await expect.poll(() => bodies.some((b) => b.row === "hero")).toBe(true);
      await quiesce(page, booted);
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
        const booted = await bootHome(page, "ok", { rows: ROWS.slice(0, rowCount) });
        const { bodies } = booted;
        await expect.poll(() => bodies.some((b) => b.row === "hero")).toBe(true);
        await quiesce(page, booted);
        const asked = bodies.filter((b) => b.row === "genre");
        const log = await page.evaluate(() => (window as unknown as { __railLog: RailLog }).__railLog);
        for (const genre of new Set(asked.map((b) => b.genre!))) {
          const first = log.asked[genre]?.[0];
          expect(first, `genre:${genre} has a recorded request`).toBeDefined();
          // The observer reported the rail in range BEFORE it asked, from within the margin.
          const inRange = (log.near[genre] ?? []).filter((n) => n.seq < first!);
          expect(inRange.length, `genre:${genre} asked without the observer reporting it in range`).toBeGreaterThanOrEqual(1);
          const at = inRange[0];
          expect(at.top, `genre:${genre} came into range at ${at.top}`).toBeLessThanOrEqual(at.viewport + 200 + 1);
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
    const { bodies } = await bootHome(page, "ok");
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
    const { bodies } = await bootHome(page, "ok", { digest: DIGEST });
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
    const { bodies } = await bootHome(page, "ok");
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBeGreaterThanOrEqual(1);
    const firstHero = bodies.find((b) => b.row === "hero")!;
    for (const id of ROWS.flatMap((r) => r.items).map((c) => c.titleId)) {
      expect(firstHero.pageExcludeTitleIds).toContain(id);
    }
  });
});

test.describe("leaving a rail while its request is out", () => {
  test("navigating away mid-request schedules nothing more", async ({ page }) => {
    const { bodies, answered, release } = await bootHome(page, "fail", { holdFirst: true, clock: true });
    // The hero tops up at once, and its answer is held: the request is out when the user leaves.
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBe(1);
    expect(answered()).toBe(0);
    // From here the page's time moves only when the test moves it: `install()` alone leaves it
    // running, and a timer firing during the navigation would race the unmount.
    await page.clock.pauseAt((await page.evaluate(() => Date.now())) + 1000);
    await page.getByRole("link", { name: /saved/i }).first().click();
    // Left means unmounted, not the URL changing: the router keeps Home up until Saved has loaded.
    await expect(page.getByRole("heading", { level: 1, name: "Saved" })).toBeVisible();
    await expect(page.getByTestId("hero-card")).toHaveCount(0);
    await expect(page.getByTestId("genre-rail")).toHaveCount(0);
    // Nothing can have been scheduled yet: no request has failed, and the clock has not moved.
    expect(bodies).toHaveLength(1);
    // The held 503 lands on a Home that has gone...
    release();
    await expect.poll(() => answered()).toBe(1);
    // ...and then the whole ladder (1 s + 2 s + 4 s) and more passes on the page's clock. A row that
    // re-armed its backoff from beyond the grave would fire now.
    await page.clock.runFor(30_000);
    await settle(page);
    expect(bodies.length - 1, JSON.stringify(bodies.slice(1).map(railOf))).toBe(0);
  });

  test("switching the medium mid-request retires the old hero; only the new one pages", async ({ page }) => {
    const { bodies, answered, release } = await bootHome(page, "fail", { holdFirst: true, clock: true });
    await expect.poll(() => bodies.filter((b) => b.row === "hero").length).toBe(1);
    // The old hero's request is held and the page's time is paused, so nothing of the old hero can
    // fire before the switch, however slow the click.
    await page.clock.pauseAt((await page.evaluate(() => Date.now())) + 1000);
    await page.getByTestId("browse-filters").getByRole("button", { name: "Movies" }).click();
    const newHero = () => bodies.filter((b) => b.row === "hero" && b.mediaType === "movie");
    // The new hero has asked, so the old one is gone: its 503 lands on a retired rail.
    await expect.poll(() => newHero().length).toBeGreaterThanOrEqual(1);
    release();
    // The new hero runs its ladder to the Retry tile on the page's clock.
    await advanceUntil(page, async () => answered() === bodies.length && (await page.getByTestId("rail-retry").count()) === 1);
    // Long past the ladder, nothing else asks.
    await page.clock.runFor(30_000);
    await settle(page);
    const oldHero = bodies.filter((b) => b.row === "hero" && !b.mediaType);
    // The old hero made the one request that was already out, and nothing after it.
    expect(oldHero).toHaveLength(1);
    // The new hero is one rail with its own bounded backoff: four attempts, then the tile.
    expect(newHero()).toHaveLength(4);
  });
});

/** Every rail that asked, with how many times. */
function perRail(bodies: MoreBody[]): Map<string, number> {
  const counts = new Map<string, number>();
  for (const body of bodies) counts.set(railOf(body), (counts.get(railOf(body)) ?? 0) + 1);
  return counts;
}

test.describe("a failing /feed/more", () => {
  test("backs off, stops at four requests per rail, and ends in a Retry tile with no shimmer", async ({ page }) => {
    const { bodies, answered } = await bootHome(page, "fail", { clock: true });
    await scrollThrough(page);
    // The hero (seed of five, below the top-up line) and every genre rail try; each runs the
    // 1 s, 2 s, 4 s ladder on the page's clock and ends in its tile.
    const rails = GENRES.length + 1;
    await advanceUntil(page, async () => answered() === bodies.length && (await page.getByTestId("rail-retry").count()) === rails);
    // A storm would not need a timer to fire — so let a long stretch pass and look again.
    await page.clock.runFor(30_000);
    await scrollThrough(page);
    await settle(page);

    const counts = perRail(bodies);
    expect(counts.get("hero")).toBe(4);
    for (const genre of GENRES) expect(counts.get(`genre:${genre}`), `genre:${genre}`).toBe(4);
    expect(counts.size).toBe(rails);
    await expect(page.getByTestId("rail-retry")).toHaveCount(rails);
    await expect(page.locator(".reel-shimmer")).toHaveCount(0);

    // The seeded rows never needed a page, so they never failed and still show their cards.
    await expect(page.getByText("Row 3", { exact: true })).toBeVisible();
  });

  test("Retry is one more request for that rail only", async ({ page }) => {
    const { bodies, answered } = await bootHome(page, "fail", { clock: true });
    await scrollThrough(page);
    const action = page.locator('[data-testid="genre-rail"][data-genre="action"]');
    // Every rail (eight genres and the hero) spends its retries, so nothing else is scheduled.
    await advanceUntil(page, async () => answered() === bodies.length && (await page.getByTestId("rail-retry").count()) === GENRES.length + 1);
    const before = bodies.length;
    const actionBefore = bodies.filter((b) => b.genre === "action").length;
    expect(actionBefore).toBe(4);

    await action.getByTestId("rail-retry-button").click();
    await expect.poll(() => bodies.filter((b) => b.genre === "action").length).toBe(actionBefore + 1);
    await expect.poll(() => answered()).toBe(bodies.length);
    // Straight back to the tile — no fresh round of automatic retries, however long it waits — and
    // no other rail moved.
    await page.clock.runFor(30_000);
    await settle(page);
    expect(bodies.filter((b) => b.genre === "action").length).toBe(actionBefore + 1);
    expect(bodies.length - before).toBe(1);
    await expect(action.getByTestId("rail-retry")).toBeVisible();
  });
});
