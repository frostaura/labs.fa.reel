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

/**
 * Alpha/Border/Cedar as `done` leaves them: the SERVER's array, verbatim.
 *
 * It was blend order until SB-01. The client derives an order while it is assembling one frame by
 * frame — the phase-boundary settle a few tests down still pins exactly that — but `done` IS the
 * server's order arriving, and keeping the derived one past it made the same answer render one way
 * live and another after a reload, which reads back the stored `(Sequence, Rank)`. The `done`
 * frames below deliberately send arrival order, which is NOT blend order, so a client that
 * re-derives fails here.
 */
const SETTLED_ORDER = ["Alpha Creek", "Border Town", "Cedar Falls"];

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

  test("stopping before the first card hands the previous answer back whole", async ({ page }) => {
    // The reported repro, and both halves of it. Asking a question used to blank the grid
    // instantly, so a Stop at 2.5s cost the user the twelve cards they were reading in exchange for
    // an answer that never arrived — that half was fixed by holding the answer. F6-06 is what was
    // left: the grid survived, and its tray, its footnote and its paging did not, with the whole of
    // it rendered at 40% opacity as a resting state. Reloading the page brought all of it back.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("a tense heist movie");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "done", {
      results: [ALPHA, BORDER, CEDAR],
      belowFloor: [card("h8", "Held Back", 0.2)],
      droppedByFloor: 1,
      reason: "3 picks ranked for you, from 40 pulled · 1 under your 6.0 bar — shown below.",
    });
    await askEnd(page);
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
    await expect(page.getByTestId("askreel-below-floor")).toBeVisible();
    await expect(page.getByTestId("askreel-footnote")).toBeVisible();
    await expect(page.getByTestId("askreel-more")).toBeVisible();

    await page.getByTestId("askreel-composer").fill("slow burn detective series");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });

    // While the new turn runs it IS a previous answer: held, labelled and dimmed, so it never reads
    // as an answer to the question just asked. The order the previous `done` committed is unchanged
    // by the phase frame — a held answer is finished work and must not re-sort to another turn's
    // progress.
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
    await expect(page.getByTestId("askreel-previous-answer")).toBeVisible();
    // Labelled, and still readable: chat UI v2 holds the previous answer at 0.9, never below — the
    // 40 % it used to drop to read as disabled for an answer the reader was entitled to use.
    await expect(page.getByTestId("semantic-results")).toHaveClass(/opacity-90/);
    expect(Number(await page.getByTestId("semantic-results").evaluate((el) => getComputedStyle(el).opacity))).toBeGreaterThanOrEqual(0.9);

    await page.getByTestId("askreel-stop").click();

    // …and now it is simply the answer again. Nothing is coming, so nothing is being held for.
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
    const stopped = page.getByTestId("askreel-stopped");
    await expect(stopped).toHaveText(/Stopped before any picks arrived\./);
    await expect(stopped.getByTestId("askreel-retry")).toBeVisible();

    // Full contrast, not 90% of it, and no "previous answer" label over an answer nothing replaced.
    await expect(page.getByTestId("semantic-results")).not.toHaveClass(/opacity-/);
    await expect(page.getByTestId("askreel-previous-answer")).toHaveCount(0);
    // Instead, the seam that names the question these cards answer — the one directly above them in
    // the transcript being the one that was abandoned.
    await expect(page.getByTestId("askreel-kept-answer")).toContainText("a tense heist movie");
    // Chat UI v2: the kept answer stays under the reply that produced it — ABOVE the question that
    // was stopped and the stopped card about it — with its provenance line beside it, not under
    // the stopped turn.
    const keptAboveStopped = await page.evaluate(() => {
      const row = document.querySelector('[data-testid="askreel-stopped"]');
      const grid = document.querySelector('[data-testid="semantic-results"]');
      const footnote = document.querySelector('[data-testid="askreel-footnote"]');
      const follows = (a: Element | null, b: Element | null) => !!a && !!b && !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
      return { gridThenStopped: follows(grid, row), footnoteThenGrid: follows(footnote, grid) };
    });
    expect(keptAboveStopped).toEqual({ gridThenStopped: true, footnoteThenGrid: true });

    // Everything the Stop used to strip. Sixteen tray picks in the measured run.
    await expect(page.getByTestId("askreel-below-floor").locator("summary")).toHaveText("Show the 1 held back by your 6.0 bar");
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("3 picks ranked for you, from 40 pulled · 1 under your 6.0 bar — shown below.");
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
  });

  test("paging follows the answer on screen, never the question that was stopped", async ({ page }) => {
    // R1-04's actual mechanism, pinned where it lives: the question a page SENDS. The reported
    // sequence pressed "Show me more" over a held answer and the page sent the STOPPED question —
    // the 37 held cards dropped their dim and became page one of a question that had never been
    // answered, with nothing on screen saying so.
    //
    // F6-06 puts the control back, because after a Stop the grid is a finished answer that nothing
    // replaced rather than a placeholder for one. The two facts have to hold together: the button
    // exists, AND it extends the heist answer on screen rather than the detective question above it.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("a tense heist movie");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    // A completed turn IS pageable — the control has to exist before its presence means anything.
    await expect(page.getByTestId("askreel-more")).toBeVisible();

    await page.getByTestId("askreel-composer").fill("slow burn detective series");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    // Withdrawn while the answer is genuinely held: those cards are not this question's answer yet,
    // and nothing on screen can say which question a page of them would extend.
    await expect(page.getByTestId("askreel-previous-answer")).toBeVisible();
    await expect(page.getByTestId("askreel-more")).toHaveCount(0);

    await page.getByTestId("askreel-stop").click();
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await page.waitForTimeout(400);
    expect(await askRequests(page)).toHaveLength(2);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 3);
    const paged = (await askRequests(page)) as { query: string }[];
    expect(paged[2].query).toBe("a tense heist movie");
    await askPush(page, "done", { results: [CEDAR], reason: "1 more from the same angle.", kept: 1 });
    await askEnd(page);
    await expect(gridNames(page)).toHaveText(SETTLED_ORDER);
  });

  test("Show me more after a Stop names the turn it extends, not just the question", async ({ page }) => {
    // R3-02, the client half. The fix above made the page send the ANSWERED question — correct for
    // the user's intent, and the server then read `kind` off the conversation's last USER turn,
    // which the Stop had just made "something tense set at sea". So it wrote a second `ask`: the
    // client rendered an append, the server stored a new answer, and on the next reload 42 of the
    // 66 cards on screen were gone, the transcript carried a question nobody typed, and the stopped
    // turn lost its own explanation.
    //
    // `pageOf` names the row instead of leaving it to be inferred. The two halves travel together —
    // the question the grid answers, and the turn that grid IS.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("a 90s romantic comedy");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: "1f2e3d4c-0000-4000-8000-000000000001", turnId: "assistant-turn-1", sequence: 1 });
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    await expect(page.getByTestId("askreel-more")).toBeVisible();

    // The follow-up opens a turn of its own — and it is that turn's id the page must NOT send.
    await page.getByTestId("askreel-composer").fill("something tense set at sea");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "conversation", { id: "1f2e3d4c-0000-4000-8000-000000000001", turnId: "assistant-turn-2", sequence: 3 });
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await page.getByTestId("askreel-stop").click();
    await expect(page.getByTestId("askreel-more")).toBeVisible();

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 3);
    const paged = (await askRequests(page)) as Record<string, unknown>[];
    expect(paged[2].query).toBe("a 90s romantic comedy");
    expect(paged[2].pageOf).toBe("assistant-turn-1");
    // The two ordinary turns carry no such field: R3 measured every body as exactly four keys, and
    // an `ask` that claimed to extend something would be the same disagreement in reverse.
    expect(Object.keys(paged[0])).not.toContain("pageOf");
    expect(Object.keys(paged[1])).not.toContain("pageOf");
  });

  test("a page of a page still extends the answer the whole grid belongs to", async ({ page }) => {
    // Fixture variation on the test above: no Stop, two pages instead of one. The second page must
    // name the ASK's turn — the row the question was asked in — rather than the first page's, so
    // `pageOf` and `query` keep pointing at the same turn however many pages are stacked on it.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("a 90s romantic comedy");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: "1f2e3d4c-0000-4000-8000-000000000001", turnId: "assistant-turn-1", sequence: 1 });
    await askPush(page, "done", { results: [ALPHA, BORDER], reason: "2 picks ranked for you, from 40 pulled." });
    await askEnd(page);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await askPush(page, "conversation", { id: "1f2e3d4c-0000-4000-8000-000000000001", turnId: "assistant-turn-2", sequence: 2 });
    await askPush(page, "done", { results: [CEDAR], reason: "1 more from the same angle.", kept: 1 });
    await askEnd(page);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 3);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests[1].pageOf).toBe("assistant-turn-1");
    expect(requests[2].pageOf).toBe("assistant-turn-1");
    expect(requests[2].query).toBe("a 90s romantic comedy");
  });

  test("Ask again after a Stop replays the question that was stopped, not the one on screen", async ({ page }) => {
    // The other half of the same pair, and the reason `lastQuery` is deliberately NOT restored by
    // the Stop: the two controls on this screen replay two different questions, and a single field
    // cannot serve both.
    await bootSearch(page, FILTERS_WITH_FLOOR);

    await page.getByTestId("askreel-composer").fill("a tense heist movie");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);

    await page.getByTestId("askreel-composer").fill("slow burn detective series");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await page.getByTestId("askreel-stop").click();

    await page.getByTestId("askreel-stopped").getByTestId("askreel-retry").click();
    await waitForAsk(page, 3);
    const retried = (await askRequests(page)) as { query: string }[];
    expect(retried[2].query).toBe("slow burn detective series");

    await askPush(page, "done", { results: [ALPHA, BORDER], reason: "2 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    // The seam goes with the answer it named: the grid is now this question's own.
    await expect(page.getByTestId("askreel-kept-answer")).toHaveCount(0);
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 4);
    const requests = (await askRequests(page)) as { query: string }[];
    expect(requests[3].query).toBe("slow burn detective series");
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
    // No saved bar was mocked, so the label cannot name one and says so in words instead of
    // printing "under your null bar".
    await expect(tray.locator("summary")).toHaveText("Show the 1 pick Reel held back");
  });

  test("a qualified reply is tagged, and the notice sits under it naming the excluded terms", async ({ page }) => {
    // FX3 put two fields on the wire that nothing read: `conflict` on the reply, and
    // `excludedKeywords` beside `excludedGenres`. The reply is written before a single candidate
    // exists, so the server rewrites it when the question collides with a standing exclusion — and
    // the flag is what lets the page mark THAT bubble and anchor the notice to it instead of
    // floating it above the grid, detached from the sentence it explains.
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("a funny anime");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    await askPush(page, "assistant-message", {
      text: "Here are some light picks — under your conditions that means no comedy and anime.",
      conflict: true,
    });
    // F4-06's first half. The server's rewrite ENDS in the clause the pill carries, so rendering
    // both printed "…no comedy and anime.under your conditions" — the annotation repeating the
    // sentence it annotates, with no space between them. The sentence keeps it; the pill stands
    // down. What is left is one statement, read once.
    await expect(page.getByTestId("askreel-reply")).toContainText("under your conditions that means no comedy and anime.");
    await expect(page.getByTestId("askreel-reply-conflict")).toHaveCount(0);
    const clauses = await page.getByTestId("askreel-reply").evaluate(
      (el) => (el.textContent ?? "").match(/under your conditions/gi)?.length ?? 0,
    );
    expect(clauses).toBe(1);

    // `excludedGenres` and `excludedKeywords` together: the keyword half is the one a turn asking
    // for an avoid-keyword had no way to name at all, since no genre collides.
    await askPushConflict(page, "Your conditions permanently exclude comedy and anime, so nothing you asked for there can appear.", {
      excludedGenres: ["comedy"],
      excludedKeywords: ["anime"],
      poolSize: 12,
      catalogueSize: 40_000,
    });

    const notice = page.getByTestId("askreel-conflict");
    await expect(notice).toBeVisible();
    await expect(notice.getByTestId("askreel-conflict-terms")).toHaveText("Rules out comedy and anime.");
    // …and it is under the reply, not above the grid. The reply that carries the flag is the
    // element the notice explains, and the two used to be a banner and a control apart.
    const noticeFollowsReply = await page.evaluate(() => {
      const reply = document.querySelector('[data-testid="askreel-reply"]');
      const n = document.querySelector('[data-testid="askreel-conflict"]');
      const grid = document.querySelector('[data-testid="semantic-results"]');
      return {
        afterReply: !!reply && !!n && !!(reply.compareDocumentPosition(n) & Node.DOCUMENT_POSITION_FOLLOWING),
        insideTranscript: !!n?.closest('[data-testid="askreel-transcript"]'),
        beforeGrid: !grid || !!(n!.compareDocumentPosition(grid) & Node.DOCUMENT_POSITION_FOLLOWING),
      };
    });
    expect(noticeFollowsReply).toEqual({ afterReply: true, insideTranscript: true, beforeGrid: true });

    await askPush(page, "done", { results: [ALPHA], reason: "1 pick ranked for you, from 40 pulled." });
    await askEnd(page);
    // It survives the answer it annotates — the collision did not stop being true.
    await expect(notice).toBeVisible();
    await expect(page.getByTestId("askreel-reply-conflict")).toHaveCount(0);
  });

  test("the accumulated count is picks, and the outside channel is counted where it is named", async ({ page }) => {
    // F4-05. A paged answer carrying an outside-request channel read "12 picks so far · last page:
    // 3 picks matched everything you asked · 3 closest-but-outside shown separately" over 9 picks
    // and 3 outside cards — the 12 folding in the very cards the clause beside it had just called
    // not-matching, in the same sentence.
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("a tense thriller, no horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "done", {
      results: [ALPHA, BORDER, XCLUDED],
      reason: "2 picks matched everything you asked · 1 closest-but-outside shown separately",
      kept: 2,
    });
    await askEnd(page);
    await expect(page.getByTestId("askreel-outside")).toBeVisible();
    // Un-paged: the server's own sentence, verbatim, with no accumulated prefix to get wrong.
    await expect(page.getByTestId("askreel-status"))
      .toHaveText("2 picks ready. 1 closest-but-outside shown separately.");

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await askPush(page, "done", { results: [CEDAR], reason: "1 pick matched everything you asked", kept: 1 });
    await askEnd(page);

    // Three picks on screen and one outside card. Four is the number that was printed.
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("3 picks so far · last page: 1 pick matched everything you asked");
    await expect(page.getByTestId("askreel-status"))
      .toHaveText("3 picks ready. 1 closest-but-outside shown separately.");
  });

  test("a reply qualified without saying so still gets the pill", async ({ page }) => {
    // Fixture variation on one field of the test above: the same `conflict: true` over a reply
    // that does NOT carry the clause — an older server, and a row stored before the rewrite
    // existed. Dropping the pill outright would leave those bubbles unmarked, which is the state
    // F4-06's other half was about; the pill is scanned out, not deleted.
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("a funny anime");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "assistant-message", { text: "I can’t recommend a funny anime.", conflict: true });

    await expect(page.getByTestId("askreel-reply-conflict")).toHaveText("under your conditions");
    const clauses = await page.getByTestId("askreel-reply").evaluate(
      (el) => (el.textContent ?? "").match(/under your conditions/gi)?.length ?? 0,
    );
    expect(clauses).toBe(1);
  });

  test("an unqualified reply carries no tag, and a message-only conflict names no terms", async ({ page }) => {
    // Fixture variation on exactly the two fields the test above turns on: `conflict: false` on
    // the reply, and a conflict frame with no term lists — the shape an older server sends. The
    // notice must still render its sentence, and nothing may invent a "Rules out" line from it.
    await bootSearch(page);

    await page.getByTestId("askreel-composer").fill("small-town horror");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    await askPush(page, "assistant-message", { text: "Three that fit.", conflict: false });
    await expect(page.getByTestId("askreel-reply")).toBeVisible();
    await expect(page.getByTestId("askreel-reply-conflict")).toHaveCount(0);

    await askPushConflict(page, "You asked for horror, but your saved conditions exclude it.");
    await expect(page.getByTestId("askreel-conflict"))
      .toContainText("You asked for horror, but your saved conditions exclude it.");
    await expect(page.getByTestId("askreel-conflict-terms")).toHaveCount(0);

    await askPush(page, "done", { results: [ALPHA], reason: "1 pick ranked for you, from 40 pulled." });
    await askEnd(page);
  });

  test("the collision notice and the pill come back with the answer after a reload", async ({ page }) => {
    // F4-06. The cards were restored from rows and the reason line from a column; the
    // EXPLANATION was stream-only. A reloaded thread that asked for a 90s romantic comedy against
    // standing exclusions on both genres came back as post-2000 dramas under that question with
    // `askreel-conflict` and `askreel-reply-conflict` both absent — nothing left on the page
    // saying why the answer looks nothing like the question. This is the continuity failure class
    // the founder complained about, applied to the explanation rather than to the cards.
    await stubEventSource(page);
    await stubAskReel(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/search/conversations/latest", {
      id: "9e1a0000-0000-4000-8000-000000000009",
      title: "a 90s romantic comedy",
      lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
      turns: [
        { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "I can’t recommend a 90s romantic comedy.", kind: "ask", status: "Completed" },
      ],
      cards: [ALPHA],
      reason: "1 pick ranked for you, from 40 pulled.",
      removed: 0,
      lastQuery: "a 90s romantic comedy",
      mediaTypes: [],
      shown: [],
      // The stored object, exactly as the turn record hands it back: no `poolSize` /
      // `catalogueSize`, because both stop being true the moment the next title is ingested.
      conflict: {
        excludedGenres: ["comedy", "romance"],
        excludedKeywords: [],
        minYear: 2000,
        maxYear: null,
        message: "Your conditions permanently exclude comedy and romance, so this is the closest fit.",
      },
    });
    await page.goto("/search");

    await expect(gridNames(page)).toHaveText(["Alpha Creek"]);
    const notice = page.getByTestId("askreel-conflict");
    await expect(notice).toContainText("Your conditions permanently exclude comedy and romance");
    await expect(notice.getByTestId("askreel-conflict-terms")).toHaveText("Rules out comedy and romance.");
    // …and on the bubble it qualifies, which is the newest assistant row — the same row the
    // notice anchors under.
    await expect(page.getByTestId("askreel-reply-conflict")).toHaveText("under your conditions");
  });

  test("a resumed thread that had no collision shows no notice and no pill", async ({ page }) => {
    // Fixture variation, one field: `conflict` removed from the same payload. A warning pill on
    // every resumed answer would be worse than none at all, because it IS a warning.
    await stubEventSource(page);
    await stubAskReel(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/search/conversations/latest", {
      id: "9e1a0000-0000-4000-8000-00000000000a",
      title: "a tense heist",
      lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
      turns: [
        { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "One that fits.", kind: "ask", status: "Completed" },
      ],
      cards: [ALPHA],
      reason: "1 pick ranked for you, from 40 pulled.",
      removed: 0,
      lastQuery: "a tense heist",
      mediaTypes: [],
      shown: [],
    });
    await page.goto("/search");

    await expect(gridNames(page)).toHaveText(["Alpha Creek"]);
    await expect(page.getByTestId("askreel-conflict")).toHaveCount(0);
    await expect(page.getByTestId("askreel-reply-conflict")).toHaveCount(0);
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
    await expect(tray.locator("summary")).toHaveText("Show the 1 held back by your 6.0 bar");
    await tray.locator("summary").click();
    await expect(tray.locator('a[href*="/title/"]')).toHaveCount(1);
  });
});

