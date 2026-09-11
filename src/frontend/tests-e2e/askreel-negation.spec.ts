import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askPush, askPushConflict, askRequests, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";

/**
 * Ask Reel's honest channel and stream discipline, driven frame-by-frame through the scripted
 * SSE stub (route.fulfill cannot trickle frames, and "no reorder on a scored frame" is only
 * observable between frames):
 *  - outsideRequest-flagged cards render fenced off under "Outside your request — closest we
 *    could get", after the main grid, never interleaved;
 *  - candidate-scored frames update data in place — DOM order holds until a phase frame, the one
 *    boundary where re-sorting is allowed;
 *  - a fresh /search shows the preset mood chips, and tapping one sends its text as the turn;
 *  - one message costs exactly one /ask request, paging being a button rather than a sentinel;
 *  - Stop is a state and not an absence: it keeps whatever arrived, keeps the PREVIOUS answer when
 *    nothing did, names what happened in the live region, and hands the composer back;
 *  - the server's conflict sentence lands above the grid, and the quality floor's cut is
 *    accounted for in its own collapsed tray under it.
 */

const card = (id: string, name: string, similarity: number, outside = false) => ({
  titleId: `00000000-0000-0000-0000-0000000000${id}`,
  mediaType: "Movie",
  tmdbId: Number(`90${id.charCodeAt(0)}`),
  name,
  year: 2023,
  posterPath: null,
  genres: ["thriller"],
  similarity,
  predictedRating: null,
  seen: false,
  ...(outside ? { outsideRequest: true, why: "Trips your 'no horror' exclusion." } : {}),
});

// Blend = similarity·0.35 + (predictedRating ?? 6)/10·0.25 + (fit ?? 0.5)·0.4 — chosen so the
// arrival order (Alpha, Border, Cedar) differs from the blend order both before the scored frame
// (Border, Cedar, Alpha) and after it (Border, Alpha, Cedar), making any premature re-sort visible.
const ALPHA = card("a1", "Alpha Creek", 0.4);
const BORDER = card("b2", "Border Town", 0.9);
const CEDAR = card("c3", "Cedar Falls", 0.5);
const XCLUDED = card("d4", "Xcluded Manor", 0.95, true);

/** Alpha/Border/Cedar as `done` leaves them: blend order, which is not arrival order. */
const SETTLED_ORDER = ["Border Town", "Cedar Falls", "Alpha Creek"];

/**
 * The account's saved conditions, as `/settings/filters` returns them. `minPredictedRating` is the
 * bar the floor tray names, and it is read from here rather than from the stream on purpose: it is
 * the user's own standing setting, the same one `PickConditionsSummary` renders at the top of the
 * page, so the two can never quote different numbers.
 */
const FILTERS_WITH_FLOOR = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

async function bootSearch(page: Page, filters?: unknown) {
  await stubEventSource(page);
  await stubAskReel(page);
  // Registered only when a test cares: without it the query fails and the page must still render
  // the tray with its unnumbered fallback, which is what the collision test above exercises.
  if (filters !== undefined) await mockJson(page, "**/api/settings/filters", filters);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  // A cold /search resumes the latest conversation; 404 = there is none, so the page stays fresh.
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
}

/** The main grid's card names, in DOM order. */
function gridNames(page: Page) {
  return page.locator('[data-testid="semantic-results"] a > p[title]');
}

