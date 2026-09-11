import { afterEach, describe, expect, it, vi } from "vitest";
import {
  askRequest,
  chatReducer,
  createMountGuard,
  failureFor,
  mintTurnId,
  resumeTarget,
  initialChatState,
  lastUserQuery,
  mediaFilterFor,
  CONVERSATION_ID_KEY,
  floorHeldBack,
  floorPartition,
  formatFloor,
  readStoredConversationId,
  storeConversationId,
  nextBootstrap,
  plural,
  showMoodPresets,
  type ChatTurn,
} from "./useAskReelChat";
import { AskStreamError, type AskCard } from "./askReelStream";

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
 * The caps are GONE, with the fields they trimmed. History and the no-repeat set no longer ride on
 * the request at all — the server derives both from rows it wrote — so what these tests pin now is
 * the opposite property: the state this hook carries is allowed to grow, because nothing sends it.
 */
describe("the no-repeat set is no longer a payload", () => {
  it("keeps every id a long thread has shown, without a cap to trim it", () => {
    // Fixture variation: the old cap was 300, so 20 pages of 30 is well past it — the previous
    // assertion here was `toHaveLength(300)` and the FIRST 300 ids were silently dropped, which is
    // how a long thread re-offered a title it had already shown. Same fixture, opposite verdict.
    let state = chatReducer(initialChatState, { type: "userTurn", message: "sci-fi", fresh: true });
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
    expect(state.shown).toHaveLength(600);
    expect(state.shown[0]).toBe(0);
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
      const state = chatReducer(initialChatState, { type: "resume", payload: { id: "c1", turns: t } });
      expect(state.status).toBe("resumed");
      expect(state.order).toEqual([]);
    }
  });

  it("resume_restores_lastQuery_from_the_last_user_turn", () => {
    // Fixture variation: the same two transcripts. The trailing-assistant one is the trap — the
    // last ENTRY is not the last QUESTION, so `turns.at(-1)` would replay the model's own reply.
    expect(chatReducer(initialChatState, { type: "resume", payload: { id: "c1", turns } }).lastQuery)
      .toBe("something quieter");
    expect(chatReducer(initialChatState, { type: "resume", payload: { id: "c1", turns: turns.slice(0, -1) } }).lastQuery)
      .toBe("something quieter");
    // And an assistant-only transcript has no question to replay — better empty than the reply.
    expect(lastUserQuery([{ role: "assistant", text: "hello" }])).toBe("");
  });

  it("keeps the shown ids when the server sends them, so a resumed thread still never repeats", () => {
    // Fixture variation: `shown` provided, and omitted. Omitted must not crash or invent ids —
    // a stored conversation from before cards were persisted has no id list to give back.
    expect(chatReducer(initialChatState, { type: "resume", payload: { id: "c1", turns, shown: [1, 2, 3] } }).shown)
      .toEqual([1, 2, 3]);
    expect(chatReducer(initialChatState, { type: "resume", payload: { id: "c1", turns } }).shown).toEqual([]);
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

/**
 * Asking a question used to blank the answer already on screen, immediately, for the fifteen-odd
 * seconds the next turn takes. Two failures fell out of that: the page was empty while it worked,
 * and pressing Stop inside that window destroyed an answer the user was still reading in exchange
 * for one that never arrived. The previous answer is now held until the first `candidate` of the
 * new turn — the first moment there is anything to put in its place.
 */
describe("chatReducer — holding the previous answer", () => {
  /** A settled two-card answer, the state every hold starts from. */
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "tense heist movie", fresh: true }),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
  );

  it("a follow-up keeps the previous cards until its own first card arrives", () => {
    const asking = chatReducer(answered, { type: "userTurn", message: "something quieter", fresh: false });
    expect(asking.status).toBe("streaming");
    expect(asking.holdingPrevious).toBe(true);
    expect(asking.order).toEqual(["t1", "t2"]);
    expect(asking.arrived).toBe(0);

    // A phase frame is normally the sanctioned re-sort point; a held answer is finished work and
    // must not shuffle in response to the NEW turn's progress.
    const phased = chatReducer(asking, { type: "phase", data: { stage: "scoring", found: 9, scored: 0 } });
    expect(phased.order).toEqual(["t1", "t2"]);
    expect(phased.holdingPrevious).toBe(true);

    // The swap: the first candidate REPLACES the held answer rather than joining it.
    const swapped = chatReducer(phased, { type: "candidate", data: card(7) });
    expect(swapped.holdingPrevious).toBe(false);
    expect(swapped.order).toEqual(["t7"]);
    expect(swapped.arrived).toBe(1);

    // …and from there it is an ordinary turn again: the second card appends.
    expect(chatReducer(swapped, { type: "candidate", data: card(8) }).order).toEqual(["t7", "t8"]);
    expect(chatReducer(swapped, { type: "candidate", data: card(8) }).arrived).toBe(2);
  });

  it("holds on a re-ask but never on a fresh turn or a page", () => {
    // Fixture variation over the three other routes into a stream, because each one wants a
    // different answer and only the follow-up above shares the rule.
    // A re-ask (the media toggle, the retry) replaces the same question's answer — hold.
    const reasked = chatReducer(answered, { type: "reaskTurn" });
    expect(reasked.holdingPrevious).toBe(true);
    expect(reasked.order).toEqual(["t1", "t2"]);
    expect(chatReducer(reasked, { type: "candidate", data: card(7) }).order).toEqual(["t7"]);

    // A FRESH turn is a new conversation and takes the transcript with it, so there is no previous
    // answer left to hold — cards under a blank transcript answer a question no longer on screen.
    const fresh = chatReducer(answered, { type: "userTurn", message: "anything", fresh: true });
    expect(fresh.holdingPrevious).toBe(false);
    expect(fresh.order).toEqual([]);

    // A page EXTENDS what is on screen, so its first candidate must append. Holding here would
    // have made "Show me more" delete the answer it was adding to.
    const paging = chatReducer(answered, { type: "moreTurn" });
    expect(paging.holdingPrevious).toBe(false);
    expect(chatReducer(paging, { type: "candidate", data: card(7) }).order).toEqual(["t1", "t2", "t7"]);

    // A follow-up asked with nothing on screen holds nothing — there is no previous answer.
    const cold = chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "first", fresh: true }),
      { type: "userTurn", message: "second", fresh: false },
    );
    expect(cold.holdingPrevious).toBe(false);
  });

  it("a done frame ends the hold even when the new answer is empty", () => {
    // The trap: a turn that returns nothing must not leave the previous answer up under "No
    // matches for that one", which is a verdict on THIS question.
    const asking = chatReducer(answered, { type: "userTurn", message: "something quieter", fresh: false });
    const empty = chatReducer(asking, { type: "done", data: { results: [], reason: "no fresh matches" } });
    expect(empty.holdingPrevious).toBe(false);
    expect(empty.order).toEqual([]);
    expect(empty.status).toBe("done");
  });
});

