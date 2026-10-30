import { test, expect, type Page } from "@playwright/test";
import {
  SESSION_FOUNDER, askEnd, askFailNext, askKeepalive, askPush, askRequests, mockJson,
  stubAskReel, stubEventSource, waitForAsk,
} from "./helpers";

/**
 * Continuity — the turn record, from the browser.
 *
 * Round 1 found four ways for Ask Reel to forget what it had just said, and every one of them was
 * the same fact wearing a different hat: the conversation lived in a `useReducer` inside a route
 * component, and the server kept turns without keeping what they returned.
 *
 *  - **F3-05** — Home, then Back, came back as a cold start. The route unmounts; the answer went
 *    with it. 23 cards and a transcript replaced by the mood presets.
 *  - **F3-06** — every resume said "transcript only — this thread predates saved picks", seen on a
 *    thread 17 seconds old, because that was the only branch a resume could reach.
 *  - **F1-06** — a reload resumed the ACCOUNT's newest thread, not this tab's, so two tabs
 *    silently converged on one conversation.
 *  - **R2/F3-02** — a stopped page left sub-floor cards in the recommended grid beneath a tray
 *    saying sub-floor cards had been held back.
 *
 * The mocks are the SERVER's record, not the client's memory: `getConversation` returns the cards
 * in a deliberately non-blend order, so any client that re-derives the order fails here rather
 * than in front of a person.
 */

const CONVERSATION = "7f3a1b2c-0000-4000-8000-000000000001";
const OTHER_CONVERSATION = "7f3a1b2c-0000-4000-8000-000000000002";

const card = (id: string, name: string, extra: Record<string, unknown> = {}) => ({
  titleId: `00000000-0000-0000-0000-0000000000${id}`,
  mediaType: "Movie",
  tmdbId: Number(`80${id.charCodeAt(0)}`),
  name,
  year: 2023,
  posterPath: null,
  genres: ["thriller"],
  similarity: 0.5,
  predictedRating: 7.1,
  why: `Why ${name}.`,
  seen: false,
  ...extra,
});

const ALPHA = card("a1", "Alpha Creek", { blend: 0.2 });
const BORDER = card("b2", "Border Town", { blend: 0.9 });
const CEDAR = card("c3", "Cedar Falls", { blend: 0.5 });

/**
 * The answer, in the order the server committed as `Rank` — which is the order on screen from the
 * moment `done` arrives, live or resumed. Alpha's blend is the LOWEST and it is first: a client
 * that sorts by blend puts Border first and shows a different answer from the one that was stored.
 */
const STORED_ORDER = ["Alpha Creek", "Border Town", "Cedar Falls"];

/**
 * What a client that re-derived the order would show instead. Asserted NEGATIVELY: the client does
 * derive exactly this while a turn is still streaming and it has no server order to use, and SB-01
 * was that derivation surviving `done` — so the same answer rendered one way live and the other
 * way after a reload, with the page turn in between flipping it.
 */
const BLEND_ORDER = ["Border Town", "Cedar Falls", "Alpha Creek"];

const RESUME_PAYLOAD = {
  id: CONVERSATION,
  title: "a tense heist",
  lastCardsAt: new Date(Date.now() - 3 * 60 * 60 * 1000).toISOString(),
  turns: [
    { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
  ],
  cards: [ALPHA, BORDER, CEDAR],
  reason: "3 picks ranked for you, from 40 pulled.",
  removed: 0,
  lastQuery: "a tense heist",
  mediaTypes: [],
  shown: [ALPHA.tmdbId, BORDER.tmdbId, CEDAR.tmdbId],
};

const CONVERSATION_LIST = [
  { id: CONVERSATION, title: "a tense heist", turnCount: 2, lastTurnAt: new Date().toISOString(), lastCardsAt: RESUME_PAYLOAD.lastCardsAt },
  { id: OTHER_CONVERSATION, title: "something funny", turnCount: 4, lastTurnAt: new Date(Date.now() - 86_400_000).toISOString(), lastCardsAt: null },
];

const FEED = { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 };

/** The account's saved bar — what the stopped-page floor is applied against. */
const FILTERS_WITH_FLOOR = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

/**
 * Boots /search with the shell mocked. `resume` decides what the conversation routes answer:
 * "none" is a browser that has never asked (a 404 on the deprecated alias), "stored" serves the
 * populated payload from BOTH the id route and the alias. `payload` overrides what "stored"
 * serves, so a spec can vary one field of the resume shape without restating the whole mock stack.
 */
async function boot(
  page: Page,
  opts: { resume?: "none" | "stored"; storedId?: string; payload?: Record<string, unknown> } = {},
) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/settings/filters", FILTERS_WITH_FLOOR);
  await mockJson(page, "**/api/feed", FEED);
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/search/conversations", CONVERSATION_LIST);

  if (opts.resume === "stored") {
    const payload = opts.payload ?? RESUME_PAYLOAD;
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, payload);
    await mockJson(page, "**/api/search/conversations/latest", payload);
  } else {
    await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  }
  if (opts.storedId) {
    await page.addInitScript(
      (id) => localStorage.setItem("reel.ask.conversationId", id as string),
      opts.storedId,
    );
  }
  await page.goto("/search");
}

/**
 * Serves `GET /api/search/conversations` the way the server does — honouring the client's own
 * `limit` against an account of `total` threads — and records every limit that was asked for.
 *
 * Registered AFTER `boot`, which matches first in Playwright, so the flat mock there is the
 * default and this is the opt-in for specs that care how many rows the client requests. The
 * current thread is row one so the menu's "this is the one you are on" marker still has a target.
 */
async function routeThreadList(page: Page, total: number): Promise<number[]> {
  const limits: number[] = [];
  const threads = Array.from({ length: total }, (_, i) => ({
    id: i === 0 ? CONVERSATION : `7f3a1b2c-0000-4000-8000-${String(i).padStart(12, "0")}`,
    title: i === 0 ? "a tense heist" : `thread ${i}`,
    turnCount: 2,
    lastTurnAt: new Date(Date.now() - i * 60_000).toISOString(),
    lastCardsAt: null,
  }));
  await page.route(
    (u) => u.pathname === "/api/search/conversations",
    (route) => {
      const limit = Number(new URL(route.request().url()).searchParams.get("limit") ?? 20);
      limits.push(limit);
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(threads.slice(0, Math.min(limit, total))),
      });
    },
  );
  return limits;
}

/**
 * The main grid's card names, in DOM order. `p.truncate` and not `p[title]`: the why-line carries
 * a title attribute too, so the looser selector returns two elements per card and an order
 * assertion against it passes for the wrong reason.
 */
function gridNames(page: Page) {
  return page.locator('[data-testid="semantic-results"] a > p.truncate');
}

/** Every why-line under the grid's cards, in DOM order. */
function gridWhy(page: Page) {
  return page.locator('[data-testid="semantic-results"] a > p.line-clamp-2');
}

/** Runs one full turn against the stub and leaves a settled three-card answer on screen. */
async function askAndAnswer(page: Page) {
  await page.getByTestId("askreel-composer").fill("a tense heist");
  await page.getByTestId("askreel-composer").press("Enter");
  await waitForAsk(page);
  await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-1", sequence: 1 });
  // A keepalive mid-turn: the server writes one every 10 seconds, and a parser that treats the
  // comment as a frame loses whatever is next. Nothing on screen may react to it.
  await askKeepalive(page);
  await askPush(page, "assistant-message", { text: "Three that fit." });
  await askPush(page, "candidate", ALPHA);
  await askPush(page, "candidate", BORDER);
  await askPush(page, "candidate", CEDAR);
  await askPush(page, "done", {
    results: [ALPHA, BORDER, CEDAR],
    reason: "3 picks ranked for you, from 40 pulled.",
  });
  await askEnd(page);
  await expect(gridNames(page)).toHaveCount(3);
}

test.describe("Ask Reel — the thread survives leaving the page", () => {
  test("Home then Back restores the same answer, in the same order, with its why-lines", async ({ page }) => {
    // F3-05, verbatim: get a full answer, click Home in the header, press Back.
    await boot(page);
    await askAndAnswer(page);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    const whyBefore = await gridWhy(page).allTextContents();

    // The stored record exists from here on — the server kept the cards this turn produced.
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, RESUME_PAYLOAD);

    await page.getByLabel("Reel home").click();
    await expect(page).toHaveURL(/\/home$/);
    // Wait for /search to genuinely UNMOUNT before going back. The URL changes synchronously on
    // pushState, so a goBack issued straight after it lands inside the same React batch and the
    // route never unmounts at all — the test would then pass against a page that had never had to
    // restore anything, which is precisely the bug it exists to catch.
    await expect(page.getByTestId("askreel-composer")).toHaveCount(0);
    await page.goBack();

    // Not a cold start. The mood presets are the tell — they render only on a page with no
    // conversation at all, and they were what came back before.
    await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    // Same why-lines, each still under its own card — the server's copy, not a re-derivation.
    expect((await gridWhy(page).allTextContents()).sort()).toEqual([...whyBefore].sort());
    expect(await gridWhy(page).allTextContents()).toEqual(STORED_ORDER.map((n) => `Why ${n}.`));
    // The transcript came back with it, and the controls are alive rather than a frozen picture.
    await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
    await expect(page.getByTestId("askreel-chip").first()).toBeEnabled();
    // The seam says WHEN, dated from the server's own stamp. A grid restored in silence reads as
    // a live answer to a question this visit never asked.
    await expect(page.getByTestId("askreel-seam")).toContainText("Picked up where you left off");
    // …and it is NOT the transcript-only card, on a thread minutes old (F3-06).
    await expect(page.getByTestId("askreel-transcript-only")).toHaveCount(0);
    // F6-15. The seam is the sighted half; this is the other one. It was the empty string on every
    // resumed landing, while a seam, a grid, a footnote and three enabled chips were on screen.
    await expect(page.getByTestId("askreel-status")).toHaveText("Picked up where you left off · 3 picks.");
  });

  test("a hard reload comes back to the same answer", async ({ page }) => {
    // Fixture variation on the test above: same destination, different route in — a reload
    // remounts the whole app rather than just the route, so nothing in memory survives at all.
    await boot(page);
    await askAndAnswer(page);
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, RESUME_PAYLOAD);

    await page.reload();

    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-transcript-only")).toHaveCount(0);
    // The reload asked for THIS conversation by id — the whole of F1-06. `/latest` would have
    // answered with the account's newest thread, which in a second tab is somebody else's.
    const resumeRequests = page.url();
    expect(resumeRequests).toContain("/search");
  });

  test("a second tab opens this tab's thread, not the account's newest", async ({ page, context }) => {
    // F1-06. The two tabs share localStorage, so the second one knows which thread the first was
    // on; `/conversations/latest` — which is what the client used to ask for — is mocked here to a
    // DIFFERENT conversation, so a client that still reaches for it fails this test loudly.
    await boot(page);
    await askAndAnswer(page);

    const second = await context.newPage();
    await stubEventSource(second);
    await stubAskReel(second);
    await mockJson(second, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(second, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(second, "**/api/settings/filters", FILTERS_WITH_FLOOR);
    await mockJson(second, `**/api/search/conversations/${CONVERSATION}`, RESUME_PAYLOAD);
    await mockJson(second, "**/api/search/conversations/latest", {
      ...RESUME_PAYLOAD,
      id: OTHER_CONVERSATION,
      title: "someone else's thread",
      turns: [{ sequence: 0, role: "user", text: "a no-read hold probe", kind: "ask", status: "Completed" }],
      cards: [],
      lastQuery: "a no-read hold probe",
    });
    await second.goto("/search");

    await expect(gridNames(second)).toHaveText(STORED_ORDER);
    await expect(second.getByTestId("askreel-transcript")).toContainText("a tense heist");
    await expect(second.getByTestId("askreel-transcript")).not.toContainText("a no-read hold probe");
    await second.close();
  });
});

