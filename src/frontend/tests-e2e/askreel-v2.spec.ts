import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askPush, askRequests, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";

/**
 * Chat UI v2 (WS-2 task 3), driven frame by frame with §4.9's v2 frames through the scripted SSE
 * stub. One test per acceptance line of the task: every answer's cards under its own reply with the
 * latest expanded, a previous answer that never drops under 0.9, a narration line that follows the
 * phase frames, a none-found card whose relaxations open the Conditions editor on their rule, a
 * near-miss tray that moves focus to its heading, an item question that leaves the grid alone, a
 * clarifying question whose readings are buttons, refine chips built from the reading, a 14 px
 * provenance line, and — at 390 × 844 — the reply plus a whole poster above the composer.
 */

const CONV = "7a0b1c2d-0000-4000-8000-000000000001";

const card = (n: number, extra: Record<string, unknown> = {}) => ({
  titleId: `00000000-0000-0000-0000-0000000001${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 7100 + n,
  name: `Title ${n}`,
  year: 2001 + n,
  posterPath: null,
  genres: ["action"],
  similarity: 0.5,
  predictedRating: 7.2,
  satisfies: "yes",
  why: `Ninja evidence ${n}.`,
  ...extra,
});

const FILTERS = {
  excludeGenres: ["comedy"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

async function boot(page: Page, filters: Record<string, unknown> = FILTERS) {
  await stubEventSource(page);
  await stubAskReel(page);
  // Registered first so every specific mock below wins and nothing reaches the e2e tripwire — the
  // Conditions editor asks for its own cost meter and settings.
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
  await mockJson(page, "**/api/settings/filters", filters);
  await page.goto("/search");
}

async function ask(page: Page, text: string, n = 1) {
  await page.getByTestId("askreel-composer").fill(text);
  await page.getByTestId("askreel-composer").press("Enter");
  await waitForAsk(page, n);
}

const done = (extra: Record<string, unknown> = {}) => ({
  results: [], streamed: 0, kept: 0, droppedByFloor: 0, belowFloor: [], nearMiss: [], unchecked: [],
  mode: "Picks", supply: null, degraded: [], reason: null, timingsMs: null, debug: null, ...extra,
});

/** One whole v2 picks turn: plan, a labelled phase, the reply, two batches, a near miss, done. */
async function ninjaTurn(page: Page) {
  await ask(page, "ninja movies");
  await askPush(page, "conversation", { id: CONV, turnId: "11111111-1111-4111-8111-111111111111", sequence: 1 });
  await askPush(page, "plan", { act: "NewRequest", concepts: ["ninja"], references: [] });
  await askPush(page, "phase", { stage: "judging", label: "Checking 6 against your request" });
  await expect(page.getByTestId("askreel-narration")).toHaveText("Checking 6 against your request");
  await askPush(page, "assistant-message", { text: "Here are ninja films, each with ninja evidence." });
  await askPush(page, "picks", { cards: [card(1), card(2)] });
  await askPush(page, "picks", { cards: [card(3)] });
  await askPush(page, "tray", { kind: "nearMiss", cards: [card(9, { satisfies: "partial", name: "Near Miss" })] });
  await askPush(page, "done", done({
    results: [card(1), card(2), card(3)], kept: 3, streamed: 4,
    nearMiss: [card(9, { satisfies: "partial", name: "Near Miss" })],
    reason: "3 picks with ninja evidence",
  }));
  await askEnd(page);
  await expect(page.getByTestId("askreel-footnote")).toBeVisible();
}

test.describe("Ask Reel — chat UI v2", () => {
  test("cards sit under the reply that produced them, in the order the batches arrived", async ({ page }) => {
    await boot(page);
    await ninjaTurn(page);

    const names = page.getByTestId("semantic-results").getByTestId("askreel-card").locator("p.fa-body");
    await expect(names).toHaveText(["Title 1", "Title 2", "Title 3"]);
    // The grid follows ITS reply in the document — the latest answer, expanded.
    const order = await page.evaluate(() => {
      const reply = document.querySelector('[data-testid="askreel-reply"]');
      const grid = document.querySelector('[data-testid="semantic-results"]');
      return !!reply && !!grid && !!(reply.compareDocumentPosition(grid) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    expect(order).toBe(true);
    // The provenance line is at least 14 px.
    const size = await page.getByTestId("askreel-footnote").evaluate((el) => parseFloat(getComputedStyle(el).fontSize));
    expect(size).toBeGreaterThanOrEqual(14);
    // Refine chips are built from the reading, and none offers what a standing rule rules out:
    // comedy is excluded, so nothing asks for "lighter".
    await expect(page.getByTestId("askreel-chip")).toHaveText(["Newer ninja picks", "Older ninja picks", "More like the first one"]);
  });

  test("the near-miss tray opens from the keyboard and moves focus to its heading", async ({ page }) => {
    await boot(page);
    await ninjaTurn(page);

    const toggle = page.getByTestId("askreel-near-miss-toggle");
    await toggle.focus();
    await page.keyboard.press("Enter");
    const heading = page.getByTestId("askreel-near-miss-heading");
    await expect(heading).toBeFocused();
    await expect(heading).toHaveText("Close, but not ninja");
  });

  test("a follow-up holds the previous answer at 0.9 under its reply, then keeps it there collapsed", async ({ page }) => {
    await boot(page);
    await ninjaTurn(page);

    await ask(page, "samurai films", 2);
    await askPush(page, "phase", { stage: "planning", label: "Reading your request" });
    await expect(page.getByTestId("askreel-previous-answer")).toBeVisible();
    const opacity = await page.getByTestId("semantic-results").evaluate((el) => Number(getComputedStyle(el).opacity));
    expect(opacity).toBeGreaterThanOrEqual(0.9);
    // Every card on screen stays at or above 0.9 while the turn streams.
    const lowest = await page.evaluate(() =>
      Math.min(...Array.from(document.querySelectorAll('[data-testid="askreel-card"]')).map((el) => {
        let o = 1;
        for (let n: Element | null = el; n; n = n.parentElement) o *= Number(getComputedStyle(n).opacity);
        return o;
      })),
    );
    expect(lowest).toBeGreaterThanOrEqual(0.9);

    await askPush(page, "assistant-message", { text: "Here are samurai films." });
    await askPush(page, "picks", { cards: [card(4)] });
    // The first new card releases the hold: the ninja answer moves under its own reply, collapsed.
    const earlier = page.getByTestId("askreel-earlier");
    await expect(earlier).toHaveCount(1);
    await expect(page.getByTestId("askreel-earlier-toggle")).toHaveText("Earlier answer · 3 picks · 1 in its trays");
    await expect(earlier).not.toHaveAttribute("open", "");
    await expect(page.getByTestId("semantic-results").getByTestId("askreel-card")).toHaveCount(1);
    await askPush(page, "done", done({ results: [card(4)], kept: 1 }));
    await askEnd(page);
    await page.getByTestId("askreel-earlier-toggle").click();
    await expect(earlier.getByTestId("askreel-card")).toHaveCount(4);
  });

  test("a none-found answer's relaxation opens the Conditions editor on that rule", async ({ page }) => {
    await boot(page);
    await ask(page, "a ninja comedy");
    await askPush(page, "conversation", { id: CONV, turnId: "22222222-2222-4222-8222-222222222222", sequence: 1 });
    await askPush(page, "assistant-message", { text: "Nothing left that is a ninja comedy: 4 more without your comedy rule." });
    const knockout = { kind: "ExcludeGenre", value: "comedy", contentFilterId: null, label: "excluded genre: comedy", wouldUnlock: 4 };
    await askPush(page, "none-found", { supply: { knockouts: [knockout] } });
    await askPush(page, "done", done({ mode: "NoneFound", supply: { knockouts: [knockout] } }));
    await askEnd(page);

    await expect(page.getByTestId("askreel-none-found")).toBeVisible();
    await expect(page.getByTestId("askreel-status")).toHaveText("Nothing left that matches — Reel’s reply says why.");
    const chip = page.getByTestId("askreel-relax");
    await expect(chip).toHaveText("Without “excluded genre: comedy”: +4");
    await chip.click();
    await expect(page.getByTestId("conditions-editor")).toBeVisible();
    await expect(page.getByTestId("genre-comedy")).toBeFocused();
  });

  test("an item question answers beside the grid and leaves it exactly as it was", async ({ page }) => {
    await boot(page);
    await ninjaTurn(page);

    await ask(page, "is the first one violent?", 2);
    await askPush(page, "plan", { act: "ItemQuestion", concepts: [], references: ["T1.1"] });
    await askPush(page, "answer", { text: "Yes — Title 1 opens on a massacre." });
    await askPush(page, "done", done({ mode: "Answer" }));
    await askEnd(page);

    await expect(page.getByText("Yes — Title 1 opens on a massacre.")).toBeVisible();
    await expect(page.getByTestId("semantic-results").getByTestId("askreel-card")).toHaveCount(3);
    await expect(page.getByTestId("askreel-earlier")).toHaveCount(0);
    await expect(page.getByTestId("askreel-previous-answer")).toHaveCount(0);
    const opacity = await page.getByTestId("semantic-results").evaluate((el) => Number(getComputedStyle(el).opacity));
    expect(opacity).toBe(1);
  });

  test("a clarifying question offers its readings as buttons that ask them", async ({ page }) => {
    await boot(page);
    await ask(page, "something like the boxer one");
    await askPush(page, "conversation", { id: CONV, turnId: "33333333-3333-4333-8333-333333333333", sequence: 1 });
    await askPush(page, "clarify", { question: "Which did you mean?", options: ["Raging Bull", "Million Dollar Baby"] });
    await askPush(page, "done", done({ mode: "Clarify" }));
    await askEnd(page);

    await expect(page.getByTestId("semantic-empty")).toHaveCount(0);
    await page.getByTestId("askreel-clarify-option").filter({ hasText: "Raging Bull" }).click();
    await waitForAsk(page, 2);
    const requests = (await askRequests(page)) as { query: string }[];
    expect(requests.at(-1)?.query).toBe("Raging Bull");
  });

  test("a degraded reading is marked on its reply while the turn is still running", async ({ page }) => {
    await boot(page);
    await ask(page, "none of these are ninja movies");
    await askPush(page, "assistant-message", { text: "I couldn’t fully read that — searching for ninja as before." });
    await askPush(page, "degraded", { what: "plan" });
    await expect(page.getByTestId("askreel-reply-degraded")).toBeVisible();
    // Inside the transcript's polite log, so the notice is announced as it lands.
    await expect(page.locator('[role="log"][aria-live="polite"] [data-testid="askreel-reply-degraded"]')).toHaveCount(1);
  });
});

test.describe("Ask Reel — chat UI v2 resume", () => {
  test("a resumed four-turn conversation renders every turn's cards under its own reply", async ({ page }) => {
    const turnsOf = (n: number) => [
      { sequence: n * 2, role: "user", text: `question ${n}`, kind: "ask", status: "Completed" },
      {
        sequence: n * 2 + 1, role: "assistant", text: `answer ${n}`, kind: "ask", status: "Completed",
        id: `44444444-4444-4444-8444-00000000000${n}`, cards: [card(n * 10 + 1), card(n * 10 + 2)], mode: "Picks",
      },
    ];
    const payload = {
      id: CONV, title: "four turns", lastCardsAt: new Date().toISOString(), lastTurnAt: new Date().toISOString(),
      turns: [...turnsOf(1), ...turnsOf(2), ...turnsOf(3), ...turnsOf(4)],
      cards: [card(41), card(42)], belowFloor: [], nearMiss: [], unchecked: [], mode: "Picks",
      reason: "2 picks", conflict: null, removed: 0, removedEarlier: 0, lastQuery: "question 4", mediaTypes: [], shown: [],
    };
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
    await mockJson(page, "**/api/settings/filters", FILTERS);
    await mockJson(page, "**/api/search/conversations/latest", payload);
    await page.goto("/search");

    await expect(page.getByTestId("askreel-seam")).toBeVisible();
    // The newest answer is the grid, expanded; the three before it are kept under their replies.
    await expect(page.getByTestId("semantic-results").getByTestId("askreel-card")).toHaveCount(2);
    const earlier = page.getByTestId("askreel-earlier-toggle");
    await expect(earlier).toHaveCount(3);
    for (const toggle of await earlier.all()) await toggle.click();
    await expect(page.getByTestId("askreel-earlier").getByTestId("askreel-card")).toHaveCount(6);
    // Every turn's cards are on the page, each under the reply that produced it.
    const placed = await page.evaluate(() =>
      Array.from(document.querySelectorAll('[data-testid="askreel-reply"]')).map((reply) => {
        const block = reply.parentElement!;
        return block.querySelectorAll('[data-testid="askreel-card"]').length;
      }),
    );
    expect(placed).toEqual([2, 2, 2, 0]);
  });
});

test.describe("Ask Reel — chat UI v2 on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test("conditions collapse to one button and the first screen shows the reply and a whole poster", async ({ page }) => {
    await boot(page, {
      ...FILTERS,
      excludeGenres: ["comedy", "documentary", "horror", "musical", "romance", "war", "western"],
      excludeKeywords: ["anime"], maturityCeiling: "R", minCrowdRating: 6.5, minYear: 2000,
    });
    const toggle = page.getByTestId("askreel-conditions-toggle");
    await expect(toggle).toHaveText(/^\d+ conditions$/);

    await ninjaTurn(page);
    await page.evaluate(() => window.scrollTo(0, 0));
    const composerTop = await page.getByTestId("askreel-composer").evaluate((el) => el.closest("form")!.getBoundingClientRect().top);
    const reply = await page.getByTestId("askreel-reply").boundingBox();
    expect(reply, "the reply is on the first screen").not.toBeNull();
    expect(reply!.y + reply!.height).toBeLessThanOrEqual(composerTop);
    const poster = await page.getByTestId("semantic-results").getByTestId("askreel-card").first().locator("img, div").first().boundingBox();
    expect(poster, "a poster is on the first screen").not.toBeNull();
    expect(poster!.y).toBeGreaterThanOrEqual(0);
    expect(poster!.y + poster!.height, "the whole first poster is above the composer").toBeLessThanOrEqual(composerTop);
  });
});
