import { describe, expect, it } from "vitest";
import {
  MAX_HISTORY_TURNS,
  MAX_SHOWN_IDS,
  capHistory,
  capShown,
  chatReducer,
  createMountGuard,
  initialChatState,
  lastUserQuery,
  nextBootstrap,
  showMoodPresets,
  type ChatTurn,
} from "./useAskReelChat";
import type { AskCard } from "./askReelStream";

/**
 * Regression for "reload mid-conversation and lose every refinement". The ?q= seed used to sit in
 * the URL for the life of the thread, so a reload after three refinements re-fired it as a FRESH
 * turn — wiping the conversation and answering the original question again — and the resume path
 * below it could never run, because `seed` was still truthy. The rule these tests pin: a seed is
 * consumed exactly once, and "no seed" only means "resume" on the very first pass.
 */
describe("nextBootstrap", () => {
  it("starts a fresh turn on a cold landing with ?q=", () => {
    expect(nextBootstrap("slow-burn sci-fi", null)).toEqual({ action: "seed", handled: "slow-burn sci-fi" });
  });

  it("consumes the seed exactly once — a StrictMode double-invoke must not restart the turn", () => {
    const first = nextBootstrap("slow-burn sci-fi", null);
    expect(first.action).toBe("seed");
    expect(nextBootstrap("slow-burn sci-fi", first.handled).action).toBe("none");
  });

  it("does nothing on the settling render after the ?q= param is cleared", () => {
    // The clear brings us back with an empty seed; resuming here is exactly what would clobber
    // the conversation the seed just started.
    expect(nextBootstrap("", "slow-burn sci-fi")).toEqual({ action: "none", handled: "" });
  });

  it("resumes the last conversation when the page loads with no seed", () => {
    expect(nextBootstrap("", null)).toEqual({ action: "resume", handled: "" });
  });

  it("resumes only once, so a StrictMode double-invoke does not double-fetch", () => {
    const first = nextBootstrap("", null);
    expect(first.action).toBe("resume");
    expect(nextBootstrap("", first.handled).action).toBe("none");
  });

  it("still starts a turn when the same phrase is searched again later", () => {
    // Consume, settle, then search the identical phrase from the header box.
    const consumed = nextBootstrap("arrival", null);
    const settled = nextBootstrap("", consumed.handled);
    expect(nextBootstrap("arrival", settled.handled)).toEqual({ action: "seed", handled: "arrival" });
  });

  it("starts a turn for a new search made while the page is already mounted", () => {
    const consumed = nextBootstrap("arrival", null);
    expect(nextBootstrap("sicario", consumed.handled)).toEqual({ action: "seed", handled: "sicario" });
  });
});

/**
 * Regression for "the media toggle duplicates my question". Every filter click re-sent the last
 * user message through `send`, and the reducer appends a user turn for any non-fresh send — so the
 * transcript filled with copies of the same sentence. A re-ask is the `moreTurn` pattern with one
 * difference: it replaces the answer instead of appending to it.
 */
describe("chatReducer — reaskTurn", () => {
  const answered = chatReducer(
    chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "something funny", fresh: true }),
      { type: "assistant", data: { text: "Here are some comedies." } },
    ),
    { type: "done", data: { results: [], reason: "3 picks ranked for you, from 40 pulled." } },
  );

  it("adds no second copy of the user's bubble", () => {
    const reasked = chatReducer(answered, { type: "reaskTurn" });
    expect(reasked.turns.filter((t) => t.role === "user")).toEqual([{ role: "user", text: "something funny" }]);
  });

  it("drops the stale answer rather than stacking a second one under the same question", () => {
    const reasked = chatReducer(answered, { type: "reaskTurn" });
    expect(reasked.turns.some((t) => t.role === "assistant")).toBe(false);

    const replied = chatReducer(reasked, { type: "assistant", data: { text: "Movies only, then." } });
    expect(replied.turns).toEqual([
      { role: "user", text: "something funny" },
      { role: "assistant", text: "Movies only, then." },
    ]);
  });

  it("clears the cards and keeps the question, so the replacement answer has somewhere to land", () => {
    const reasked = chatReducer(answered, { type: "reaskTurn" });
    expect(reasked.status).toBe("streaming");
    expect(reasked.order).toEqual([]);
    expect(reasked.appending).toBe(false);
    expect(reasked.exhausted).toBe(false);
    expect(reasked.lastQuery).toBe("something funny");
  });

  it("leaves a user-only transcript alone — nothing to replace after a failed first turn", () => {
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "something funny", fresh: true });
    expect(chatReducer(chatReducer(asked, { type: "error" }), { type: "reaskTurn" }).turns).toEqual([
      { role: "user", text: "something funny" },
    ]);
  });
});

