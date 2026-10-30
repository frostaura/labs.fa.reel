import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { mockFilterChips } from "./filterChips.fixture";
import { SESSION_FOUNDER, axeScan, mockJson, stubEventSource, syncJob, syncStatus } from "./helpers";
import {
  PERSON_ID,
  TITLE,
  mockDna,
  mockHome,
  mockLab,
  mockPerson,
  mockRate,
  mockSaved,
  mockSearch,
  mockTitle,
  mockTmdbImages,
  mockTrackRecord,
} from "./route-fixtures";

/**
 * Accessibility on every route (WS-9 task 10, ADD-19): the FULL axe rule set — not only colour
 * contrast — in Chromium and WebKit, at a desktop and a phone viewport, on each page as a person
 * actually reads it, and with each overlay open.
 *
 * PH-03: this gate used to check pages in their empty or error states — /dna with no genres, eras,
 * creators or pockets, /lab and /person unmocked (404 error states), a title with no cast, /rate
 * with no suggestions, at 1440 × 900 only, with no overlay ever open — and reported green while the
 * same routes on real data failed axe. Now:
 *
 * - every page is fed a POPULATED fixture (tests-e2e/route-fixtures.ts) and `ready` proves the
 *   populated content rendered before axe runs;
 * - every /api call the page makes must be answered by a fixture: an unanswered one fails the case
 *   (it would otherwise be checked in a partial error state and pass on less than the page);
 * - every route runs at 1440 × 900 and on a 390 × 844 touch phone;
 * - the card's rate and reason popovers, the sync-pill popover and the title sheet are checked open.
 *
 * Each surface owner fixes its own violations, so cases are held to a baseline that only shrinks
 * (`a11y-baseline.json`, case → rule → node count): a rule a case does not already break, or more
 * nodes breaking one it does, fails the run. Removing a fixed rule from the baseline is the owner's
 * last step, and the baseline is EXACT: a case breaking fewer nodes than its entry allows fails too,
 * naming the entry to lower, so a fix cannot leave slack behind for a regression to use. An entry
 * is added only when new coverage finds debt that already shipped, never for a regression, and
 * every entry names its owner and change request in the baseline's `_owed` map.
 */

type Baseline = Record<string, Record<string, number>>;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const { _owed: OWED, ...BASELINE } = JSON.parse(readFileSync(path.join(HERE, "a11y-baseline.json"), "utf8")) as {
  _owed: Record<string, string>;
} & Baseline;

const FILTERS = {
  excludeGenres: ["horror"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

const VIEWPORTS = {
  desktop: { viewport: { width: 1440, height: 900 } },
  phone: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true },
} as const;
type Viewport = keyof typeof VIEWPORTS;

/**
 * Answers the shell's own calls, and every /api call nothing else answers with a 404 that is
 * RECORDED: a case fails if its page asked for data no fixture gave it.
 */
async function shell(page: Page, session: unknown = SESSION_FOUNDER, status: unknown = syncStatus()): Promise<string[]> {
  const unanswered: string[] = [];
  await stubEventSource(page);
  await mockTmdbImages(page);
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => {
      unanswered.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
      return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
    }
  );
  if (session) await mockJson(page, "**/api/auth/me", session);
  else await mockJson(page, "**/api/auth/me", {}, 401);
  await mockJson(page, "**/api/sync/status", status);
  return unanswered;
}

interface AxeCase {
  name: string;
  path: string;
  /** What the page needs beyond the shell. */
  mock?: (page: Page) => Promise<void>;
  /** Proof the page rendered its POPULATED content, so axe checks the page and not an empty state. */
  ready: (page: Page, viewport: Viewport) => Promise<void>;
  /** Opens the overlay under test, for overlay cases; resolves once it is open. */
  open?: (page: Page, viewport: Viewport) => Promise<void>;
  session?: unknown;
  status?: unknown;
}

