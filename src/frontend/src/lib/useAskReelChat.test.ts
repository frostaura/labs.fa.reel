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
  FRESH_CONVERSATION,
  NO_RESUME,
  canPage,
  floorHeldBack,
  excludedTerms,
  footnote,
  listPhrase,
  floorPartition,
  formatFloor,
  readStoredConversationId,
  storeConversationId,
  nextBootstrap,
  plural,
  showMoodPresets,
  showsFootnote,
  recentListing,
  lastTurnOutcome,
  answeredQuestion,
  failedTurnMessage,
  RECENT_LIMIT,
  OLDER_LIMIT,
  transcriptOf,
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

  it("takes the server's order on done, whatever the client had settled on", () => {
    // Was "re-sorts on done", and that was SB-01: the client's blend order is a stand-in for a
    // server order that has not arrived yet, and `done` IS that order arriving. `results` puts the
    // weak card first — against the client's own settled order — and the client yields to it,
    // which is what makes a reload of this turn render the identical array.
    const done = chatReducer(streamed, {
      type: "done",
      data: { reason: "2 picks", results: [weak, strong] },
    });
    expect(done.order).toEqual(["t1", "t2"]);
  });

  it("never interleaves outside cards among clean ones, however strong their blend", () => {
    // The outside card outranks every clean card on blend — the partition must still trail it.
    const outside = card(3, { similarity: 1, predictedRating: 10, fit: 1, outsideRequest: true });
    const withOutside = chatReducer(streamed, { type: "candidate", data: outside });

    const phased = chatReducer(withOutside, { type: "phase", data: { stage: "ranking", found: 3, scored: 3 } });
    expect(phased.order).toEqual(["t2", "t1", "t3"]);

    // On `done` the partition is the SERVER's to make, and it made it: the outside card trails in
    // `results`. The client takes the array as sent rather than re-deriving a second partition —
    // and nothing on screen depends on it either way, because the page renders the two channels
    // from `outsideRequest` and a filter preserves relative order whatever the array looks like.
    const done = chatReducer(withOutside, {
      type: "done",
      data: { reason: "2 picks matched everything you asked; 1 closest-but-outside shown separately.", results: [weak, strong, outside] },
    });
    expect(done.order).toEqual(["t1", "t2", "t3"]);
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
    const conflicted = chatReducer(asked, { type: "conflict", data: { message: "Horror is excluded on this account." } });
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

/**
 * R1-04 — "Show me more" over an answer that is not this question's.
 *
 * The reported sequence: get a 37-card answer, ask something new, press Stop before the first
 * card arrives. The previous answer is correctly held and dimmed — and the page control rendered
 * underneath it, outside the stopped branch, with no idea the cards above belonged to an earlier
 * question. Pressing it replayed the STOPPED question, dropped the dim, and the 37 held cards
 * silently stopped being "the previous answer" and became page one of a different question.
 *
 * The rule this pins: a page extends a COMPLETED turn. Nothing else is pageable — not a turn in
 * flight, not one the user abandoned, not a held previous answer, not a failure, not a 409.
 */
describe("canPage", () => {
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "a tense heist movie", fresh: true }),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
  );

  it("pages_a_completed_turn_and_a_resumed_one", () => {
    // The two states where what is on screen is a finished answer to the last question asked: a
    // turn that completed this visit, and one the server handed back whole.
    expect(canPage("done", false, false, 2)).toBe(true);
    expect(canPage("resumed", false, false, 2)).toBe(true);
  });

  it("never_pages_a_held_previous_answer", () => {
    // The R1-04 repro, as state: stopped, holding, cards on screen, a `lastQuery` pointing at the
    // question that was never answered. Every one of those three states used to render the button.
    const asking = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    const stopped = chatReducer(asking, { type: "stop" });
    expect(stopped.holdingPrevious).toBe(true);
    expect(stopped.order).toHaveLength(2);
    expect(canPage(stopped.status, stopped.holdingPrevious, stopped.exhausted, stopped.order.length)).toBe(false);
    // …and the hold alone is disqualifying, whatever the status beside it says.
    expect(canPage("streaming", true, false, 2)).toBe(false);
    expect(canPage("blocked", true, false, 2)).toBe(false);
  });

  it("never_pages_a_turn_that_did_not_finish", () => {
    // Fixture variation on the status alone, cards and hold held constant: a stopped page's cards
    // are real but unranked and unfloored, a failure has an "Ask again" of its own, and a 409
    // means the answer is already on its way. None of the three is a completed turn.
    expect(canPage("stopped", false, false, 2)).toBe(false);
    expect(canPage("error", false, false, 2)).toBe(false);
    expect(canPage("blocked", false, false, 2)).toBe(false);
    expect(canPage("streaming", false, false, 2)).toBe(false);
    expect(canPage("idle", false, false, 2)).toBe(false);
  });

  it("stops_asking_once_the_server_says_there_is_no_more", () => {
    expect(canPage("done", false, true, 2)).toBe(false);
    // Nothing on screen is nothing to extend.
    expect(canPage("done", false, false, 0)).toBe(false);
  });
});

