import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubAskReel, stubEventSource } from "./helpers";

/**
 * Ask Reel's rendering, measured rather than described.
 *
 * Round 2's headed-browser pass (F6) filed five defects that no assertion in this suite could
 * have caught, because every one of them is about a BOX rather than about text: a popover whose
 * left edge sat 114 px off-screen at 320 with no way to scroll to it, controls between 25 and
 * 36 px tall against a 44 px minimum, a seam that printed a 1 000-character question as one
 * caption, and the only destructive control in the product being a 26 px tap with nothing between
 * it and a permanent delete.
 *
 * So these tests read `getBoundingClientRect` and `elementFromPoint` and assert numbers. Text
 * assertions belong in `askreel-continuity.spec.ts`; what lives here is geometry, and the reason
 * it is its own file is that geometry needs a viewport, which is a `test.use` and therefore a
 * file-level decision.
 */

// `hasTouch`, because half of what is under test is what a THUMB can reach: `locator.tap()` fires
// real touch events, and a control that only responds to a synthetic click would pass a mouse
// test and fail a person.
test.use({ viewport: { width: 375, height: 812 }, hasTouch: true });

/** WCAG 2.1 AA, success criterion 2.5.5 target size. Not a house style — the published minimum. */
const TOUCH_MIN = 44;

const CONVERSATION = "5c0a1b2c-0000-4000-8000-000000000001";
const OTHER = "5c0a1b2c-0000-4000-8000-000000000002";

/**
 * Thread titles long enough to be cut by a clipped popover. F6-05 read the damage off exactly
 * this: "…drama with supe…", "…e with a clever en…" — the left half of every row gone, and
 * unreachable, because the document does not scroll horizontally.
 */
const CONVERSATION_LIST = [
  {
    id: CONVERSATION,
    title: "a slow-burn detective drama with superb photography",
    turnCount: 4,
    lastTurnAt: new Date().toISOString(),
    lastCardsAt: new Date().toISOString(),
  },
  {
    id: OTHER,
    title: "something tense with a clever ending",
    turnCount: 2,
    lastTurnAt: new Date(Date.now() - 86_400_000).toISOString(),
    lastCardsAt: new Date(Date.now() - 86_400_000).toISOString(),
  },
];

const FILTERS = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

/**
 * The conversation list as a SERVER, not as a constant.
 *
 * The delete tests need the list route and the delete route to agree: a deferred delete that is
 * cancelled must leave the row there on the next fetch, and one that commits must take it away.
 * Mocking the list as a frozen array makes both of those unobservable — the row comes back either
 * way, so an optimistic hide and a real delete look identical.
 */
function conversationServer(initial: Record<string, unknown>[] = CONVERSATION_LIST) {
  return { rows: [...initial], deletes: [] as string[], resume: null as Record<string, unknown> | null };
}

/** A thread with one answered turn, so `/search` has a conversation id on screen to delete. */
const RESUME_PAYLOAD = {
  id: CONVERSATION,
  title: "a slow-burn detective drama with superb photography",
  lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
  turns: [
    { sequence: 0, role: "user", text: "a slow-burn detective drama with superb photography", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
  ],
  cards: [],
  reason: null,
  removed: 0,
  lastQuery: "a slow-burn detective drama with superb photography",
  mediaTypes: [],
  shown: [],
};

async function boot(
  page: Page,
  opts: { conversations?: unknown[]; server?: ReturnType<typeof conversationServer> } = {},
) {
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
  const server = opts.server;
  if (server) {
    await page.route(
      (u) => u.pathname === "/api/search/conversations",
      (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(server.rows) }),
    );
    await page.route(
      (u) => u.pathname.startsWith("/api/search/conversations/"),
      (route) => {
        const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop()!);
        if (route.request().method() !== "DELETE") {
          return server.resume && id === server.resume.id
            ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(server.resume) })
            : route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
        }
        server.deletes.push(id);
        server.rows = server.rows.filter((r) => r.id !== id);
        return route.fulfill({ status: 204, body: "" });
      },
    );
    if (server.resume) {
      await page.addInitScript(
        (id) => localStorage.setItem("reel.ask.conversationId", id as string),
        server.resume.id as string,
      );
    }
  } else {
    await mockJson(page, "**/api/search/conversations", opts.conversations ?? CONVERSATION_LIST);
    await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  }
  await page.goto("/search");
  await expect(page.getByTestId("askreel-recent")).toBeVisible();
}

/** The box, in viewport coordinates — the only unit any of these findings were stated in. */
async function boxOf(page: Page, testId: string) {
  const box = await page.getByTestId(testId).first().boundingBox();
  expect(box, `${testId} has no box`).not.toBeNull();
  return box!;
}

