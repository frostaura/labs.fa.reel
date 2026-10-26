import { describe, expect, it } from "vitest";
import type { AskCard, DoneData } from "../askReelStream";
import { chatReducer, gridRowOf, initialChatState, type ChatAction } from "./reducer";
import { refineChips } from "./copy";
import { transcriptOf, type ConversationPayload } from "./transcript";

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