/**
 * The other half of R1-04, in the reducer: `moreTurn` used to clear `holdingPrevious`
 * unconditionally, which is the mechanism by which the held cards changed ownership. The guard
 * above means a page can no longer start from a hold — and if one ever did, the previous answer
 * must stay labelled as previous rather than being silently adopted by the new question.
 */
describe("chatReducer — moreTurn never adopts a held answer", () => {
  it("leaves_the_hold_exactly_as_it_found_it", () => {
    const answered = chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "a tense heist movie", fresh: true }),
      { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
    );
    // Fixture variation: the same action from the two states that differ only in the hold.
    const held = chatReducer(answered, { type: "userTurn", message: "slow burn detective", fresh: false });
    expect(chatReducer(held, { type: "moreTurn" }).holdingPrevious).toBe(true);
    expect(chatReducer(answered, { type: "moreTurn" }).holdingPrevious).toBe(false);
  });
});

/**
 * R1-03 — the `gained === 0` rule, and the three faces one gesture wore.
 *
 * Paging used to decide two things from one number: `gained`, the count of ids the page added to
 * `order` that were not already in it. A page's `done.results` is always a subset of what already
 * streamed as `candidate` frames, so `gained` was usually 0 — the page's `reason` was discarded
 * and `exhausted` latched, hiding "Show me more" after a page that had just added thirteen cards.
 * It was not ALWAYS 0, which is why it survived: `candidate` early-returns on a title already in
 * `byId`, and `byId` still holds every card of every earlier turn INCLUDING its below-floor tray,
 * so a page overlapping the previous tray reached `done` with ten genuinely new ids and behaved
 * correctly. The same gesture, opposite behaviour, decided by tray overlap the user cannot see.
 *
 * The rule now: a page's own `done` is authoritative for that page. Exhaustion is what the SERVER
 * says — a page that returned no cards at all — and the accounting line is always the latest page
 * that actually added something, over a count of what is on screen.
 */
describe("chatReducer — paging is the server's account, not an id diff", () => {
  /** A settled two-card answer with its own footnote — where every page below starts. */
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "a tense heist movie", fresh: true }),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks ranked for you, from 40 pulled" } },
  );

  it("a_page_that_adds_new_cards_takes_over_the_accounting", () => {
    // Face one: the ordinary page. Thirteen new ids, none of them on screen before.
    const paging = chatReducer(answered, { type: "moreTurn" });
    const more = [card(3), card(4), card(5)];
    const done = chatReducer(paging, {
      type: "done",
      data: { results: more, reason: "3 picks ranked for you, from 65 pulled", kept: 3 },
    });
    expect(done.order).toHaveLength(5);
    expect(done.exhausted).toBe(false);
    expect(done.reason).toBe("3 picks ranked for you, from 65 pulled");
    expect(done.paged).toBe(true);
  });

  it("a_page_whose_results_all_arrived_as_candidates_is_still_a_page", () => {
    // Face two, and the one that broke: every id in `done.results` already reached `order` as a
    // `candidate`, so `gained` was 0 — the page's sentence was thrown away and paging latched shut
    // over a grid that had just grown. `gained` is no longer consulted: the page returned cards,
    // so it is a page.
    let state = chatReducer(answered, { type: "moreTurn" });
    for (const n of [3, 4, 5]) state = chatReducer(state, { type: "candidate", data: card(n) });
    expect(state.order).toHaveLength(5);
    const done = chatReducer(state, {
      type: "done",
      data: { results: [card(3), card(4), card(5)], reason: "3 picks ranked for you, from 65 pulled", kept: 3 },
    });
    expect(done.order).toHaveLength(5);
    expect(done.exhausted).toBe(false);
    expect(done.reason).toBe("3 picks ranked for you, from 65 pulled");
    expect(done.paged).toBe(true);
  });

  it("a_page_the_server_returned_nothing_for_is_the_end_and_says_nothing_new", () => {
    // Face three: the honest exhaustion. `kept === 0` and no results — the server's own statement
    // that there is nothing left under this question. It latches, and it must NOT re-describe the
    // answer: `AskReelReason.Empty` ("no fresh matches — try another angle") is true of the PAGE
    // and false of the five cards on screen, so the previous accounting stands.
    const paging = chatReducer(answered, { type: "moreTurn" });
    const done = chatReducer(paging, {
      type: "done",
      data: { results: [], reason: "no fresh matches — try another angle", kept: 0 },
    });
    expect(done.exhausted).toBe(true);
    expect(done.reason).toBe("2 picks ranked for you, from 40 pulled");
    expect(done.paged).toBe(false);
    expect(done.order).toHaveLength(2);
  });

  it("a_fresh_answer_replaces_the_accounting_and_reopens_paging", () => {
    // The latch is per-answer, not per-thread: the next question starts over.
    const paging = chatReducer(answered, { type: "moreTurn" });
    const exhaustedState = chatReducer(paging, { type: "done", data: { results: [], reason: null, kept: 0 } });
    expect(exhaustedState.exhausted).toBe(true);
    const asking = chatReducer(exhaustedState, { type: "userTurn", message: "something quieter", fresh: false });
    expect(asking.exhausted).toBe(false);
    expect(asking.paged).toBe(false);
    const next = chatReducer(asking, { type: "done", data: { results: [card(9)], reason: "1 pick ranked for you, from 12 pulled" } });
    expect(next.exhausted).toBe(false);
    expect(next.paged).toBe(false);
    expect(next.reason).toBe("1 pick ranked for you, from 12 pulled");
  });
});