/**
 * A caught server exception arrives as a `done` frame with no results, indistinguishable on the
 * wire from an honest "nothing matched" — which is how the empty state and the failure reason
 * ended up on screen together.
 */
describe("chatReducer — a failed done frame", () => {
  const asked = chatReducer(initialChatState, { type: "userTurn", message: "arrival but lighter", fresh: true });

  it("is an error, not an empty answer", () => {
    const state = chatReducer(asked, {
      type: "done",
      data: { results: [], reason: "search hit a snag — please try again" },
    });
    expect(state.status).toBe("error");
    expect(state.reason).toBeNull();
  });

  it("still reads a genuinely empty answer as done", () => {
    const state = chatReducer(asked, {
      type: "done",
      data: { results: [], reason: "no fresh matches — try another angle" },
    });
    expect(state.status).toBe("done");
  });
});

/**
 * Regression for unbounded payload growth: history and the no-repeat list were sent in full on
 * every request, and both grow with each refinement and each scrolled page.
 */
describe("conversation payload caps", () => {
  it("sends only the most recent turns — recency is what the next turn is about", () => {
    const turns: ChatTurn[] = Array.from({ length: MAX_HISTORY_TURNS + 8 }, (_, i) => ({
      role: i % 2 === 0 ? "user" : "assistant",
      text: `turn ${i}`,
    }));
    const capped = capHistory(turns);
    expect(capped).toHaveLength(MAX_HISTORY_TURNS);
    expect(capped.at(-1)).toEqual({ role: turns.at(-1)!.role, text: `turn ${turns.length - 1}` });
    expect(capped[0].text).toBe(`turn ${turns.length - MAX_HISTORY_TURNS}`);
  });

  it("leaves a short transcript untouched", () => {
    expect(capHistory([{ role: "user", text: "hi" }])).toEqual([{ role: "user", text: "hi" }]);
  });

  it("sends only the most recent shown ids", () => {
    const shown = Array.from({ length: MAX_SHOWN_IDS + 50 }, (_, i) => i);
    const capped = capShown(shown);
    expect(capped).toHaveLength(MAX_SHOWN_IDS);
    expect(capped.at(-1)).toBe(shown.at(-1));
    expect(capped[0]).toBe(shown.length - MAX_SHOWN_IDS);
  });

  it("caps the ids held in state too, so paging cannot grow the hook without bound", () => {
    let state = chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true });
    // Every page contributes a fresh block of tmdbIds; well past the cap after enough of them.
    for (let page = 0; page < 20; page++) {
      state = chatReducer(state, { type: "moreTurn" });
      state = chatReducer(state, {
        type: "done",
        data: {
          reason: "more",
          results: Array.from({ length: 30 }, (_, i) => card(page * 30 + i)),
        },
      });
    }
    expect(state.shown).toHaveLength(MAX_SHOWN_IDS);
    expect(state.shown.at(-1)).toBe(599);
  });
});

/**
 * Regression for cards jumping rows on every SSE event. The hook used to re-sort at render time,
 * so each scored/reranked frame could shuffle the grid under the cursor. The rule these pin:
 * candidates append in arrival order, data-only events (scored / reranked) never move a row, and
 * the order re-sorts ONLY at a phase boundary or on done — clean ids first, outside ids after,
 * each block by the personal blend.
 */