const homeReady = async (page: Page) => {
  await expect(page.getByTestId("hero-card")).toHaveCount(5);
  await expect(page.getByTestId("rec-card")).toHaveCount(6);
};

/** The first rail card's quick actions, revealed the way each pointer reveals them. */
async function railCard(page: Page, viewport: Viewport) {
  const card = page.getByTestId("rec-card").first();
  await card.scrollIntoViewIfNeeded();
  if (viewport === "desktop") await card.hover();
  return card;
}

const ROUTES: AxeCase[] = [
  {
    name: "landing",
    path: "/",
    session: null,
    ready: (page) => expect(page.getByRole("button", { name: /sign in with trakt/i })).toBeVisible(),
  },
  { name: "home", path: "/home", mock: mockHome, ready: homeReady },
  {
    name: "dna",
    path: "/dna",
    mock: mockDna,
    ready: async (page) => {
      await expect(page.getByTestId("taste-pocket")).toHaveCount(3);
      await expect(page.getByTestId("genre-fingerprint")).toBeVisible();
      await expect(page.getByText("Michael Mann").first()).toBeVisible();
      // The dislike history, expanded, so its rows are checked too.
      await page.getByTestId("dislike-toggle").click();
      await expect(page.getByTestId("dislike-row")).toHaveCount(3);
    },
  },
  {
    name: "title",
    path: "/title/movie/1001",
    mock: (page) => mockTitle(page),
    ready: async (page) => {
      await expect(page.getByTestId("title-detail")).toBeVisible();
      await expect(page.getByText("Cast Member 1").first()).toBeVisible();
      await expect(page.locator('[data-testid="title-detail"] img[src*="/cast1.jpg"]').first()).toBeAttached();
    },
  },
  {
    name: "settings",
    path: "/settings",
    mock: async (page) => {
      await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
      await mockJson(page, "**/api/preferences/tags", []);
      await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
      await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
      await mockJson(page, "**/api/settings/filters", FILTERS);
      await mockFilterChips(page);
    },
    ready: (page) => expect(page.getByTestId("prefs-section")).toBeVisible(),
  },
  {
    name: "saved",
    path: "/saved",
    mock: mockSaved,
    ready: async (page) => {
      await expect(page.getByText("Saved 1").first()).toBeVisible();
      await expect(page.getByText("Saved 8").first()).toBeAttached();
    },
  },
  {
    name: "search",
    path: "/search",
    mock: async (page) => {
      await mockJson(page, "**/api/settings/filters", FILTERS);
      await mockSearch(page);
    },
    ready: async (page) => {
      await expect(page.getByText("Answer 1").first()).toBeVisible();
      await expect(page.getByText("Answer 3").first()).toBeVisible();
    },
  },
  {
    name: "track-record",
    path: "/track-record",
    mock: mockTrackRecord,
    ready: async (page) => {
      await expect(page.getByTestId("track-record")).toBeVisible();
      await expect(page.getByText("Right Call").first()).toBeVisible();
    },
  },
  {
    name: "lab",
    path: "/lab",
    mock: mockLab,
    ready: async (page) => {
      await expect(page.getByTestId("gate-badge")).toBeVisible();
      await expect(page.getByTestId("interleave-slates")).toHaveText("148");
      await expect(page.getByTestId("iteration-ledger")).toBeVisible();
    },
  },
  {
    name: "rate",
    path: "/rate",
    mock: mockRate,
    ready: (page) => expect(page.getByTestId("rate-card")).toHaveCount(8),
  },
  {
    name: "onboarding",
    path: "/onboarding",
    session: { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Ingesting" },
    status: syncStatus({ pipelineStage: "Ingesting" }),
    ready: (page) => expect(page.getByTestId("stage-copy")).toBeVisible(),
  },
  {
    name: "person",
    path: `/person/${PERSON_ID}`,
    mock: mockPerson,
    ready: async (page) => {
      await expect(page.getByRole("heading", { name: "Cast Member 1" })).toBeVisible();
      await expect(page.getByText("Credit 1").first()).toBeVisible();
      await expect(page.getByTestId("person-hidden")).toBeVisible();
    },
  },
];

const OVERLAYS: AxeCase[] = [
  {
    name: "card-rate-popover",
    path: "/home",
    mock: mockHome,
    ready: homeReady,
    open: async (page, viewport) => {
      const card = await railCard(page, viewport);
      await card.getByTestId("quick-rate").click();
      await expect(page.getByTestId("quick-rate-picker")).toBeVisible();
    },
  },
  {
    name: "card-reason-popover",
    path: "/home",
    mock: mockHome,
    ready: homeReady,
    open: async (page, viewport) => {
      const card = await railCard(page, viewport);
      // Right-click is the desktop path and long-press the touch one; both open the same picker.
      await card.getByTestId("quick-not-interested").click({ button: "right" });
      await expect(page.getByTestId("quick-reason-picker")).toBeVisible();
    },
  },
  {
    name: "sync-pill-popover",
    path: "/home",
    mock: mockHome,
    status: syncStatus({ activeJob: syncJob() }),
    ready: homeReady,
    open: async (page) => {
      await page.getByTestId("sync-pill").click();
      await expect(page.getByTestId("sync-tooltip")).toBeVisible();
    },
  },
  {
    // HREV5-PH-04: Radix's modal menu aria-hid everything outside itself, the skip link and the
    // header's links included, while they stayed focusable — and no case ever opened it.
    name: "account-menu",
    path: "/home",
    mock: mockHome,
    ready: homeReady,
    open: async (page, viewport) => {
      // Tapped on the phone, clicked on the desktop: each pointer's own way in.
      const trigger = page.getByRole("button", { name: /account menu/i });
      if (viewport === "phone") await trigger.tap();
      else await trigger.click();
      await expect(page.getByRole("menu")).toBeVisible();
      await expect(page.getByRole("menuitem", { name: "Sign out" })).toBeVisible();
    },
  },
  {
    name: "title-sheet",
    path: "/home",
    mock: async (page) => {
      await mockHome(page);
      await mockTitle(page, { ...TITLE, tmdbId: 9101, name: "Pick 1" });
    },
    ready: homeReady,
    open: async (page) => {
      await page.getByTestId("hero-card-link").first().click();
      const dialog = page.getByRole("dialog");
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText("Cast Member 1").first()).toBeVisible();
    },
  },
];