/**
 * Pressing Stop left the page blank: `status: "idle"` over a transcript matches no render branch
 * at all, so there were no cards, no message, no retry and no way back to the answer that asking
 * the question had already cleared. A stopped turn is now its own state, and it keeps everything.
 */
describe("chatReducer — stop", () => {
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "tense heist movie", fresh: true }),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
  );

  it("stopping before the first card keeps the previous answer and says so", () => {
    // Probe one: Stop at 2.5s, before any candidate — the reported repro, and the case that used
    // to produce 700px of void.
    const asking = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    const stopped = chatReducer(chatReducer(asking, { type: "phase", data: { stage: "searching", found: 0, scored: 0 } }), { type: "stop" });

    expect(stopped.status).toBe("stopped");
    expect(stopped.arrived).toBe(0);
    // The answer the user was reading is still there — stopping a turn must not cost them one.
    expect(stopped.order).toEqual(["t1", "t2"]);
    expect(stopped.holdingPrevious).toBe(true);
    expect(stopped.phase).toBeNull();
    expect(stopped.appending).toBe(false);
    // Something to re-run, which is what "Ask again" needs.
    expect(stopped.lastQuery).toBe("slow burn detective series");
  });

  it("stopping after three cards keeps those three and counts them", () => {
    // Probe two: the same action taken later in the same turn. `arrived` is the whole difference,
    // and it is what the copy quotes — "3 picks arrived before you stopped" is only true if the
    // count comes from the turn rather than from what happens to be on screen.
    let state = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    for (const n of [7, 8, 9]) state = chatReducer(state, { type: "candidate", data: card(n) });
    const stopped = chatReducer(state, { type: "stop" });

    expect(stopped.status).toBe("stopped");
    expect(stopped.arrived).toBe(3);
    expect(stopped.order).toEqual(["t7", "t8", "t9"]);
    expect(stopped.holdingPrevious).toBe(false);
  });

  it("counts only the stopped PAGE's cards, not the answer it was extending", () => {
    // A stopped page is the third shape: the previous cards are legitimately still on screen and
    // are not what arrived. `order.length` would have said five.
    let state = chatReducer(answered, { type: "moreTurn" });
    for (const n of [7, 8, 9]) state = chatReducer(state, { type: "candidate", data: card(n) });
    const stopped = chatReducer(state, { type: "stop" });
    expect(stopped.arrived).toBe(3);
    expect(stopped.order).toEqual(["t1", "t2", "t7", "t8", "t9"]);
  });
});