/**
 * The footnote. On a paged answer the server's sentence describes the PAGE — "13 picks ranked for
 * you, from 65 pulled" — and R1-03 watched it sit under a grid of 37, accounting for whichever
 * turn happened to win the id diff. Neither the first page's sentence nor the last one's is true
 * of the accumulated set, so the line says both: what is on screen, and where the last of it came
 * from.
 */
describe("footnote", () => {
  it("is_the_servers_sentence_verbatim_on_an_unpaged_answer", () => {
    expect(footnote("24 picks ranked for you, from 60 pulled", 24, false))
      .toBe("24 picks ranked for you, from 60 pulled");
  });

  it("says_what_is_on_screen_and_what_the_last_page_added", () => {
    // Fixture variation on the one field that changes the sentence: `paged`. Same reason, same
    // count, and the paged form is the only one that can be true of a 37-card grid.
    expect(footnote("13 picks ranked for you, from 65 pulled", 37, true))
      .toBe("37 picks so far · last page: 13 picks ranked for you, from 65 pulled");
    expect(footnote("1 pick ranked for you, from 9 pulled", 1, true))
      .toBe("1 pick so far · last page: 1 pick ranked for you, from 9 pulled");
  });

  it("says_nothing_when_the_server_said_nothing", () => {
    expect(footnote(null, 12, true)).toBeNull();
    expect(footnote(null, 0, false)).toBeNull();
    // Nothing on screen to count: fall back to the sentence rather than claiming "0 picks so far".
    expect(footnote("no fresh matches — try another angle", 0, true))
      .toBe("no fresh matches — try another angle");
  });
});

/**
 * FX3 put two fields on the wire that nothing on the client read: a `conflict` flag on the
 * assistant reply, and `excludedKeywords` beside `excludedGenres` on the conflict frame. Without
 * the flag the notice could only sit above the grid, detached from the sentence it qualifies;
 * without the keywords a turn that collided with an avoid-keyword ("anime", "musical") named
 * nothing at all, because `excludedGenres` was empty.
 */
describe("chatReducer — the conflict flag and the excluded terms", () => {
  it("marks_the_reply_that_was_qualified_and_leaves_the_others_alone", () => {
    let state = chatReducer(initialChatState, { type: "userTurn", message: "something scary", fresh: true });
    state = chatReducer(state, { type: "assistant", data: { text: "Three that fit.", conflict: true } });
    expect(state.turns.at(-1)).toEqual({ role: "assistant", text: "Three that fit.", conflict: true });

    // Fixture variation on the one field: an unqualified reply, and one from a server that sends
    // no flag. Neither may render the tag.
    let plain = chatReducer(initialChatState, { type: "userTurn", message: "something funny", fresh: true });
    plain = chatReducer(plain, { type: "assistant", data: { text: "Here you go.", conflict: false } });
    expect(plain.turns.at(-1)?.conflict).toBe(false);
    let older = chatReducer(initialChatState, { type: "userTurn", message: "something funny", fresh: true });
    older = chatReducer(older, { type: "assistant", data: { text: "Here you go." } });
    expect(older.turns.at(-1)?.conflict).toBeUndefined();
  });

  it("keeps_the_excluded_terms_beside_the_message_and_clears_both_together", () => {
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "a funny anime", fresh: true });
    const conflicted = chatReducer(asked, {
      type: "conflict",
      data: { message: "Your conditions permanently exclude comedy and anime.", excludedGenres: ["comedy"], excludedKeywords: ["anime"] },
    });
    expect(conflicted.conflict).toBe("Your conditions permanently exclude comedy and anime.");
    expect(conflicted.conflictTerms).toEqual(["comedy", "anime"]);
    // A note belongs to the question that produced it — both halves go with it.
    const next = chatReducer(conflicted, { type: "userTurn", message: "something else", fresh: false });
    expect(next.conflict).toBeNull();
    expect(next.conflictTerms).toEqual([]);
  });
});

/**
 * The excluded terms, as the notice states them. Genres and avoid-keywords are two server-side
 * lists and one fact for the reader: "your conditions rule out comedy and anime" is a sentence
 * about both, and until FX3 the keyword half was not on the wire at all.
 */