/**
 * R3-03. Leaving `/search` and coming back with Back re-rendered the answer as it was at the last
 * GET, not as it is.
 *
 * `getConversation` provides `{type:"Conversation", id}` and only `deleteConversation` invalidated
 * it, so a completed `/ask` — which streams outside RTK Query entirely — changed the thread and
 * told the cache nothing. A 21 + 21 paged answer came back as 14 + 10 under the footnote of the
 * turn before the page, on the very gesture F6-03 had just been fixed to make reachable, and it
 * did not self-correct on a wait. A hard reload restored all 42, which is what pinned it to the
 * client rather than to the record.
 */
test.describe("Ask Reel — leaving the page and coming back shows the thread as it is", () => {
  const DELTA = card("d4", "Delta Ridge", { blend: 0.4 });
  const ECHO = card("e5", "Echo Valley", { blend: 0.3 });

  /** The server's record AFTER the page — five cards across two turns, as a resume replays it. */
  const PAGED_PAYLOAD = {
    ...RESUME_PAYLOAD,
    turns: [
      ...RESUME_PAYLOAD.turns,
      { sequence: 2, role: "assistant", text: "", kind: "page", status: "Completed" },
    ],
    cards: [ALPHA, BORDER, CEDAR, DELTA, ECHO],
    // Deliberately NOT the sentence the live page frame carries, so the two are distinguishable on
    // screen: this is the stored turn's reason, and it may only appear once the page has actually
    // re-read the thread.
    reason: "2 picks ranked for you, from 65 pulled.",
    shown: [ALPHA, BORDER, CEDAR, DELTA, ECHO].map((c) => c.tmdbId),
  };

  /** Runs "Show me more" against the stub and settles a five-card, two-turn answer. */
  async function pageTheAnswer(page: Page) {
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page);
    await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-2", sequence: 2 });
    await askPush(page, "done", { results: [DELTA, ECHO], reason: "2 more from the same angle.", kept: 2 });
    await askEnd(page);
    await expect(gridNames(page)).toHaveCount(5);
  }

  test("a paged answer survives Home and Back at its paged size", async ({ page }) => {
    // The exact repro: resume a thread (which warms the cache with the thread as it was), page it,
    // leave by the header mark, come back with Back.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    // The server's record from the moment the page lands. Registered before the turn so the
    // re-read the completed page triggers gets the thread as it now is.
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, PAGED_PAYLOAD);
    await pageTheAnswer(page);
    await expect(page.getByTestId("askreel-footnote")).toContainText("2 more from the same angle.");

    await page.getByLabel("Reel home").click();
    await expect(page).toHaveURL(/\/home$/);
    // Wait for /search to genuinely UNMOUNT — a goBack inside the same React batch never re-mounts
    // the route, and the test would pass against a page that never had to restore anything.
    await expect(page.getByTestId("askreel-composer")).toHaveCount(0);
    await page.goBack();

    // Five, not three. Before the fix this was the thread as of the last fetch — the answer before
    // the page — with the earlier turn's footnote under it.
    await expect(gridNames(page)).toHaveCount(5);
    await expect(gridNames(page)).toHaveText([...STORED_ORDER, "Delta Ridge", "Echo Valley"]);
    await expect(page.getByTestId("askreel-footnote")).toContainText("2 picks ranked for you, from 65 pulled.");
  });

  test("the completed page re-reads the thread it changed, without disturbing the answer on screen", async ({ page }) => {
    // The mechanism, pinned on its own: the invalidation fires, AND the payload it fetches does not
    // replace the answer the reader is looking at. Both halves matter — a refetch that clobbered
    // would swap a live paged answer for the server's account of it mid-read, which is a state
    // change nobody asked for even when the data is right.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    // Registered AFTER boot so it wins the match, then reloaded so the count starts at this
    // page's own first read of the thread.
    const reads: string[] = [];
    // Flipped by the test rather than counted off, so a second read from a StrictMode remount
    // cannot decide what the FIRST render sees.
    let hasPaged = false;
    await page.route(
      (u) => u.pathname === `/api/search/conversations/${CONVERSATION}`,
      (route) => {
        reads.push(route.request().url());
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(hasPaged ? PAGED_PAYLOAD : RESUME_PAYLOAD),
        });
      },
    );
    await page.reload();
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    const before = reads.length;
    hasPaged = true;

    await pageTheAnswer(page);
    // The thread was re-read because a turn finished, not because anything re-mounted.
    await expect.poll(() => reads.length).toBeGreaterThan(before);
    // …and the screen is still the live answer: the stored sentence has NOT taken over the
    // footnote, and the five cards are the five the page produced.
    await expect(page.getByTestId("askreel-footnote")).toContainText("2 more from the same angle.");
    await expect(gridNames(page)).toHaveCount(5);
  });

  test("switching to another thread is never mistaken for a re-read of this one", async ({ page }) => {
    // Fixture variation on the guard: the same "a payload arrived while this visit owns the screen"
    // shape, with the one difference that matters — the user asked for a DIFFERENT thread. Blocking
    // that would make `Recent ▾` inert after the first question of a visit.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, PAGED_PAYLOAD);
    await pageTheAnswer(page);

    await mockJson(page, `**/api/search/conversations/${OTHER_CONVERSATION}`, {
      ...RESUME_PAYLOAD,
      id: OTHER_CONVERSATION,
      title: "something funny",
      turns: [
        { sequence: 0, role: "user", text: "something funny", kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "One that fits.", kind: "ask", status: "Completed" },
      ],
      cards: [CEDAR],
      reason: "1 pick ranked for you, from 12 pulled.",
      lastQuery: "something funny",
    });
    await page.getByTestId("askreel-recent").click();
    await page.getByTestId("askreel-recent-open").nth(1).click();

    await expect(page.getByTestId("askreel-transcript")).toContainText("something funny");
    await expect(gridNames(page)).toHaveText(["Cedar Falls"]);
  });
});

test.describe("Ask Reel — the server's order is the contract", () => {
  test("a page never re-orders the answer it extends, and the reload reproduces the screen", async ({ page }) => {
    // SB-01, both halves in one run. Wave-B pressed `Show me more` and watched page-two's picks
    // interleave into page-one's answer by blend — then hard-reloaded and got the stored
    // `(Sequence, Rank)` order instead. Same 34 cards, two orders, one gesture apart.
    await boot(page);
    await askAndAnswer(page);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    // …and NOT the order the client derives while it is still assembling one.
    await expect(gridNames(page)).not.toHaveText(BLEND_ORDER);

    // Delta outranks every card of turn one on blend; Echo is below all of them. If the page's
    // frames re-sort the merged array, Delta jumps to the very top and Echo to the very bottom —
    // the exact interleave the smoke recorded.
    const delta = card("d4", "Delta Ridge", { blend: 0.95 });
    const echo = card("e5", "Echo Point", { blend: 0.1 });

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    // Arrival order is the weaker card first, so the settle at the phase boundary is observable:
    // the page's OWN two cards may move, and nothing above them may.
    await askPush(page, "candidate", echo);
    await askPush(page, "candidate", delta);
    await askPush(page, "phase", { stage: "ranking", found: 5, scored: 5 });
    await expect(gridNames(page)).toHaveText([...STORED_ORDER, "Delta Ridge", "Echo Point"]);

    await askPush(page, "done", {
      results: [delta, echo],
      reason: "2 picks ranked for you, from 65 pulled.",
      kept: 2,
    });
    await askEnd(page);
    const onScreen = await gridNames(page).allTextContents();
    expect(onScreen).toEqual([...STORED_ORDER, "Delta Ridge", "Echo Point"]);

    // The record the server now holds: turn one's block then turn two's, each in the order it was
    // sent — which is what `(Sequence, Rank)` means. A reload must land on the identical array.
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, {
      ...RESUME_PAYLOAD,
      cards: [ALPHA, BORDER, CEDAR, delta, echo],
      turns: [
        ...RESUME_PAYLOAD.turns,
        { sequence: 2, role: "assistant", text: "Two more.", kind: "page", status: "Completed" },
      ],
      reason: "2 picks ranked for you, from 65 pulled.",
    });
    await page.reload();

    await expect(gridNames(page)).toHaveText(onScreen);
  });
});

