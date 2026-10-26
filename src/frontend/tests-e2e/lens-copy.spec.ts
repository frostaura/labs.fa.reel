import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * "Through your lens" on the title modal.
 *
 * The three things worth pinning are the ones that are easy to lose in a refactor and invisible
 * when they break: the caution is reachable (a recommender that never names a downside is one you
 * stop believing about the upside), the synopsis is never replaced (when the lens is wrong or
 * absent the page still tells you what the thing is about), and a missing lens changes nothing —
 * the budget, the provider and the output guards can each withhold copy, and a page that only
 * looks right when the LLM answered is a page that looks broken on a bad day.
 */

const TITLE = {
  titleId: "00000000-0000-0000-0000-000000000042",
  mediaType: "Movie",
  tmdbId: 4242,
  name: "Soldado",
  year: 2018,
  overview: "A task force escalates the war on the cartels.",
  whyThis: "You rate Taylor Sheridan's writing highly.",
  tagline: null,
  runtimeMinutes: 122,
  certification: "R",
  genres: ["crime", "thriller"],
  network: null,
  status: "Released",
  traktRating: 7.1,
  traktVotes: 9000,
  crowdRating: 7.1,
  crowdVotes: 9000,
  posterPath: null,
  backdropPath: null,
  trailerUrl: null,
  cast: [],
  directors: [],
  writers: [],
  prediction: null,
  contributionDetails: [],
  userState: {
    isFullyWatched: false, plays: 0, userRating: null,
    savedForLater: false, notInterested: false, dropped: false,
  },
};

const LENS = {
  source: "fresh",
  lens:
    "A cartel task force, told with the same moral flatness you keep rating highly — no one to "
    + "root for and no attempt to give you one.",
  hook: "The bleak procedural you have been circling.",
  caution: "It takes far longer than Sicario did to commit to being about anything.",
  anchors: [{ title: "Sicario", relation: "same register, less momentum" }],
  mood: "heavy",
  whenToWatch: "needs-full-attention",
  generatedAt: "2026-09-09T20:00:00Z",
};

async function bootTitle(page: Page, lens: unknown, { mockLens = true }: { mockLens?: boolean } = {}) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/titles/movie/4242", { ...TITLE, lens });
  if (mockLens) await mockJson(page, "**/api/titles/movie/4242/lens", lens);
  await mockJson(page, "**/api/titles/movie/4242/providers", { region: "US", providers: [] });
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await page.goto("/title/movie/4242");
  // The lens sits behind "More" under the one pitch line (F-14); these tests are about what it
  // says once opened. That it starts closed is pinned on its own below.
  if ((lens as { lens?: string | null }).lens) await page.getByTestId("lens-more").click();
}