async function survey(page: Page, key: string) {
  // One page per case, never two (HREV5-PH-02): WebKit's per-browser page cap is what the WebKit
  // shards budget against, so a scan that opened a page of its own would spend it unseen.
  const opened: Page[] = [];
  const onPage = (p: Page) => opened.push(p);
  page.context().on("page", onPage);
  const found = (await axeScan(page).analyze()).violations;
  page.context().off("page", onPage);
  expect(opened.length, "the axe scan opened a page of its own").toBe(0);
  if (process.env.A11Y_RECORD) {
    // Survey mode for an owner checking progress: what this case breaks now, per browser. A11Y_RECORD
    // names the directory (a path), so a Chromium and a WebKit run do not clear each other's files.
    const dir = path.isAbsolute(process.env.A11Y_RECORD) ? process.env.A11Y_RECORD : path.join(test.info().project.outputDir, "a11y-survey");
    mkdirSync(dir, { recursive: true });
    const detail = found.map((v) => ({ rule: v.id, impact: v.impact, nodes: v.nodes.length, targets: v.nodes.map((n) => n.target.join(" ")) }));
    writeFileSync(path.join(dir, `${key}.${test.info().project.name}.json`), JSON.stringify(detail, null, 1));
  }
  const allowed = BASELINE[key] ?? {};
  const over = found
    .filter((v) => v.nodes.length > (allowed[v.id] ?? 0))
    .map((v) => ({
      rule: v.id,
      impact: v.impact,
      nodes: v.nodes.length,
      baseline: allowed[v.id] ?? 0,
      help: v.help,
      targets: v.nodes.slice(0, 5).map((n) => n.target.join(" ")),
    }));
  // Slack: an owed rule now breaking fewer nodes (or none) — the fix landed; lower the entry.
  const loose = Object.entries(allowed)
    .map(([rule, nodes]) => ({ rule, baseline: nodes, nodes: found.find((v) => v.id === rule)?.nodes.length ?? 0 }))
    .filter((r) => r.nodes < r.baseline)
    .map((r) => ({ ...r, fix: `lower a11y-baseline.json "${key}".${r.rule} to ${r.nodes}` }));
  return { over, loose };
}