/**
 * The screen reader announced "1 picks ready." over a one-result turn while the server's own
 * sentence, rendered two elements below it, was correctly singular. Four count-copy sites on the
 * page, only some of them agreeing.
 */
describe("plural", () => {
  it("is singular at exactly one and plural everywhere else", () => {
    // Fixture variation across the boundary AND the degenerate case: zero is plural in English
    // ("no picks ready"), which a naive `n > 1` check gets wrong in the one direction a spot-check
    // of 1 and 2 cannot see.
    expect(plural(1, "pick")).toBe("pick");
    expect(plural(0, "pick")).toBe("picks");
    expect(plural(2, "pick")).toBe("picks");
    expect(plural(17, "title")).toBe("titles");
  });
});

/**
 * The under-floor tray's label. Two independent defects lived in one sentence: the count came from
 * a scalar beside the cards rather than from the cards ("Show the 2 under your 6 bar" over a tray
 * holding one title), and the number was formatted with the ambient locale and no minimum fraction
 * digits, so it read "6" beside the server's own "6.0 bar" — and "6,5" beside "6.5" on a
 * comma-decimal machine.
 */
describe("floorHeldBack", () => {
  it("counts the tray's own contents, and the server's scalar only when the tray is empty", () => {
    // Fixture variation on the ONE field that differs between the two sources. The disagreeing
    // case is the whole point: while they agree — which is what live turns did — a wrong
    // implementation is indistinguishable from a right one.
    expect(floorHeldBack(1, 2)).toBe(1);
    expect(floorHeldBack(3, 3)).toBe(3);
    // Nothing sent down, but the server counted a cut: the scalar is the honest answer there.
    expect(floorHeldBack(0, 3)).toBe(3);
    // Silence is not a count, and neither is a zero. Both leave the tray closed.
    expect(floorHeldBack(0, null)).toBe(0);
    expect(floorHeldBack(0, 0)).toBe(0);
    // Cards but no scalar — an older server that sends the tray and not the count.
    expect(floorHeldBack(2, null)).toBe(2);
  });
});

describe("formatFloor", () => {
  it("always renders one decimal, matching the server's own sentence", () => {
    // Fixture variation across the three shapes a saved bar takes: a whole number (the one that
    // printed "6" beside "6.0"), one that already has a decimal, and one that needs rounding.
    expect(formatFloor(6)).toBe("6.0");
    expect(formatFloor(6.5)).toBe("6.5");
    expect(formatFloor(7.25)).toBe("7.3");
    // No saved bar is not a bar of zero — the tray falls back to wording rather than "0.0".
    expect(formatFloor(null)).toBeNull();
    expect(formatFloor(undefined)).toBeNull();
  });
});