test.describe("through your lens", () => {
  test("starts closed behind “More”, under the one pitch line", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/titles/movie/4242", { ...TITLE, lens: LENS });
    await mockJson(page, "**/api/titles/movie/4242/lens", LENS);
    await mockJson(page, "**/api/titles/movie/4242/providers", { region: "US", providers: [] });
    await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
    await page.goto("/title/movie/4242");
    await expect(page.getByTestId("why-this")).toContainText("Taylor Sheridan");
    await expect(page.getByTestId("lens-more")).toHaveAttribute("aria-expanded", "false");
    await expect(page.getByTestId("lens-text")).toHaveCount(0);
    await expect(page.getByTestId("lens-mood")).toHaveCount(0);
  });

  test("renders the lens above the synopsis, and never instead of it", async ({ page }) => {
    await bootTitle(page, LENS);

    await expect(page.getByTestId("lens-block")).toBeVisible();
    await expect(page.getByTestId("lens-text")).toContainText("moral flatness");
    await expect(page.getByTestId("lens-hook")).toContainText("bleak procedural");
    // The studio blurb is still there. The lens is a way of reading the title, not a replacement
    // for knowing what it is.
    await expect(page.getByText("A task force escalates the war on the cartels.")).toBeVisible();
  });

  test("the anchors name titles from your own library with what links them", async ({ page }) => {
    await bootTitle(page, LENS);

    await expect(page.getByTestId("lens-anchors")).toContainText("Sicario");
    await expect(page.getByTestId("lens-anchors")).toContainText("same register, less momentum");
  });

  test("mood and occasion render as the closed-vocabulary labels", async ({ page }) => {
    await bootTitle(page, LENS);

    await expect(page.getByTestId("lens-mood")).toContainText("heavy");
    await expect(page.getByTestId("lens-mood")).toContainText("wants your full attention");
  });

  test("the caution is behind a tap, and it really does argue against the pick", async ({ page }) => {
    await bootTitle(page, LENS);

    // Present but not shouted: it is a reservation, not a warning label.
    await expect(page.getByTestId("lens-caution-text")).toHaveCount(0);
    await page.getByTestId("lens-caution-toggle").click();
    await expect(page.getByTestId("lens-caution-text")).toContainText("far longer than Sicario");
  });

  test("a stale note says it is stale rather than passing itself off as current", async ({ page }) => {
    await bootTitle(page, { ...LENS, source: "stale" });

    await expect(page.getByTestId("lens-stale")).toBeVisible();
    // Still rendered — old prose beats no prose.
    await expect(page.getByTestId("lens-text")).toBeVisible();
  });

  test("no lens means the page renders exactly as it did before the feature existed", async ({ page }) => {
    await bootTitle(page, {
      source: "none", lens: null, hook: null, caution: null, anchors: [],
      mood: null, whenToWatch: null, generatedAt: null,
    });

    await expect(page.getByTestId("lens-block")).toHaveCount(0);
    // The templated why-line and the synopsis, both untouched.
    await expect(page.getByTestId("why-this")).toContainText("Taylor Sheridan");
    await expect(page.getByText("A task force escalates the war on the cartels.")).toBeVisible();
  });

  test("reporting a spoiler withdraws the note at once and leaves the synopsis standing", async ({ page }) => {
    await bootTitle(page, LENS);

    let reported = false;
    await page.route("**/api/titles/movie/4242/lens/spoiled", async (route) => {
      reported = true;
      return route.fulfill({
        status: 200, contentType: "application/json", body: JSON.stringify({ withdrawn: 1 }),
      });
    });

    await page.getByTestId("lens-spoiled").click();

    await expect.poll(() => reported).toBe(true);
    // Gone immediately, without waiting for a refetch — the damage a spoiler does is not undone
    // by a note that lingers for another second.
    await expect(page.getByTestId("lens-block")).toHaveCount(0);
    await expect(page.getByText("A task force escalates the war on the cartels.")).toBeVisible();
  });

  test("a null caution simply has no disclosure", async ({ page }) => {
    await bootTitle(page, { ...LENS, caution: null });

    await expect(page.getByTestId("lens-text")).toBeVisible();
    await expect(page.getByTestId("lens-caution-toggle")).toHaveCount(0);
  });

  // WS-1 task 7 moved the lens off the read path: the GET answers from storage and queues the pass,
  // saying so with `pending`. A modal that is already open must pick the note up when it lands.
  test("a queued pass shows one quiet line, then the note lands without reopening the title", async ({ page }) => {
    const NONE_PENDING = {
      source: "none", lens: null, hook: null, caution: null, anchors: [],
      mood: null, whenToWatch: null, generatedAt: null, pending: true,
    };
    // The first answer says "pending"; the first re-ask finds the note.
    let lensCalls = 0;
    await page.route("**/api/titles/movie/4242/lens", async (route) => {
      lensCalls += 1;
      return route.fulfill({
        status: 200, contentType: "application/json",
        body: JSON.stringify(lensCalls === 1 ? NONE_PENDING : { ...LENS, pending: false }),
      });
    });
    await bootTitle(page, NONE_PENDING, { mockLens: false });

    await expect(page.getByTestId("lens-pending")).toHaveText("Your note for this title is being written.");
    await expect(page.getByTestId("lens-more")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("lens-pending")).toHaveCount(0);
    await page.getByTestId("lens-more").click();
    await expect(page.getByTestId("lens-text")).toContainText("moral flatness");
    // A fresh answer stops the re-asking.
    const settled = lensCalls;
    await page.waitForTimeout(6_000);
    expect(lensCalls).toBe(settled);
  });

  test("nothing queued means no re-asking and no pending line", async ({ page }) => {
    let lensCalls = 0;
    page.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/titles/movie/4242/lens") lensCalls += 1;
    });
    await bootTitle(page, { ...LENS, source: "stale", pending: false });
    await expect(page.getByTestId("lens-stale")).toBeVisible();
    await expect(page.getByTestId("lens-pending")).toHaveCount(0);
    const settled = lensCalls;
    await page.waitForTimeout(6_000);
    expect(lensCalls).toBe(settled);
  });
});