describe("chatReducer — stream-stable ordering", () => {
  // Weak arrives first, strong second: blend(weak) < blend(strong), so any live re-sort would
  // flip them immediately — which is exactly what must NOT happen between phase frames.
  const weak = card(1, { similarity: 0.1 });
  const strong = card(2, { similarity: 0.9 });
  const streamed = [weak, strong].reduce(
    (s, c) => chatReducer(s, { type: "candidate", data: c }),
    chatReducer(initialChatState, { type: "userTurn", message: "tense thriller", fresh: true }),
  );

  it("appends candidates in arrival order, even when a later card outranks an earlier one", () => {
    expect(streamed.order).toEqual(["t1", "t2"]);
  });

  it("does not reorder on scored — a data-only event updates the card in place", () => {
    const scored = chatReducer(streamed, { type: "scored", data: { titleId: "t2", predictedRating: 9.9 } });
    expect(scored.order).toEqual(["t1", "t2"]);
    expect(scored.byId.t2.predictedRating).toBe(9.9);
  });

  it("does not reorder on reranked either", () => {
    const reranked = chatReducer(streamed, { type: "reranked", data: { titleId: "t2", fit: 0.99, why: "spot on" } });
    expect(reranked.order).toEqual(["t1", "t2"]);
    expect(reranked.byId.t2.fit).toBe(0.99);
  });

  it("re-sorts by blend when a phase frame arrives", () => {
    const phased = chatReducer(streamed, { type: "phase", data: { stage: "scoring", found: 2, scored: 0 } });
    expect(phased.order).toEqual(["t2", "t1"]);
  });

  it("re-sorts on done", () => {
    const done = chatReducer(streamed, {
      type: "done",
      data: { reason: "2 picks", results: [weak, strong] },
    });
    expect(done.order).toEqual(["t2", "t1"]);
  });

  it("never interleaves outside cards among clean ones, however strong their blend", () => {
    // The outside card outranks every clean card on blend — the partition must still trail it.
    const outside = card(3, { similarity: 1, predictedRating: 10, fit: 1, outsideRequest: true });
    const withOutside = chatReducer(streamed, { type: "candidate", data: outside });

    const phased = chatReducer(withOutside, { type: "phase", data: { stage: "ranking", found: 3, scored: 3 } });
    expect(phased.order).toEqual(["t2", "t1", "t3"]);

    const done = chatReducer(withOutside, {
      type: "done",
      data: { reason: "2 picks matched everything you asked; 1 closest-but-outside shown separately.", results: [weak, strong, outside] },
    });
    expect(done.order).toEqual(["t2", "t1", "t3"]);
  });
});

/**
 * The preset mood chips belong to the cold start only: any transcript (typed OR resumed), any
 * card on screen, or an in-flight stream means the page is a conversation now, and the presets
 * must yield to the refine chips.
 */
describe("showMoodPresets", () => {
  it("shows on a genuinely fresh page", () => {
    expect(showMoodPresets(0, 0, false)).toBe(true);
  });

  it("hides the moment a transcript exists — including a resumed one", () => {
    expect(showMoodPresets(1, 0, false)).toBe(false);
  });

  it("hides while a turn is streaming", () => {
    expect(showMoodPresets(0, 0, true)).toBe(false);
  });

  it("hides when cards are on screen", () => {
    expect(showMoodPresets(0, 3, false)).toBe(false);
  });
});

/**
 * A resumed conversation is not a completed search. `resume` used to report `status: "done"` with
 * zero cards, which is the exact shape of "your search found nothing" — so reopening a thread that
 * had produced twenty picks greeted the user with the empty state, and `lastQuery` came back blank
 * so the media toggle and the retry had nothing to replay.
 */
describe("chatReducer — resume", () => {
  const turns: ChatTurn[] = [
    { role: "user", text: "slow-burn sci-fi" },
    { role: "assistant", text: "Here are a few." },
    { role: "user", text: "something quieter" },
    { role: "assistant", text: "Quieter, then." },
  ];

  it("resume_sets_status_resumed_not_done", () => {
    // Fixture variation: a transcript ending in an assistant turn (the normal case) and one
    // ending in a user turn (a thread whose last question failed mid-stream). Both are resumed
    // conversations; neither is a completed search, and the second is the one that would have
    // tempted a "last turn answered?" heuristic into reporting `done`.
    for (const t of [turns, turns.slice(0, -1)]) {
      const state = chatReducer(initialChatState, { type: "resume", turns: t, conversationId: "c1" });
      expect(state.status).toBe("resumed");
      expect(state.order).toEqual([]);
    }
  });

  it("resume_restores_lastQuery_from_the_last_user_turn", () => {
    // Fixture variation: the same two transcripts. The trailing-assistant one is the trap — the
    // last ENTRY is not the last QUESTION, so `turns.at(-1)` would replay the model's own reply.
    expect(chatReducer(initialChatState, { type: "resume", turns, conversationId: "c1" }).lastQuery)
      .toBe("something quieter");
    expect(chatReducer(initialChatState, { type: "resume", turns: turns.slice(0, -1), conversationId: "c1" }).lastQuery)
      .toBe("something quieter");
    // And an assistant-only transcript has no question to replay — better empty than the reply.
    expect(lastUserQuery([{ role: "assistant", text: "hello" }])).toBe("");
  });

  it("keeps the shown ids when the server sends them, so a resumed thread still never repeats", () => {
    // Fixture variation: `shown` provided, and omitted. Omitted must not crash or invent ids —
    // a stored conversation from before cards were persisted has no id list to give back.
    expect(chatReducer(initialChatState, { type: "resume", turns, conversationId: "c1", shown: [1, 2, 3] }).shown)
      .toEqual([1, 2, 3]);
    expect(chatReducer(initialChatState, { type: "resume", turns, conversationId: "c1" }).shown).toEqual([]);
  });
});