describe("excludedTerms and listPhrase", () => {
  it("merges_the_two_lists_without_repeating_a_term", () => {
    expect(excludedTerms(["comedy"], ["anime"])).toEqual(["comedy", "anime"]);
    // A term can legitimately be both a genre and an avoid-keyword on the same account; naming it
    // twice in one sentence reads as a bug in the very place the product is explaining itself.
    expect(excludedTerms(["comedy"], ["Comedy", "anime"])).toEqual(["comedy", "anime"]);
    // Either list absent is every older server, and both absent is a message-only conflict.
    expect(excludedTerms(undefined, ["anime"])).toEqual(["anime"]);
    expect(excludedTerms(["comedy"], undefined)).toEqual(["comedy"]);
    expect(excludedTerms(undefined, undefined)).toEqual([]);
    // Blank entries are not terms; printing "rules out  and anime" is worse than saying less.
    expect(excludedTerms(["", "  "], ["anime"])).toEqual(["anime"]);
  });

  it("reads_as_a_sentence_at_one_two_and_three_terms", () => {
    expect(listPhrase(["comedy"])).toBe("comedy");
    expect(listPhrase(["comedy", "anime"])).toBe("comedy and anime");
    expect(listPhrase(["comedy", "anime", "musical"])).toBe("comedy, anime and musical");
    expect(listPhrase([])).toBe("");
  });
});

/**
 * SB-01 — the server's order is the contract, and a reload has to reproduce the screen.
 *
 * Wave-B watched a page turn re-sort the answer ALREADY on screen: `partitionedOrder` ran over the
 * whole merged array on every `phase` frame, so page-two's picks interleaved into page-one's
 * answer by blend, and the hard reload that followed rendered the same 34 cards in the stored
 * `(Sequence, Rank)` order instead — the same answer, two orders, one gesture apart.
 *
 * The rule these pin: mid-stream ordering may settle the cards of the turn CURRENTLY streaming and
 * nothing else, and a turn that has reached `done` is frozen in the order the server sent. That is
 * what makes the live screen and the stored record the same object.
 */
describe("chatReducer — server order is the contract", () => {
  // Turn one's answer as the server committed it — deliberately NOT blend order. `t1` carries the
  // WEAKEST blend and is first, so any re-derivation is visible in the first assertion.
  const first = [card(1, { blend: 0.1 }), card(2, { blend: 0.9 }), card(3, { blend: 0.5 })];
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "quiet character dramas", fresh: true }),
    { type: "done", data: { results: first, reason: "3 picks ranked for you, from 40 pulled" } },
  );
  /** The page, out of blend order on the wire so a settle at the boundary is observable. */
  const page = [card(4, { blend: 0.2 }), card(5, { blend: 0.99 })];

  /** Streams `page` as candidates over the settled answer and takes one phase frame. */
  function pageInFlight() {
    let state = chatReducer(answered, { type: "moreTurn" });
    for (const c of page) state = chatReducer(state, { type: "candidate", data: c });
    return chatReducer(state, { type: "phase", data: { stage: "ranking", found: 5, scored: 5 } });
  }

  it("takes_the_servers_results_order_verbatim_on_done", () => {
    expect(answered.order).toEqual(["t1", "t2", "t3"]);
  });

  it("a_pages_phase_frames_never_touch_the_answer_already_on_screen", () => {
    const phased = pageInFlight();
    expect(phased.order.slice(0, 3)).toEqual(["t1", "t2", "t3"]);
    // …while the IN-FLIGHT block still settles at the boundary, which is what the rhythm is for.
    expect(phased.order.slice(3)).toEqual(["t5", "t4"]);
  });

  it("a_page_appends_after_the_previous_turn_in_the_servers_order", () => {
    const done = chatReducer(pageInFlight(), {
      type: "done",
      data: { results: page, reason: "2 picks ranked for you, from 65 pulled", kept: 2 },
    });
    expect(done.order).toEqual(["t1", "t2", "t3", "t4", "t5"]);
  });

  it("a_reload_reproduces_exactly_what_was_on_screen", () => {
    const done = chatReducer(pageInFlight(), {
      type: "done",
      data: { results: page, reason: "2 picks ranked for you, from 65 pulled", kept: 2 },
    });
    // The server stores `(Sequence, Rank)` — turn one's block, then turn two's, each in the order
    // it sent. Resuming that payload must land on the identical array.
    const resumed = chatReducer(initialChatState, {
      type: "resume",
      payload: {
        id: "conv-1",
        lastCardsAt: "2026-09-11T09:00:00Z",
        cards: [...first, ...page],
        turns: [
          { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "Five that fit.", kind: "ask", status: "Completed" },
        ],
        reason: "2 picks ranked for you, from 65 pulled",
        lastQuery: "quiet character dramas",
      },
    });
    expect(resumed.order).toEqual(done.order);
  });

  it("a_page_keeps_only_what_its_own_done_kept", () => {
    // Fixture variation, one field: the page streams a THIRD candidate the server's `done` then
    // drops (the quality floor cut it). A fresh turn already drops those — `order = incoming` —
    // and a page has to agree, or the screen carries a card the stored record does not.
    let state = chatReducer(answered, { type: "moreTurn" });
    for (const c of [...page, card(6, { blend: 0.4 })]) state = chatReducer(state, { type: "candidate", data: c });
    const done = chatReducer(state, {
      type: "done",
      data: { results: page, reason: "2 picks ranked for you, from 65 pulled", kept: 2 },
    });
    expect(done.order).toEqual(["t1", "t2", "t3", "t4", "t5"]);
  });

  it("a_new_question_settles_nothing_until_its_own_answer_lands", () => {
    // The held previous answer is frozen by `holdingPrevious`; once the first candidate of the new
    // turn replaces it, the screen is a live stream again and phase frames may settle all of it.
    const asking = chatReducer(answered, { type: "userTurn", message: "something older", fresh: false });
    let state = chatReducer(asking, { type: "candidate", data: card(7, { blend: 0.1 }) });
    state = chatReducer(state, { type: "candidate", data: card(8, { blend: 0.9 }) });
    const phased = chatReducer(state, { type: "phase", data: { stage: "ranking", found: 2, scored: 2 } });
    expect(phased.order).toEqual(["t8", "t7"]);
  });
});