test.describe("Ask Reel — a resumed thread says what happened, once", () => {
  /** An ask, its reply, and the page that replayed it — the shape the smoke reloaded into. */
  const PAGED_TURNS = [
    ...RESUME_PAYLOAD.turns,
    // The server stores a page as an assistant row with NO user row: the user said nothing, the
    // model was replayed. Its text is identical because it is the same answer to the same question.
    { sequence: 2, role: "assistant", text: "Three that fit.", kind: "page", status: "Completed" },
  ];

  test("a paged thread replays one assistant bubble, not two", async ({ page }) => {
    // SB-04. The DB rows at sequence 1 (`ask`) and 2 (`page`) carry identical `Text`, and the
    // resumed page rendered three reply bubbles for two real answers.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, turns: PAGED_TURNS },
    });

    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-reply")).toHaveCount(1);
    await expect(page.getByTestId("askreel-reply")).toContainText("Three that fit.");
  });

  test("a restored answer still says how it was assembled", async ({ page }) => {
    // SB-06. The payload carries the sentence, the reducer stores it, and the page gated it on
    // `status === "done"` — which a resume deliberately is not. A full-text scan of the resumed
    // page for "picks ranked" came back false, so the only account of a relaxed quality floor went
    // with it.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...RESUME_PAYLOAD,
        reason: "3 picks ranked for you, from 40 pulled · only 1 pick cleared your 6.0 bar, so it was relaxed",
      },
    });

    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("3 picks ranked for you, from 40 pulled · only 1 pick cleared your 6.0 bar, so it was relaxed");
  });

  /**
   * The thread after a Stop: a question, and an assistant row that never got written.
   *
   * The payload that carries these has to move `lastQuery` with them — the server reads it off the
   * last USER row, which on this thread is precisely the question that never got an answer, and
   * that is what "Ask again" replays. A fixture that left it on the older question passed every
   * render assertion here and silently retried the wrong one.
   */
  const STOPPED_TURNS = [
    ...RESUME_PAYLOAD.turns,
    { sequence: 2, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
  ];

  const STOPPED_PAYLOAD = {
    ...RESUME_PAYLOAD,
    turns: STOPPED_TURNS,
    lastQuery: "something surreal and dreamlike",
  };

  test("a stopped last turn is on the page, and the grid says whose answer it is", async ({ page }) => {
    // SB-03. The DB was right (`Failed` / `client_aborted`, zero cards) and the grid was right
    // (the last COMPLETED answer). What the screen said was: "You said: something surreal and
    // dreamlike", then 24 cards answering something else, and a full-text scan for
    // `stopped|failed|didn't finish|Ask again` came back false.
    await boot(page, { resume: "stored", storedId: CONVERSATION, payload: STOPPED_PAYLOAD });

    // The unanswered question is still in the transcript — it is still what was asked.
    await expect(page.getByTestId("askreel-transcript")).toContainText("something surreal and dreamlike");
    const failed = page.getByTestId("askreel-failed-turn");
    await expect(failed).toBeVisible();
    // F6-09: NOT "you stopped it". `client_aborted` is written for a Stop and for a reload, a
    // slept phone and a discarded tab alike — the abort reaches the server identically — so the
    // sentence says the one thing that is true of all four.
    await expect(failed).toContainText("didn’t finish before you left");
    await expect(failed.getByTestId("askreel-retry")).toBeVisible();

    // The answer is kept, and the seam now names the question it belongs to.
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-seam-answers")).toContainText("a tense heist");
    await expect(page.getByTestId("askreel-pending-turn")).toHaveCount(0);

    // "Ask again" replays the question that never got answered, not the one on screen.
    await failed.getByTestId("askreel-retry").click();
    await waitForAsk(page);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests[0].query).toBe("something surreal and dreamlike");
    // …and the notice goes with the gesture: the offer it carried has just been taken.
    await expect(page.getByTestId("askreel-failed-turn")).toHaveCount(0);
  });

  test("a turn that crashed is not described as something the user did", async ({ page }) => {
    // Fixture variation, one field: the failure code. `client_aborted` means the client went away;
    // anything else happened TO them, and neither sentence may claim a gesture the server cannot
    // see.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...STOPPED_PAYLOAD,
        turns: [...STOPPED_TURNS.slice(0, 3), { ...STOPPED_TURNS[3], failureCode: "HttpRequestException" }],
      },
    });

    await expect(page.getByTestId("askreel-failed-turn")).toContainText("hit a snag");
    await expect(page.getByTestId("askreel-failed-turn")).not.toContainText("you stopped it");
    await expect(page.getByTestId("askreel-failed-turn")).not.toContainText("before you left");
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
  });

  test("a turn the server is still working on is watched until it lands", async ({ page }) => {
    // The other half of SB-03: `Pending` is not a failure, it is "not yet". Saying "still working
    // on that one" is only honest if the page then finds out — which is what `GET /turns/{id}` is
    // for, and this is its first caller.
    const pendingTurns = [
      ...RESUME_PAYLOAD.turns,
      { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Pending" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Pending", id: "turn-77" },
    ];
    const landed = {
      ...RESUME_PAYLOAD,
      turns: [
        ...RESUME_PAYLOAD.turns,
        { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Completed" },
        { sequence: 3, role: "assistant", text: "Two older ones.", kind: "ask", status: "Completed" },
      ],
      lastQuery: "older, and slower",
    };

    let settled = false;
    let turnCalls = 0;
    let conversationCalls = 0;
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    // Registered after `boot`, so it wins: the conversation answers Pending until the turn does.
    await page.route(
      (u) => u.pathname === `/api/search/conversations/${CONVERSATION}`,
      (route) => {
        conversationCalls += 1;
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(settled ? landed : { ...RESUME_PAYLOAD, turns: pendingTurns }),
        });
      },
    );
    // 202 while Pending, exactly as the contract specifies, then the finished turn.
    await page.route(
      (u) => u.pathname === "/api/search/turns/turn-77",
      (route) => {
        turnCalls += 1;
        // Two 202s before it lands, so there is a window in which the turn is demonstrably being
        // polled and has demonstrably not settled — which is the only window the assertion below
        // is about. With a single 202 the settle and the second poll are the same instant, and the
        // re-read the settle correctly triggers is indistinguishable from the fallback interval.
        const done = turnCalls > 2;
        if (done) settled = true;
        return route.fulfill({
          status: done ? 200 : 202,
          contentType: "application/json",
          body: JSON.stringify({
            status: done ? "Completed" : "Pending",
            reason: null, failureCode: null, cards: [],
          }),
        });
      },
    );
    await page.reload();

    await expect(page.getByTestId("askreel-pending-turn")).toContainText("Still working on that one");
    // Not a failure: no apology, no retry offer over a turn that may be seconds from landing.
    await expect(page.getByTestId("askreel-failed-turn")).toHaveCount(0);
    // The last completed answer stays up underneath it.
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    // The cheap route is the one being polled. `GET /turns/{id}` is one row; re-reading the
    // conversation re-runs the whole replay — the eligibility filter, the on-demand re-score and
    // the card projection — every five seconds for a minute, to learn a single status field. The
    // id is on the row precisely so the client does not have to do that, so the fallback interval
    // must be OFF whenever there is one: the thread is read for the resume and then left alone.
    const whilePending = conversationCalls;
    await expect.poll(() => turnCalls, { timeout: 20_000 }).toBeGreaterThanOrEqual(2);
    expect(conversationCalls).toBe(whilePending);

    // …and when it lands, the thread is re-read WHOLE rather than patched row by row.
    await expect(page.getByTestId("askreel-pending-turn")).toHaveCount(0, { timeout: 20_000 });
    await expect(page.getByTestId("askreel-transcript")).toContainText("Two older ones.");
    await expect(page.getByTestId("askreel-seam-answers")).toHaveCount(0);
    expect(conversationCalls).toBeGreaterThan(whilePending);
  });

  test("a pending turn with no id on it falls back to re-reading the thread", async ({ page }) => {
    // The probe, one field: the same payload with `id` gone from the pending assistant row — which
    // is every server before FX7 projects it. There is then nothing to poll but the conversation,
    // and the page must still find out rather than sitting on "Still working on that one" forever.
    // Asserted as a pair with the test above: one field decides which route carries the watch, and
    // getting it backwards is either a dead page or a replay running every five seconds.
    const pendingTurns = [
      ...RESUME_PAYLOAD.turns,
      { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Pending" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Pending" },
    ];
    const landed = {
      ...RESUME_PAYLOAD,
      turns: [
        ...RESUME_PAYLOAD.turns,
        { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Completed" },
        { sequence: 3, role: "assistant", text: "Two older ones.", kind: "ask", status: "Completed" },
      ],
      lastQuery: "older, and slower",
    };

    let conversationCalls = 0;
    let turnCalls = 0;
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await page.route(
      (u) => u.pathname === `/api/search/conversations/${CONVERSATION}`,
      (route) => {
        conversationCalls += 1;
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          // Settles on the third read: the resume, one poll that is still Pending, then the answer.
          body: JSON.stringify(conversationCalls > 2 ? landed : { ...RESUME_PAYLOAD, turns: pendingTurns }),
        });
      },
    );
    // Nothing may reach the turn route — there is no id to build one from, and a request to
    // `/api/search/turns/` or `/turns/undefined` is the shape that bug takes.
    await page.route((u) => u.pathname.startsWith("/api/search/turns/"), (route) => {
      turnCalls += 1;
      return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
    });
    await page.reload();

    await expect(page.getByTestId("askreel-pending-turn")).toContainText("Still working on that one");
    await expect(page.getByTestId("askreel-pending-turn")).toHaveCount(0, { timeout: 20_000 });
    await expect(page.getByTestId("askreel-transcript")).toContainText("Two older ones.");
    expect(turnCalls).toBe(0);
    expect(conversationCalls).toBeGreaterThan(2);
  });

  test("a paged answer keeps its running count when the question after it was stopped", async ({ page }) => {
    // R6-01. Fixture variation, one row against the test above: the same paged thread with a
    // STOPPED question on the end of it. The server replays from the last COMPLETED ask, so the
    // grid is byte-identical to the one above — and `wasPaged` anchored on the last ask of ANY
    // status, walked past the answer on screen, found no page after it and withdrew the prefix.
    // The same grid then read "1 pick ranked for you, from 22 pulled" over three cards: R1-03,
    // reached by stopping a turn.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...RESUME_PAYLOAD,
        turns: [
          ...PAGED_TURNS,
          { sequence: 3, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
          { sequence: 4, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
        ],
        lastQuery: "something surreal and dreamlike",
        reason: "1 pick ranked for you, from 22 pulled.",
      },
    });

    // The stopped turn is on the page as the failure it is, and the paged answer is kept under it.
    await expect(page.getByTestId("askreel-failed-turn")).toBeVisible();
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("3 picks so far · last page: 1 pick ranked for you, from 22 pulled.");

    // …and the number in that line is the grid, checked against the grid rather than against the
    // fixture — the defect was a sentence that described a different number of cards than the one
    // beside it, and only this comparison can fail on that.
    const footnoteText = await page.getByTestId("askreel-footnote").innerText();
    expect(Number(/^(\d+) picks? so far/.exec(footnoteText)?.[1])).toBe(await gridNames(page).count());
  });

  test("a restored PAGED answer accounts for the grid, not for its last page", async ({ page }) => {
    // Fixture variation, one row: the same payload plus the `page` turn the server writes. The
    // server's sentence describes that page and the grid is two turns' work — R1-03 reaching the
    // screen by the resume path rather than the live one.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, turns: PAGED_TURNS, reason: "1 pick ranked for you, from 22 pulled." },
    });

    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("3 picks so far · last page: 1 pick ranked for you, from 22 pulled.");
  });
});