/**
 * A failed page used to leave `appending` latched true: the next `done` then APPENDED to a grid
 * the user had just been told was broken, and the progress bar kept animating under the error
 * banner because `phase` survived too.
 */
describe("chatReducer — error", () => {
  it("an_error_action_clears_appending", () => {
    // Fixture variation: the error arrives on a PAGE (appending true) and on a fresh turn
    // (appending false). Only the page case was red; the fresh-turn case is the control that
    // proves the fix did not just hardcode `appending: false` somewhere that mattered elsewhere.
    const answered = chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true }),
      { type: "done", data: { results: [card(1)], reason: "1 pick" } },
    );

    const pageFailed = chatReducer(chatReducer(answered, { type: "moreTurn" }), { type: "error" });
    expect(pageFailed.appending).toBe(false);
    expect(pageFailed.phase).toBeNull();
    expect(pageFailed.status).toBe("error");
    // The cards that were already on screen survive — a failed page offers a retry, not a wipe.
    expect(pageFailed.order).toEqual(["t1"]);

    const turnFailed = chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true }),
      { type: "error", reason: "session" },
    );
    expect(turnFailed.appending).toBe(false);
    expect(turnFailed.errorReason).toBe("session");
  });
});

/**
 * A conflict is the server naming a collision between the question and the account's standing
 * exclusions ("you asked for horror; horror is excluded"). It is a note beside the answer, not an
 * error — and it belongs to the question that produced it.
 */
describe("chatReducer — conflict", () => {
  it("a_conflict_frame_is_stored_and_cleared_by_the_next_turn", () => {
    // Fixture variation: the next turn arrives as a FRESH one and as a follow-up in the same
    // thread. Fresh resets through initialChatState; the follow-up has to clear it explicitly,
    // and that is the branch where a stale note would have hung over an unrelated answer.
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "something scary", fresh: true });
    const conflicted = chatReducer(asked, { type: "conflict", message: "Horror is excluded on this account." });
    expect(conflicted.conflict).toBe("Horror is excluded on this account.");
    // It survives the answer it annotates.
    expect(chatReducer(conflicted, { type: "done", data: { results: [card(1)], reason: "1 pick" } }).conflict)
      .toBe("Horror is excluded on this account.");

    expect(chatReducer(conflicted, { type: "userTurn", message: "something funny", fresh: false }).conflict).toBeNull();
    expect(chatReducer(conflicted, { type: "userTurn", message: "something funny", fresh: true }).conflict).toBeNull();
  });
});

/**
 * Server order is authoritative: the server computes a blend, stores it as the card's rank and
 * sends it. Re-deriving a second blend on the client from similarity/rating/fit produced a
 * different order than the one already committed to storage, so a reload silently reshuffled
 * the same answer.
 */
describe("chatReducer — server blend", () => {
  it("cards_prefer_the_server_blend_when_present", () => {
    // Fixture variation: one card carries a server blend that CONTRADICTS the local formula, the
    // other carries none. The contradiction is the point — agreeing blends cannot tell the two
    // code paths apart, which is how a test could pass while the client still ignored the field.
    const localWinner = card(1, { similarity: 0.9, predictedRating: 9, fit: 0.9, blend: 0.1 });
    const serverWinner = card(2, { similarity: 0.1, predictedRating: 2, fit: 0.1, blend: 0.9 });
    const streamed = [localWinner, serverWinner].reduce(
      (st, c) => chatReducer(st, { type: "candidate", data: c }),
      chatReducer(initialChatState, { type: "userTurn", message: "anything", fresh: true }),
    );
    expect(chatReducer(streamed, { type: "phase", data: { stage: "ranking", found: 2, scored: 2 } }).order)
      .toEqual(["t2", "t1"]);

    // Without a server blend the local formula still decides — older servers send no field.
    const unblended = [card(3, { similarity: 0.1 }), card(4, { similarity: 0.9 })].reduce(
      (st, c) => chatReducer(st, { type: "candidate", data: c }),
      chatReducer(initialChatState, { type: "userTurn", message: "anything", fresh: true }),
    );
    expect(chatReducer(unblended, { type: "phase", data: { stage: "ranking", found: 2, scored: 2 } }).order)
      .toEqual(["t4", "t3"]);
  });
});

/**
 * The quality floor cuts cards rather than showing them, and the count of what it cut used to be
 * the only trace. `belowFloor` carries the cards themselves, for a collapsed tray under the grid —
 * they must never reach `order`, or the floor would be decorative.
 */
