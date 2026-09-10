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

async function bootSearch(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
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
    const requests = (await askRequests(page)) as { query: string; continuation?: boolean }[];
    expect(requests).toHaveLength(2);
    expect(requests[1].query).toBe("tense small-town thriller, no horror");
    expect(requests[1].continuation).toBe(true);
    await askPush(page, "done", { results: [], reason: null });
    await askEnd(page);
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
  });
});