/**
 * SB-02 — `+ New` has to survive a reload.
 *
 * Clearing the remembered id made this tab look like a browser that had never asked anything, and
 * `resumeTarget` answers that with `/conversations/latest` — the ACCOUNT's newest thread, which
 * after a `+ New` is precisely the thread the user just left. The reload undid the gesture and
 * reached a deprecated route to do it, whose own doc-comment says it is "only ever the answer for
 * a browser that has never asked anything".
 *
 * So "I have finished with that thread" is a fact worth storing, distinct from "I have no idea
 * which thread this is".
 */
describe("resumeTarget — three states, not two", () => {
  it("resumes_nothing_at_all_after_a_fresh_start", () => {
    expect(resumeTarget(FRESH_CONVERSATION)).toBe(NO_RESUME);
  });

  it("still_falls_back_to_latest_only_for_a_browser_that_has_never_asked", () => {
    expect(resumeTarget(null)).toBe("latest");
    expect(resumeTarget("")).toBe("latest");
  });

  it("prefers_this_tabs_own_thread_over_both", () => {
    expect(resumeTarget("7f3a1b2c-0000-4000-8000-000000000001")).toBe("7f3a1b2c-0000-4000-8000-000000000001");
  });

  it("round_trips_the_marker_through_storage", () => {
    // The marker is a stored VALUE, not an absent key — that is the whole of the fix, and it has
    // to survive the same trip a real id does.
    const store: Record<string, string> = {};
    vi.stubGlobal("localStorage", {
      getItem: (k: string) => store[k] ?? null,
      setItem: (k: string, v: string) => { store[k] = v; },
      removeItem: (k: string) => { delete store[k]; },
    });
    storeConversationId(FRESH_CONVERSATION);
    expect(store[CONVERSATION_ID_KEY]).toBe(FRESH_CONVERSATION);
    expect(resumeTarget(readStoredConversationId())).toBe(NO_RESUME);

    // …and the first `conversation` frame of the next question replaces it with the real thread.
    storeConversationId("7f3a1b2c-0000-4000-8000-000000000009");
    expect(resumeTarget(readStoredConversationId())).toBe("7f3a1b2c-0000-4000-8000-000000000009");
  });
});

/**
 * SB-04 — a paged thread replays its assistant reply twice.
 *
 * Live, the reducer suppresses this deliberately: a page replays the last question, so the model
 * replies again, and `case "assistant"` drops that second reply rather than stuttering the same
 * sentence into the transcript. `transcriptOf` had no equivalent filter — it mapped every row with
 * text and never looked at `kind` — so a resume rendered three bubbles for two real answers, the
 * first two being the identical sentence back to back.
 *
 * The server writes a `page` turn as an assistant row with NO user row beside it, which is the
 * other half of why it reads as a duplicate: there is no question above it to explain it.
 */