test.describe("Ask Reel — what goes on the wire", () => {
  test("every request carries a client turn id, and none carries a transcript", async ({ page }) => {
    await boot(page);
    await askAndAnswer(page);
    // A page as well as a turn, because they take different paths through the hook and only one
    // of them used to set `continuation`.
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await askPush(page, "done", { results: [], reason: null });
    await askEnd(page);

    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests).toHaveLength(2);
    const ids = new Set<string>();
    for (const request of requests) {
      expect(typeof request.clientTurnId).toBe("string");
      expect((request.clientTurnId as string).length).toBeGreaterThan(8);
      ids.add(request.clientTurnId as string);
      // Removed, not renamed. A client still sending these is sending a transcript the server will
      // not read — and the server now refuses the request outright.
      expect(Object.keys(request)).not.toContain("history");
      expect(Object.keys(request)).not.toContain("shownTmdbIds");
      expect(Object.keys(request)).not.toContain("continuation");
    }
    // Two exchanges, two ids: an id shared between them would make the page a replay of the turn.
    expect(ids.size).toBe(2);
    // The page rides the conversation the `conversation` frame named, so the server can derive the
    // history and the no-repeat set instead of being told them.
    expect(requests[1].conversationId).toBe(CONVERSATION);
  });

  test("a 409 keeps the grid and offers a check-again rather than an error", async ({ page }) => {
    // The same question is already being answered — another tab, or a turn this one abandoned
    // before the server noticed. Nothing on screen is wrong, so nothing on screen may be cleared;
    // the old client mapped every non-2xx to "search hit a snag" and wiped the reel.
    await boot(page);
    await askAndAnswer(page);

    await askFailNext(page, 409, "turn_in_progress");
    await page.getByTestId("askreel-composer").fill("something lighter");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);

    const blocked = page.getByTestId("askreel-in-progress");
    await expect(blocked).toBeVisible();
    await expect(blocked).toContainText("already being answered");
    // The answer the reader was looking at is still there, and it is NOT the error card.
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("semantic-error")).toHaveCount(0);
    await expect(page.getByTestId("askreel-page-error")).toHaveCount(0);
    await expect(page.getByTestId("askreel-composer")).toBeEditable();

    // "Check again" re-sends the SAME turn id — the whole point of an idempotency key. Asking
    // afresh would run and bill the question a second time.
    const before = (await askRequests(page)) as Record<string, unknown>[];
    await blocked.getByTestId("askreel-retry").click();
    await waitForAsk(page, 3);
    const after = (await askRequests(page)) as Record<string, unknown>[];
    expect(after[2].clientTurnId).toBe(before[1].clientTurnId);
  });

  test("a 404 says the thread ended, and the next question opens a new one", async ({ page }) => {
    // A stale remembered id — the thread was deleted from another device, or aged out. Never a
    // fork, and never a crash message: those are three different things for a person to do.
    await boot(page);
    await askAndAnswer(page);

    await askFailNext(page, 404, "conversation_not_found");
    await page.getByTestId("askreel-composer").fill("something lighter");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);

    await expect(page.getByTestId("askreel-conversation-gone")).toBeVisible();
    await expect(page.getByTestId("semantic-error")).toHaveCount(0);
    // ONE message and one offer. The inline page-error retry renders for a failure that arrived
    // over a live grid, and both firing at once put two "Ask again" buttons under two different
    // explanations of the same 404.
    await expect(page.getByTestId("askreel-page-error")).toHaveCount(0);
    await expect(page.getByTestId("askreel-retry")).toHaveCount(1);
    // Forgotten, so the next visit does not ask for it again.
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBeNull();

    // …and the next question carries no conversation id at all, which is what mints a new thread.
    await page.getByTestId("askreel-conversation-gone").getByTestId("askreel-retry").click();
    await waitForAsk(page, 3);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests[2].conversationId).toBeNull();
  });
});

test.describe("Ask Reel — threads are a thing you can have more than one of", () => {
  test("+ New clears the thread and brings the cold start back", async ({ page }) => {
    await boot(page);
    await askAndAnswer(page);
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBe(CONVERSATION);

    await page.getByTestId("askreel-new").click();

    await expect(gridNames(page)).toHaveCount(0);
    await expect(page.getByTestId("askreel-transcript")).toBeEmpty();
    // The presets are the cold start, and they are the thing that proves this is one.
    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);
    // SB-02: the id is REPLACED by the fresh marker, not removed. A removed key is what a browser
    // that has never asked anything looks like, and that is answered with `/conversations/latest`
    // — the account's newest thread, which after a `+ New` is the one just left.
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBe("new");
  });

  test("+ New survives a reload, and the reload asks for no conversation at all", async ({ page }) => {
    // SB-02, the gesture the smoke actually performed: `+ New`, then refresh. The page came back
    // inside the thread the user had just finished with, via a route whose own doc-comment says it
    // is "only ever the answer for a browser that has never asked anything".
    await boot(page);
    await askAndAnswer(page);
    await page.getByTestId("askreel-new").click();
    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);

    // Both resume routes are live and would answer with the thread that was just left — the id
    // route because `boot` armed it below, and `/latest` because the account has a newest thread.
    // A correct client asks for neither.
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, RESUME_PAYLOAD);
    await mockJson(page, "**/api/search/conversations/latest", RESUME_PAYLOAD);
    const resumeCalls: string[] = [];
    page.on("request", (r) => {
      if (/\/api\/search\/conversations\/[^?]/.test(r.url())) resumeCalls.push(r.url());
    });

    await page.reload();

    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);
    await expect(gridNames(page)).toHaveCount(0);
    await expect(page.getByTestId("askreel-transcript")).toBeEmpty();
    expect(resumeCalls).toEqual([]);
    // The marker is still there after the reload — a refresh is not a question, so nothing has
    // replaced it yet.
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBe("new");

    // …and the first `conversation` frame of the next question does replace it, so the thread this
    // visit starts is remembered on the ordinary terms.
    await askAndAnswer(page);
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBe(CONVERSATION);
  });

  test("Recent lists the account's threads and deleting one removes it", async ({ page }) => {
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    await page.getByTestId("askreel-recent").click();
    const rows = page.getByTestId("askreel-recent-row");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText("a tense heist");
    await expect(rows.nth(0)).toContainText("2 turns");
    await expect(rows.nth(1)).toContainText("something funny");
    // A thread with no stored cards says so BEFORE it is opened, not after — and says only what
    // the list can see (F4-04b): no saved picks, never why there are none.
    await expect(rows.nth(1)).toContainText("no saved picks");

    // Delete the OTHER thread: the list refetches without it, and the open thread is untouched.
    await mockJson(page, "**/api/search/conversations", [CONVERSATION_LIST[0]]);
    let deleted: string | null = null;
    await page.route(`**/api/search/conversations/${OTHER_CONVERSATION}`, (route) => {
      if (route.request().method() === "DELETE") deleted = OTHER_CONVERSATION;
      return route.fulfill({ status: 204, body: "" });
    });
    // Two gestures now, not one, and the request is deferred behind the undo window — F6-07: this
    // was a 26 px tap, eight pixels from the control that OPENS the thread, taking the transcript
    // and its cards with it. Dismissing the toast is the same commit the clock makes.
    await rows.nth(1).getByTestId("askreel-recent-delete").click();
    await rows.nth(1).getByTestId("askreel-recent-delete-yes").click();
    expect(deleted, "asking is not deleting").toBeNull();
    await page.getByTestId("askreel-delete-toast-dismiss").click();

    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(1);
    await expect.poll(() => deleted).toBe(OTHER_CONVERSATION);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
  });

  test("Recent asks for a cheap page and offers the rest of the account's threads", async ({ page }) => {
    // SB-05. The account in the smoke held 51 conversations; the menu rendered 20 and gave no
    // affordance for the other 31, so a control the code calls "a means of getting back to an
    // answer" read as a complete list. This route honours `limit` the way the server does, so the
    // client's own request is what decides how many rows come back.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    const limits = await routeThreadList(page, 51);

    await page.getByTestId("askreel-recent").click();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(20);
    await page.getByTestId("askreel-recent-older").click();

    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(51);
    // One gesture, and no second one on offer: the route takes a limit and no offset, so 100 is
    // everything this control can reach and a repeatable button would return the same rows.
    await expect(page.getByTestId("askreel-recent-older")).toHaveCount(0);
    expect(limits).toEqual([20, 100]);
    // The thread on screen is still marked in the longer list, and still the current one.
    await expect(page.getByTestId("askreel-recent-row").first()).toContainText("a tense heist");
  });

  test("an account with a handful of threads is never offered older ones", async ({ page }) => {
    // Fixture variation, one number: 12 threads instead of 51. A short page IS the whole list, so
    // the offer would promise rows that do not exist — and clicking it would spend a request to
    // return the same twelve.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    const limits = await routeThreadList(page, 12);

    await page.getByTestId("askreel-recent").click();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(12);
    await expect(page.getByTestId("askreel-recent-older")).toHaveCount(0);
    expect(limits).toEqual([20]);
  });
});

test.describe("Ask Reel — Recent never shows a failed read as an empty history", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  /**
   * The list read fails `failures` times with `status`, then answers with `total` threads. Each
   * request is counted, so a test can say how many reads the menu made.
   */
  async function routeFailingThreadList(page: Page, status: number, failures: number, total: number) {
    const calls: number[] = [];
    const threads = Array.from({ length: total }, (_, i) => ({
      id: i === 0 ? CONVERSATION : `7f3a1b2c-0000-4000-8000-${String(i).padStart(12, "0")}`,
      title: i === 0 ? "a tense heist" : `thread ${i}`,
      turnCount: 2,
      lastTurnAt: new Date(Date.now() - i * 60_000).toISOString(),
      lastCardsAt: null,
    }));
    await page.route(
      (u) => u.pathname === "/api/search/conversations",
      (route) => {
        calls.push(Number(new URL(route.request().url()).searchParams.get("limit") ?? 20));
        if (calls.length <= failures) {
          return route.fulfill({ status, contentType: "application/json", body: JSON.stringify({ error: "unavailable" }) });
        }
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(threads.slice(0, total)) });
      },
    );
    return calls;
  }

  // REV7-PH-02. GET /api/search/conversations answered 500 or 503 and the menu said exactly
  // "Nothing here yet — ask something." — a failed read presented as an account with no threads,
  // and no way to ask again. A failed read is a third state: it says it failed and offers Retry.
  // Varied over the status and over what the retry finds (threads, and a real empty list).
  for (const [status, total] of [[500, 3], [503, 3], [500, 0]] as const) {
    test(`a ${status} says the list failed, offers Retry, and the retry shows ${total ? "the threads" : "the empty state"}`, async ({ page }) => {
      await boot(page, { resume: "stored", storedId: CONVERSATION });
      const calls = await routeFailingThreadList(page, status, 1, total);

      await page.getByTestId("askreel-recent").tap();
      const failed = page.getByTestId("askreel-recent-error");
      await expect(failed).toBeVisible();
      await expect(failed).toHaveAttribute("role", "alert");
      await expect(failed).toContainText("Couldn't load your conversations");
      await expect(page.getByTestId("askreel-recent-empty")).toHaveCount(0);
      await expect(page.getByTestId("askreel-recent-menu")).not.toContainText("Nothing here yet");
      expect(calls).toHaveLength(1);

      await page.getByTestId("askreel-recent-error-retry").tap();

      await expect(failed).toHaveCount(0);
      if (total > 0) {
        await expect(page.getByTestId("askreel-recent-row")).toHaveCount(total);
        await expect(page.getByTestId("askreel-recent-empty")).toHaveCount(0);
      } else {
        await expect(page.getByTestId("askreel-recent-empty")).toHaveText("Nothing here yet — ask something.");
      }
      expect(calls).toHaveLength(2);
    });
  }

  test("a list that keeps failing keeps saying so after the retry", async ({ page }) => {
    // Fixture variation, one number: every read fails. The retry must not fall through to the empty
    // copy on its second failure either.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    const calls = await routeFailingThreadList(page, 500, 99, 3);

    await page.getByTestId("askreel-recent").tap();
    await expect(page.getByTestId("askreel-recent-error")).toBeVisible();
    await page.getByTestId("askreel-recent-error-retry").tap();
    await expect.poll(() => calls.length).toBe(2);
    await expect(page.getByTestId("askreel-recent-error")).toBeVisible();
    await expect(page.getByTestId("askreel-recent-empty")).toHaveCount(0);
  });

  test("Show older that fails keeps the threads already listed and says the older ones failed", async ({ page }) => {
    // The first page arrives (20 of 51); the longer read fails. The rows on screen are still true,
    // so they stay, and the failure is named as what it is.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    const calls: number[] = [];
    const threads = Array.from({ length: 51 }, (_, i) => ({
      id: i === 0 ? CONVERSATION : `7f3a1b2c-0000-4000-8000-${String(i).padStart(12, "0")}`,
      title: i === 0 ? "a tense heist" : `thread ${i}`,
      turnCount: 2,
      lastTurnAt: new Date(Date.now() - i * 60_000).toISOString(),
      lastCardsAt: null,
    }));
    await page.route(
      (u) => u.pathname === "/api/search/conversations",
      (route) => {
        const limit = Number(new URL(route.request().url()).searchParams.get("limit") ?? 20);
        calls.push(limit);
        return limit > 20
          ? route.fulfill({ status: 503, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(threads.slice(0, limit)) });
      },
    );

    await page.getByTestId("askreel-recent").tap();
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(20);
    await page.getByTestId("askreel-recent-older").tap();

    const failed = page.getByTestId("askreel-recent-error");
    await expect(failed).toContainText("Couldn't load your older threads");
    await expect(page.getByTestId("askreel-recent-row")).toHaveCount(20);
    await expect(page.getByTestId("askreel-recent-empty")).toHaveCount(0);
    expect(calls).toEqual([20, 100]);
  });
});