for (const [viewport, options] of Object.entries(VIEWPORTS) as [Viewport, (typeof VIEWPORTS)[Viewport]][]) {
  test.describe(`axe at ${viewport} size`, () => {
    test.use(options);

    for (const c of [...ROUTES, ...OVERLAYS]) {
      const key = `${c.name}@${viewport}`;
      test(`axe: ${c.name} (${c.path}${c.open ? ", open" : ""}) breaks no rule beyond its baseline`, async ({ page }) => {
        const unanswered = await shell(page, c.session === undefined ? SESSION_FOUNDER : c.session, c.status ?? syncStatus());
        await c.mock?.(page);
        await page.goto(c.path);
        await c.ready(page, viewport);
        // Settled: the page fallback is gone and the network is quiet.
        await expect(page.getByTestId("page-fallback")).toHaveCount(0);
        await page.waitForLoadState("networkidle");
        await c.open?.(page, viewport);

        expect(unanswered, `${c.path} asked for data its fixture does not give`).toEqual([]);
        const { over, loose } = await survey(page, key);
        expect(over, `new accessibility violations on ${key}`).toEqual([]);
        expect(loose, `a11y-baseline.json allows more than ${key} now breaks`).toEqual([]);
      });
    }
  });
}

test("every a11y baseline entry is a case this file runs, and names its owner and change request", () => {
  const cases = new Set(
    (Object.keys(VIEWPORTS) as Viewport[]).flatMap((v) => [...ROUTES, ...OVERLAYS].map((c) => `${c.name}@${v}`))
  );
  for (const key of Object.keys(BASELINE)) {
    expect(cases.has(key), `a11y-baseline.json names "${key}", which no case runs`).toBe(true);
    expect(OWED[key] ?? "", `a11y-baseline.json "${key}" has no _owed note`).toMatch(/^WS-\S+ .+\(PH-\d+ CR-\d+\)$/);
  }
  expect(Object.keys(OWED).sort()).toEqual(Object.keys(BASELINE).sort());
});

// ── Keyboard paths (task 10): navigation, cards and the title modal, in Chromium and WebKit ──────

/** WebKit, like Safari, only Tabs to links with Option held. */
const tabKey = (browserName: string) => (browserName === "webkit" ? "Alt+Tab" : "Tab");

/** How long one focus check may take before the page counts as not answering. */
const FOCUS_CHECK_MS = 5_000;

/**
 * Presses Tab until the focused element matches `selector`; fails after `max` presses. Each check
 * is bounded (HREV5-PH-02): a page that stops answering fails here, naming the press, instead of
 * hanging the test past its timeout and its teardown with a trace that never finishes writing.
 */
async function tabTo(page: Page, key: string, selector: string, max = 40): Promise<void> {
  for (let i = 0; i < max; i++) {
    await page.keyboard.press(key);
    let timer: ReturnType<typeof setTimeout> | undefined;
    const silent = new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error(`the page did not answer a focus check within ${FOCUS_CHECK_MS} ms after press ${i + 1} towards ${selector}`)), FOCUS_CHECK_MS);
    });
    try {
      if (await Promise.race([page.evaluate((sel) => document.activeElement?.matches(sel) === true, selector), silent])) return;
    } finally {
      clearTimeout(timer);
    }
  }
  throw new Error(`focus never reached ${selector} in ${max} presses`);
}