describe("transcriptOf — a page is not a second answer", () => {
  const rows = [
    { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "I can keep it quiet and character-driven.", kind: "ask", status: "Completed" },
    // The page. Identical text, by design — it is the same model answering the same question.
    { sequence: 2, role: "assistant", text: "I can keep it quiet and character-driven.", kind: "page", status: "Completed" },
  ];

  it("renders_one_bubble_per_real_answer", () => {
    expect(transcriptOf(rows).map((t) => t.text)).toEqual([
      "quiet character dramas",
      "I can keep it quiet and character-driven.",
    ]);
  });

  it("matches_what_the_live_reducer_does_with_the_same_thread", () => {
    // The parity that was missing. Streaming the same thread — a turn, then a page — puts exactly
    // one assistant bubble on screen, and a resume of it has to agree.
    let live = chatReducer(initialChatState, { type: "userTurn", message: "quiet character dramas", fresh: true });
    live = chatReducer(live, { type: "assistant", data: { text: "I can keep it quiet and character-driven." } });
    live = chatReducer(live, { type: "done", data: { results: [card(1)], reason: "1 pick" } });
    live = chatReducer(live, { type: "moreTurn" });
    live = chatReducer(live, { type: "assistant", data: { text: "I can keep it quiet and character-driven." } });
    live = chatReducer(live, { type: "done", data: { results: [card(2)], reason: "1 more", kept: 1 } });

    const resumed = chatReducer(initialChatState, {
      type: "resume",
      payload: { id: "conv-4", turns: rows, cards: [card(1), card(2)], lastQuery: "quiet character dramas" },
    });
    expect(resumed.turns.map((t) => t.text)).toEqual(live.turns.map((t) => t.text));
  });

  it("drops_a_page_row_whose_text_is_not_a_literal_duplicate_either", () => {
    // Fixture variation, one field: the page's sentence differs. The rule is `kind`, not text
    // equality — a page has no user row above it, so whatever it says renders as an answer to the
    // question two bubbles up, which the live path already refuses to do. Matching on duplicate
    // text instead would pass the test above and still stutter here.
    const reworded = [rows[0], rows[1], { ...rows[2], text: "Two more in the same vein." }];
    expect(transcriptOf(reworded).map((t) => t.text)).toEqual([
      "quiet character dramas",
      "I can keep it quiet and character-driven.",
    ]);
  });
});

/**
 * SB-06 — a resumed answer never rendered its `reason` footnote.
 *
 * The payload carries the sentence verbatim ("24 picks ranked for you, from 81 pulled · widened to
 * find enough · only 5 picks cleared your 6.0 bar, so it was relaxed"), the reducer stores it, and
 * the page gated the render on `status === "done"`. A resume is `resumed` by design, so a restored
 * answer lost the only on-screen account of how it was assembled — including the fact that the
 * user's own quality bar had been relaxed to fill it.
 */
describe("showsFootnote", () => {
  it("renders_the_accounting_for_a_restored_answer_as_well_as_a_live_one", () => {
    expect(showsFootnote("done", false, 24)).toBe(true);
    expect(showsFootnote("resumed", false, 24)).toBe(true);
  });

  it("says_nothing_about_a_screen_with_no_grid_on_it", () => {
    expect(showsFootnote("resumed", false, 0)).toBe(false);
    expect(showsFootnote("done", false, 0)).toBe(false);
  });

  it("waits_for_a_page_to_finish_before_re_describing_the_grid", () => {
    // Mid-page the sentence on screen describes a turn that has not finished.
    expect(showsFootnote("done", true, 24)).toBe(false);
  });

  it("stays_silent_on_every_state_that_has_no_finished_answer_to_describe", () => {
    for (const status of ["idle", "streaming", "stopped", "blocked", "error"] as const) {
      expect(showsFootnote(status, false, 24)).toBe(false);
    }
  });
});

/**
 * …and the sentence it renders has to be true of the GRID. The payload's `reason` is the newest
 * turn's, so on a thread that was paged it describes one page and the restored grid is several —
 * R1-03 arriving by the resume path instead of the live one.
 */
describe("chatReducer — a resumed thread knows whether it was paged", () => {
  const base = {
    id: "conv-6",
    lastCardsAt: "2026-09-11T09:00:00Z",
    cards: [card(1), card(2), card(3)],
    reason: "2 picks ranked for you, from 65 pulled",
    lastQuery: "quiet character dramas",
  };
  const askTurns = [
    { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
  ];

  it("quotes_the_servers_sentence_verbatim_on_a_single_turn_thread", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload: { ...base, turns: askTurns } });
    expect(state.paged).toBe(false);
    expect(footnote(state.reason, 3, state.paged)).toBe("2 picks ranked for you, from 65 pulled");
  });

  it("accounts_for_the_whole_grid_once_a_page_row_is_in_the_transcript", () => {
    // Fixture variation, one row: the same payload with the `page` turn the server writes. Nothing
    // else moves — same cards, same reason — and the line stops claiming the last page is the grid.
    const paged = {
      ...base,
      turns: [...askTurns, { sequence: 2, role: "assistant", text: "Three that fit.", kind: "page", status: "Completed" }],
    };
    const state = chatReducer(initialChatState, { type: "resume", payload: paged });
    expect(state.paged).toBe(true);
    expect(footnote(state.reason, 3, state.paged))
      .toBe("3 picks so far · last page: 2 picks ranked for you, from 65 pulled");
  });
});