test.describe("Ask Reel — a failed resume read is never shown as a fresh start", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  const STORED_KEY = "reel.ask.conversationId";
  const storedId = (page: Page) => page.evaluate((key) => localStorage.getItem(key), STORED_KEY);

  /**
   * Answers the resume route at `path` with `failure` for the first `failures` reads and the stored
   * payload afterwards. `"abort"` is a dropped connection — the flaky phone network the defect was
   * found on — and a number is that HTTP status. Every read is counted.
   */
  async function routeFlakyResume(page: Page, path: string, failure: number | "abort", failures: number) {
    const calls: string[] = [];
    await page.route(
      (u) => u.pathname === path,
      (route) => {
        calls.push(path);
        if (calls.length > failures) {
          return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(RESUME_PAYLOAD) });
        }
        return failure === "abort"
          ? route.abort("connectionreset")
          : route.fulfill({ status: failure, contentType: "application/json", body: JSON.stringify({ error: "unavailable" }) });
      },
    );
    return calls;
  }

  /** Every resume read this page makes, id route and the deprecated alias alike. */
  function resumeReads(page: Page): string[] {
    const reads: string[] = [];
    page.on("request", (r) => {
      const path = new URL(r.url()).pathname;
      if (/^\/api\/search\/conversations\/[^/]+$/.test(path)) reads.push(path.slice("/api/search/conversations/".length));
    });
    return reads;
  }

  // HREV8-PH-01. A single 503 on GET /conversations/{id} rendered the cold start — "What are you in
  // the mood for?" and the starters, no alert, no Retry — and wiped the stored id, so the next reload
  // opened the account's newest thread instead. Varied over a 5xx and a dropped connection.
  for (const failure of [503, "abort"] as const) {
    test(`a ${failure === "abort" ? "dropped connection" : failure} says the conversation failed to load, and Retry restores the same thread`, async ({ page }) => {
      await boot(page, { resume: "stored", storedId: CONVERSATION });
      await expect(gridNames(page)).toHaveText(STORED_ORDER);
      const calls = await routeFlakyResume(page, `/api/search/conversations/${CONVERSATION}`, failure, 1);
      const reads = resumeReads(page);

      await page.reload();

      const failed = page.getByTestId("askreel-resume-error");
      await expect(failed).toBeVisible();
      await expect(failed).toHaveAttribute("role", "alert");
      await expect(failed).toContainText("Couldn't load your conversation");
      await expect(failed).toContainText("nothing on your account changed");
      await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
      await expect(gridNames(page)).toHaveCount(0);
      expect(await storedId(page)).toBe(CONVERSATION);

      await page.getByTestId("askreel-resume-error-retry").tap();

      await expect(gridNames(page)).toHaveText(STORED_ORDER);
      await expect(failed).toHaveCount(0);
      await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
      expect(await storedId(page)).toBe(CONVERSATION);
      expect(calls).toHaveLength(2);
      // Both reads were for THIS thread — never the account's newest.
      expect(reads).toEqual([CONVERSATION, CONVERSATION]);
    });
  }

  test("the stored id survives a failed read, so the next reload opens the same thread", async ({ page }) => {
    // No init script re-seeding the id on every load: the id is written once, so a client that
    // forgets it on a 503 shows up as the second reload reading `/latest`.
    await boot(page);
    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);
    await page.evaluate(([key, id]) => localStorage.setItem(key, id), [STORED_KEY, CONVERSATION] as const);
    await routeFlakyResume(page, `/api/search/conversations/${CONVERSATION}`, 503, 1);
    const reads = resumeReads(page);

    await page.reload();
    await expect(page.getByTestId("askreel-resume-error")).toBeVisible();
    expect(await storedId(page)).toBe(CONVERSATION);

    await page.reload();
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-resume-error")).toHaveCount(0);
    expect(reads).toEqual([CONVERSATION, CONVERSATION]);
    expect(await storedId(page)).toBe(CONVERSATION);
  });

  test("a 404 still forgets the thread and lands on the cold start, with no alert", async ({ page }) => {
    // The other side of the rule: the server saying the thread is gone IS an answer.
    await boot(page);
    await page.evaluate(([key, id]) => localStorage.setItem(key, id), [STORED_KEY, CONVERSATION] as const);
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, { code: "conversation_not_found" }, 404);

    await page.reload();

    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);
    await expect(page.getByTestId("askreel-resume-error")).toHaveCount(0);
    await expect.poll(() => storedId(page)).toBeNull();
  });

  test("with no stored id, a failed /latest says so rather than offering the starters", async ({ page }) => {
    // Fixture variation, one field: nothing remembered, so the read is the deprecated alias.
    await boot(page);
    await expect(page.getByTestId("askreel-preset")).toHaveCount(4);
    await routeFlakyResume(page, "/api/search/conversations/latest", 500, 1);

    await page.reload();

    await expect(page.getByTestId("askreel-resume-error")).toBeVisible();
    await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
    await page.getByTestId("askreel-resume-error-retry").tap();
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
  });
});

test.describe("Ask Reel — a stopped page leaves nothing under the bar in the grid", () => {
  test("a page keeps paging alive and says what is on screen, not what the page added", async ({ page }) => {
    // R1-03, faces one and two in one run. The old rule counted ids a page added to `order` that
    // were not already in it — and a page's `done.results` is a subset of what already streamed as
    // `candidate` frames, so on the reported turn that count was zero, the page's own sentence was
    // thrown away and "Show me more" vanished after a page that had just added thirteen cards.
    await boot(page);
    await askAndAnswer(page);
    await expect(page.getByTestId("askreel-footnote")).toHaveText("3 picks ranked for you, from 40 pulled.");

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    const delta = card("d4", "Delta Ridge", { blend: 0.7 });
    const echo = card("e5", "Echo Point", { blend: 0.6 });
    // Face two, and the one that broke: BOTH page cards arrive as candidates first, so every id in
    // the `done` below is already in `order` and the id diff is zero.
    await askPush(page, "candidate", delta);
    await askPush(page, "candidate", echo);
    await askPush(page, "done", {
      results: [delta, echo],
      reason: "2 picks ranked for you, from 65 pulled.",
      kept: 2,
    });
    await askEnd(page);

    await expect(gridNames(page)).toHaveCount(5);
    // The footnote accounts for the GRID and names where the last of it came from. The page's own
    // sentence alone would claim 2 of the 5; the first turn's would claim 3.
    await expect(page.getByTestId("askreel-footnote"))
      .toHaveText("5 picks so far · last page: 2 picks ranked for you, from 65 pulled.");
    // …and paging is still offered, because the server never said there was nothing left.
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await expect(page.getByText("That's everything on this thread")).toHaveCount(0);
  });

  test("a page the server returns nothing for ends paging and leaves the accounting alone", async ({ page }) => {
    // Face three: the honest exhaustion, which is the only thing that may latch. `kept: 0` with no
    // results is the server saying there is nothing left under this question — its `reason` is
    // true of the page and false of the three cards above it, so the existing line stands.
    await boot(page);
    await askAndAnswer(page);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await askPush(page, "done", { results: [], reason: "no fresh matches — try another angle", kept: 0 });
    await askEnd(page);

    await expect(gridNames(page)).toHaveCount(3);
    await expect(page.getByTestId("askreel-more")).toHaveCount(0);
    await expect(page.getByText("That's everything on this thread")).toBeVisible();
    // The empty page's sentence never reaches the screen: it would describe a grid it is not about.
    await expect(page.getByTestId("askreel-footnote")).toHaveText("3 picks ranked for you, from 40 pulled.");
    // …and it is not the empty-answer card either. There are three perfectly good cards on screen.
    await expect(page.getByTestId("semantic-empty")).toHaveCount(0);
  });

  test("sub-floor cards from a stopped page go to the tray, and the state says unranked", async ({ page }) => {
    // R2's F3-02 MOVED. The account's bar is 6.0. Ranking and the floor run at `done`, which a
    // stopped page never reaches — so without this the 5.2 and the 4.9 sit in the recommended grid
    // while the tray directly below says picks were held back for being under 6.0.
    await boot(page);
    await askAndAnswer(page);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    const under = card("d4", "Under Bar", { predictedRating: 5.2, blend: 0.3 });
    const wayUnder = card("e5", "Way Under", { predictedRating: 4.9, blend: 0.3 });
    const over = card("f6", "Over Bar", { predictedRating: 6.4, blend: 0.6 });
    for (const c of [under, wayUnder, over]) await askPush(page, "candidate", c);

    // The demotion happens as the cards arrive, not at the Stop (F4-08). The page's two sub-floor
    // picks never enter the grid at all — before this they sat in it for as long as the turn ran,
    // which for the measured turns was between 11.5 and 18.6 seconds. The three cards of the
    // answer this page is EXTENDING are untouched: the server already floored and committed them,
    // and the client's bar is bounded to the ids it has not ranked.
    await expect(gridNames(page)).toHaveText([...STORED_ORDER, "Over Bar"]);
    await expect(page.getByTestId("askreel-below-floor").locator("summary")).toHaveText("Show the 2 held back by your 6.0 bar");

    await page.getByTestId("askreel-stop").click();

    // …and the Stop changes none of it: three above the bar from the first turn, plus the one that
    // cleared it here.
    await expect(gridNames(page)).toHaveText([...STORED_ORDER, "Over Bar"]);
    await expect(page.getByTestId("semantic-results").getByText("Under Bar")).toHaveCount(0);
    await expect(page.getByTestId("semantic-results").getByText("Way Under")).toHaveCount(0);

    // …and they are accounted for rather than vanished. The label counts the tray's own contents,
    // so it is a number the reader can open it and verify.
    const tray = page.getByTestId("askreel-below-floor");
    await expect(tray.locator("summary")).toHaveText("Show the 2 held back by your 6.0 bar");
    await tray.locator("summary").click();
    // `p.truncate` and not a text match: each card's why-line repeats its name, so a text lookup
    // matches twice and passes (or fails) for reasons that have nothing to do with the tray.
    await expect(tray.locator("a > p.truncate")).toHaveText(["Under Bar", "Way Under"]);

    // The stopped state withdraws the claim that this is a finished, ranked answer.
    await expect(page.getByTestId("askreel-stopped")).toContainText("Unranked — stopped before Reel finished.");
  });

  test("a card that rounds UP to the bar stays in the grid", async ({ page }) => {
    // Fixture variation, one field: 5.96 instead of 5.2. The badge rounds to one decimal and
    // prints "6.0", so holding it back would put a card badged 6.0 inside a tray labelled "under
    // your 6.0 bar" — the same contradiction wearing the other hat.
    await boot(page);
    await askAndAnswer(page);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await askPush(page, "candidate", card("g7", "Rounds Up", { predictedRating: 5.96, blend: 0.3 }));
    // In the grid while the page streams, and still there after the Stop — the rounding rule has to
    // hold at both moments now that the bar is applied as cards arrive.
    await expect(gridNames(page)).toHaveCount(4);
    await expect(page.getByTestId("askreel-below-floor")).toHaveCount(0);

    await page.getByTestId("askreel-stop").click();

    await expect(gridNames(page)).toHaveText([...STORED_ORDER, "Rounds Up"]);
    await expect(page.getByTestId("askreel-below-floor")).toHaveCount(0);
  });
});