/**
 * The client persisted no conversation id at all, so a reload could only ask for the ACCOUNT's
 * newest thread — two tabs, or a phone and a laptop, silently converged onto whichever wrote last
 * and the thread you were reading vanished from the UI. This is the plumbing for the per-tab
 * resume: the id is remembered, and every failure to remember it is swallowed, because a
 * convenience must never be able to take a turn down with it.
 */
describe("conversation id persistence", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function fakeStorage(initial: Record<string, string> = {}) {
    const map = new Map(Object.entries(initial));
    return {
      getItem: (k: string) => map.get(k) ?? null,
      setItem: (k: string, v: string) => void map.set(k, v),
      removeItem: (k: string) => void map.delete(k),
      read: () => map.get(CONVERSATION_ID_KEY) ?? null,
    };
  }

  it("round-trips an id and clears it on null", () => {
    const store = fakeStorage();
    vi.stubGlobal("localStorage", store);

    storeConversationId("c1e0e3a2-0000-0000-0000-000000000001");
    expect(store.read()).toBe("c1e0e3a2-0000-0000-0000-000000000001");
    expect(readStoredConversationId()).toBe("c1e0e3a2-0000-0000-0000-000000000001");

    storeConversationId(null);
    expect(store.read()).toBeNull();
    expect(readStoredConversationId()).toBeNull();
  });

  it("survives every way storage can fail, and reads an empty value as nothing", () => {
    // Fixture variation across the three failures that are real in a browser and one that is real
    // in a test runner: no localStorage at all (this very suite runs in node), a setItem that
    // throws (a full quota, or Safari's private mode), a getItem that throws (a blocked origin),
    // and a stored empty string (a previous write that half-landed).
    vi.unstubAllGlobals();
    expect(() => storeConversationId("c1")).not.toThrow();
    expect(readStoredConversationId()).toBeNull();

    vi.stubGlobal("localStorage", {
      getItem: () => { throw new Error("blocked"); },
      setItem: () => { throw new Error("quota"); },
      removeItem: () => { throw new Error("quota"); },
    });
    expect(() => storeConversationId("c1")).not.toThrow();
    expect(readStoredConversationId()).toBeNull();

    vi.stubGlobal("localStorage", fakeStorage({ [CONVERSATION_ID_KEY]: "" }));
    expect(readStoredConversationId()).toBeNull();
  });
});

/** Minimal card — only the fields the reducer's bookkeeping and ordering actually read. */
function card(n: number, extra: Partial<AskCard> = {}): AskCard {
  return {
    titleId: `t${n}`, mediaType: "Movie" as const, tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7, ...extra,
  };
}

/**
 * The request, and the four answers the server can give that are not a stream.
 *
 * `run()` cannot be tested here — vitest runs in node with no renderer — so the two decisions it
 * makes are pure functions it calls: what goes on the wire, and what a failure MEANS. Both used to
 * be inline in the callback, which is why "409" and "the search crashed" were the same screen.
 */
describe("askRequest", () => {
  it("sends_the_turn_id_and_nothing_the_server_already_knows", () => {
    // Fixture variation: a follow-up inside a thread, and a fresh start. The fresh one differs in
    // exactly one field — a null conversation id — which is what mints a new thread rather than
    // forking an existing one.
    const followUp = askRequest("something lighter", "turn-1", "conv-9", ["movie"]);
    expect(followUp).toEqual({
      query: "something lighter", clientTurnId: "turn-1", conversationId: "conv-9", mediaTypes: ["movie"],
    });

    const fresh = askRequest("something lighter", "turn-2", null, []);
    expect(fresh.conversationId).toBeNull();

    // The three fields that were REMOVED, not renamed. A client still sending them is sending a
    // transcript the server will not read — and the server now rejects the request outright.
    for (const request of [followUp, fresh]) {
      expect(Object.keys(request).sort()).toEqual(["clientTurnId", "conversationId", "mediaTypes", "query"]);
    }
  });

  it("mints_a_different_turn_id_every_time", () => {
    // Two sends of the SAME question are two exchanges and must be billed as two; only a retry of
    // one exchange reuses an id, and that path passes the id in rather than minting.
    expect(mintTurnId()).not.toBe(mintTurnId());
    expect(mintTurnId().length).toBeGreaterThan(8);
  });
});