test.describe("the Recent popover stays inside the viewport", () => {
  test("at 320 the menu is fully on screen and every row is tappable", async ({ page }) => {
    await boot(page);
    // The width F6-05 measured at. The header wraps here, which is what moved `Recent ▾` inward
    // and put a right-anchored 288 px panel at x: -114.
    await page.setViewportSize({ width: 320, height: 568 });
    await page.getByTestId("askreel-recent").tap();
    // The list is fetched on open, so the first frame is "Loading…" and has no rows to measure.
    await expect(page.getByTestId("askreel-recent-row").first()).toBeVisible();

    const menu = await boxOf(page, "askreel-recent-menu");
    expect(menu.x, "the menu's left edge is off-screen").toBeGreaterThanOrEqual(0);
    expect(menu.x + menu.width, "the menu's right edge is off-screen").toBeLessThanOrEqual(320);

    // …and the document still does not scroll sideways, which is what made the clipping
    // unrecoverable rather than merely ugly.
    const scroll = await page.evaluate(() => ({
      w: document.documentElement.scrollWidth,
      c: document.documentElement.clientWidth,
    }));
    expect(scroll.w).toBe(scroll.c);

    // A row that is on screen but underneath something else is no better than one that is off it.
    const hit = await page.evaluate(() => {
      const row = document.querySelector('[data-testid="askreel-recent-open"]');
      if (!row) return "no row";
      const b = row.getBoundingClientRect();
      const top = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2);
      return top && row.contains(top) ? "ok" : (top?.getAttribute("data-testid") ?? top?.tagName ?? "nothing");
    });
    expect(hit).toBe("ok");
  });

  test("at 375 the menu is fully on screen too", async ({ page }) => {
    await boot(page);
    await page.getByTestId("askreel-recent").tap();
    const menu = await boxOf(page, "askreel-recent-menu");
    expect(menu.x).toBeGreaterThanOrEqual(0);
    expect(menu.x + menu.width).toBeLessThanOrEqual(375);
  });

  test("Escape closes it, and so does a tap outside", async ({ page }) => {
    await boot(page);
    const recent = page.getByTestId("askreel-recent");

    // F6-14: the component listened for `pointerdown` only, so the menu was open with focus on a
    // button whose `aria-expanded` said so and Escape did nothing at all.
    await recent.tap();
    await expect(page.getByTestId("askreel-recent-menu")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByTestId("askreel-recent-menu")).toHaveCount(0);
    await expect(recent).toHaveAttribute("aria-expanded", "false");

    await recent.tap();
    await expect(page.getByTestId("askreel-recent-menu")).toBeVisible();
    // A real touch on the page title — not a link, not inside the sheet, and above it at every
    // width, so this is the "tapped somewhere else" gesture and nothing more.
    await page.getByRole("heading", { name: "Ask Reel" }).tap();
    await expect(page.getByTestId("askreel-recent-menu")).toHaveCount(0);
  });
});