/**
 * The tray survives a reload — or the quality floor is a live-turn decoration.
 *
 * The server has stored below-floor cards as rows of their own since the turn record landed, and
 * hands them back on the resume payload precisely so a thread comes back whole. The client read
 * `cards` and dropped `belowFloor` on the floor, so the fence the user was left with —
 * "Show the 3 under your 6.0 bar", quoting a number they set themselves — was there before a
 * reload and gone after it, with the picks it accounted for gone with it.
 *
 * Three fixtures one field apart, because absent / empty / populated are three different
 * statements from the server and only the middle one is a zero.
 */
test.describe("Ask Reel — the tray comes back with the thread", () => {
  const UNDER = card("d4", "Under Bar", { predictedRating: 5.2, blend: 0.1 });
  const WAY_UNDER = card("e5", "Way Under", { predictedRating: 4.9, blend: 0.1 });
  const BARELY = card("f6", "Barely Under", { predictedRating: 5.8, blend: 0.1 });

  test("a reloaded thread restores the fence, its three cards, and keeps them out of the grid", async ({ page }) => {
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, belowFloor: [UNDER, WAY_UNDER, BARELY] },
    });

    // The grid is exactly the answer, in stored rank order. The tray's three are NOT in it: they
    // did not clear the bar, and merging them would make the floor decorative on every resume.
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    for (const name of ["Under Bar", "Way Under", "Barely Under"]) {
      await expect(page.getByTestId("semantic-results").getByText(name)).toHaveCount(0);
    }

    // …and the fence reads exactly as it did before the reload, counting its own contents against
    // the bar out of the account's own conditions.
    const tray = page.getByTestId("askreel-below-floor");
    await expect(tray.locator("summary")).toHaveText("Show the 3 held back by your 6.0 bar");
    await tray.locator("summary").click();
    // `p.truncate`, not a text match: each card's why-line repeats its name.
    await expect(tray.locator("a > p.truncate")).toHaveText(["Under Bar", "Way Under", "Barely Under"]);
  });

  test("an empty tray and no tray at all both render no fence, and neither invents one", async ({ page }) => {
    // The probe's other two rungs. `belowFloor: []` is a server saying the floor cut nothing;
    // omitting the field is an older server saying nothing at all. Both render no fence — the
    // distinction they must NOT collapse into is a fence labelled with a count from somewhere else.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, belowFloor: [] },
    });
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-below-floor")).toHaveCount(0);
  });

  test("a payload with no belowFloor field at all still resumes clean", async ({ page }) => {
    // RESUME_PAYLOAD verbatim — today's server, before FX6's field reaches every path.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-below-floor")).toHaveCount(0);
  });

  test("the removals note names the answer, because the tray is part of it", async ({ page }) => {
    // `removed` is the eligibility re-filter's count across BOTH stored lists. With the tray back
    // on screen the old wording — "2 picks are gone" beside a grid — read as a claim about the
    // grid, and a reader counting the grid to check it would be counting the wrong thing.
    //
    // F4-10 is the other half of the same sentence: the drop set is five interactions and the
    // copy named two of them, neither of which had to be the one that fired. A finder SAVED a
    // card and was told on the next load that they had watched or rated it. It named three of the
    // five after the first pass, which left the one destructive-feeling quick action on every card
    // — "not interested" — described back to the user as something they had watched, rated or
    // saved. The sentence is unconditional, so it may only claim what is true of all five.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, removed: 2, belowFloor: [UNDER, WAY_UNDER] },
    });

    await expect(page.getByTestId("askreel-removed")).toHaveText(
      "2 picks are gone from this answer — you’ve since rated, watched, saved or dismissed them.",
    );
    // Both lists are on screen, which is the reason the note cannot name just one of them.
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-below-floor").locator("summary")).toHaveText("Show the 2 held back by your 6.0 bar");
  });

  test("removals the account had made before the answer are never called 'since'", async ({ page }) => {
    // R1-11: a person query shows seen titles, so a resumed answer can lose cards rated years
    // before it. The server counts those apart (`removedEarlier`); varied over all-earlier and mixed.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, removed: 2, removedEarlier: 2, belowFloor: [UNDER, WAY_UNDER] },
    });
    await expect(page.getByTestId("askreel-removed")).toHaveText(
      "2 picks are gone from this answer — you’d already rated, watched, saved or dismissed them when it was given.",
    );
  });

  test("a mixed set of removals says which part came since", async ({ page }) => {
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, removed: 3, removedEarlier: 1, belowFloor: [UNDER] },
    });
    await expect(page.getByTestId("askreel-removed")).toHaveText(
      "3 picks are gone from this answer — you’d already rated, watched, saved or dismissed 1 of them when it was given, and you’ve acted on 2 since.",
    );
  });

  test("one removal is singular, and still about the answer", async ({ page }) => {
    // Fixture variation, one field: `removed` 2 → 1. Three agreements have to move together —
    // the noun, the verb and the pronoun — and the sentence is assembled from three separate
    // ternaries, so a copy edit that touches one of them is invisible until this runs.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: { ...RESUME_PAYLOAD, removed: 1, belowFloor: [UNDER] },
    });

    await expect(page.getByTestId("askreel-removed")).toHaveText(
      "1 pick is gone from this answer — you’ve since rated, watched, saved or dismissed it.",
    );
  });
});

