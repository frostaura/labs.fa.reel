import AxeBuilder from "@axe-core/playwright";
import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askFailNext, askPush, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * Chat UI v2's two page-wide acceptance lines (WS-2 §5.2, UI): full axe rules on `/search` report
 * 0 violations, and CLS on `/search` is at most 0.05.
 *
 * Both are measured over the states a reader actually reaches rather than the empty page alone —
 * the cold start, a v2 answer with its trays open, an earlier answer under its reply, the Recent
 * list open, a none-found answer, a refused turn, and the phone layout with its conditions open —
 * because each of those renders markup the empty page never does, and the empty page is the one
 * state WS-9's route sweep (a11y.spec.ts) already covers.
 *
 * CLS is measured the way the metric is defined — every `layout-shift` from navigation onward,
 * input-driven ones excepted — with the account's conditions answering after 800 ms as a real
 * server does, so the page's own load is inside the window rather than skipped.
 */

const CONV = "7a0b1c2d-0000-4000-8000-0000000000aa";

const card = (n: number, extra: Record<string, unknown> = {}) => ({
  titleId: `00000000-0000-0000-0000-0000000003${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 7300 + n,
  name: `Title ${n}`,
  year: 2001 + n,
  posterPath: null,
  genres: ["action"],
  similarity: 0.5,
  predictedRating: 7.2,
  crowdRating: 7.0,
  crowdVotes: 1200,
  satisfies: "yes",
  why: `Ninja evidence ${n}.`,
  ...extra,
});

const FILTERS = {
  excludeGenres: ["comedy", "documentary", "horror"], includeGenres: [], excludeKeywords: ["anime"],
  maturityCeiling: "R", minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

const done = (extra: Record<string, unknown> = {}) => ({
  results: [], streamed: 0, kept: 0, droppedByFloor: 0, belowFloor: [], nearMiss: [], unchecked: [],
  mode: "Picks", supply: null, degraded: [], reason: null, timingsMs: null, debug: null, ...extra,
});

const THREADS = [
  { id: CONV, title: "ninja movies", turnCount: 4, lastTurnAt: new Date().toISOString(), lastCardsAt: new Date().toISOString() },
  { id: "7a0b1c2d-0000-4000-8000-0000000000ab", title: "samurai films", turnCount: 2, lastTurnAt: new Date().toISOString(), lastCardsAt: null },
];

/** Installs the CLS accumulator before the app's first script, so the page's own load is measured. */
async function measureCls(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __cls: number; __shifts: { value: number; sources: string[] }[] };
    w.__cls = 0;
    w.__shifts = [];
    const describe = (node: Node | null): string => {
      const el = node as Element | null;
      if (!el?.tagName) return "(detached)";
      const id = el.getAttribute?.("data-testid");
      return `${el.tagName.toLowerCase()}${id ? `[${id}]` : `.${(el.getAttribute?.("class") ?? "").split(/\s+/)[0]}`}`;
    };
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        const shift = entry as PerformanceEntry & { value: number; hadRecentInput: boolean; sources?: { node: Node | null }[] };
        if (shift.hadRecentInput) continue;
        w.__cls += shift.value;
        w.__shifts.push({ value: shift.value, sources: (shift.sources ?? []).map((s) => describe(s.node)) });
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
}

async function readCls(page: Page): Promise<number> {
  // Layout-shift entries are delivered a frame or two after the layout that produced them.
  await page.waitForTimeout(400);
  const { cls, shifts } = await page.evaluate(() => {
    const w = window as unknown as { __cls: number; __shifts: { value: number; sources: string[] }[] };
    return { cls: w.__cls, shifts: w.__shifts };
  });
  for (const shift of shifts.filter((s) => s.value >= 0.001)) console.log(`  shift ${shift.value.toFixed(4)} ← ${shift.sources.join(", ")}`);
  return cls;
}

/** Two painted frames — a batch that lands in its own frame is the worst case for CLS. */
async function painted(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

async function boot(page: Page, opts: { resume?: unknown; filtersDelayMs?: number } = {}) {
  await stubEventSource(page);
  await stubAskReel(page);
  // Everything not mocked below answers 404, so nothing reaches the e2e tripwire.
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" }),
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  // The Conditions editor a relaxation opens asks for its genre chips and its cost meter.
  await mockFilterChips(page);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  const delay = opts.filtersDelayMs ?? 0;
  await page.route("**/api/settings/filters", async (route) => {
    if (delay > 0) await new Promise((r) => setTimeout(r, delay));
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS) });
  });
  // A predicate, not a glob: in a glob the `?` of `?limit=` is a one-character wildcard.
  await page.route(
    (u) => u.pathname === "/api/search/conversations",
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(THREADS) }),
  );
  if (opts.resume) await mockJson(page, "**/api/search/conversations/latest", opts.resume);
  else await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
}

async function ask(page: Page, text: string, n = 1) {
  await page.getByTestId("askreel-composer").fill(text);
  await page.getByTestId("askreel-composer").press("Enter");
  await waitForAsk(page, n);
}

/** One v2 picks turn, batch by batch with a paint between each: plan, phase, reply, picks, trays, done. */
async function picksTurn(page: Page, n = 1, base = 0) {
  await askPush(page, "conversation", { id: CONV, turnId: `11111111-1111-4111-8111-11111111111${n}`, sequence: n * 2 - 1 });
  await askPush(page, "plan", { act: "NewRequest", concepts: ["ninja"], references: [] });
  await askPush(page, "phase", { stage: "searching", label: "Searching the catalogue for ninja" });
  await painted(page);
  await askPush(page, "phase", { stage: "judging", label: "Checking 16 titles against your request" });
  await painted(page);
  await askPush(page, "assistant-message", { text: "Here are ninja films, each with ninja evidence." });
  await painted(page);
  const picks = Array.from({ length: 8 }, (_, i) => card(base + i + 1));
  for (let i = 0; i < picks.length; i += 2) {
    await askPush(page, "picks", { cards: picks.slice(i, i + 2) });
    await painted(page);
  }
  const near = [card(base + 40, { satisfies: "partial", name: "Near Miss" })];
  const under = [card(base + 50, { predictedRating: 5.3, name: "Under The Bar" })];
  await askPush(page, "tray", { kind: "nearMiss", cards: near });
  await askPush(page, "tray", { kind: "belowFloor", cards: under });
  await painted(page);
  await askPush(page, "done", done({
    results: picks, kept: picks.length, streamed: 10, nearMiss: near, belowFloor: under, droppedByFloor: 1,
    reason: "8 picks with ninja evidence · 1 held back by your 6.0 bar — shown below",
  }));
  await askEnd(page);
  await expect(page.getByTestId("askreel-footnote")).toBeVisible();
}

/**
 * Full axe rule set; the failing rules named with their first node. `exclude` is for a FOREIGN
 * component overlaying the page, named with the change request that owns it — never for Ask Reel's
 * own markup.
 */
async function expectNoViolations(page: Page, state: string, exclude: string[] = []) {
  let builder = new AxeBuilder({ page });
  for (const selector of exclude) builder = builder.exclude(selector);
  const { violations } = await builder.analyze();
  const named = violations.map((v) => `${v.id} (${v.impact}, ${v.nodes.length}): ${v.nodes[0]?.target.join(" ")} — ${v.nodes[0]?.failureSummary?.split("\n")[1]?.trim() ?? ""}`);
  expect(named, `axe on /search — ${state}`).toEqual([]);
}

test.describe("Ask Reel — axe reports 0 violations on /search", () => {
  test("cold start, a v2 answer with its trays open, an earlier answer, and the Recent list", async ({ page }) => {
    await boot(page);
    await expect(page.getByTestId("askreel-preset").first()).toBeVisible();
    await expectNoViolations(page, "cold start");

    await ask(page, "ninja movies");
    await picksTurn(page);
    await page.getByTestId("askreel-near-miss-toggle").click();
    await page.getByTestId("askreel-below-floor-toggle").click();
    await expectNoViolations(page, "a settled answer, trays open");

    // A follow-up: the first answer moves under its reply, collapsed; open it.
    await ask(page, "samurai films", 2);
    await askPush(page, "phase", { stage: "planning", label: "Reading your request" });
    await expect(page.getByTestId("askreel-previous-answer")).toBeVisible();
    await expectNoViolations(page, "streaming, previous answer held");
    await picksTurn(page, 2, 100);
    await page.getByTestId("askreel-earlier-toggle").click();
    await expectNoViolations(page, "an earlier answer open under its reply");

    await page.getByTestId("askreel-recent").click();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(2);
    await page.getByTestId("askreel-recent-delete").first().click();
    await expect(page.getByTestId("askreel-recent-confirm")).toBeVisible();
    await expectNoViolations(page, "the Recent list open, a delete asking");
  });

  test("a none-found answer with its relaxations, and the Conditions editor it opens", async ({ page }) => {
    await boot(page);
    await ask(page, "a ninja comedy");
    await askPush(page, "conversation", { id: CONV, turnId: "22222222-2222-4222-8222-222222222222", sequence: 1 });
    await askPush(page, "assistant-message", { text: "Nothing left that is a ninja comedy: 4 more without your comedy rule." });
    const knockout = { kind: "ExcludeGenre", value: "comedy", contentFilterId: null, label: "excluded genre: comedy", wouldUnlock: 4 };
    await askPush(page, "none-found", { supply: { knockouts: [knockout] } });
    await askPush(page, "done", done({ mode: "NoneFound", supply: { knockouts: [knockout] } }));
    await askEnd(page);
    await expect(page.getByTestId("askreel-none-found")).toBeVisible();
    await expectNoViolations(page, "none found");

    await page.getByTestId("askreel-relax").click();
    await expect(page.getByTestId("genre-comedy-exclude")).toBeFocused();
    // The whole dialog, the condition summary included (R1-44 moved its muted tone to text-3), is held to zero.
    await expectNoViolations(page, "the Conditions editor open on the comedy rule");
  });

  test("a refused turn says which limit and stays accessible", async ({ page }) => {
    await boot(page);
    await expect(page.getByTestId("askreel-preset").first()).toBeVisible();
    await askFailNext(page, 429, "ask_rate_minute");
    await ask(page, "ninja movies");
    const card429 = page.getByTestId("semantic-error");
    await expect(card429).toHaveAttribute("data-reason", "rate");
    await expect(card429).toContainText("That’s the most questions Reel answers in a minute");
    await expect(card429).not.toContainText("search engine");
    await expect(page.getByTestId("askreel-status")).toHaveText("That’s the most questions Reel answers in a minute.");
    await expect(card429.getByTestId("askreel-retry")).toBeVisible();
    await expectNoViolations(page, "429 ask_rate_minute");
  });
});

test.describe("Ask Reel — axe on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test("an answer with its conditions opened", async ({ page }) => {
    await boot(page);
    await ask(page, "ninja movies");
    await picksTurn(page);
    await page.getByTestId("askreel-conditions-toggle").tap();
    await expect(page.getByTestId("askreel-conditions-toggle")).toHaveAttribute("aria-expanded", "true");
    await expectNoViolations(page, "phone, conditions open");
  });
});

for (const viewport of [{ width: 390, height: 844 }, { width: 1440, height: 900 }]) {
  test.describe(`Ask Reel — CLS on /search at ${viewport.width}×${viewport.height}`, () => {
    test.use({ viewport });
    // Layout Instability is a Chromium API: WebKit reports no `layout-shift` entries at all, so a
    // WebKit run would "measure" 0 and prove nothing.
    test.skip(({ browserName }) => browserName !== "chromium", "layout-shift entries are Chromium-only");

    test("load (conditions answering late) and a streamed v2 turn stay at or under 0.05", async ({ page }) => {
      await measureCls(page);
      await boot(page, { filtersDelayMs: 800 });
      await expect(page.getByTestId("askreel-conditions")).toBeVisible();
      await painted(page);
      // The question is typed and sent; shifts within 500 ms of that input are the reader's own.
      await ask(page, "ninja movies");
      await page.waitForTimeout(600);
      await picksTurn(page);
      const cls = await readCls(page);
      console.log(`CLS[/search ${viewport.width}: load + turn]=${cls.toFixed(4)}`);
      expect(cls).toBeLessThanOrEqual(0.05);
    });

    test("landing on a resumed four-turn thread stays at or under 0.05", async ({ page }) => {
      const turnsOf = (n: number) => [
        { sequence: n * 2, role: "user", text: `question ${n}`, kind: "ask", status: "Completed" },
        {
          sequence: n * 2 + 1, role: "assistant", text: `answer ${n}`, kind: "ask", status: "Completed",
          id: `44444444-4444-4444-8444-00000000000${n}`, cards: [card(n * 10 + 1), card(n * 10 + 2)], mode: "Picks",
        },
      ];
      await measureCls(page);
      await boot(page, {
        filtersDelayMs: 800,
        resume: {
          id: CONV, title: "four turns", lastCardsAt: new Date().toISOString(), lastTurnAt: new Date().toISOString(),
          turns: [...turnsOf(1), ...turnsOf(2), ...turnsOf(3), ...turnsOf(4)],
          cards: [card(41), card(42)], belowFloor: [], nearMiss: [], unchecked: [], mode: "Picks",
          reason: "2 picks", conflict: null, removed: 0, removedEarlier: 0, lastQuery: "question 4", mediaTypes: [], shown: [],
        },
      });
      await expect(page.getByTestId("askreel-seam")).toBeVisible();
      await expect(page.getByTestId("askreel-conditions")).toBeVisible();
      await expect(page.getByTestId("askreel-earlier-toggle")).toHaveCount(3);
      const cls = await readCls(page);
      console.log(`CLS[/search ${viewport.width}: resume]=${cls.toFixed(4)}`);
      expect(cls).toBeLessThanOrEqual(0.05);
    });
  });
}