describe("chatReducer — belowFloor", () => {
  it("done_stores_belowFloor_cards_separately", () => {
    // Fixture variation: a done frame WITH a tray, and the same frame without one. The absent
    // case is what every server before the floor split sends, and it must clear the previous
    // turn's tray rather than leaving it under a new answer.
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true });
    const withTray = chatReducer(asked, {
      type: "done",
      data: { results: [card(1)], reason: "1 pick", droppedByFloor: 2, belowFloor: [card(8), card(9)] },
    });
    expect(withTray.order).toEqual(["t1"]);
    expect(withTray.belowFloor.map((c) => c.titleId)).toEqual(["t8", "t9"]);
    // The tray is not part of the no-repeat set: those titles were never offered.
    expect(withTray.shown).toEqual([1]);

    const reasked = chatReducer(withTray, { type: "reaskTurn" });
    expect(reasked.belowFloor).toEqual([]);
    const without = chatReducer(reasked, { type: "done", data: { results: [card(1)], reason: "1 pick" } });
    expect(without.belowFloor).toEqual([]);
  });

  it("done_exposes_droppedByFloor_and_a_new_turn_clears_it", () => {
    // Fixture variation, one field: the SAME done frame with `droppedByFloor` present and absent.
    // That single field is the whole probe — the tray's label reads the server's count when it has
    // one and falls back to counting cards when it does not, and the two branches are otherwise
    // indistinguishable from state.
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true });
    expect(asked.droppedByFloor).toBeNull();

    const counted = chatReducer(asked, {
      type: "done",
      data: { results: [card(1)], reason: "1 pick", droppedByFloor: 3 },
    });
    expect(counted.droppedByFloor).toBe(3);

    // Absent is NOT zero: an older server that never sends the field must leave the tray free to
    // count the cards it was handed, which a 0 would suppress.
    const silent = chatReducer(asked, { type: "done", data: { results: [card(1)], reason: "1 pick" } });
    expect(silent.droppedByFloor).toBeNull();

    // A genuine zero survives as a zero — the server saying "I cut nothing" is information.
    const zero = chatReducer(asked, {
      type: "done",
      data: { results: [card(1)], reason: "1 pick", droppedByFloor: 0 },
    });
    expect(zero.droppedByFloor).toBe(0);

    // Every route into a new turn clears it, or last question's count labels this question's tray.
    expect(chatReducer(counted, { type: "userTurn", message: "horror", fresh: false }).droppedByFloor).toBeNull();
    expect(chatReducer(counted, { type: "userTurn", message: "horror", fresh: true }).droppedByFloor).toBeNull();
    expect(chatReducer(counted, { type: "reaskTurn" }).droppedByFloor).toBeNull();

    // A page is not a new turn: it adds its own cut to the running total, exactly as the tray it
    // labels appends its cards.
    const paged = chatReducer(chatReducer(counted, { type: "moreTurn" }), {
      type: "done",
      data: { results: [card(2)], reason: "1 more", droppedByFloor: 2 },
    });
    expect(paged.droppedByFloor).toBe(5);
  });
});

/**
 * StrictMode mounts, unmounts and remounts every component in development. The unmount abort is
 * correct on a real unmount and fatal on the fake one — it killed the request the first mount had
 * just started, and `nextBootstrap` refuses to restart a seed it has already recorded as handled,
 * so /search sat on a spinner forever.
 */
describe("createMountGuard", () => {
  function harness() {
    const queue: (() => void)[] = [];
    let aborted = 0;
    const guard = createMountGuard(
      () => { aborted += 1; },
      (fn) => { queue.push(fn); return queue.length - 1; },
      (handle) => { queue[handle as number] = () => {}; },
    );
    return { guard, flush: () => { const pending = queue.splice(0); for (const fn of pending) fn(); }, aborted: () => aborted };
  }

  it("an aborted-then-restarted mount does not kill the stream", () => {
    // Fixture variation: the StrictMode cycle (mount → unmount → mount) against a real teardown
    // (mount → unmount). Same guard, same scheduler; only the trailing mount differs, which is
    // exactly the one bit the old unconditional cleanup could not see.
    const strict = harness();
    strict.guard.mount();
    strict.guard.unmount();
    strict.guard.mount();
    strict.flush();
    expect(strict.aborted()).toBe(0);

    const real = harness();
    real.guard.mount();
    real.guard.unmount();
    real.flush();
    expect(real.aborted()).toBe(1);
  });
});

/** Minimal card — only the fields the reducer's bookkeeping and ordering actually read. */
function card(n: number, extra: Partial<AskCard> = {}): AskCard {
  return {
    titleId: `t${n}`, mediaType: "Movie" as const, tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7, ...extra,
  };
}