test.describe("keyboard paths", () => {
  test("the first Tab is a skip link to the page, and the header navigates by keyboard", async ({ page, browserName }) => {
    const key = tabKey(browserName);
    await shell(page);
    await mockJson(page, "**/api/saved", { managedListUrl: null, sort: "tonight", eveningBudgetMinutes: 150, expectedAbsoluteError: 1.9, unscoredRemaining: 0, items: [] });
    await mockJson(page, "**/api/taste", { userMean: 6.7, contrarianScore: 0.2, ratingsCount: 10, hoursWatched: 10, titlesWatched: 10, showCompletionRate: 0.7, topGenres: [], eras: [], creators: [], topActors: [], histogram: [], drift: [] });
    await page.goto("/saved");
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();

    await page.keyboard.press(key);
    const skip = page.getByRole("link", { name: "Skip to content" });
    await expect(skip).toBeFocused();
    // Shown on focus, not just present: an sr-only link is a 1 px box that is also "in the viewport".
    await expect.poll(async () => (await skip.boundingBox())?.width ?? 0).toBeGreaterThan(40);
    await page.keyboard.press("Enter");
    await expect(page.locator("main#main")).toBeFocused();

    await page.locator("body").focus();
    await tabTo(page, key, 'header a[aria-label="Taste DNA"]');
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/dna$/);
  });

  test("the account menu opens by keyboard without hiding the page, and Escape returns to its button", async ({ page, browserName }) => {
    // HREV5-PH-04: a modal menu aria-hid the skip link, the header and <main> while it was open.
    const key = tabKey(browserName);
    await shell(page);
    await mockHome(page);
    await page.goto("/home");
    await expect(page.getByTestId("hero-card")).toHaveCount(5);

    const trigger = page.getByRole("button", { name: /account menu/i });
    await page.locator("body").focus();
    await tabTo(page, key, 'header button[aria-haspopup="menu"]');
    await expect(trigger).toBeFocused();
    await page.keyboard.press("Enter");
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible();
    await expect(page.getByRole("menuitem", { name: "Track record" })).toBeFocused();

    // It renders in the header's landmark, and still drops from its button, inside the screen:
    // the header's backdrop blur makes it the containing block the menu is positioned in.
    await expect(page.locator("header [role=menu]")).toHaveCount(1);
    const at = await page.evaluate(() => {
      const t = document.querySelector('header button[aria-haspopup="menu"]')!.getBoundingClientRect();
      const m = document.querySelector("[role=menu]")!.getBoundingClientRect();
      return { gap: m.top - t.bottom, rightGap: t.right - m.right, left: m.left, right: m.right, width: innerWidth };
    });
    expect(at.gap, "the menu's top sits just under its button").toBeGreaterThanOrEqual(0);
    expect(at.gap).toBeLessThan(16);
    expect(Math.abs(at.rightGap), "the menu is end-aligned to its button").toBeLessThan(2);
    expect(at.left).toBeGreaterThanOrEqual(0);
    expect(at.right).toBeLessThanOrEqual(at.width);

    // Nothing outside the menu is hidden from assistive tech or made inert while it is open.
    const hidden = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLElement>("[aria-hidden='true'], [inert], [data-aria-hidden]")]
        .filter((e) => !e.closest("[role=menu]"))
        .filter((e) => e.matches("main, header, a, button, [tabindex]") || e.querySelector("main, a[href], button"))
        .map((e) => `${e.tagName.toLowerCase()}${e.id ? `#${e.id}` : ""}`)
    );
    expect(hidden, "elements holding the page's controls were hidden while the menu was open").toEqual([]);

    await page.keyboard.press("ArrowDown");
    await expect(page.getByRole("menuitem", { name: "Settings" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(menu).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test("a card opens the title modal by keyboard; focus stays inside it, and Escape returns to the card", async ({ page, browserName }) => {
    const key = tabKey(browserName);
    await shell(page);
    await mockHome(page);
    await mockJson(page, "**/api/titles/movie/9101", { ...TITLE, tmdbId: 9101, name: "Pick 1" });
    await mockJson(page, "**/api/titles/movie/9101/providers", { region: "US", attribution: "JustWatch", tmdbWatchPage: null, providers: [] });
    await page.goto("/home");
    await expect(page.getByTestId("hero-card")).toHaveCount(5);

    await tabTo(page, key, '[data-testid="hero-card-link"]');
    const card = page.getByTestId("hero-card-link").first();
    await expect(card).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/title\/movie\/9101$/);
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest("[role=dialog]"))).toBe(true);

    for (let i = 0; i < 25; i++) {
      await page.keyboard.press(key);
      expect(await page.evaluate(() => !!document.activeElement?.closest("[role=dialog]")), `Tab ${i + 1} left the dialog`).toBe(true);
    }

    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(/\/home$/);
    await expect(dialog).toHaveCount(0);
    await expect(card).toBeFocused();
  });
});

