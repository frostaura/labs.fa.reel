import { describe, expect, it } from "vitest";
import type { AskCard, DoneData } from "../askReelStream";
import { AskStreamError } from "../askReelStream";
import { chatReducer, failureFor, gridRowOf, initialChatState, type ChatAction } from "./reducer";
import { failureCopy, refineChips, waitPhrase } from "./copy";
import { answerRowIndex, transcriptOf, type ConversationPayload } from "./transcript";

/**
 * Chat UI v2 (WS-2 task 3) — the reducer half. Each test drives the state machine with the frames
 * §4.9 adds, the way the hook forwards them, and pins one rule of the v2 screen:
 * picks never move once shown, every answer keeps its cards under its own reply, an item question
 * leaves the grid alone, and the degraded / none-found facts land on the turn they describe.
 */

function card(n: number, extra: Partial<AskCard> = {}): AskCard {
  return {
    titleId: `t${n}`, mediaType: "Movie", tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7, ...extra,
  };
}

function done(extra: Partial<DoneData> = {}): DoneData {
  return { results: [], reason: null, mode: "Picks", degraded: [], ...extra };
}

function run(actions: ChatAction[]) {
  return actions.reduce(chatReducer, initialChatState);
}

/** One finished answer: the question, Reel's reply, and these cards. */
function answered(question: string, cards: AskCard[]): ChatAction[] {
  return [
    { type: "userTurn", message: question, fresh: false },
    { type: "assistant", data: { text: `Reply to ${question}` } },
    { type: "picks", data: { cards } },
    { type: "done", data: done({ results: cards, kept: cards.length }) },
  ];
}

describe("v2 picks — appended per batch, never reordered once shown (§4.8)", () => {
  it("keeps emission order through a phase boundary even when a later card blends higher", () => {
    const s = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "picks", data: { cards: [card(1, { blend: 0.2 }), card(2, { blend: 0.3 })] } },
      { type: "picks", data: { cards: [card(3, { blend: 0.9 })] } },
      { type: "phase", data: { stage: "judging", found: 0, scored: 0 } },
    ]);
    expect(s.order).toEqual(["t1", "t2", "t3"]);
    expect(s.arrived).toBe(3);
  });

  it("never re-floors a server-gated pick on the client", () => {
    const s = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "picks", data: { cards: [card(1, { predictedRating: 4 })] } },
    ]);
    // `unsettled` is what the page's client floor may touch; a v2 pick already passed the gate.
    expect(s.order.slice(Math.max(s.settled, s.pinned))).toEqual([]);
  });
});

describe("v2 — each answer keeps its cards under its own reply", () => {
  it("freezes the previous answer onto its reply row when the new turn's first pick lands", () => {
    const s = run([
      ...answered("ninja movies", [card(1), card(2)]),
      { type: "userTurn", message: "samurai films", fresh: false },
      { type: "assistant", data: { text: "Here are samurai films." } },
      { type: "picks", data: { cards: [card(3)] } },
    ]);
    expect(s.turns[1].answer?.cards.map((c) => c.titleId)).toEqual(["t1", "t2"]);
    expect(s.order).toEqual(["t3"]);
    // The live grid belongs to the newest reply.
    expect(gridRowOf(s)).toBe(3);
  });

  it("holds the previous grid under ITS reply while the new turn has produced nothing", () => {
    const s = run([
      ...answered("ninja movies", [card(1)]),
      { type: "userTurn", message: "samurai films", fresh: false },
    ]);
    expect(s.holdingPrevious).toBe(true);
    expect(gridRowOf(s)).toBe(1);
    expect(s.turns[1].answer).toBeUndefined();
  });

  it("does not freeze an answer a re-ask replaces — it was the same question", () => {
    const s = run([
      ...answered("ninja movies", [card(1)]),
      { type: "reaskTurn" },
      { type: "picks", data: { cards: [card(2)] } },
    ]);
    expect(s.turns.some((t) => t.answer)).toBe(false);
    expect(s.order).toEqual(["t2"]);
  });

  it("restores per-turn cards from the resume payload and keeps the newest as the grid", () => {
    const payload: ConversationPayload = {
      id: "c1",
      turns: [
        { role: "user", text: "ninja movies", status: "Completed" },
        { role: "assistant", text: "Ninja picks.", status: "Completed", kind: "ask", cards: [card(1)] },
        { role: "assistant", text: "", status: "Completed", kind: "page", cards: [card(2)] },
        { role: "user", text: "samurai films", status: "Completed" },
        { role: "assistant", text: "Samurai picks.", status: "Completed", kind: "ask", cards: [card(3)] },
      ],
      cards: [card(3)],
    };
    const s = chatReducer(initialChatState, { type: "resume", payload });
    // The page row folds into the answer it extended.
    expect(s.turns[1].answer?.cards.map((c) => c.titleId)).toEqual(["t1", "t2"]);
    expect(s.turns[3].answer).toBeUndefined();
    expect(gridRowOf(s)).toBe(3);
    expect(s.order).toEqual(["t3"]);
  });

  it("drops a malformed stored card rather than the row it sits on", () => {
    const rows = transcriptOf([
      { role: "user", text: "q" },
      { role: "assistant", text: "a", status: "Completed", cards: [card(1), { titleId: "" } as unknown as AskCard] },
    ]);
    expect(rows[1].answer?.cards.map((c) => c.titleId)).toEqual(["t1"]);
  });
});