test.describe("Ask Reel — continuity on a phone", () => {
  test.use({ viewport: { width: 375, height: 812 } });

  test("a resumed thread renders its grid, its seam and its controls at 375", async ({ page }) => {
    // The same continuity, at the width where the composer, the tab bar and the header all
    // compete for the bottom of the screen. Cold-booted from a STORED id, which is the phone's
    // normal path back in: it has one tab and it left the app rather than the page.
    await boot(page, { resume: "stored", storedId: CONVERSATION });

    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    await expect(page.getByTestId("askreel-seam")).toContainText("Picked up where you left off");
    await expect(page.getByTestId("askreel-transcript-only")).toHaveCount(0);
    await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
    await expect(page.getByTestId("askreel-composer")).toBeEditable();
    await expect(page.getByTestId("askreel-new")).toBeVisible();
    await expect(page.getByTestId("askreel-recent")).toBeVisible();
  });

  test("a thread that really does predate stored results says so, and only then", async ({ page }) => {
    // F3-06's honest half. Fixture variation on the test above: `lastCardsAt: null` and no cards,
    // which is the one shape that may claim the thread predates saved results. Everything else
    // about the payload is identical, so the branch cannot be reached for any other reason.
    await stubEventSource(page);
    await stubAskReel(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/settings/filters", FILTERS_WITH_FLOOR);
    await mockJson(page, "**/api/search/conversations/latest", {
      ...RESUME_PAYLOAD, cards: [], lastCardsAt: null, reason: null, shown: [],
    });
    await page.goto("/search");

    await expect(page.getByTestId("askreel-transcript-only")).toBeVisible();
    await expect(page.getByTestId("askreel-transcript-only")).toContainText("predates saved results");
    // Never the empty-answer card: this page ran no search, so it has no verdict to report.
    await expect(page.getByTestId("semantic-empty")).toHaveCount(0);
    // The transcript and a way to bring the answer back are both still there.
    await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");
    await expect(page.getByTestId("askreel-retry")).toBeVisible();
  });

  test("a thread whose only answer failed is not told it predates saved results", async ({ page }) => {
    // F4-04. Fixture variation, one field on the same payload: the assistant row is `Failed`
    // rather than `Completed`. `lastCardsAt == null` is true for that too — a turn that never
    // reached `done` stores no cards — so a seven-minute-old thread got the failure row AND
    // "Transcript only — this thread predates saved results" on one screen: two "Ask again"
    // buttons, and two mutually exclusive explanations of the same empty grid.
    await stubEventSource(page);
    await stubAskReel(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/settings/filters", FILTERS_WITH_FLOOR);
    await mockJson(page, "**/api/search/conversations", [
      { id: CONVERSATION, title: "an abort probe: slow burn desert noir", turnCount: 2, lastTurnAt: new Date().toISOString(), lastCardsAt: null },
    ]);
    await mockJson(page, "**/api/search/conversations/latest", {
      ...RESUME_PAYLOAD,
      cards: [],
      lastCardsAt: null,
      lastTurnAt: new Date(Date.now() - 7 * 60_000).toISOString(),
      reason: null,
      shown: [],
      turns: [
        { sequence: 0, role: "user", text: "an abort probe: slow burn desert noir", kind: "ask", status: "Failed" },
        { sequence: 1, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
      ],
    });
    await page.goto("/search");

    // The transcript row is the right answer and it is already there.
    await expect(page.getByTestId("askreel-failed-turn")).toBeVisible();
    await expect(page.getByTestId("askreel-transcript-only")).toHaveCount(0);
    // …and not the OTHER false explanation either: nothing was watched or rated away, because
    // nothing was ever found.
    await expect(page.getByTestId("askreel-all-removed")).toHaveCount(0);
    await expect(page.getByTestId("semantic-empty")).toHaveCount(0);
    // Exactly one offer to try again, not two.
    await expect(page.getByTestId("askreel-retry")).toHaveCount(1);

    // F4-04b, the same claim on the menu: the row said "transcript only", which the contract
    // defines as a thread that predates stored results. What the list can actually see is that
    // there are no stored picks, so that is what it says.
    await page.getByTestId("askreel-recent").click();
    const row = page.getByTestId("askreel-recent-row").first();
    await expect(row).toContainText("no saved picks");
    await expect(row).not.toContainText("transcript only");

    // The probe: the same payload with one field moved — `Failed` to `Pending`. A turn the server
    // is STILL working on has stored no cards either, and must not be told its thread is old.
    // Served from BOTH routes because the first resume remembered the id, so the reload asks for
    // the thread by name rather than through the `/latest` alias.
    const pendingPayload = {
      ...RESUME_PAYLOAD,
      cards: [], lastCardsAt: null, reason: null, shown: [],
      turns: [
        { sequence: 0, role: "user", text: "an abort probe: slow burn desert noir", kind: "ask", status: "Pending" },
        { sequence: 1, role: "assistant", text: "", kind: "ask", status: "Pending" },
      ],
    };
    await mockJson(page, "**/api/search/conversations/latest", pendingPayload);
    await mockJson(page, `**/api/search/conversations/${CONVERSATION}`, pendingPayload);
    await page.reload();
    await expect(page.getByTestId("askreel-pending-turn")).toBeVisible();
    await expect(page.getByTestId("askreel-transcript-only")).toHaveCount(0);
    await expect(page.getByTestId("askreel-all-removed")).toHaveCount(0);
  });
});

/**
 * F6-01 — the thread switch that bound a running answer to the wrong question.
 *
 * Seen at 375: `+ New` was `disabled` while streaming and `Recent ▾` was not, so opening another
 * thread mid-stream put THAT thread's question and reply above the old thread's progress banner and
 * 34 of its cards, with no seam, no dim and no label. Fourteen seconds later the in-flight results
 * landed *in the new thread* and replaced its answer; re-opening it from a clean context showed the
 * damage was durable — turn count 2 → 3, ten foreign cards under the question, and the question that
 * had actually been asked existing nowhere at all.
 *
 * Both halves are pinned here: the gesture is not offered while a turn runs, and a frame that
 * arrives anyway changes nothing.
 */
test.describe("Ask Reel — a thread switch never adopts a running answer", () => {
  /** The other thread, served whole — its own question, its own reply, its own single card. */
  const OTHER_PAYLOAD = {
    id: OTHER_CONVERSATION,
    title: "a 90s romantic comedy",
    lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
    turns: [
      { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "One that fits.", kind: "ask", status: "Completed" },
    ],
    cards: [card("z9", "Zither Lane")],
    reason: "1 pick ranked for you, from 12 pulled.",
    removed: 0,
    lastQuery: "a 90s romantic comedy",
    mediaTypes: [],
    shown: [],
  };

  test("Recent is inert while a turn streams, exactly as + New is", async ({ page }) => {
    await boot(page);
    await page.getByTestId("askreel-composer").fill("a heist with a great ensemble");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-1", sequence: 1 });
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });

    // The measured asymmetry, as an assertion: `{newDisabled: true, recentDisabled: false}`.
    await expect(page.getByTestId("askreel-banner")).toBeVisible();
    await expect(page.getByTestId("askreel-new")).toBeDisabled();
    await expect(page.getByTestId("askreel-recent")).toBeDisabled();
    // Inert to a real tap, not merely styled as such — the menu must not open.
    await page.getByTestId("askreel-recent").click({ force: true });
    await expect(page.getByTestId("askreel-recent-menu")).toHaveCount(0);

    // …and it comes back the moment the turn settles, or the control would be a one-way door.
    await askPush(page, "done", { results: [ALPHA], reason: "1 pick ranked for you, from 9 pulled." });
    await askEnd(page);
    await expect(page.getByTestId("askreel-recent")).toBeEnabled();
  });

  test("the switch, once the turn settles, leaves nothing of the old run behind", async ({ page }) => {
    // The other side of the same gesture. `Recent ▾` is a one-way door if it never comes back, so
    // the switch is taken the moment the turn is done — and the arriving thread must be whole, with
    // no card, no reply and no footnote from the turn that just finished somewhere else.
    //
    // The reducer's own guard against a frame from an abandoned run is pinned in
    // `useAskReelChat.test.ts` ("a stale stream's frames change nothing"); it cannot be driven from
    // here, because React refuses to dispatch a click to an element whose props say `disabled` —
    // which is the fix working.
    await boot(page);
    await mockJson(page, `**/api/search/conversations/${OTHER_CONVERSATION}`, OTHER_PAYLOAD);
    await askAndAnswer(page);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    await page.getByTestId("askreel-recent").click();
    await page.getByTestId("askreel-recent-row").nth(1).getByTestId("askreel-recent-open").click();

    await expect(page.getByTestId("askreel-transcript")).toContainText("a 90s romantic comedy");
    await expect(gridNames(page)).toHaveText(["Zither Lane"]);
    await expect(page.getByTestId("askreel-transcript")).not.toContainText("a tense heist");
    await expect(page.getByTestId("askreel-footnote")).toContainText("1 pick ranked for you");
    await expect(page.getByTestId("askreel-banner")).toHaveCount(0);
    // No stream left open for a thread nobody is looking at.
    expect(await page.evaluate(() => (window as unknown as { __ask: { count: () => number } }).__ask.count())).toBe(0);
    expect(await page.evaluate(() => localStorage.getItem("reel.ask.conversationId"))).toBe(OTHER_CONVERSATION);
  });
});

/**
 * F6-02 — the page that never scrolled to the turn it was running.
 *
 * At 375 a 1 000-character question (the length the server accepts and the history keeps) renders a
 * bubble ~1 100 px tall. `scrollY` stayed at 0 for the whole 19.4-second turn, so the viewport held
 * nothing but the user's own words — no banner, no narration, no progress bar, no reply, no cards —
 * and four frames captured at 400 ms, 1 s, 3 s and 8 s of the NEXT turn were pixel-identical for
 * exactly the same reason. The only evidence anything was happening anywhere was the Send arrow
 * greying out.
 */
test.describe("Ask Reel — the live turn is on screen", () => {
  test.use({ viewport: { width: 375, height: 812 } });

  /** The cap the history keeps, to the character. */
  const LONG_QUESTION = `${"a slow-burn heist with a great ensemble and a twist I will not see coming, ".repeat(14)}please`.slice(0, 1000);

  test("a 1000-character question does not hide the turn it started", async ({ page }) => {
    await boot(page);
    expect(LONG_QUESTION).toHaveLength(1000);

    await page.getByTestId("askreel-composer").fill(LONG_QUESTION);
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-1", sequence: 1 });
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });

    // The thing the finder could not see for nineteen seconds.
    await expect(page.getByTestId("askreel-banner")).toBeInViewport();
    await expect(page.getByTestId("askreel-narration")).toBeInViewport();

    // …and the bubble is capped rather than 1 100 px tall, which is the half that makes the scroll
    // survive a second long question further down the thread.
    const bubble = page.getByTestId("askreel-user-bubble");
    const clamped = (await bubble.boundingBox())!.height;
    expect(clamped).toBeLessThan(300);

    // The rest is one tap away, and the tap is offered only because there IS a rest.
    await expect(page.getByTestId("askreel-bubble-toggle")).toHaveText("Show more");
    await page.getByTestId("askreel-bubble-toggle").click();
    expect((await bubble.boundingBox())!.height).toBeGreaterThan(clamped);
    await expect(page.getByTestId("askreel-bubble-toggle")).toHaveText("Show less");
    await page.getByTestId("askreel-bubble-toggle").click();
    expect((await bubble.boundingBox())!.height).toBeCloseTo(clamped, 0);
  });

  test("an ordinary question gets no toggle, and Stop brings the kept answer back into view", async ({ page }) => {
    // Fixture variation on the length: the clamp must be invisible on the questions people
    // actually ask, or every bubble in the transcript grows a control.
    await boot(page);
    await askAndAnswer(page);
    await expect(page.getByTestId("askreel-bubble-toggle")).toHaveCount(0);

    // Now the other scroll: a new question, a Stop before anything arrives, and the answer the
    // user was reading is below a transcript that already fills the screen.
    await page.getByTestId("askreel-composer").fill(LONG_QUESTION);
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "phase", { stage: "searching", found: 0, scored: 0 });
    await expect(page.getByTestId("askreel-banner")).toBeInViewport();
    await page.getByTestId("askreel-stop").click();

    await expect(page.getByTestId("askreel-stopped")).toBeInViewport();
    await expect(page.getByTestId("askreel-stopped")).toContainText("Stopped before any picks arrived");
  });
});

/**
 * F6-03 — Home, then browser Back, reported as a dead end.
 *
 * The finder measured `location.pathname === "/home"` after Back from a settled answer, and read it
 * as the header's home link pointing at `/` and the `/` → `/home` redirect consuming the history
 * entry. Pinned here at the width where the browser's own back gesture is the primary one, through
 * BOTH ways out of /search: the mobile tab bar and the header mark.
 */
test.describe("Ask Reel — Back from Home returns to the answer", () => {
  test.use({ viewport: { width: 375, height: 812 } });

  for (const route of ["tab bar", "header mark"] as const) {
    test(`Back after leaving via the ${route} lands on /search with the same cards`, async ({ page }) => {
      // Entered the way a phone enters: the app root first, which is the redirect under suspicion,
      // then /search. If that redirect pushed rather than replaced, Back from /home re-redirects
      // forward and the answer is unreachable by the gesture entirely.
      await boot(page, { resume: "stored", storedId: CONVERSATION });
      await expect(gridNames(page)).toHaveText(STORED_ORDER);

      if (route === "tab bar") {
        await page.getByTestId("mobile-tabbar").getByRole("link", { name: "Home" }).click();
      } else {
        await page.getByLabel("Reel home").click();
      }
      await expect(page).toHaveURL(/\/home$/);
      // Wait for /search to genuinely UNMOUNT. The URL changes synchronously on pushState, so a
      // goBack issued straight after `toHaveURL` lands inside the same React batch and the route
      // never unmounts — and `history.back()` is itself asynchronous, so reading `pathname`
      // immediately after it can still report the page being left. Both traps are the reason the
      // original report is asserted here rather than in a console probe.
      await expect(page.getByTestId("askreel-composer")).toHaveCount(0);

      await page.goBack();
      await expect(page).toHaveURL(/\/search$/);
      await expect(gridNames(page)).toHaveText(STORED_ORDER);
      await expect(page.getByTestId("askreel-transcript")).toContainText("a tense heist");
      await expect(page.getByTestId("askreel-preset")).toHaveCount(0);
    });
  }

  test("the app root is replaced, not pushed, so Back is never a redirect loop", async ({ page }) => {
    // The reported mechanism, isolated. `/` renders the landing page, which bounces a signed-in
    // visitor onward — and if that bounce consumed a history entry of its own, Back from /home
    // would land on `/` and be redirected straight forward again, which is what "Back is inert"
    // means. Entered the way a returning phone enters: the root first, then the answer.
    await boot(page, { resume: "stored", storedId: CONVERSATION });
    await page.addInitScript(() => localStorage.setItem("reel.hasSession", "1"));

    await page.goto("/");
    await expect(page).toHaveURL(/\/home$/);
    await page.goto("/search");
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    await page.getByLabel("Reel home").click();
    await expect(page).toHaveURL(/\/home$/);
    await expect(page.getByTestId("askreel-composer")).toHaveCount(0);

    await page.goBack();
    await expect(page).toHaveURL(/\/search$/);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    // One more step back: the entry before /search is the REPLACED root, so this reaches /home
    // once and settles there rather than bouncing between `/` and `/home`.
    await page.goBack();
    await expect(page).toHaveURL(/\/home$/);
  });
});