test.describe("deleting a thread", () => {
  test("the trash asks first, and No leaves the thread alone", async ({ page }) => {
    const server = conversationServer();
    await boot(page, { server });
    await page.getByTestId("askreel-recent").tap();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(2);

    // F6-07: this tap WAS the delete. Now it is a question.
    await page.getByTestId("askreel-recent-delete").first().tap();
    await expect(page.getByTestId("askreel-recent-delete-no").first()).toBeVisible();
    expect(server.deletes, "asking must not delete").toEqual([]);

    await page.getByTestId("askreel-recent-delete-no").first().tap();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(2);
    expect(server.deletes).toEqual([]);
  });

  test("Yes defers the delete behind an undo window, and Undo cancels it outright", async ({ page }) => {
    const server = conversationServer();
    await boot(page, { server });
    await page.getByTestId("askreel-recent").tap();
    await page.getByTestId("askreel-recent-delete").first().tap();
    await page.getByTestId("askreel-recent-delete-yes").first().tap();

    // The row goes at once — the answer to "did that work" has to be immediate — but the request
    // does not. An undo that cannot un-delete is not an undo, and the server cannot restore a
    // transcript and its cards once they are gone.
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(1);
    await expect(page.getByTestId("askreel-delete-toast")).toBeVisible();
    expect(server.deletes, "the delete must not have been sent yet").toEqual([]);

    await page.getByTestId("askreel-delete-toast-undo").tap();
    await expect(page.getByTestId("askreel-delete-toast")).toHaveCount(0);
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(2);
    // …and it is still not sent, now or later.
    await page.waitForTimeout(1500);
    expect(server.deletes).toEqual([]);
  });

  test("left alone, the window closes and the delete commits", async ({ page }) => {
    const server = conversationServer();
    await boot(page, { server });
    await page.getByTestId("askreel-recent").tap();
    await page.getByTestId("askreel-recent-delete").first().tap();
    await page.getByTestId("askreel-recent-delete-yes").first().tap();
    await expect(page.getByTestId("askreel-delete-toast")).toBeVisible();

    // Dismiss is the same commit the clock makes, six seconds sooner — asserted through the button
    // so the test does not spend six seconds proving a `setTimeout` works.
    await page.getByTestId("askreel-delete-toast-dismiss").tap();
    await expect(page.getByTestId("askreel-delete-toast")).toHaveCount(0);
    await expect.poll(() => server.deletes).toEqual([CONVERSATION]);

    // The RTK invalidation still fires: reopening refetches the list rather than serving the
    // cached two rows that the optimistic hide was only pretending about.
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(1);
  });

  /**
   * The probe: one field changed — the thread being deleted is the one ON SCREEN — and the same
   * three gestures run again. This is where a deferred delete can go wrong in a way the other
   * tests cannot see: resetting the page at "Yes" would leave an undo that restores a row into a
   * list while the transcript it belongs to has already been thrown away.
   */
  test("deleting the thread you are reading resets the page only when the delete commits", async ({ page }) => {
    const server = conversationServer();
    server.resume = RESUME_PAYLOAD;
    await boot(page, { server });
    await expect(page.getByTestId("askreel-user-bubble")).toBeVisible();

    await page.getByTestId("askreel-recent").tap();
    await page.getByTestId("askreel-recent-delete").first().tap();
    await page.getByTestId("askreel-recent-delete-yes").first().tap();
    // Still the thread it was: the transcript is untouched while the window is open.
    await expect(page.getByTestId("askreel-user-bubble")).toBeVisible();

    await page.getByTestId("askreel-delete-toast-undo").tap();
    await expect(page.getByTestId("askreel-user-bubble")).toBeVisible();
    expect(server.deletes).toEqual([]);

    // …and on commit the page does fall back to a fresh thread, because it is now rendering
    // something the server no longer has. The menu is still open — neither the confirm nor the
    // toast closes it, both of them being inside the component's own box.
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(2);
    await page.getByTestId("askreel-recent-delete").first().tap();
    await page.getByTestId("askreel-recent-delete-yes").first().tap();
    await page.getByTestId("askreel-delete-toast-dismiss").tap();
    await expect.poll(() => server.deletes).toEqual([CONVERSATION]);
    await expect(page.getByTestId("askreel-user-bubble")).toHaveCount(0);
    await expect(page.getByTestId("askreel-preset").first()).toBeVisible();
  });
});