describe("v2 trays stream with the turn", () => {
  it("fills both trays from tray frames and does not double them when done repeats them", () => {
    const s = run([
      { type: "userTurn", message: "samurai films", fresh: true },
      { type: "tray", data: { kind: "nearMiss", cards: [card(1)] } },
      { type: "tray", data: { kind: "belowFloor", cards: [card(2)] } },
      { type: "done", data: done({ mode: "NoneFound", nearMiss: [card(1)], belowFloor: [card(2)] }) },
    ]);
    expect(s.nearMiss.map((c) => c.titleId)).toEqual(["t1"]);
    expect(s.belowFloor.map((c) => c.titleId)).toEqual(["t2"]);
  });

  it("keeps streamed trays when done omits them", () => {
    const s = run([
      { type: "userTurn", message: "samurai films", fresh: true },
      { type: "tray", data: { kind: "nearMiss", cards: [card(1)] } },
      { type: "done", data: done({ mode: "NoneFound" }) },
    ]);
    expect(s.nearMiss.map((c) => c.titleId)).toEqual(["t1"]);
  });
});

describe("v2 none-found, answer, clarify, degraded and plan", () => {
  it("takes the none-found supply's relaxations before done arrives", () => {
    const s = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      {
        type: "noneFound",
        data: { supply: { knockouts: [{ kind: "Language", value: "en", contentFilterId: null, label: "English only", wouldUnlock: 4 }] } },
      },
      { type: "done", data: done({ mode: "NoneFound" }) },
    ]);
    expect(s.mode).toBe("NoneFound");
    expect(s.knockouts.map((k) => k.label)).toEqual(["English only"]);
  });

  it("answers an item question beside the grid and leaves the grid exactly where it was", () => {
    const s = run([
      ...answered("ninja movies", [card(1), card(2)]),
      { type: "userTurn", message: "is the first one violent?", fresh: false },
      { type: "answer", data: { text: "Yes — it opens on a massacre." } },
      { type: "done", data: done({ mode: "Answer" }) },
    ]);
    expect(s.order).toEqual(["t1", "t2"]);
    expect(s.holdingPrevious).toBe(false);
    expect(s.status).toBe("done");
    expect(s.turns.at(-1)).toMatchObject({ role: "assistant", text: "Yes — it opens on a massacre." });
    // The grid stays under the answer that produced it, not under the item question.
    expect(gridRowOf(s)).toBe(1);
    expect(s.mode).toBe("Picks");
  });

  it("asks back with its options and keeps the grid", () => {
    const s = run([
      ...answered("ninja movies", [card(1)]),
      { type: "userTurn", message: "the other one", fresh: false },
      { type: "clarify", data: { question: "Which one did you mean?", options: ["Title 1", "Something else"] } },
      { type: "done", data: done({ mode: "Clarify" }) },
    ]);
    expect(s.turns.at(-1)).toMatchObject({ text: "Which one did you mean?", clarifyOptions: ["Title 1", "Something else"] });
    expect(s.order).toEqual(["t1"]);
  });

  it("marks the turn's own reply degraded the moment the frame lands", () => {
    const s = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "assistant", data: { text: "I couldn’t fully read that — searching for ninja as before." } },
      { type: "degraded", data: { what: ["plan", "judge:1/3"] } },
    ]);
    expect(s.turns.at(-1)).toMatchObject({ degraded: true, unchecked: true });
  });

  it("applies a degraded frame that arrived before the reply to the reply when it lands", () => {
    const s = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "degraded", data: { what: ["plan"] } },
      { type: "assistant", data: { text: "I couldn’t fully read that." } },
    ]);
    expect(s.turns.at(-1)).toMatchObject({ degraded: true });
  });

  it("never marks an earlier turn's reply", () => {
    const s = run([
      ...answered("ninja movies", [card(1)]),
      { type: "userTurn", message: "samurai", fresh: false },
      { type: "degraded", data: { what: ["plan"] } },
    ]);
    expect(s.turns[1].degraded).toBeUndefined();
  });

  it("describes the grid by its own turn's reading, never the question about to replace it", () => {
    const ninja = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "plan", data: { act: "NewRequest", concepts: ["ninja"], references: [] } },
      { type: "picks", data: { cards: [card(1)] } },
      { type: "done", data: done({ results: [card(1)] }) },
    ]);
    expect(ninja.plan?.concepts).toEqual(["ninja"]);
    // A follow-up's reading waits until its answer is the grid…
    const asking = run([
      ...[{ type: "userTurn", message: "ninja movies", fresh: true } as ChatAction],
      { type: "plan", data: { act: "NewRequest", concepts: ["ninja"], references: [] } },
      { type: "picks", data: { cards: [card(1)] } },
      { type: "done", data: done({ results: [card(1)] }) },
      { type: "userTurn", message: "samurai", fresh: false },
      { type: "plan", data: { act: "NewRequest", concepts: ["samurai"], references: [] } },
    ]);
    expect(asking.plan?.concepts).toEqual(["ninja"]);
    // …and a turn that sent no plan at all (the v1 engine) inherits nothing.
    const v1 = chatReducer(asking, { type: "done", data: done({ results: [card(2)] }) });
    expect(v1.plan?.concepts).toEqual(["samurai"]);
    const bare = run([
      { type: "userTurn", message: "ninja movies", fresh: true },
      { type: "plan", data: { act: "NewRequest", concepts: ["ninja"], references: [] } },
      { type: "done", data: done({ results: [card(1)] }) },
      { type: "userTurn", message: "samurai", fresh: false },
      { type: "candidate", data: card(2) },
      { type: "done", data: done({ results: [card(2)] }) },
    ]);
    expect(bare.plan).toBeNull();
  });
});

