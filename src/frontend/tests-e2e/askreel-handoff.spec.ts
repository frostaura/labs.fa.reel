import { test, expect, type Page } from "@playwright/test";
import {
  SESSION_FOUNDER, askAbortLive, askEnd, askPush, askRequests, mockJson, stubAskReel, stubEventSource,
  waitForAsk,
} from "./helpers";

/**
 * The Home → /search hand-off.
 *
 * `04bcc36` moved the question out of `?q=` and into the navigation's own state. That removed the
 * address-bar round trip and introduced two defects the composer path can never show, because the
 * composer types into a page that is already mounted and never navigates.
 *
 *  - **The send was killed by the page itself.** A hand-off MOUNTS /search, and a first mount in
 *    development is StrictMode's mount → unmount → remount. The unmount cleanup aborted the
 *    request the first mount had just started, `nextBootstrap` refused to restart a seed already
 *    recorded as handled, and the page sat on "Thinking…" forever behind one aborted POST.
 *  - **The question outlived its navigation.** `location.state` lives in the history entry, so a
 *    reload of /search — or a Back onto the entry the search box pushed — handed the same question
 *    to a document that had never asked it, which asked it again. The server sees the same text as
 *    the conversation's last user turn and records the result as a PAGE of it: a phantom turn,
 *    billed, appending cards nobody asked for.
 */

const CONVERSATION = "7f3a1b2c-0000-4000-8000-000000000009";

const CARD = {
  titleId: "00000000-0000-0000-0000-0000000000a1",
  mediaType: "Movie",
  tmdbId: 8001,
  name: "Alpha Creek",
  year: 2023,
  posterPath: null,
  genres: ["thriller"],
  similarity: 0.5,
  predictedRating: 7.1,
  why: "Why Alpha Creek.",
  seen: false,
};

const FILTERS = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

/** The thread the server has, for the visits that are supposed to resume rather than ask. */
const RESUME_PAYLOAD = {
  id: CONVERSATION,
  title: "a tense heist",
  lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
  lastTurnAt: new Date(Date.now() - 60_000).toISOString(),
  turns: [
    { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "One that fits.", kind: "ask", status: "Completed" },
  ],
  cards: [CARD],
  reason: "1 pick ranked for you, from 40 pulled.",
  removed: 0,
  lastQuery: "a tense heist",
  mediaTypes: [],
  shown: [CARD.tmdbId],
};

const gridNames = (page: Page) => page.locator('[data-testid="semantic-results"] a > p.truncate');

/**
 * Boots the shell. `resume` is what the conversation routes answer — "none" is a browser that has
 * never asked anything, "stored" serves the thread above from every conversation route.
 */
async function boot(page: Page, opts: { resume?: "none" | "stored" } = {}) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/settings/filters", FILTERS);
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/search/conversations", []);
  await page.route(
    (u) => u.pathname === "/api/search/typeahead",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ titles: [], people: [], global: [] }),
    }),
  );
  // The header box's second ask since the global typeahead landed (`/typeahead/global`, beyond the
  // catalogue): typing the question fires it, so it is mocked empty like its local sibling.
  await page.route(
    (u) => u.pathname === "/api/search/typeahead/global",
    (route) => route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ global: [], hiddenByConditions: 0 }),
    }),
  );
  if (opts.resume === "stored") {
    await page.route(
      (u) => u.pathname.startsWith("/api/search/conversations/"),
      (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(RESUME_PAYLOAD) }),
    );
  } else {
    await page.route(
      (u) => u.pathname.startsWith("/api/search/conversations/"),
      (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" }),
    );
  }
}

/** Types a question into the HEADER box and presses Enter — the hand-off itself. */
async function askFromHeader(page: Page, question: string) {
  const box = page.getByTestId("search-input").first();
  await box.fill(question);
  await box.press("Enter");
}

/** Streams a one-card answer down whatever /ask stream is currently open. */
async function answer(page: Page) {
  await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-1", sequence: 1 });
  await askPush(page, "assistant-message", { text: "One that fits." });
  await askPush(page, "candidate", CARD);
  await askPush(page, "done", { results: [CARD], reason: "1 pick ranked for you, from 40 pulled." });
  await askEnd(page);
}

test.describe("Ask Reel — the header box hands the question to /search", () => {
  test("Enter in the header box asks exactly once and streams the answer", async ({ page }) => {
    await boot(page);
    await page.goto("/home");
    await expect(page.getByTestId("search-input").first()).toBeVisible();

    await askFromHeader(page, "a tense heist");
    await expect(page).toHaveURL(/\/search$/);

    // The bubble appears the moment the turn starts; what matters is that a live REQUEST is behind
    // it. Before the fix the POST was aborted by the page's own StrictMode cleanup, the stream
    // rejected, and nothing on screen ever moved again.
    await waitForAsk(page);
    await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");

    await answer(page);
    await expect(gridNames(page)).toHaveCount(1);
    await expect(page.getByTestId("askreel-stop")).toHaveCount(0);

    const asks = await askRequests(page);
    expect(asks).toHaveLength(1);
    expect(asks[0]).toMatchObject({ query: "a tense heist" });
    // No `pageOf`: a hand-off is an ask, and a page is only ever stated, never inferred.
    expect(Object.keys(asks[0] as object)).not.toContain("pageOf");
  });

  test("an abort nobody asked for lands in the error state, not on Thinking… forever", async ({ page }) => {
    await boot(page);
    await page.goto("/home");
    await expect(page.getByTestId("search-input").first()).toBeVisible();

    await askFromHeader(page, "something slow");
    await waitForAsk(page);
    await expect(page.getByTestId("askreel-stop")).toBeVisible();

    // Cut at the page's own controller, with no Stop pressed — the abort the catch used to
    // swallow whole, leaving the reducer streaming behind a progress bar with no way out.
    expect(await askAbortLive(page)).toBe(true);

    await expect(page.getByTestId("semantic-error")).toBeVisible();
    await expect(page.getByTestId("askreel-stop")).toHaveCount(0);
    // …and the way out is on screen, which is the whole point of not sitting at "Thinking…".
    await expect(page.getByRole("button", { name: /try again|ask again/i })).toBeVisible();
  });

  test("a reload of /search never re-asks the handed-off question", async ({ page }) => {
    await boot(page, { resume: "stored" });
    await page.goto("/search");
    await expect(page.getByTestId("askreel-composer")).toBeVisible();

    // The history entry still carrying a hand-off, which is what a reload restores. Stamped
    // directly because that is the only durable half of the navigation: React Router reads
    // `history.state.usr` back into `location.state` on the next document load.
    await page.evaluate(() => {
      const current = (history.state ?? {}) as Record<string, unknown>;
      history.replaceState(
        { ...current, usr: { ask: "a tense heist", askTurnId: "11111111-2222-4333-8444-555555555555" } },
        "",
      );
    });
    await page.reload();

    await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");
    await expect(gridNames(page)).toHaveCount(1);
    // The thread came back from the server; nothing was asked, and nothing was billed.
    expect(await askRequests(page)).toHaveLength(0);
    // …and the leftover is gone, so a second reload cannot inherit it either.
    expect(await page.evaluate(() => (history.state as { usr?: unknown } | null)?.usr ?? null)).toBeNull();

    await page.reload();
    await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");
    expect(await askRequests(page)).toHaveLength(0);
  });
});