/**
 * SB-05 — `Recent ▾` showed 20 of the account's 51 threads with no way to reach the rest.
 *
 * The menu is deliberately small ("a means of getting back to an answer, not a filing system") and
 * `?limit=20` matches the contract, so this is not a wrong request — it is a missing affordance.
 * Anyone reading the menu as a complete thread list is simply wrong, and nothing on screen said so.
 *
 * The route takes `limit` (clamped 1..100) and NO offset, so 100 is the ceiling reachable from
 * here and the menu says so rather than offering a control that cannot go further.
 */
describe("recentListing", () => {
  it("asks_for_the_cheap_page_first", () => {
    expect(recentListing(false, 0).limit).toBe(RECENT_LIMIT);
    expect(recentListing(true, 0).limit).toBe(OLDER_LIMIT);
  });

  it("offers_the_rest_only_when_the_page_came_back_full", () => {
    // A short page IS the whole list — offering "show older" over it promises threads that do not
    // exist. A full one is the only evidence the client has that there may be more.
    expect(recentListing(false, RECENT_LIMIT).canShowOlder).toBe(true);
    expect(recentListing(false, RECENT_LIMIT - 1).canShowOlder).toBe(false);
    expect(recentListing(false, 0).canShowOlder).toBe(false);
  });

  it("stops_offering_once_it_has_asked_for_everything_it_can_reach", () => {
    // Fixture variation, one field: the same full page, already expanded. The route has no offset,
    // so there is no second gesture to offer and the control must not reappear.
    expect(recentListing(true, OLDER_LIMIT).canShowOlder).toBe(false);
    expect(recentListing(true, 42).canShowOlder).toBe(false);
  });

  it("says_so_when_it_is_standing_at_the_ceiling", () => {
    // 100 rows back from a 100-row request means the account has at least that many and the route
    // cannot page past it. Saying nothing there is how the menu became a list people trusted.
    expect(recentListing(true, OLDER_LIMIT).atCeiling).toBe(true);
    expect(recentListing(true, OLDER_LIMIT - 1).atCeiling).toBe(false);
    expect(recentListing(false, RECENT_LIMIT).atCeiling).toBe(false);
  });
});

/**
 * SB-03 — a resumed thread attributed the previous answer to the question that never got one.
 *
 * The DB is right and the grid is right: a stopped turn is stored `Failed` / `client_aborted` with
 * zero cards, and the server replays the last COMPLETED answer. What was missing was any sentence
 * on the page saying so — a full-text scan for `stopped|failed|didn't finish|Ask again` came back
 * false — so the screen read "You said: something surreal and dreamlike" and then, directly under
 * it, 24 cards answering a different question entirely.
 *
 * The information was already on the wire: `turns[6].status === "Failed"` was in that very payload.
 * `transcriptOf` simply never looked at it.
 */