/**
 * F6-04 and F4-08 — the grid that jumped, and the cards that were taken away.
 *
 * One ordinary turn scored **CLS 0.672** against Google's 0.25 "poor" boundary; the same page with
 * one "Show me more" on top scored **1.158** in four discrete jumps. The document went 837 px →
 * 4 482 px in a single frame when the first card batch landed, and 4 482 → 4 851 again when `done`
 * retired cards into the tray. Anything the reader was reaching for moved under them.
 *
 * Half of that shift budget is F4-08: 23–24 posters sat on screen for 11.5–18.6 seconds and then
 * collapsed to 10, with no transition, no announcement, and the remainder folded into a collapsed
 * `<details>`. On relaxed turns nothing vanished at all, so the behaviour differed turn to turn with
 * nothing on screen explaining why.
 */
test.describe("Ask Reel — the grid holds its shape", () => {
  // 375, because that is where the finder measured and where a grid arriving below a banner moves
  // the most of what is already on screen.
  test.use({ viewport: { width: 375, height: 812 } });

  /** 24 candidates, the count the finder watched arrive before `done` kept 10. */
  const STREAMED = Array.from({ length: 24 }, (_, i) =>
    card(String.fromCharCode(97 + (i % 26)) + String(i), `Streamed ${String(i).padStart(2, "0")}`, 0.5 + i / 100),
  );
  /** What `done` keeps, and what it retires under the fence — F4-08's 10 + 14. */
  const KEPT = STREAMED.slice(0, 10);
  const RETIRED = STREAMED.slice(10);

  /**
   * Accumulates `layout-shift` values the way CLS is defined: unbuffered, `hadRecentInput` out —
   * and records what moved, so a regression names its own element in the run log instead of
   * leaving the next reader with a number and a page to re-derive it from.
   */
  async function startCls(page: Page): Promise<void> {
    await page.evaluate(() => {
      const w = window as unknown as { __cls: number; __shifts: { value: number; sources: string[] }[] };
      w.__cls = 0;
      w.__shifts = [];
      const describe = (node: Node | null): string => {
        const el = node as Element | null;
        if (!el?.tagName) return "(detached)";
        const testid = el.getAttribute?.("data-testid");
        return `${el.tagName.toLowerCase()}${testid ? `[${testid}]` : `.${(el.getAttribute?.("class") ?? "").split(/\s+/)[0]}`}`;
      };
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          const shift = entry as PerformanceEntry & {
            value: number;
            hadRecentInput: boolean;
            sources?: { node: Node | null }[];
          };
          if (shift.hadRecentInput) continue;
          w.__cls += shift.value;
          w.__shifts.push({ value: shift.value, sources: (shift.sources ?? []).map((s) => describe(s.node)) });
        }
      }).observe({ type: "layout-shift", buffered: false });
    });
  }

  /**
   * Two frames, actually rendered.
   *
   * `waitForTimeout` paces the stub in wall-clock time, which is not the same thing as a paint:
   * on a fast machine several batches coalesce into one layout pass and on a loaded CI runner they
   * do not, so the number of layout passes per turn — and therefore the number of chances to
   * shift — used to depend on the runner. A double `requestAnimationFrame` after each batch makes
   * every batch its own painted frame everywhere, which is the WORST case for CLS and now the
   * measured one. This is what took the run-to-run spread out of the measurement; it is not what
   * took the shift out of the page, which is the product fix in `SearchResults.tsx`.
   */
  async function painted(page: Page): Promise<void> {
    await page.evaluate(
      () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))),
    );
  }

  /**
   * The page's own load, finished — the account's conditions strip is the last thing
   * `GET /settings/filters` puts on screen, and it lands about a second after the shell does.
   *
   * Measuring from before it folds page load into a number labelled `CLS[one-turn]`: the strip
   * arriving pushes the page down 81 px for 0.053, every run, throttled or not, and it happens
   * before the question is even typed. It is a real shift and it is written up in `memory/watch.md`
   * as its own item — reserving a strip whose height only the response knows is a product decision
   * about accounts that may have no conditions at all, not a fix to make this assertion pass. What
   * this test is for is the turn: from here to `done`, nothing may move.
   */
  async function pageSettled(page: Page): Promise<void> {
    await expect(page.getByTestId("askreel-conditions")).toBeVisible();
    await painted(page);
  }

  /** The height the page occupies — the number that went 837 → 4 482 px in a single frame. */
  async function docHeight(page: Page): Promise<number> {
    return page.evaluate(() => Math.round(document.documentElement.scrollHeight));
  }

  /** Layout-shift entries are queued, so the observer needs a couple of frames to have them all. */
  async function readCls(page: Page): Promise<{ cls: number; shifts: { value: number; sources: string[] }[] }> {
    await page.waitForTimeout(400);
    return page.evaluate(() => ({
      cls: (window as unknown as { __cls: number }).__cls,
      shifts: (window as unknown as { __shifts: { value: number; sources: string[] }[] }).__shifts,
    }));
  }

  test("a 24-candidate turn that settles on 10 never shows 24, and the page does not jump", async ({ page }) => {
    await bootSearch(page, FILTERS_WITH_FLOOR);
    // The window opens where the turn does. See `pageSettled` for why page load is not in it.
    await pageSettled(page);
    await startCls(page);

    await page.getByTestId("askreel-composer").fill("a twisty courtroom drama");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    // Paced, with a paint between batches. Pushing all 24 frames back-to-back collapses the whole
    // turn into one layout pass and scores ~0.02 whatever the page does, which measures the stub
    // rather than the product. `painted` is what makes "a paint between batches" true rather than
    // hoped for — the wall-clock waits alone left the number at the mercy of the runner's speed.
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await page.waitForTimeout(120);
    await painted(page);
    await askPush(page, "phase", { stage: "discovered", found: 24, scored: 0 });
    await page.waitForTimeout(150);
    await painted(page);
    const reserved = await docHeight(page);

    for (let i = 0; i < STREAMED.length; i += 4) {
      for (const c of STREAMED.slice(i, i + 4)) await askPush(page, "candidate", c);
      await page.waitForTimeout(120);
      await painted(page);
    }
    // …and the scoring pass the real server runs over every candidate. Ten clear the account's 6.0
    // bar and fourteen do not, which is exactly the split `done` is about to commit.
    for (const c of KEPT) await askPush(page, "candidate-scored", { titleId: c.titleId, predictedRating: 7.4 });
    for (const c of RETIRED) await askPush(page, "candidate-scored", { titleId: c.titleId, predictedRating: 5.1 });
    await page.waitForTimeout(200);
    await painted(page);

    // F4-08, as one assertion: the grid never holds a card it is about to take away. Before this,
    // 23–24 posters sat here for between 11.5 and 18.6 seconds and then collapsed to ten.
    await expect(page.getByTestId("semantic-results").locator("a")).toHaveCount(10);
    const peak = await docHeight(page);

    await askPush(page, "done", {
      results: KEPT,
      belowFloor: RETIRED,
      streamed: 24,
      kept: 10,
      droppedByFloor: 14,
      reason: "10 picks ranked for you, from 94 pulled · 14 under your 6.0 bar — shown below.",
    });
    await askEnd(page);
    await expect(page.getByTestId("semantic-results").locator("a")).toHaveCount(10);
    await expect(page.getByTestId("askreel-below-floor")).toContainText("Show the 14 held back by your 6.0 bar");

    const settledHeight = await docHeight(page);
    const { cls, shifts } = await readCls(page);
    console.log(`CLS[one-turn]=${cls.toFixed(3)} reserved=${reserved}px peak=${peak}px settled=${settledHeight}px`);
    // Whatever did move, named. Silent on a clean turn, and on a dirty one it is the first line of
    // the diagnosis rather than the start of it — both regressions this test has caught were a
    // single element, and neither was guessable from the total.
    for (const shift of shifts.filter((s) => s.value >= 0.001)) {
      console.log(`  shift ${shift.value.toFixed(3)} ← ${shift.sources.join(", ")}`);
    }

    // Three assertions, and the height ones are the load-bearing pair.
    //
    // The document no longer triples and then shrinks: 812 → 4 401 → 2 340 px on this fixture
    // before the reserve (the finder measured 837 → 4 482 → 4 851 against a real build), and
    // 1 960 → 2 328 → 2 340 after it. Monotone is the property — nothing the reader is reaching for
    // moves up when the answer settles.
    expect(reserved).toBeGreaterThan(1500);
    expect(settledHeight).toBeGreaterThanOrEqual(peak - 1);

    // CLS is now the strongest of the three, because the turn no longer shifts at all: the measured
    // value is 0.000 unthrottled and 0.000 at 8× CPU throttling, where before the fixes it ran
    // 0.15 / 0.20 / 0.28 / 0.42 / 0.62 at 1× / 4× / 4× / 6× / 8×. That spread was this test's CI
    // flake — CI landed on 0.198 one run and 0.423 the next on the same commit — and it had one
    // cause: the grid's trailing placeholders were keyed by their position among the placeholders
    // rather than by the CELL they hold, so every card that arrived in its own frame shuffled all
    // of them forward one cell. A fast machine coalesced the batch into one layout pass and scored
    // nothing; a loaded runner painted each card and scored up to 0.47. The other two shifts were
    // real and constant: the progress banner leaving flow at `done` (0.0995 — it now hands its box
    // to the provenance line) and the progress bar's sweep animating `left` (now a transform).
    //
    // Asserted at 0.05 rather than at Google's 0.1 "good" boundary, let alone the 0.25 "poor" one
    // it used to sit at. Nothing on this page is allowed to shift during a turn any more, so the
    // budget only has to cover sub-pixel reflow; anything above it is a real element moving and
    // the loop above will say which. Page load is deliberately outside the window (`pageSettled`)
    // and carries a 0.053 shift of its own, which is `memory/watch.md`'s to close, not this test's.
    expect(cls).toBeLessThan(0.05);
  });

  test("a discovered phase reserves the grid before the first card lands", async ({ page }) => {
    await bootSearch(page, FILTERS_WITH_FLOOR);
    await page.getByTestId("askreel-composer").fill("a twisty courtroom drama");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);

    // Before the server has found anything, the reserve is the default eight — the grid is going to
    // be filled either way, and a zero-height grid is what made the document triple in one frame.
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await expect(page.getByTestId("askreel-skeleton")).toHaveCount(8);

    // A turn that will return fewer reserves fewer: over-reserving is its own shift, downward.
    await askPush(page, "phase", { stage: "discovered", found: 3, scored: 0 });
    await expect(page.getByTestId("askreel-skeleton")).toHaveCount(3);

    // …and a big turn is capped at eight rather than reserving 24 rows of nothing.
    await askPush(page, "phase", { stage: "discovered", found: 24, scored: 0 });
    await expect(page.getByTestId("askreel-skeleton")).toHaveCount(8);

    // Cards fill the reserved space rather than adding to it: eight slots, two of them now real.
    await askPush(page, "candidate", STREAMED[0]);
    await askPush(page, "candidate", STREAMED[1]);
    await expect(page.getByTestId("askreel-skeleton")).toHaveCount(6);
    await expect(page.getByTestId("semantic-results").locator("a")).toHaveCount(2);

    // Nothing of the reserve survives the answer.
    await askPush(page, "done", { results: [STREAMED[0], STREAMED[1]], reason: "2 picks ranked for you, from 9 pulled." });
    await askEnd(page);
    await expect(page.getByTestId("askreel-skeleton")).toHaveCount(0);
  });

  test("a card scored under the account's own bar goes to the tray as it is scored", async ({ page }) => {
    // F4-08's mechanism, at the frame level: the grid must not show a card it is about to demote.
    await bootSearch(page, FILTERS_WITH_FLOOR);
    await page.getByTestId("askreel-composer").fill("a twisty courtroom drama");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "candidate", ALPHA);
    await askPush(page, "candidate", BORDER);
    await askPush(page, "candidate", CEDAR);
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Border Town", "Cedar Falls"]);

    // Unscored cards are evidence of nothing and stay in the grid; a score under the bar is the
    // first moment the client knows, so it acts then rather than 18 seconds later at `done`.
    await askPush(page, "candidate-scored", { titleId: BORDER.titleId, predictedRating: 5.2 });
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Cedar Falls"]);
    const tray = page.getByTestId("askreel-below-floor");
    await expect(tray).toContainText("Show the 1 held back by your 6.0 bar");

    // Badge precision, not the raw value: 5.96 prints as "6.0", and a card badged 6.0 inside a tray
    // labelled "under your 6.0 bar" is the same contradiction wearing the other hat.
    await askPush(page, "candidate-scored", { titleId: CEDAR.titleId, predictedRating: 5.96 });
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Cedar Falls"]);

    // The server stays authoritative. Its `done` puts Border back in the answer — a relaxed floor
    // does exactly this — and the client's provisional demotion yields to it without argument.
    await askPush(page, "done", {
      results: [ALPHA, BORDER, CEDAR],
      reason: "3 picks ranked for you, from 40 pulled · relaxed to fill the answer.",
    });
    await askEnd(page);
    await expect(gridNames(page)).toHaveText(["Alpha Creek", "Border Town", "Cedar Falls"]);
    await expect(page.getByTestId("askreel-below-floor")).toHaveCount(0);
  });
});