/**
 * R1-32. The server restores the newest SEARCH answer as the grid and skips an item question or a
 * clarifying question (`SearchTurnMode.LeavesGrid`), but the client anchored that grid — and the
 * collision notice, and the question the seam names, and the turn a page extends — to the newest
 * assistant row of any mode. Resume after "is the first one violent?" and the ninja cards sat
 * under "Yes — it opens on a massacre." with the item question named as what they answered.
 */
describe("an item question or a clarifying question never becomes the grid's row (R1-32)", () => {
  /** A search, then a follow-up whose reply is `mode`. Ids on every assistant row, as the server sends. */
  function thread(mode: string | null): ConversationPayload {
    return {
      id: "c1",
      turns: [
        { role: "user", text: "ninja movies", status: "Completed", kind: "ask" },
        { id: "turn-1", role: "assistant", text: "Ninja picks.", status: "Completed", kind: "ask", mode: "Picks", cards: [card(1), card(2)] },
        { role: "user", text: "is the first one violent?", status: "Completed", kind: "ask" },
        { id: "turn-2", role: "assistant", text: "Yes — it opens on a massacre.", status: "Completed", kind: "ask", mode, cards: [] },
      ],
      cards: [card(1), card(2)],
      mode: "Picks",
      conflict: { message: "Your conditions permanently exclude comedy.", excludedGenres: ["Comedy"] },
    };
  }

  it.each(["Answer", "Clarify"])("resumes a thread whose newest reply is %s with the grid under the search reply", (mode) => {
    const s = chatReducer(initialChatState, { type: "resume", payload: thread(mode) });
    expect(gridRowOf(s)).toBe(1);
    expect(answerRowIndex(s.turns)).toBe(1);
    // The notice explains the grid, so it goes on the grid's reply and nowhere else.
    expect(s.turns[1].conflict).toBe(true);
    expect(s.turns[3].conflict).toBeUndefined();
    // The seam names the question the cards answer, and a page extends THAT turn…
    expect(s.answeredQuery).toBe("ninja movies");
    expect(s.pageQuery).toBe("ninja movies");
    expect(s.pageTurnId).toBe("turn-1");
    // …while "Ask again" still replays the newest question.
    expect(s.lastQuery).toBe("is the first one violent?");
  });

  it.each([["Picks"], ["NoneFound"], [null]])("keeps the newest reply as the grid's row when it is a search (%s)", (mode) => {
    // Fixture variation, one field: the newest reply's `mode`. Absent reads as Picks, as on the server.
    const s = chatReducer(initialChatState, { type: "resume", payload: thread(mode) });
    expect(gridRowOf(s)).toBe(3);
    expect(answerRowIndex(s.turns)).toBe(3);
    expect(s.turns[3].conflict).toBe(true);
    expect(s.answeredQuery).toBe("is the first one violent?");
    expect(s.pageTurnId).toBe("turn-2");
  });

  it.each([
    ["answer", { type: "answer", data: { text: "Yes — it opens on a massacre." } }, "Answer"],
    ["clarify", { type: "clarify", data: { question: "Which one did you mean?", options: ["Title 1"] } }, "Clarify"],
  ] as [string, ChatAction, string][])("anchors the live answer's row past a %s reply", (_name, frame, mode) => {
    const s = run([
      ...answered("ninja movies", [card(1), card(2)]),
      { type: "userTurn", message: "is the first one violent?", fresh: false },
      frame,
      { type: "done", data: done({ mode }) },
    ]);
    expect(gridRowOf(s)).toBe(1);
    expect(answerRowIndex(s.turns)).toBe(1);
  });

  /**
   * The live half, found on the real server: "samurai films" came back NoneFound with one card in
   * the under-bar tray and four relaxations, then "is the first one violent?" answered beside it —
   * and the live screen dropped the tray, the relaxations and the none-found card (the hold only
   * ever covered PICKS, so an answer made of trays was cleared at ask-time), while a reload put all
   * of it back under the samurai reply. One answer, two screens, a reload apart.
   */
  const knockout = { kind: "ExcludeGenre", value: "comedy", contentFilterId: null, label: "excluded genre: comedy", wouldUnlock: 4 };
  function noneFound(trays: { belowFloor?: AskCard[]; nearMiss?: AskCard[] }): ChatAction[] {
    return [
      { type: "userTurn", message: "samurai films", fresh: false },
      { type: "assistant", data: { text: "None of your unseen samurai titles clears your bar." } },
      ...(trays.belowFloor ? [{ type: "tray", data: { kind: "belowFloor", cards: trays.belowFloor } } as ChatAction] : []),
      ...(trays.nearMiss ? [{ type: "tray", data: { kind: "nearMiss", cards: trays.nearMiss } } as ChatAction] : []),
      { type: "noneFound", data: { supply: { knockouts: [knockout] } } },
      { type: "done", data: done({ mode: "NoneFound", ...trays }) },
    ];
  }

  it.each([
    ["a tray", { belowFloor: [card(7)] }],
    ["near misses", { nearMiss: [card(8)] }],
    ["nothing but the reply", {}],
  ] as [string, { belowFloor?: AskCard[]; nearMiss?: AskCard[] }][])(
    "keeps a none-found answer made of %s on screen past an item question, as a reload does",
    (_name, trays) => {
      const s = run([
        ...noneFound(trays),
        { type: "userTurn", message: "is the first one violent?", fresh: false },
        { type: "answer", data: { text: "Rated R; the facts don’t say how violent." } },
        { type: "done", data: done({ mode: "Answer" }) },
      ]);
      expect(s.status).toBe("done");
      expect(s.mode).toBe("NoneFound");
      expect(s.knockouts.map((k) => k.label)).toEqual(["excluded genre: comedy"]);
      expect(s.belowFloor.map((c) => c.titleId)).toEqual((trays.belowFloor ?? []).map((c) => c.titleId));
      expect(s.nearMiss.map((c) => c.titleId)).toEqual((trays.nearMiss ?? []).map((c) => c.titleId));
      expect(gridRowOf(s)).toBe(1);
      expect(answerRowIndex(s.turns)).toBe(1);
    },
  );

  it("still replaces a none-found answer with the next SEARCH's", () => {
    // One field: the follow-up's mode. A search is the new answer, whatever the old one was made of.
    const s = run([
      ...noneFound({ belowFloor: [card(7)] }),
      { type: "userTurn", message: "ninja movies", fresh: false },
      { type: "assistant", data: { text: "Ninja picks." } },
      { type: "picks", data: { cards: [card(1)] } },
      { type: "done", data: done({ results: [card(1)], kept: 1 }) },
    ]);
    expect(s.mode).toBe("Picks");
    expect(s.knockouts).toEqual([]);
    expect(s.order).toEqual(["t1"]);
    expect(s.belowFloor).toEqual([]);
    // …and the tray it had is kept under its own reply, as the resume keeps it.
    expect(s.turns[1].answer?.belowFloor.map((c) => c.titleId)).toEqual(["t7"]);
    expect(gridRowOf(s)).toBe(3);
  });

  it("is minus one when the only reply is an item question's", () => {
    const s = run([
      { type: "userTurn", message: "is Heat violent?", fresh: true },
      { type: "answer", data: { text: "Yes." } },
      { type: "done", data: done({ mode: "Answer" }) },
    ]);
    expect(answerRowIndex(s.turns)).toBe(-1);
  });
});