describe("failureFor", () => {
  it("tells_the_four_server_answers_apart", () => {
    // Fixture variation: each status with its code, then each status with the code MISSING (a
    // proxy's own 404, a gateway's 409). The code is preferred where present because the status is
    // shared; both must land on the same action, or a client behind a proxy gets a different UI.
    expect(failureFor(new AskStreamError(409, "turn_in_progress"))).toEqual({ type: "turnInProgress" });
    expect(failureFor(new AskStreamError(409, null))).toEqual({ type: "turnInProgress" });
    expect(failureFor(new AskStreamError(404, "conversation_not_found"))).toEqual({ type: "conversationGone" });
    expect(failureFor(new AskStreamError(404, null))).toEqual({ type: "conversationGone" });
    expect(failureFor(new AskStreamError(401))).toEqual({ type: "error", reason: "session" });
    expect(failureFor(new AskStreamError(400, "query_too_long"))).toEqual({ type: "error", reason: "request" });
    // Anything else, and anything that is not an AskStreamError at all (a network drop), is the
    // unclassified failure — the only one that may say "search hit a snag".
    expect(failureFor(new AskStreamError(500))).toEqual({ type: "error", reason: null });
    expect(failureFor(new TypeError("Failed to fetch"))).toEqual({ type: "error", reason: null });
  });
});

describe("chatReducer — 409 turn_in_progress", () => {
  /** A settled answer with three cards on screen — what a 409 must not cost the user. */
  function settled() {
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "a tense heist", fresh: true });
    return chatReducer(asked, {
      type: "done",
      data: { reason: "3 picks ranked for you.", results: [card(1), card(2), card(3)] },
    });
  }

  it("keeps_the_grid_and_is_not_an_error", () => {
    const blocked = chatReducer(settled(), { type: "turnInProgress" });
    expect(blocked.status).toBe("blocked");
    expect(blocked.order).toHaveLength(3);
    expect(blocked.errorReason).toBeNull();
    expect(blocked.reason).toBe("3 picks ranked for you.");
    expect(blocked.appending).toBe(false);
    expect(blocked.phase).toBeNull();
  });

  it("keeps_a_HELD_previous_answer_too", () => {
    // Fixture variation: the 409 arrives on the SECOND question, while the first answer is still
    // being held on screen. That answer is now the only one there is — a 409 means the new turn
    // never ran — so clearing it would blank the page for a request that failed to even start.
    const second = chatReducer(settled(), { type: "userTurn", message: "something lighter", fresh: false });
    expect(second.holdingPrevious).toBe(true);
    const blocked = chatReducer(second, { type: "turnInProgress" });
    expect(blocked.holdingPrevious).toBe(true);
    expect(blocked.order).toHaveLength(3);
  });
});

describe("chatReducer — 404 conversation_not_found", () => {
  it("forgets_the_thread_so_the_next_turn_starts_a_new_one", () => {
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "a tense heist", fresh: true });
    const inThread = chatReducer(asked, { type: "conversation", data: { id: "conv-9", turnId: "t-1", sequence: 1 } });
    expect(inThread.conversationId).toBe("conv-9");

    const gone = chatReducer(inThread, { type: "conversationGone" });
    expect(gone.conversationId).toBeNull();
    expect(gone.status).toBe("error");
    expect(gone.errorReason).toBe("conversation");
    // The transcript stays: it is still what the reader was reading, and the page explains rather
    // than silently emptying the screen.
    expect(gone.turns).toHaveLength(1);
  });
});

describe("resumeTarget", () => {
  it("prefers_this_tabs_own_thread_over_the_accounts_newest", () => {
    // The multi-device steal, in one line. Fixture variation: a stored id, no stored id, and the
    // empty string a cleared-but-present key leaves behind — the last one used to read as truthy
    // in the page and send a request for the conversation with no id.
    expect(resumeTarget("conv-9")).toBe("conv-9");
    expect(resumeTarget(null)).toBe("latest");
    expect(resumeTarget("")).toBe("latest");
  });
});

