import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askPush, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";

/**
 * HREV6-PH-01. A follow-up question on a phone, frames spaced the way a real turn spaces them. When
 * the follow-up's first picks arrive, the previous answer folds into its "Earlier answer" tray —
 * about a screen of posters ABOVE the live turn disappears. Chromium's CSS scroll anchoring hides
 * that; WebKit has none, so the reply and the first poster row were left above the viewport and the
 * reader saw only the tail of the grid. WS-2 task 3's mobile acceptance — "the first screen shows
 * the reply plus at least one row of posters" — is asserted here on the SECOND turn, after `done`,
 * with no scroll reset, in both engines.
 */

const CONV = "7a0b1c2d-0000-4000-8000-0000000000f1";

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000002${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 8100 + n,
  name: `Title ${n}`,
  year: 1990 + n,
  posterPath: null,
  genres: ["action"],
  similarity: 0.5,
  predictedRating: 7.2,
  satisfies: "yes",
  why: `Evidence ${n}.`,
});

const cards = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => card(from + i));

const done = (results: ReturnType<typeof card>[]) => ({
  results, streamed: results.length, kept: results.length, droppedByFloor: 0, belowFloor: [], nearMiss: [], unchecked: [],
  mode: "Picks", supply: null, degraded: [], reason: `${results.length} picks`, timingsMs: null, debug: null,
});

async function boot(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" }),
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await mockJson(page, "**/api/search/conversations?limit=20", []);
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  await page.goto("/search");
}

async function ask(page: Page, text: string, n: number) {
  await page.getByTestId("askreel-composer").fill(text);
  await page.getByTestId("askreel-composer").press("Enter");
  await waitForAsk(page, n);
}

/** One whole picks turn, with `gapMs` between frames — a real turn spaces them over seconds. */
async function turn(
  page: Page, n: number, text: string, reply: string, batches: ReturnType<typeof card>[][], gapMs: number,
  beforePicks?: () => Promise<void>,
) {
  await ask(page, text, n);
  const frames: [string, Record<string, unknown>][] = [
    ["conversation", { id: CONV, turnId: `1111111${n}-1111-4111-8111-111111111111`, sequence: n }],
    ["plan", { act: "NewRequest", concepts: [text.split(" ")[0]], references: [] }],
    ["phase", { stage: "judging", label: "Checking them against your request" }],
    ["assistant-message", { text: reply }],
    ...batches.map((b): [string, Record<string, unknown>] => ["picks", { cards: b }]),
    ["done", done(batches.flat())],
  ];
  for (const [event, data] of frames) {
    if (gapMs > 0) await page.waitForTimeout(gapMs);
    if (event === "picks" && data.cards === batches[0] && beforePicks) await beforePicks();
    await askPush(page, event, data);
  }
  await askEnd(page);
  await expect(page.getByTestId("askreel-footnote")).toBeVisible();
}

/** The second reply and its first whole poster are on screen, between the header and the composer. */
async function expectReplyAndFirstPosterOnScreen(page: Page) {
  // Let any scroll the page itself started settle before measuring what the reader sees.
  await page.waitForTimeout(800);
  const box = await page.evaluate(() => {
    const header = document.querySelector("header")!.getBoundingClientRect();
    const composer = document.querySelector('[data-testid="askreel-composer"]')!.closest("form")!.getBoundingClientRect();
    const replies = document.querySelectorAll('[data-testid="askreel-reply"]');
    const reply = replies[replies.length - 1].getBoundingClientRect();
    const firstCard = document.querySelector('[data-testid="semantic-results"] [data-testid="askreel-card"]')!;
    const poster = firstCard.querySelector("img, div")!.getBoundingClientRect();
    return {
      headerBottom: header.bottom, composerTop: composer.top, replyTop: reply.top, replyBottom: reply.bottom,
      posterTop: poster.top, posterBottom: poster.bottom, scrollY: window.scrollY,
    };
  });
  const where = JSON.stringify(box);
  expect(box.replyTop, `the second reply is below the header ${where}`).toBeGreaterThanOrEqual(box.headerBottom);
  expect(box.replyBottom, `the second reply is above the composer ${where}`).toBeLessThanOrEqual(box.composerTop);
  expect(box.posterTop, `the first poster is below the header ${where}`).toBeGreaterThanOrEqual(box.headerBottom);
  expect(box.posterBottom, `the whole first poster is above the composer ${where}`).toBeLessThanOrEqual(box.composerTop);
}

test.describe("Ask Reel — a follow-up on a phone keeps its reply on screen", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test("frames spaced like a real turn: the earlier answer folds and the new reply stays put", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "ninja movies", "Here are ninja films.", [cards(1, 4), cards(5, 8), cards(9, 12)], 0);
    await turn(page, 2, "samurai films", "Here are samurai films instead.", [cards(13, 16), cards(17, 18)], 1200);

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expect(page.getByTestId("semantic-results").getByTestId("askreel-card")).toHaveCount(6);
    await expectReplyAndFirstPosterOnScreen(page);
  });

  test("a longer first answer and a single picks batch: the fold is larger, the reply still stays put", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "heist movies", "Here are heist films.", [cards(1, 8), cards(9, 16)], 0);
    await turn(page, 2, "caper comedies", "Here are capers.", [cards(17, 20)], 900);

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expectReplyAndFirstPosterOnScreen(page);
  });

  test("every frame at once: the fold lands while the page is still scrolling to the new question", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "ninja movies", "Here are ninja films.", [cards(1, 4), cards(5, 8), cards(9, 12)], 0);
    await turn(page, 2, "samurai films", "Here are samurai films instead.", [cards(13, 16), cards(17, 18)], 0);

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expectReplyAndFirstPosterOnScreen(page);
  });

  test("the fold lands mid-scroll: frames 150 ms apart", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "ninja movies", "Here are ninja films.", [cards(1, 4), cards(5, 8), cards(9, 12)], 0);
    await turn(page, 2, "samurai films", "Here are samurai films instead.", [cards(13, 16), cards(17, 18)], 150);

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expectReplyAndFirstPosterOnScreen(page);
  });

  test("frames 60 ms apart: Chromium's anchoring fixes the offset but not the smooth scroll's target", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "ninja movies", "Here are ninja films.", [cards(1, 4), cards(5, 8), cards(9, 12)], 0);
    await turn(page, 2, "samurai films", "Here are samurai films instead.", [cards(13, 16), cards(17, 18)], 60);

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expectReplyAndFirstPosterOnScreen(page);
  });

  test("a reader who scrolled back up into the answer that folds is brought to the new turn", async ({ page }) => {
    await boot(page);
    await turn(page, 1, "ninja movies", "Here are ninja films.", [cards(1, 4), cards(5, 8), cards(9, 12)], 0);
    await turn(page, 2, "samurai films", "Here are samurai films instead.", [cards(13, 16), cards(17, 18)], 1200, async () => {
      // The reader's own scroll (a touch, then the page moving): up into the held ninja answer, so
      // the live anchor is below the viewport when that answer folds.
      await page.evaluate(() => {
        window.dispatchEvent(new Event("touchstart"));
        window.scrollBy({ top: -900, behavior: "instant" as ScrollBehavior });
      });
      const anchorTop = await page.getByTestId("askreel-live-anchor").evaluate((el) => el.getBoundingClientRect().top);
      expect(anchorTop, "the fixture puts the anchor below the viewport").toBeGreaterThan(844);
    });

    await expect(page.getByTestId("askreel-earlier")).toHaveCount(1);
    await expectReplyAndFirstPosterOnScreen(page);
  });
});