describe("refineChips — built from the reading and the account's conditions", () => {
  it("names the concept the user asked for", () => {
    expect(refineChips({ concepts: ["ninja"], hasCards: true, excludedGenres: [], minYear: null, maxYear: null }))
      .toEqual(["Lighter ninja picks", "Newer ninja picks", "Older ninja picks", "More like the first one"]);
  });

  it("falls back to the generic directions with no reading", () => {
    expect(refineChips({ concepts: [], hasCards: true, excludedGenres: [], minYear: null, maxYear: null }))
      .toEqual(["Something lighter", "More intense", "More like the first one"]);
  });

  it("never offers what a standing condition rules out", () => {
    // "lighter" reads as comedy, deterministically (WS-2 1f) — offering it against an excluded
    // comedy is a chip that can only produce a conflict notice.
    expect(refineChips({ concepts: ["heist"], hasCards: false, excludedGenres: ["comedy"], minYear: 2000, maxYear: 2010 }))
      .toEqual([]);
    expect(refineChips({ concepts: [], hasCards: false, excludedGenres: ["Comedy"], minYear: null, maxYear: null }))
      .toEqual(["More intense"]);
  });
});

describe("a refused or failed turn says why (WS-8 CR-5)", () => {
  it("maps each ask_rate window to a rate failure carrying the wait", () => {
    expect(failureFor(new AskStreamError(429, "ask_rate_minute", undefined, 41))).toEqual({
      type: "error", reason: "rate", rateLimit: { window: "minute", retryAfterSeconds: 41 },
    });
    expect(failureFor(new AskStreamError(429, "ask_rate_day", undefined, 7200))).toMatchObject({ rateLimit: { window: "day" } });
    // A 429 with no ask_rate code is still a limit, just not one the page can name.
    expect(failureFor(new AskStreamError(429, null, undefined, null))).toEqual({
      type: "error", reason: "rate", rateLimit: { window: null, retryAfterSeconds: null },
    });
    // …and the 409 family is untouched: the limiter's in-flight refusal is still "in progress".
    expect(failureFor(new AskStreamError(409, "turn_in_progress", undefined, 2))).toEqual({ type: "turnInProgress" });
  });

  it("names the limit and the wait instead of a crash, and keeps the retry", () => {
    const state = chatReducer(initialChatState, failureFor(new AskStreamError(429, "ask_rate_minute", undefined, 41)) as ChatAction);
    expect(state.status).toBe("error");
    expect(state.errorReason).toBe("rate");
    const copy = failureCopy(state.errorReason, state.rateLimit);
    expect(copy.title).toBe("That’s the most questions Reel answers in a minute");
    expect(copy.detail).toBe("You can ask again in 41 seconds. Nothing was lost — your conversation is as you left it.");
    expect(copy.inline).toBe("That’s the most questions Reel answers in a minute — you can ask again in 41 seconds.");
    expect(copy.retryable).toBe(true);
    expect(failureCopy("rate", { window: "hour", retryAfterSeconds: 1500 }).title).toBe("That’s the most questions Reel answers in an hour");
    expect(failureCopy("rate", { window: null, retryAfterSeconds: null }).inline).toBe("Reel is answering too many of your questions at once — give it a moment.");
  });

  it("never offers a retry that cannot work, and never blames the engine for it", () => {
    expect(failureCopy("session").retryable).toBe(false);
    expect(failureCopy("request").retryable).toBe(false);
    expect(failureCopy("timeout").retryable).toBe(true);
    for (const reason of ["rate", "session", "timeout", "request"] as const) {
      expect(failureCopy(reason).detail).not.toMatch(/search engine/);
    }
    expect(failureCopy(null).title).toBe("Search hit a snag");
  });

  it("says the wait in the reader's units", () => {
    expect(waitPhrase(null)).toBeNull();
    expect(waitPhrase(0)).toBeNull();
    expect(waitPhrase(1)).toBe("in 1 second");
    expect(waitPhrase(59)).toBe("in 59 seconds");
    expect(waitPhrase(60)).toBe("in about a minute");
    expect(waitPhrase(61)).toBe("in about 2 minutes");
    expect(waitPhrase(3600)).toBe("in about an hour");
    expect(waitPhrase(5 * 3600 + 1)).toBe("in about 6 hours");
  });
});