/**
 * R5-03 / R5-04 — paging a thread whose last question was stopped.
 *
 * The state R5 found is the ordinary end of an ordinary session: settle an answer, ask a follow-up,
 * press Stop, come back tomorrow. `pageTurnId` came off the last completed ask and `pageQuery` off
 * the payload's `lastQuery` — which the contract defines as the last USER row — so "Show me more"
 * put the kept answer's turn id and the abandoned question on the wire together. The server refused
 * it `409 page_query_mismatch`, the client read every 409 as `turn_in_progress`, and the screen
 * announced that a turn was running while deleting the footnote and the control the user had just
 * pressed. Nothing but a reload restored them, and the reload reproduced the 409.
 */
test.describe("a resumed thread with a stopped tail can still be paged", () => {
  /** The thread as the server stores it: one answered ask, one stopped one, with row ids on both. */
  const STOPPED_WITH_IDS = [
    { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed", id: "turn-romcom" },
    { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted", id: "turn-sea" },
  ];

  const PAYLOAD = {
    ...RESUME_PAYLOAD,
    turns: STOPPED_WITH_IDS,
    // The server reads this off the last USER row, which on this thread is the stopped question.
    // A fixture that put the answered question here would pass every assertion below against the
    // client that shipped R5-03.
    lastQuery: "something tense set at sea",
  };

  test("Show me more sends the answered question, not the one that was stopped", async ({ page }) => {
    await boot(page, { resume: "stored", storedId: CONVERSATION, payload: PAYLOAD });
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    // Both halves name ONE row. R5-03 captured `pageOf` correct and `query` wrong on the wire.
    expect(requests[0].pageOf).toBe("turn-romcom");
    expect(requests[0].query).toBe("a 90s romantic comedy");
    // …and the OTHER control still replays the other question: `lastQuery` keeps its own job, which
    // is why the two cannot be one field. (The retry itself is covered above and in
    // `askreel-negation.spec.ts`; what matters here is that the page did not take it over.)
    expect(requests[0].query).not.toBe(PAYLOAD.lastQuery);
  });

  test("an ordinary resumed thread pages the same question it always did", async ({ page }) => {
    // Fixture variation, ONE field: the tail's status. Everything else — the ids, the text, the
    // ordering, `lastQuery` — is what the thread would carry either way, so this cannot pass for
    // any reason other than the status being read.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...PAYLOAD,
        turns: [
          ...STOPPED_WITH_IDS.slice(0, 2),
          { ...STOPPED_WITH_IDS[2], status: "Completed" },
          { ...STOPPED_WITH_IDS[3], text: "Two more.", status: "Completed", failureCode: null },
        ],
      },
    });

    await page.getByTestId("askreel-more").click();
    await waitForAsk(page);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests[0].pageOf).toBe("turn-sea");
    expect(requests[0].query).toBe("something tense set at sea");
  });

  test("a refused page keeps the footnote, the control and the grid", async ({ page }) => {
    // R5-04, measured exactly as R5 measured it: the live region, the footnote, the control and
    // the grid, before and after the 409.
    await boot(page, { resume: "stored", storedId: CONVERSATION, payload: PAYLOAD });
    await expect(page.getByTestId("askreel-footnote")).toBeVisible();
    await expect(page.getByTestId("askreel-more")).toBeVisible();

    await askFailNext(page, 409, "page_query_mismatch");
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page);

    // The refusal says what it is, in words that do not claim a turn is running.
    const refused = page.getByTestId("askreel-page-refused");
    await expect(refused).toContainText("no longer matches its answer");
    await expect(page.getByTestId("askreel-status")).not.toContainText("still being answered");
    // Nothing on screen was lost. All three of these were gone after the 409 and came back only on
    // a reload — which then reproduced R5-03, so the state did not self-correct at all.
    await expect(page.getByTestId("askreel-footnote")).toBeVisible();
    await expect(page.getByTestId("askreel-more")).toBeVisible();
    await expect(gridNames(page)).toHaveText(STORED_ORDER);
    // …and it is not the error card either: nothing failed, and the answer is still the answer.
    await expect(page.getByTestId("askreel-page-error")).toHaveCount(0);
    await expect(page.getByTestId("askreel-in-progress")).toHaveCount(0);

    // Recoverable without a reload: the control works, and the refusal goes with the gesture.
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 2);
    await expect(page.getByTestId("askreel-page-refused")).toHaveCount(0);
  });

  test("a turn that really is in progress still says so", async ({ page }) => {
    // Fixture variation, one field: the `code` on the same 409. The two refusals share a status
    // and mean opposite things, and collapsing them is what R5-04 was.
    await boot(page, { resume: "stored", storedId: CONVERSATION, payload: PAYLOAD });
    await askFailNext(page, 409, "turn_in_progress");
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page);

    await expect(page.getByTestId("askreel-in-progress")).toBeVisible();
    await expect(page.getByTestId("askreel-page-refused")).toHaveCount(0);
  });
});

/**
 * R5-02 — a turn that did not finish says so wherever it sits in the thread.
 *
 * The failure row was read off the LAST row of the resumed transcript, so it existed only while the
 * stopped turn was the tail. Page the kept answer, or ask anything else, and it went: the reload
 * rendered a bare user bubble with nothing under it, and live the page kept the `assistant-message`
 * that had streamed before the abort and presented it as that turn's answer — a promise, over zero
 * cards, with `Status='Failed'` in the database and nothing on screen saying it had not been kept.
 */
test.describe("a stopped turn that is no longer the tail", () => {
  test("a failure in the middle of a thread is still a failure", async ({ page }) => {
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...RESUME_PAYLOAD,
        turns: [
          { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
          { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
          { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted", id: "turn-sea" },
          { sequence: 4, role: "user", text: "something older", kind: "ask", status: "Completed" },
          { sequence: 5, role: "assistant", text: "Two older ones.", kind: "ask", status: "Completed", id: "turn-older" },
        ],
        lastQuery: "something older",
      },
    });

    const failed = page.getByTestId("askreel-failed-turn");
    // Exactly one, and it is the middle turn's — not the thread's, and not the tail's.
    await expect(failed).toHaveCount(1);
    await expect(failed).toContainText("didn’t finish before you left");
    // In the MIDDLE: after the question it is about, before the one that followed it.
    const order = await page.getByTestId("askreel-transcript").evaluate((el) =>
      [...el.querySelectorAll('[data-testid="askreel-user-bubble"], [data-testid="askreel-reply"], [data-testid="askreel-failed-turn"]')]
        .map((n) => `${n.getAttribute("data-testid")}:${(n.textContent ?? "").slice(0, 24)}`));
    expect(order.map((r) => r.split(":")[0])).toEqual([
      "askreel-user-bubble", "askreel-reply", "askreel-user-bubble", "askreel-failed-turn",
      "askreel-user-bubble", "askreel-reply",
    ]);

    // …and its "Ask again" replays ITS question, not the thread's newest one.
    await failed.getByTestId("askreel-retry").click();
    await waitForAsk(page);
    const requests = (await askRequests(page)) as Record<string, unknown>[];
    expect(requests[0].query).toBe("something tense set at sea");
  });

  test("a thread where nothing failed grows no such row", async ({ page }) => {
    // Fixture variation, ONE field: the middle assistant row's status, plus the text a completed
    // turn would carry. That text is the point — dropping empty rows looked correct precisely
    // because a finished turn always has some.
    await boot(page, {
      resume: "stored",
      storedId: CONVERSATION,
      payload: {
        ...RESUME_PAYLOAD,
        turns: [
          { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
          { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Completed" },
          { sequence: 3, role: "assistant", text: "Here’s a tense sea-set pick.", kind: "ask", status: "Completed", id: "turn-sea" },
        ],
        lastQuery: "something tense set at sea",
      },
    });

    await expect(page.getByTestId("askreel-reply")).toHaveCount(2);
    await expect(page.getByTestId("askreel-failed-turn")).toHaveCount(0);
  });

  test("a Stop does not leave the half-written reply on screen as the answer", async ({ page }) => {
    // R5-02 live. The `assistant-message` landed at 2 578 ms and the abort at 4 118 ms, and the
    // transcript read "Reel said: Here's a tense sea-set pick…" over a turn with zero cards.
    await boot(page, { resume: "none" });
    await page.getByTestId("askreel-composer").fill("a 90s romantic comedy");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-romcom", sequence: 1 });
    await askPush(page, "assistant-message", { text: "Three that fit." });
    await askPush(page, "done", { results: [ALPHA, BORDER, CEDAR], reason: "3 picks ranked for you, from 40 pulled." });
    await askEnd(page);
    await expect(gridNames(page)).toHaveText(STORED_ORDER);

    await page.getByTestId("askreel-composer").fill("something tense set at sea");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page, 2);
    await askPush(page, "conversation", { id: CONVERSATION, turnId: "turn-sea", sequence: 3 });
    await askPush(page, "assistant-message", { text: "Here’s a tense sea-set pick that stays in your lane." });
    await expect(page.getByTestId("askreel-reply")).toHaveCount(2);
    await page.getByTestId("askreel-stop").click();

    // The promise is gone. The kept answer's own reply is still there — one reply, not two.
    await expect(page.getByTestId("askreel-stopped")).toBeVisible();
    await expect(page.getByTestId("askreel-transcript")).not.toContainText("stays in your lane");
    await expect(page.getByTestId("askreel-reply")).toHaveCount(1);
    // Exactly one retry: the stopped card's. The transcript row stands down while that card is up.
    await expect(page.getByTestId("askreel-retry")).toHaveCount(1);
    await expect(page.getByTestId("askreel-failed-turn")).toHaveCount(0);

    // …and the moment the kept answer is paged, the stopped turn speaks for itself instead.
    await page.getByTestId("askreel-more").click();
    await waitForAsk(page, 3);
    await askPush(page, "done", { results: [], kept: 0, reason: "Nothing more on this one." });
    await askEnd(page);
    const failed = page.getByTestId("askreel-failed-turn");
    await expect(failed).toHaveCount(1);
    await expect(failed).toContainText("didn’t finish");
    await expect(page.getByTestId("askreel-transcript")).not.toContainText("stays in your lane");
  });
});