/**
 * Resume, with the answer in it.
 *
 * Until the turn record existed, `/conversations/latest` returned turns and nothing else, so the
 * ONLY branch a resume could reach was "transcript only — this thread predates saved picks" — copy
 * that states a cause which was never the real one, seen on a thread seventeen seconds old. These
 * pin the other half: the cards come back, in the server's order, with what changed while the user
 * was away.
 */
describe("chatReducer — resume with stored cards", () => {
  /** The server's payload, verbatim. `cards` order is the ANSWER's order and is never re-derived. */
  const payload = {
    id: "conv-9",
    title: "a tense heist",
    lastCardsAt: "2026-09-11T09:00:00Z",
    // Deliberately NOT blend order: card 1 outranks the rest and arrives last. A client that
    // re-sorts — as it does for a live turn — would put it first and silently show a different
    // answer from the one the server stored as Rank.
    cards: [card(3, { blend: 0.2 }), card(2, { blend: 0.4 }), card(1, { blend: 0.9 })],
    turns: [
      { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "Here are three.", kind: "ask", status: "Completed" },
    ],
    reason: "3 picks ranked for you, from 40 pulled.",
    removed: 2,
    lastQuery: "a tense heist",
    mediaTypes: ["movie"],
    shown: [1, 2, 3, 77],
  };

  it("takes_the_servers_card_order_verbatim", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload });
    expect(state.order).toEqual(["t3", "t2", "t1"]);
    expect(state.byId.t1.blend).toBe(0.9);
  });

  it("is_resumed_not_done_even_with_a_full_grid", () => {
    // The distinction the empty-state branch turns on. `done` over a restored answer is what put
    // "No matches for that one" under a question that had matched plenty.
    const state = chatReducer(initialChatState, { type: "resume", payload });
    expect(state.status).toBe("resumed");
    expect(state.order).toHaveLength(3);
  });

  it("carries_the_reason_the_removals_and_the_whole_threads_shown_set", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload });
    expect(state.reason).toBe("3 picks ranked for you, from 40 pulled.");
    expect(state.removed).toBe(2);
    expect(state.lastCardsAt).toBe("2026-09-11T09:00:00Z");
    // The whole conversation's no-repeat set, not just this block's — 77 came from an earlier turn.
    expect(state.shown).toEqual([1, 2, 3, 77]);
    expect(state.lastQuery).toBe("a tense heist");
    expect(state.conversationId).toBe("conv-9");
  });

  it("resumes_a_transcript_only_thread_with_no_cards_at_all", () => {
    // Fixture variation: the same payload with `cards` empty and `lastCardsAt` null — a genuinely
    // old thread. This is the ONLY shape that may render the "predates saved picks" copy, and it
    // must still restore the transcript and the last query so the thread can be asked again.
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...payload, cards: [], lastCardsAt: null, reason: null, removed: 0, shown: [] },
    });
    expect(state.status).toBe("resumed");
    expect(state.order).toEqual([]);
    expect(state.lastCardsAt).toBeNull();
    expect(state.turns).toHaveLength(2);
    expect(state.lastQuery).toBe("a tense heist");
  });

  it("never_renders_a_turn_the_server_has_not_written_text_for", () => {
    // A turn in flight is a row with no text yet, and a failed one keeps its empty text forever.
    // Rendering them puts blank bubbles in the transcript — an assistant that said nothing.
    const turns = [
      ...payload.turns,
      { sequence: 2, role: "user", text: "something lighter", kind: "ask", status: "Completed" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Pending" },
    ];
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...payload, turns, lastQuery: "something lighter" },
    });
    expect(state.turns.map((t) => t.text)).toEqual(["a tense heist", "Here are three.", "something lighter"]);
    // …and the last question is still the one to replay, even though its answer never landed.
    expect(state.lastQuery).toBe("something lighter");

    // Fixture variation: the same rows with the server's own `lastQuery` withheld (what
    // `/conversations/latest` returned before Phase 1). The fallback reads the transcript — and it
    // has to read the FILTERED one, or an empty assistant row would be the last thing it saw.
    const withoutServerQuery = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...payload, turns, lastQuery: undefined },
    });
    expect(withoutServerQuery.lastQuery).toBe("something lighter");
  });
});

