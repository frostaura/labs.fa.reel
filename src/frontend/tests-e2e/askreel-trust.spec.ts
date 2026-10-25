import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askPush, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";

/**
 * The WS-2 review round's two client-side failures, driven frame by frame through the scripted
 * SSE stub:
 *  - acceptance D1 (critical): the server wrote a relaxation offer's `kind` as a number, the
 *    client's guard rejected the whole `done` frame, and the "ninja movies" and "samurai films"
 *    turns spun forever with their none-found card never rendered;
 *  - D1 / acceptance D3: a turn whose judge failed outright is a none-found answer whose cards sit
 *    in a labelled "not checked" tray, and the reply says some cards went unchecked.
 */

const card = (id: string, name: string) => ({
  titleId: `00000000-0000-0000-0000-0000000000${id}`,
  mediaType: "Movie",
  tmdbId: Number(`91${id.charCodeAt(0)}`),
  name,
  year: 2012,
  posterPath: null,
  genres: ["action"],
  similarity: 0.4,
  predictedRating: null,
  seen: false,
  satisfies: null,
});

async function bootSearch(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
}

async function ask(page: Page, text: string) {
  await page.getByTestId("askreel-composer").fill(text);
  await page.getByTestId("askreel-composer").press("Enter");
  await waitForAsk(page);
}

test.describe("Ask Reel — WS-2 review round 1", () => {
  test("a none-found answer renders even when one relaxation offer is malformed", async ({ page }) => {
    await bootSearch(page);
    await ask(page, "ninja movies");

    await askPush(page, "assistant-message", {
      text: "I couldn't check what I found against what you asked this time.",
      conflict: false,
      degraded: false,
    });
    await askPush(page, "done", {
      results: [], streamed: 0, kept: 0, droppedByFloor: 0, belowFloor: [], nearMiss: [],
      unchecked: [card("a1", "Shinobi Dawn")],
      mode: "NoneFound",
      supply: {
        knockouts: [
          // The shape the server used to send: an enum as a number. Dropped alone.
          { kind: 5, value: "en", contentFilterId: null, label: "English only", wouldUnlock: 4 },
          { kind: "MinYear", value: "2000", contentFilterId: null, label: "released 2000 or later", wouldUnlock: 19 },
        ],
      },
      degraded: ["judge:2/2"],
      reason: "1 not checked against what you asked — shown below",
      timingsMs: null,
      debug: null,
    });
    await askEnd(page);

    const noneFound = page.getByTestId("askreel-none-found");
    await expect(noneFound).toBeVisible();
    await expect(noneFound).toContainText("Reel couldn’t check what it found");
    const offers = page.getByTestId("askreel-none-found-relaxations").locator("li");
    await expect(offers).toHaveText(["Without “released 2000 or later”: +19"]);

    await expect(page.getByTestId("askreel-unchecked-toggle")).toHaveText("Not checked against your request — 1 title");
    await expect(page.getByTestId("askreel-reply-unchecked")).toHaveText("some cards unchecked");
    // Finished, not streaming: the composer is handed back.
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
  });

  test("a stream that closes without its done frame is a failure, never a spinner", async ({ page }) => {
    await bootSearch(page);
    await ask(page, "samurai films");

    await askPush(page, "assistant-message", { text: "Samurai films, coming up.", conflict: false });
    await askEnd(page);

    await expect(page.getByTestId("semantic-error")).toBeVisible();
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
  });
});