/**
 * PH2-03: the Settings range sliders set `outline: none` in index.css and drew nothing on focus, so a
 * keyboard user could not see which slider the arrow keys were moving — the slider looked the same
 * focused and unfocused, pixel for pixel. Each slider must LOOK different once Tab reaches it, at
 * both sizes and with the thumb at rest (floor off) and mid-track (floor set).
 *
 * "Looks different" means a person could see it, not that a byte moved: the first fix passed a
 * not-byte-equal check on the phone while Tab parked min-rating-slider under the fixed tab bar and
 * only a faint glow (24 pixels past 20/255 in Chromium, nothing past 22/255 in WebKit) leaked
 * through. So the ring must also be UNCOVERED — the thumb's centre hit-tests to the input itself,
 * and no fixed or sticky bar overlaps the ring — and the change must be at least RING_MIN_PIXELS
 * pixels moving by more than RING_MIN_DELTA in some channel.
 */
const RING_MIN_DELTA = 60;
const RING_MIN_PIXELS = 100;
/** Thumb diameter (index.css `.fa-range` thumb) and the focus ring's reach past its centre. */
const THUMB_PX = 16;
const RING_REACH_PX = 13;

/** Pixels whose largest channel difference between two same-size PNGs exceeds `delta`. */
async function visiblyChangedPixels(page: Page, a: Buffer, b: Buffer, delta: number): Promise<number> {
  return page.evaluate(
    async ({ a, b, delta }) => {
      const decode = async (b64: string) => {
        const bitmap = await createImageBitmap(await (await fetch(`data:image/png;base64,${b64}`)).blob());
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const ctx = canvas.getContext("2d")!;
        ctx.drawImage(bitmap, 0, 0);
        return ctx.getImageData(0, 0, bitmap.width, bitmap.height);
      };
      const [x, y] = await Promise.all([decode(a), decode(b)]);
      if (x.width !== y.width || x.height !== y.height) return -1;
      let n = 0;
      for (let i = 0; i < x.data.length; i += 4) {
        const d = Math.max(
          Math.abs(x.data[i] - y.data[i]),
          Math.abs(x.data[i + 1] - y.data[i + 1]),
          Math.abs(x.data[i + 2] - y.data[i + 2]),
        );
        if (d > delta) n++;
      }
      return n;
    },
    { a: a.toString("base64"), b: b.toString("base64"), delta },
  );
}