test.describe("the resume seam", () => {
  /** The cap the server accepts and the history keeps — and, until now, the seam reproduced. */
  const LONG = "a slow-burn detective story set in a cold northern city with superb photography, "
    .repeat(13)
    .slice(0, 1000);

  /** A thread whose newest turn failed, so the seam has to name which question the grid answers. */
  function seamPayload(question: string) {
    return {
      id: CONVERSATION,
      title: question.slice(0, 40),
      lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
      turns: [
        { sequence: 0, role: "user", text: question, kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
        { sequence: 2, role: "user", text: "something tenser", kind: "ask", status: "Failed" },
        { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "server_error" },
      ],
      cards: [],
      reason: null,
      removed: 0,
      lastQuery: "something tenser",
      mediaTypes: [],
      shown: [],
    };
  }

  test("a 1 000-character question is one line in the seam, and opens on tap", async ({ page }) => {
    const server = conversationServer();
    server.resume = seamPayload(LONG);
    await boot(page, { server });

    const answers = page.getByTestId("askreel-seam-answers");
    await expect(answers).toBeVisible();

    // F6-08: the seam rendered the whole thing as one unwrapped caption — at 375 it was taller
    // than the viewport, on the one element whose entire job is to be glanceable.
    const collapsed = (await answers.boundingBox())!;
    expect(collapsed.height, `the seam is ${Math.round(collapsed.height)} px tall`).toBeLessThan(40);

    // Clamped, not truncated: the whole question is still in the DOM and on the title attribute,
    // so a hover, a screen reader and a copy-paste all still get the real thing.
    await expect(answers).toHaveAttribute("title", LONG);
    // Clipped by the box, not cut out of the text — the element overflows its own width, which is
    // what `truncate` does and what a JavaScript substring would not.
    const clipped = await answers.evaluate((el) => el.scrollWidth > el.clientWidth);
    expect(clipped, "the seam is not actually clamped").toBe(true);

    await answers.tap();
    const expanded = (await answers.boundingBox())!;
    expect(expanded.height).toBeGreaterThan(collapsed.height);
    expect(await answers.innerText()).toContain(LONG.slice(0, 60));

    await answers.tap();
    expect((await answers.boundingBox())!.height).toBeLessThan(40);
  });

  test("an ordinary question is unchanged and still readable whole", async ({ page }) => {
    // The probe: one field — the question's length — moved from 1 000 characters to 24. A clamp
    // that also hides a short question has traded one defect for a quieter one.
    const server = conversationServer();
    server.resume = seamPayload("a tense heist in Lisbon");
    await boot(page, { server });

    const answers = page.getByTestId("askreel-seam-answers");
    await expect(answers).toContainText("a tense heist in Lisbon");
    expect((await answers.boundingBox())!.height).toBeLessThan(40);
  });
});

test.describe("touch targets", () => {
  /**
   * Every Ask Reel control this fixer owns, at the reference phone width. Foreign components
   * (`BrowseFilters`, the card overlay, the tab bar) are deliberately not asserted here — they are
   * other people's files, and a test that fails on someone else's box is a test that gets deleted.
   */
  async function expectTarget(page: Page, testId: string) {
    const box = await boxOf(page, testId);
    expect(box.height, `${testId} is ${Math.round(box.height)} px tall`).toBeGreaterThanOrEqual(TOUCH_MIN);
    expect(box.width, `${testId} is ${Math.round(box.width)} px wide`).toBeGreaterThanOrEqual(TOUCH_MIN);
  }

  test("the header, the composer and the presets all meet 44 x 44", async ({ page }) => {
    await boot(page);
    for (const id of ["askreel-new", "askreel-recent", "askreel-preset", "askreel-send", "askreel-mic"]) {
      await expectTarget(page, id);
    }
    // The delete control is the smallest of them and the only destructive one — F6-07 measured it
    // at 26 x 26, eight pixels from the row that opens the thread.
    await page.getByTestId("askreel-recent").tap();
    await expectTarget(page, "askreel-recent-delete");
  });

  test("the answer's own controls meet 44 x 44 on a resumed thread", async ({ page }) => {
    // The probe, and the reason this case is separate: these five only exist over an ANSWER, so a
    // cold page can never measure them. One fixture change — a resumed thread with cards, a tray,
    // a long question and a failed tail — reaches all of them at once.
    const card = (n: string) => ({
      titleId: `00000000-0000-0000-0000-0000000000${n}`,
      mediaType: "Movie", tmdbId: 900 + Number(n), name: `Title ${n}`, year: 2021,
      posterPath: null, genres: ["thriller"], similarity: 0.5, predictedRating: 7.2,
      why: "Because.", seen: false,
    });
    const server = conversationServer(
      Array.from({ length: 20 }, (_, i) => ({
        id: `thread-${i}`, title: `thread ${i}`, turnCount: 2,
        lastTurnAt: new Date().toISOString(), lastCardsAt: new Date().toISOString(),
      })),
    );
    server.resume = {
      id: CONVERSATION,
      title: "long one",
      lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
      turns: [
        { sequence: 0, role: "user", text: "a slow-burn detective story ".repeat(20), kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
        { sequence: 2, role: "user", text: "tenser", kind: "ask", status: "Failed" },
        { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "server_error" },
      ],
      cards: [card("11"), card("12")],
      belowFloor: [card("13")],
      reason: "2 picks ranked for you, from 40 pulled.",
      removed: 0,
      lastQuery: "tenser",
      mediaTypes: [],
      shown: [],
    };
    await boot(page, { server });
    await expect(page.getByTestId("semantic-results")).toBeVisible();

    for (const id of ["askreel-more", "askreel-below-floor-toggle", "askreel-retry", "askreel-bubble-toggle"]) {
      await expectTarget(page, id);
    }
    await page.getByTestId("askreel-recent").tap();
    await expectTarget(page, "askreel-recent-older");
  });

  test("the chips and Stop meet 44 x 44 while a turn runs", async ({ page }) => {
    await boot(page);
    await page.getByTestId("askreel-composer").fill("a heist with a great ensemble");
    await page.getByTestId("askreel-composer").press("Enter");
    await expect(page.getByTestId("askreel-banner")).toBeVisible();

    await expectTarget(page, "askreel-stop");
    await expectTarget(page, "askreel-chip");
  });
});