/**
 * R5-01 — the collision notice belongs to the answer whose cards are on screen.
 *
 * Two wrong states on one thread, one gesture apart. While the kept answer was held after a Stop
 * the notice was GONE — the grid, the tray and the footnote all stayed and the explanation left,
 * which is the one part of the turn F4-06 was filed to preserve. Once the page landed it came back
 * attached to the WRONG bubble: DOM order put `askreel-conflict` under the sea-thriller reply, a
 * question that collided with nothing. A hard reload corrected both, so live state and resumed
 * state disagreed about which answer the explanation belonged to.
 */
test.describe("Ask Reel — the collision notice stays with its answer", () => {
  const CONV = "1f2e3d4c-0000-4000-8000-000000000077";

  /** Settles a colliding answer: a reply, a notice, three cards. */
  async function askColliding(page: Page) {
    await page.getByTestId("askreel-composer").fill("a 90s romantic comedy");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: CONV, turnId: "turn-romcom", sequence: 1 });
    await askPush(page, "assistant-message", { text: "I can’t recommend a 90s romantic comedy.", conflict: true });
    await askPushConflict(page, "Your conditions permanently exclude comedy and romance.", {
      excludedGenres: ["comedy", "romance"],
    });
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    await expect(page.getByTestId("askreel-conflict")).toBeVisible();
  }

  test("a Stop over a held answer keeps the notice with the answer", async ({ page }) => {
    await bootSearch(page);
    await askColliding(page);

    await page.getByTestId("askreel-composer").fill("something tense set at sea");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "conversation", { id: CONV, turnId: "turn-sea", sequence: 3 });
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    // Held, not replaced: the grid is still the romcom answer, so its explanation is still true.
    await expect(page.getByTestId("askreel-conflict")).toBeVisible();

    await page.getByTestId("askreel-stop").click();
    await expect(page.getByTestId("askreel-kept-answer")).toBeVisible();
    const notice = page.getByTestId("askreel-conflict");
    await expect(notice).toHaveCount(1);
    await expect(notice).toContainText("permanently exclude comedy and romance");
    await expect(notice.getByTestId("askreel-conflict-terms")).toHaveText("Rules out comedy and romance.");

    // …and it is under the answer's reply, not under the question that was stopped.
    const place = await page.evaluate(() => {
      const replies = [...document.querySelectorAll('[data-testid="askreel-reply"]')];
      const n = document.querySelector('[data-testid="askreel-conflict"]');
      return {
        replies: replies.length,
        underRomcom: !!n && !!replies[0] && !!(replies[0].compareDocumentPosition(n) & Node.DOCUMENT_POSITION_FOLLOWING),
      };
    });
    // One reply: the stopped turn never produced one, and its half-written sentence is not an answer.
    expect(place).toEqual({ replies: 1, underRomcom: true });

    // And the page that follows does not move it — the answer did not change.
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 3);
    await askPush(page, "done", { results: [], kept: 0, reason: "Nothing more on this one." });
    await askEnd(page);
    await expect(page.getByTestId("askreel-conflict")).toHaveCount(1);
    await expect(page.getByTestId("askreel-conflict")).toContainText("permanently exclude comedy and romance");
    const stillUnderRomcom = await page.evaluate(() => {
      const reply = document.querySelector('[data-testid="askreel-reply"]');
      const n = document.querySelector('[data-testid="askreel-conflict"]');
      const failed = document.querySelector('[data-testid="askreel-failed-turn"]');
      return {
        afterReply: !!reply && !!n && !!(reply.compareDocumentPosition(n) & Node.DOCUMENT_POSITION_FOLLOWING),
        beforeTheStoppedTurn: !!failed && !!n && !!(n.compareDocumentPosition(failed) & Node.DOCUMENT_POSITION_FOLLOWING),
      };
    });
    expect(stillUnderRomcom).toEqual({ afterReply: true, beforeTheStoppedTurn: true });
  });

  test("the stopped turn's own collision is shown with the stopped turn", async ({ page }) => {
    // Fixture variation: the SECOND question collides too. One slot could not hold both, so the
    // follow-up's notice overwrote the kept answer's and then anchored under the wrong bubble.
    await bootSearch(page);
    await askColliding(page);

    await page.getByTestId("askreel-composer").fill("a funny anime");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "conversation", { id: CONV, turnId: "turn-anime", sequence: 3 });
    await askPushConflict(page, "Your conditions permanently exclude anime.", { excludedKeywords: ["anime"] });
    await page.getByTestId("askreel-stop").click();

    // Two notices, each with its own row: the answer keeps its account, the abandoned turn keeps its.
    const stopped = page.getByTestId("askreel-stopped");
    await expect(stopped.getByTestId("askreel-conflict")).toContainText("permanently exclude anime");
    // The stopped card sits in the transcript now, under the turn it is about (chat UI v2), so the
    // answer's own notices are the ones OUTSIDE it.
    const inTranscript = page.locator(
      '[data-testid="askreel-transcript"] [data-testid="askreel-conflict"]:not([data-testid="askreel-stopped"] *)',
    );
    await expect(inTranscript).toHaveCount(1);
    await expect(inTranscript).toContainText("permanently exclude comedy and romance");
    await expect(inTranscript).not.toContainText("anime");
  });
});