describe("mediaFilterFor", () => {
  it("maps_the_wire_tokens_back_to_the_browse_toggle", () => {
    // The toggle and the answer on screen have to agree: a resumed grid that ran under "Movies"
    // beside a toggle reading "All" is a control that lies about what produced what is under it.
    expect(mediaFilterFor(["movie"])).toBe("movie");
    expect(mediaFilterFor(["tv"])).toBe("show");
    expect(mediaFilterFor([])).toBe("all");
    expect(mediaFilterFor(undefined)).toBe("all");
    // Both, or anything unrecognised, is no constraint — never a guess.
    expect(mediaFilterFor(["movie", "tv"])).toBe("all");
    expect(mediaFilterFor(["film"])).toBe("all");
  });
});

/**
 * The floor on a STOPPED page (R2's re-created F3-02).
 *
 * The floor is the server's job and runs when the turn completes. Stop a page ten seconds in and
 * `done` never arrives — so the cards that already streamed keep whatever score they had, and nine
 * sub-floor picks sat in the recommended grid with a tray directly beneath them saying seventeen
 * picks were held back for being under the 6.0 bar. Three numbers on one screen that cannot all be
 * true. The client cannot rank, but it CAN apply the account's own bar to what arrived.
 */
describe("floorPartition", () => {
  const bar = 6;

  it("moves_sub_floor_cards_out_of_the_grid_and_into_the_tray", () => {
    const cards = [card(1, { predictedRating: 6.4 }), card(2, { predictedRating: 5.2 }), card(3, { predictedRating: 6 })];
    const { kept, held } = floorPartition(cards, bar);
    expect(kept.map((c) => c.titleId)).toEqual(["t1", "t3"]);
    expect(held.map((c) => c.titleId)).toEqual(["t2"]);
  });

  it("judges_the_number_the_badge_prints_not_the_one_behind_it", () => {
    // Fixture variation: one field, three values astride the bar — 5.96, 5.94 and 6.04. The badge
    // rounds to one decimal, so 5.96 PRINTS as "6.0" and holding it back puts a card in the "under
    // your 6.0 bar" tray badged 6.0. The whole class of contradiction R2 found is a comparison
    // made against a precision the screen does not show.
    expect(floorPartition([card(1, { predictedRating: 5.96 })], bar).kept).toHaveLength(1);
    expect(floorPartition([card(1, { predictedRating: 5.94 })], bar).held).toHaveLength(1);
    expect(floorPartition([card(1, { predictedRating: 6.04 })], bar).kept).toHaveLength(1);
  });

  it("never_holds_back_a_card_it_cannot_judge", () => {
    // Fixture variation: a null rating (streamed before the scoring pass reached it) and no bar at
    // all (an account that has set none). Neither is evidence a card is under anything, and
    // hiding a card on an absence would be the same lie in the other direction.
    expect(floorPartition([card(1, { predictedRating: null })], bar).kept).toHaveLength(1);
    expect(floorPartition([card(1, { predictedRating: 2 })], null).held).toEqual([]);
    expect(floorPartition([card(1, { predictedRating: 2 })], undefined).kept).toHaveLength(1);
  });

  it("keeps_the_order_it_was_given_within_each_half", () => {
    // Stop does not rank. Whatever order the cards arrived in is the only order there is, and
    // re-sorting here would invent a ranking the server never produced.
    const cards = [card(3, { predictedRating: 9 }), card(1, { predictedRating: 7 }), card(2, { predictedRating: 8 })];
    expect(floorPartition(cards, bar).kept.map((c) => c.titleId)).toEqual(["t3", "t1", "t2"]);
  });
});