describe("lastTurnOutcome", () => {
  const completed = [
    { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
  ];

  it("says_nothing_about_a_thread_that_ended_on_an_answer", () => {
    expect(lastTurnOutcome(completed)).toBeNull();
  });

  it("reports_a_stopped_turn_as_the_users_own_doing", () => {
    const stopped = [
      ...completed,
      { sequence: 2, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
    ];
    expect(lastTurnOutcome(stopped)).toEqual({ status: "failed", failureCode: "client_aborted", turnId: null });
    expect(failedTurnMessage("client_aborted")).toContain("you stopped it");
  });

  it("reports_any_other_failure_as_a_snag_the_user_did_not_cause", () => {
    // Fixture variation, one field: the failure code. Telling someone they abandoned a turn they
    // never touched is a small lie that costs trust in everything else on the page.
    const crashed = [
      ...completed,
      { sequence: 2, role: "user", text: "something surreal", kind: "ask", status: "Failed" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "HttpRequestException" },
    ];
    expect(lastTurnOutcome(crashed)?.status).toBe("failed");
    expect(failedTurnMessage("HttpRequestException")).toContain("hit a snag");
  });

  it("claims_no_cause_at_all_when_the_server_named_none", () => {
    // The live case, not a hypothetical: the resume payload projects no `failureCode`, so today
    // every resumed failure arrives unattributed. Guessing "you stopped it" or "it hit a snag"
    // there is a coin flip presented as a fact.
    expect(failedTurnMessage(null)).toBe("That one didn’t finish.");
    expect(failedTurnMessage(null)).not.toContain("stopped");
    expect(failedTurnMessage(null)).not.toContain("snag");
  });

  it("reports_a_turn_the_server_is_still_working_on", () => {
    const pending = [
      ...completed,
      { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Pending" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Pending", id: "turn-77" },
    ];
    expect(lastTurnOutcome(pending)).toEqual({ status: "pending", failureCode: null, turnId: "turn-77" });
  });

  it("carries_no_turn_id_when_the_payload_has_none", () => {
    // Today's resume payload projects sequence / role / text / kind / status and no id, so the
    // pending state has to be renderable — and re-checkable — without one.
    const pending = [
      ...completed,
      { sequence: 2, role: "user", text: "older, and slower", kind: "ask", status: "Pending" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Pending" },
    ];
    expect(lastTurnOutcome(pending)?.turnId).toBeNull();
  });

  it("ignores_a_failure_the_thread_has_already_moved_past", () => {
    // A failure four turns back is history the transcript shows; it is not a state the page is in.
    const recovered = [
      { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Failed" },
      { sequence: 1, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
      ...completed.map((t, i) => ({ ...t, sequence: i + 2 })),
    ];
    expect(lastTurnOutcome(recovered)).toBeNull();
  });
});

/** Which question the restored grid belongs to — the server's own `lastAsk` rule, client-side. */
describe("answeredQuestion", () => {
  it("names_the_last_question_that_actually_got_an_answer", () => {
    expect(answeredQuestion([
      { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
      { sequence: 2, role: "assistant", text: "Two more.", kind: "page", status: "Completed" },
      { sequence: 3, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
      { sequence: 4, role: "assistant", text: "", kind: "ask", status: "Failed" },
    ])).toBe("quiet character dramas");
  });

  it("skips_a_page_row_the_way_the_server_does", () => {
    // Fixture variation: the page is the NEWEST completed assistant row, and it has no user row of
    // its own. The server anchors on the last completed `ask`; anchoring on the newest completed
    // row of any kind would walk back past the page and name the same question by luck, not rule.
    expect(answeredQuestion([
      { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
      { sequence: 2, role: "assistant", text: "Two more.", kind: "page", status: "Completed" },
    ])).toBe("quiet character dramas");
  });

  it("names_nothing_on_a_thread_that_has_never_completed_an_ask", () => {
    expect(answeredQuestion([
      { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Failed" },
      { sequence: 1, role: "assistant", text: "", kind: "ask", status: "Failed" },
    ])).toBe("");
  });
});

describe("chatReducer — a resumed thread with an unfinished tail", () => {
  const turns = [
    { sequence: 0, role: "user", text: "quiet character dramas", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
    { sequence: 2, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
  ];
  const payload = {
    id: "conv-3",
    lastCardsAt: "2026-09-11T09:00:00Z",
    turns,
    cards: [card(1), card(2), card(3)],
    reason: "3 picks ranked for you, from 40 pulled",
    // The server's `lastQuery` is the last USER row — the question a retry replays — which on this
    // thread is precisely the one that never got an answer.
    lastQuery: "something surreal and dreamlike",
  };

  it("keeps_the_grid_and_names_both_questions", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload });
    expect(state.order).toHaveLength(3);
    expect(state.resumedTail).toEqual({ status: "failed", failureCode: "client_aborted", turnId: null });
    // The grid belongs to the first question; the retry replays the second.
    expect(state.answeredQuery).toBe("quiet character dramas");
    expect(state.lastQuery).toBe("something surreal and dreamlike");
    // …and the unanswered question is still in the transcript, because it is still what was asked.
    expect(state.turns.map((t) => t.text)).toEqual([
      "quiet character dramas", "Three that fit.", "something surreal and dreamlike",
    ]);
  });

  it("says_nothing_of_the_kind_on_an_ordinary_resume", () => {
    // Fixture variation, one field: the tail's status. Everything else about the payload is
    // identical, so the notice cannot be reached for any other reason.
    const settled = {
      ...payload,
      turns: [...turns.slice(0, 2), { ...turns[2], status: "Completed" }, { ...turns[3], text: "Three more.", status: "Completed", failureCode: null }],
    };
    const state = chatReducer(initialChatState, { type: "resume", payload: settled });
    expect(state.resumedTail).toBeNull();
    expect(state.answeredQuery).toBe("something surreal and dreamlike");
  });

  it("lets_go_of_the_tail_the_moment_this_visit_asks_anything", () => {
    // The notice carries a retry offer, and asking IS taking it. Every route into a turn clears it:
    // a question, a re-ask, and a page over the restored answer.
    const resumed = chatReducer(initialChatState, { type: "resume", payload });
    for (const action of [
      { type: "userTurn", message: "something older", fresh: false },
      { type: "reaskTurn" },
      { type: "moreTurn" },
    ] as const) {
      const next = chatReducer(resumed, action);
      expect(next.resumedTail).toBeNull();
      expect(next.answeredQuery).toBe("");
    }
  });

  it("dates_a_transcript_only_thread_from_its_last_turn", () => {
    // `lastCardsAt` is null on a thread that predates stored results, so it is the one case the
    // seam cannot date from cards. `lastTurnAt` is the only stamp such a thread has.
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...payload, cards: [], lastCardsAt: null, lastTurnAt: "2026-09-10T21:00:00Z" },
    });
    expect(state.lastCardsAt).toBeNull();
    expect(state.lastTurnAt).toBe("2026-09-10T21:00:00Z");
  });
});