test.describe("Ask Reel — outside-request channel and stream stability", () => {
  test("outside cards render fenced after the grid; order holds on scored frames, moves on phase frames", async ({ page }) => {
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("tense small-town thriller, no horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await askPush(page, "candidate", ALPHA);
    await askPush(page, "candidate", BORDER);
    await askPush(page, "candidate", CEDAR);
    await askPush(page, "candidate", XCLUDED);

    // Arrival order, untouched — candidates append, they do not sort.
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Border Town", "Cedar Falls"]);
    // The flagged card never enters the main grid, even mid-stream.
    const outside = page.getByTestId("askreel-outside");
    await expect(outside).toBeVisible();
    await expect(outside.getByText("Xcluded Manor")).toBeVisible();

    // A scored frame lifts Alpha's blend above Cedar's — but scored frames update in place, so
    // the DOM order must NOT move. Wait for the score to paint before asserting stillness.
    await askPush(page, "candidate-scored", { titleId: ALPHA.titleId, predictedRating: 9.9 });
    await expect(page.getByTestId("semantic-results").getByText("9.9")).toBeVisible();
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Border Town", "Cedar Falls"]);

    // A phase frame is the sanctioned settle point: now the blend order applies.
    await askPush(page, "phase", { stage: "ranking", found: 4, scored: 1 });
    await expect(gridNames(page)).toHaveText(["Border Town", "Alpha Creek", "Cedar Falls"]);

    await askPush(page, "done", {
      results: [BORDER, { ...ALPHA, predictedRating: 9.9 }, CEDAR, XCLUDED],
      reason: "4 picks ranked for you, from 40 pulled.",
    });
    await askEnd(page);

    // The fenced section: exact header, and strictly AFTER the main grid in document order.
    await expect(outside.getByText("Outside your request — closest we could get")).toBeVisible();
    await expect(outside.getByText("Trips your 'no horror' exclusion.")).toBeVisible();
    await expect(outside.getByText("Xcluded Manor")).toBeVisible();
    await expect(page.getByTestId("semantic-results").getByText("Xcluded Manor")).toHaveCount(0);
    const gridPrecedesOutside = await page.evaluate(() => {
      const grid = document.querySelector('[data-testid="semantic-results"]');
      const section = document.querySelector('[data-testid="askreel-outside"]');
      return !!grid && !!section && !!(grid.compareDocumentPosition(section) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    expect(gridPrecedesOutside).toBe(true);
  });

  test("a fresh /search offers preset mood chips and tapping one sends its text", async ({ page }) => {
    await bootSearch(page);

    const presets = page.getByTestId("askreel-preset");
    await expect(presets).toHaveCount(4);
    await expect(page.getByText("What are you in the mood for?")).toBeVisible();

    await presets.first().click();
    await waitForAsk(page);
    const requests = (await askRequests(page)) as { query: string }[];
    expect(requests).toHaveLength(1);
    expect(requests[0].query).toBe("Under 90 minutes, feel-good");

    // The tap became a real turn: the user's bubble carries the preset, and the presets yield.
    await expect(page.getByText("Under 90 minutes, feel-good")).toBeVisible();
    await expect(presets).toHaveCount(0);

    await askPush(page, "done", { results: [], reason: null });
    await askEnd(page);
  });

  test("one message produces exactly one ask request", async ({ page }) => {
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("tense small-town thriller, no horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    await askPush(page, "candidate", ALPHA);
    await askPush(page, "candidate", BORDER);
    await askPush(page, "done", { results: [ALPHA, BORDER], reason: "2 picks ranked for you, from 40 pulled." });
    await askEnd(page);

    // The scroll sentinel that used to sit here had `rootMargin: 600px`, so it was already inside
    // the viewport when the first page painted: every message cost two full-price /ask turns, the
    // second one's interpret reply generated, billed and thrown away. Paging is now a button, so
    // settling on the results must issue nothing at all.
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await page.waitForTimeout(750);
    expect(await askRequests(page)).toHaveLength(1);

    // …and the button still pages, so the capability was moved rather than dropped.
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    const requests = (await askRequests(page)) as { query: string; clientTurnId: string; continuation?: boolean }[];
    expect(requests).toHaveLength(2);
    expect(requests[1].query).toBe("tense small-town thriller, no horror");
    // `continuation` is gone: the server derives ask-vs-page from whether the query equals the
    // conversation's last user turn, which is a fact it can check rather than one it is told. What
    // the page carries instead is its OWN turn id — a page is a new exchange, not a replay of one.
    expect(requests[1].continuation).toBeUndefined();
    expect(requests[1].clientTurnId).not.toBe(requests[0].clientTurnId);
    await askPush(page, "done", { results: [], reason: null });
    await askEnd(page);
  });

  test("Stop keeps the cards that arrived, says so, and leaves the composer usable", async ({ page }) => {
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("slow burn detective series");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await askPush(page, "candidate", ALPHA);
    await askPush(page, "candidate", BORDER);
    await askPush(page, "candidate", CEDAR);
    await expect(gridNames(page)).toHaveCount(3);

    await page.getByTestId("askreel-stop").click();

    // The three that arrived are still on screen — Stop abandons the turn, not its results.
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Border Town", "Cedar Falls"]);
    // …and the page SAYS what happened. Before this the banner simply vanished and every render
    // branch read the state as "nothing to say", leaving a blank page with no way back.
    const stopped = page.getByTestId("askreel-stopped");
    await expect(stopped).toBeVisible();
    await expect(stopped).toHaveText(/Stopped — 3 picks arrived before you stopped\./);
    await expect(stopped.getByTestId("askreel-retry")).toBeVisible();
    // The screen-reader announcement matches the card, rather than staying the empty string.
    // …including the "unranked" half, which is the claim a stopped turn has to withdraw: ranking
    // and the quality floor both run at `done`, so what is on screen is arrival order with
    // whatever score each card had when the stream ended.
    await expect(page.getByTestId("askreel-status"))
      .toHaveText("Stopped — 3 picks arrived before you stopped. Unranked — stopped before Reel finished.");
    // Neither the empty state nor the error state may claim this turn.
    await expect(page.getByTestId("semantic-empty")).toHaveCount(0);
    await expect(page.getByTestId("semantic-error")).toHaveCount(0);

    // The composer is live again: the turn is over, so the next question can go straight out.
    await expect(page.getByTestId("askreel-banner")).toHaveCount(0);
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
    await page.getByTestId("askreel-composer").fill("something else then");
    await expect(page.getByTestId("askreel-send")).toBeEnabled();
    await expect(page.getByTestId("askreel-chip").first()).toBeEnabled();
  });

  test("stopping before the first card leaves the previous answer intact", async ({ page }) => {
    // The reported repro, and the expensive half of it: asking a question used to blank the grid
    // instantly, so a Stop at 2.5s cost the user the twelve cards they were reading in exchange
    // for an answer that never arrived. Same control as the test above, one turn earlier.
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("a tense heist movie");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);

    await page.getByTestId("askreel-composer").fill("slow burn detective series");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });

    // Held, not blanked — and labelled, so it never reads as an answer to the new question.
    // Blend order, settled by the previous turn's `done`, and unchanged by the phase frame above:
    // a held answer is finished work and must not re-sort in response to the new turn's progress.
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
    await expect(page.getByTestId("askreel-previous-answer")).toBeVisible();

    await page.getByTestId("askreel-stop").click();

    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
    const stopped = page.getByTestId("askreel-stopped");
    await expect(stopped).toHaveText(/Stopped before any picks arrived\./);
    await expect(stopped.getByTestId("askreel-retry")).toBeVisible();
    // The seam stays, and re-words itself: nothing is being waited for any more.
    await expect(page.getByTestId("askreel-previous-answer"))
      .toHaveText("Previous answer — the last question never replaced it.");
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
  });

  test("a filter collision is named above the grid, and the floor's cut gets its own tray", async ({ page }) => {
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("small-town horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    // Only `message` — the shape an older server sends. The page must render the server's
    // sentence verbatim and must not compose one of its own from the numeric fields.
    await askPushConflict(page, "You asked for horror, but your saved conditions exclude it.");
    const notice = page.getByTestId("askreel-conflict");
    await expect(notice).toBeVisible();
    await expect(notice).toHaveText(/You asked for horror, but your saved conditions exclude it\./);

    await askPush(page, "done", {
      results: [ALPHA, BORDER],
      reason: "2 picks ranked for you, from 40 pulled.",
      belowFloor: [CEDAR],
      droppedByFloor: 1,
    });
    await askEnd(page);

    // The floor's cut is accounted for, collapsed, under the grid — never mixed into it.
    const tray = page.getByTestId("askreel-below-floor");
    await expect(tray).toBeVisible();
    await expect(tray.getByText("Cedar Falls")).toBeHidden();
    await expect(page.getByTestId("semantic-results").getByText("Cedar Falls")).toHaveCount(0);
    await tray.locator("summary").click();
    await expect(tray.getByText("Cedar Falls")).toBeVisible();

    const gridPrecedesTray = await page.evaluate(() => {
      const grid = document.querySelector('[data-testid="semantic-results"]');
      const t = document.querySelector('[data-testid="askreel-below-floor"]');
      return !!grid && !!t && !!(grid.compareDocumentPosition(t) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    expect(gridPrecedesTray).toBe(true);
    // No saved bar was mocked, so the label cannot name one and says so in words instead of
    // printing "under your null bar".
    await expect(tray.locator("summary")).toHaveText("Show the 1 pick Reel held back");
  });

  test("the floor tray names the account's own bar, to one decimal, and counts its own contents", async ({ page }) => {
    // Same tray, one field different from the test above: the account has a saved
    // `minPredictedRating`. That single field is what turns "Reel held some back" into a sentence
    // naming a number the user chose and can go and change.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("small-town horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    // The two sources DISAGREE on purpose: the server says it cut three, and sends one card down.
    // The label used to quote the scalar, so it read "Show the 2 under your 6 bar" over a tray the
    // user could open and count to one. A number beside its own contents has to match them.
    await askPush(page, "done", {
      results: [ALPHA, BORDER],
      reason: "2 picks ranked for you, from 40 pulled.",
      belowFloor: [CEDAR],
      droppedByFloor: 3,
    });
    await askEnd(page);

    const tray = page.getByTestId("askreel-below-floor");
    await expect(tray).toBeVisible();
    // …and "6.0", not "6": `minPredictedRating` is 6, and the server's own sentence beside this
    // one says "6.0 bar". One number, one rendering, whatever the browser's locale.
    await expect(tray.locator("summary")).toHaveText("Show the 1 under your 6.0 bar");
    await tray.locator("summary").click();
    await expect(tray.locator('a[href*="/title/"]')).toHaveCount(1);
  });
});