test.describe("range sliders show where keyboard focus is", () => {
  const settings = ROUTES.find((c) => c.name === "settings")!;
  const floors = [
    { name: "floors off", filters: FILTERS },
    { name: "floors set", filters: { ...FILTERS, minCrowdRating: 7, minPredictedRating: 6.5 } },
  ];
  for (const [viewport, options] of Object.entries(VIEWPORTS) as [Viewport, (typeof VIEWPORTS)[Viewport]][]) {
    test.describe(`at ${viewport} size`, () => {
      test.use(options);
      for (const floor of floors) {
        for (const slider of ["crowd-rating-slider", "min-rating-slider"]) {
          test(`${slider}, ${floor.name}: Tab onto it changes how it looks`, async ({ page, browserName }) => {
            await shell(page);
            await settings.mock!(page);
            await mockJson(page, "**/api/settings/filters", floor.filters);
            await page.goto(settings.path);
            await settings.ready(page, viewport);
            const input = page.getByTestId(slider);
            await page.locator("body").focus();
            await tabTo(page, tabKey(browserName), `[data-testid="${slider}"]`, 80);
            await expect(input).toBeFocused();

            // Where Tab left it, the ring is not under a fixed or sticky bar (the phone's bottom
            // tab bar, the sticky header): the thumb's centre hits the input, and no such bar
            // overlaps the box the ring draws in.
            const cover = await input.evaluate(
              (el, { thumb, reach }) => {
                const input = el as HTMLInputElement;
                const r = input.getBoundingClientRect();
                const min = Number(input.min || 0);
                const max = Number(input.max || 100);
                const share = max > min ? (Number(input.value) - min) / (max - min) : 0;
                const x = r.left + thumb / 2 + share * (r.width - thumb);
                const y = r.top + r.height / 2;
                const ring = { left: x - reach, right: x + reach, top: y - reach, bottom: y + reach };
                const overlapping = [...document.querySelectorAll<HTMLElement>("body *")]
                  .filter((e) => !e.contains(input) && ["fixed", "sticky"].includes(getComputedStyle(e).position))
                  .filter((e) => {
                    const b = e.getBoundingClientRect();
                    return b.width > 0 && b.height > 0 && b.left < ring.right && b.right > ring.left && b.top < ring.bottom && b.bottom > ring.top;
                  })
                  .map((e) => `${e.tagName.toLowerCase()}${e.dataset.testid ? `[${e.dataset.testid}]` : ""}`);
                const hit = document.elementFromPoint(x, y);
                return {
                  hitsInput: hit === input,
                  hit: hit ? `${hit.tagName.toLowerCase()}${(hit as HTMLElement).dataset?.testid ? `[${(hit as HTMLElement).dataset.testid}]` : ""}` : "nothing",
                  overlapping,
                  ring: `${Math.round(ring.top)}-${Math.round(ring.bottom)} of ${innerHeight}`,
                };
              },
              { thumb: THUMB_PX, reach: RING_REACH_PX },
            );
            expect(cover.hitsInput, `${slider}'s thumb centre hits ${cover.hit}, not the slider (ring y ${cover.ring})`).toBe(true);
            expect(cover.overlapping, `${slider}'s focus ring (y ${cover.ring}) is under a fixed or sticky bar`).toEqual([]);

            const focused = await input.screenshot({ animations: "disabled", caret: "hide" });

            // Blurred where it stands: the slider's track is transparent over a page gradient, so
            // comparing against a shot taken at another scroll offset differs by a pixel's shading
            // with no focus style at all. Same position, focus the only change.
            await input.evaluate((el) => (el as HTMLElement).blur());
            await expect(input).not.toBeFocused();
            const blurred = await input.screenshot({ animations: "disabled", caret: "hide" });
            const changed = await visiblyChangedPixels(page, focused, blurred, RING_MIN_DELTA);
            expect(
              changed,
              `${slider}: only ${changed} pixels change by more than ${RING_MIN_DELTA}/255 between focused and unfocused`,
            ).toBeGreaterThanOrEqual(RING_MIN_PIXELS);
          });
        }
      }
    });
  }
});

test("with reduced motion asked for, no CSS animation keeps running on Home", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await shell(page);
  await mockHome(page);
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(5);
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          document.getAnimations().filter((a) => {
            const duration = Number(a.effect?.getComputedTiming().duration ?? 0);
            return a.playState === "running" && duration > 1;
          }).length
      )
    )
    .toBe(0);
});
