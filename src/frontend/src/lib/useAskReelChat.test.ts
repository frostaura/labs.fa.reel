import { afterEach, describe, expect, it, vi } from "vitest";
import {
  askRequest,
  chatReducer,
  createMountGuard,
  guardFor,
  mintHandoff,
  claimHandoff,
  abortFallout,
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
  showsConflictPill,
  pickSummary,
  resumeAnnouncement,
  composerHeight,
  composerSends,
  COMPOSER_MIN_PX,
  COMPOSER_MAX_PX,
  turnSettled,
  recentListing,
  lastTurnOutcome,
  answeredQuestion,
  answeredTurnId,
  resumedPageTarget,
  pageRefusalMessage,
  stoppedTranscript,
  showsInlineFailure,
  answerRowIndex,
  failedTurnMessage,
  RECENT_LIMIT,
  OLDER_LIMIT,
  transcriptOf,
  staleFrame,
  mintStreamId,
  scrollTargetFor,
  bubbleOverflows,
  prefersReducedMotion,
  skeletonCount,
  SKELETON_CAP,
  wasPaged,
  resumedConflict,
  type ChatTurn,
  type StreamTag,
} from "./useAskReelChat";
import { AskStreamError, type AskCard } from "./askReelStream";

/**
 * Regression for "reload mid-conversation and lose every refinement". The ?q= seed used to sit in
 * the URL for the life of the thread, so a reload after three refinements re-fired it as a FRESH
 * turn — wiping the conversation and answering the original question again — and the resume path
 * below it could never run, because `seed` was still truthy. The rule these tests pin: a seed is
 * consumed exactly once, and "no seed" only means "resume" on the very first pass.
 */
/**
 * A hand-off is a fact about a NAVIGATION, and `location.state` is a fact about a HISTORY ENTRY.
 * The two come apart the moment the document reloads or the user presses Back, and the page then
 * asked a question nobody had typed. The registry is what closes the gap: it is created with the
 * document, so nothing a restored entry carries can ever be in it.
 */
describe("mintHandoff / claimHandoff", () => {
  it("claims a hand-off this document minted — exactly once", () => {
    const registry = new Set<string>();
    const id = mintHandoff(registry);
    expect(claimHandoff(id, registry)).toBe(true);
    // Back onto the same entry, a second mount reading the same state, a double-invoke: all false.
    expect(claimHandoff(id, registry)).toBe(false);
  });

  it("refuses a hand-off restored from a history entry, and one with no id at all", () => {
    // The reload, modelled exactly: the id was minted by the document that is now gone, so the new
    // document's registry has never heard of it.
    const before = new Set<string>();
    const id = mintHandoff(before);
    const afterReload = new Set<string>();
    expect(claimHandoff(id, afterReload)).toBe(false);
    // …and an entry written before the id existed cannot be a live hand-off either.
    expect(claimHandoff(undefined, before)).toBe(false);
  });

  it("keeps two hand-offs apart, so consuming one leaves the other live", () => {
    const registry = new Set<string>();
    const first = mintHandoff(registry);
    const second = mintHandoff(registry);
    expect(first).not.toEqual(second);
    expect(claimHandoff(first, registry)).toBe(true);
    expect(claimHandoff(second, registry)).toBe(true);
  });
});

describe("nextBootstrap", () => {
  it("starts a fresh turn on a cold landing with ?q=", () => {
    expect(nextBootstrap("slow-burn sci-fi", null)).toEqual({ action: "seed", handled: "slow-burn sci-fi", clear: true });
  });

  it("consumes the seed exactly once — a StrictMode double-invoke must not restart the turn", () => {
    const first = nextBootstrap("slow-burn sci-fi", null);
    expect(first.action).toBe("seed");
    expect(nextBootstrap("slow-burn sci-fi", first.handled).action).toBe("none");
  });

  it("does nothing on the settling render after the ?q= param is cleared", () => {
    // The clear brings us back with an empty seed; resuming here is exactly what would clobber
    // the conversation the seed just started.
    expect(nextBootstrap("", "slow-burn sci-fi")).toEqual({ action: "none", handled: "", clear: false });
  });

  it("resumes the last conversation when the page loads with no seed", () => {
    expect(nextBootstrap("", null)).toEqual({ action: "resume", handled: "", clear: false });
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
    expect(nextBootstrap("arrival", settled.handled)).toMatchObject({ action: "seed", handled: "arrival" });
  });

  it("starts a turn for a new search made while the page is already mounted", () => {
    const consumed = nextBootstrap("arrival", null);
    expect(nextBootstrap("sicario", consumed.handled)).toMatchObject({ action: "seed", handled: "sicario" });
  });

  /**
   * A hand-off is a NAVIGATION, and a navigation happens once. React Router keeps `location.state`
   * in the history entry, so a reload — or a Back onto the entry the search box pushed — hands the
   * same question back to a document that has never asked it, and the page fires it again as if it
   * had just been typed. On the wire that is a second ask with the same text, which the server
   * labels a `page` of the first because the text matches the conversation's last user turn: a
   * phantom turn, billed, with cards nobody asked for.
   *
   * `restored` is the router's own `POP` — the initial render of a freshly loaded document, and
   * every Back/Forward. A restored seed is consumed and thrown away, and the visit resumes the
   * thread like any other cold landing.
   */
  it("never re-fires a seed restored from the history entry — a reload resumes instead", () => {
    expect(nextBootstrap("a tense heist", null, () => false)).toEqual({
      action: "resume", handled: "a tense heist", clear: true,
    });
  });

  it("clears the stale seed even when there is nothing to resume, and settles quietly after", () => {
    const restored = nextBootstrap("a tense heist", null, () => false);
    // The state is gone by the next render, so the seed cannot come back round.
    expect(nextBootstrap("", restored.handled)).toEqual({ action: "none", handled: "", clear: false });
  });

  it("a live hand-off is still a seed, and clears the state as it sends", () => {
    expect(nextBootstrap("a tense heist", null, () => true)).toEqual({
      action: "seed", handled: "a tense heist", clear: true,
    });
  });

  it("re-renders of a restored entry ask for nothing at all, and never re-spend the claim", () => {
    let claims = 0;
    const live = () => { claims += 1; return false; };
    const restored = nextBootstrap("a tense heist", null, live);
    expect(nextBootstrap("a tense heist", restored.handled, live)).toEqual({
      action: "none", handled: "a tense heist", clear: false,
    });
    // Claiming a hand-off spends it, so the pass that decides nothing must not consult it at all.
    expect(claims).toBe(1);
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
    // thread. Fresh resets through initialChatState; the follow-up clears the TURN slot, and with
    // no answer on screen behind it the answer slot goes too.
    const asked = chatReducer(initialChatState, { type: "userTurn", message: "something scary", fresh: true });
    const conflicted = chatReducer(asked, { type: "conflict", data: { message: "Horror is excluded on this account." } });
    // In flight it is the TURN's, which is what lets a held previous answer keep its own (R5-01).
    expect(conflicted.turnConflict).toBe("Horror is excluded on this account.");
    // It survives the answer it annotates, as that answer's own.
    const answered = chatReducer(conflicted, { type: "done", data: { results: [card(1)], reason: "1 pick" } });
    expect(answered.conflict).toBe("Horror is excluded on this account.");
    expect(answered.turnConflict).toBeNull();

    expect(chatReducer(conflicted, { type: "userTurn", message: "something funny", fresh: false }).conflict).toBeNull();
    expect(chatReducer(conflicted, { type: "userTurn", message: "something funny", fresh: false }).turnConflict).toBeNull();
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

    // The tray moves with the grid it belongs to (F6-06). A re-ask HOLDS the previous answer until
    // its replacement exists, so the tray is held with it — clearing it at the moment the question
    // is asked is what left a Stop three seconds later showing the grid and none of its accounting,
    // with sixteen tray picks that had been one tap away simply gone.
    const reasked = chatReducer(withTray, { type: "reaskTurn" });
    expect(reasked.holdingPrevious).toBe(true);
    expect(reasked.belowFloor.map((c) => c.titleId)).toEqual(["t8", "t9"]);
    // …and released at the single moment the grid is replaced.
    expect(chatReducer(reasked, { type: "candidate", data: card(5) }).belowFloor).toEqual([]);

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

    // Every route into a new turn clears it, or last question's count labels this question's tray —
    // but not one moment before the tray itself goes. A fresh turn takes the whole screen with it
    // and so clears immediately; a follow-up and a re-ask hold the previous answer, and its count
    // is held with it until the first card of the new turn replaces both (F6-06).
    expect(chatReducer(counted, { type: "userTurn", message: "horror", fresh: true }).droppedByFloor).toBeNull();
    const followUp = chatReducer(counted, { type: "userTurn", message: "horror", fresh: false });
    expect(followUp.droppedByFloor).toBe(3);
    expect(chatReducer(followUp, { type: "candidate", data: card(5) }).droppedByFloor).toBeNull();
    const reasking = chatReducer(counted, { type: "reaskTurn" });
    expect(reasking.droppedByFloor).toBe(3);
    expect(chatReducer(reasking, { type: "candidate", data: card(5) }).droppedByFloor).toBeNull();
    // A follow-up asked with nothing on screen has nothing to hold, so it clears at once.
    const cold = chatReducer(initialChatState, { type: "userTurn", message: "horror", fresh: false });
    expect(cold.droppedByFloor).toBeNull();

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

  /**
   * The guard was correct and the hook still aborted, because the hook built a NEW guard inside
   * the effect body. StrictMode's cleanup scheduled the abort on guard #1; the remount then called
   * `mount()` on guard #2, which cancelled its own empty queue while guard #1's timer still saw
   * `mounted === false` and killed a live request. Every Home → /search hand-off died that way.
   *
   * The fixture is the EFFECT, not the guard — mount/unmount/mount driven through whatever the
   * effect body would construct — because the guard's own test above passes either way. Reusing
   * one instance per hook is the whole of the fix, so the rebuilt shape is kept here as the
   * control: it must still abort, or this test is not watching the right difference.
   */
  it("one guard per hook instance survives the StrictMode remount; a rebuilt one does not", () => {
    function effects(build: (abort: () => void, schedule: Sched, cancel: Cancel) => MountGuardShape) {
      const queue: (() => void)[] = [];
      let aborted = 0;
      const schedule: Sched = (fn) => { queue.push(fn); return queue.length - 1; };
      const cancel: Cancel = (handle) => { queue[handle as number] = () => {}; };
      // One invocation of the mount effect: build (or reuse) the guard, mount, hand back cleanup.
      const mount = () => {
        const guard = build(() => { aborted += 1; }, schedule, cancel);
        guard.mount();
        return () => guard.unmount();
      };
      return { mount, flush: () => { for (const fn of queue.splice(0)) fn(); }, aborted: () => aborted };
    }

    // What the hook does now: the instance lives in a ref, so both effect invocations get it.
    const box: { current: MountGuardShape | null } = { current: null };
    const kept = effects((abort, schedule, cancel) => guardFor(box, abort, schedule, cancel));
    kept.mount()();
    kept.mount();
    kept.flush();
    expect(kept.aborted()).toBe(0);

    // What it did before: a fresh guard per invocation, which is the defect itself.
    const rebuilt = effects((abort, schedule, cancel) => createMountGuard(abort, schedule, cancel));
    rebuilt.mount()();
    rebuilt.mount();
    rebuilt.flush();
    expect(rebuilt.aborted()).toBe(1);

    // …and a real teardown still aborts through the kept guard — the half that must not regress.
    const real: { current: MountGuardShape | null } = { current: null };
    const torn = effects((abort, schedule, cancel) => guardFor(real, abort, schedule, cancel));
    torn.mount()();
    torn.flush();
    expect(torn.aborted()).toBe(1);
  });
});

type Sched = (fn: () => void) => unknown;
type Cancel = (handle: unknown) => void;
interface MountGuardShape { mount: () => void; unmount: () => void }

/**
 * An abort the page did not ask for must not read as silence.
 *
 * `streamAskReel`'s rejection was swallowed whole whenever the signal was aborted — correct for
 * Stop, for a re-ask replacing the run, for a resume and for a real unmount, and wrong for
 * everything else, because "wrong" means the reducer stays in `streaming` with a progress bar and
 * no way out. The Home → /search hand-off spent its whole life in that hole.
 */
describe("abortFallout", () => {
  it("says nothing when the abort was the page's own doing", () => {
    for (const cause of ["stop", "replaced", "resume", "reset", "timeout", "hidden", "unmount"] as const) {
      expect(abortFallout(cause)).toBeNull();
    }
  });

  it("errors on an abort nobody claimed, so the page offers a retry instead of a spinner", () => {
    expect(abortFallout(null)).toEqual({ type: "error", reason: null });
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

  it("stopping before the first card hands the previous answer back WHOLE", () => {
    // Probe one: Stop at 2.5s, before any candidate — the reported repro, and the case that used
    // to produce 700px of void. F6-06 is the second half of it: the answer survived, but stripped.
    const withTray = chatReducer(answered, {
      type: "done",
      data: { results: [card(1), card(2)], reason: "2 picks", droppedByFloor: 16, belowFloor: [card(8)] },
    });
    const asking = chatReducer(withTray, { type: "userTurn", message: "slow burn detective series", fresh: false });
    const stopped = chatReducer(chatReducer(asking, { type: "phase", data: { stage: "searching", found: 0, scored: 0 } }), { type: "stop" });

    expect(stopped.status).toBe("stopped");
    expect(stopped.arrived).toBe(0);
    // The answer the user was reading is still there — stopping a turn must not cost them one.
    expect(stopped.order).toEqual(["t1", "t2"]);
    expect(stopped.phase).toBeNull();
    expect(stopped.appending).toBe(false);
    // …and the HOLD is over. "Held until the new one arrives" is only true while something is still
    // coming; latched past the Stop it left the kept grid at 40% opacity as a resting state, under
    // a label about a question that would never be answered.
    expect(stopped.holdingPrevious).toBe(false);
    // Whole: the tray, its count and the provenance sentence are all still with the grid they
    // describe. Sixteen picks one tap away had been ceasing to exist at this moment, and a reload
    // brought every one of them back — which is how it was known to be a client-side loss.
    expect(stopped.belowFloor.map((c) => c.titleId)).toEqual(["t8"]);
    expect(stopped.droppedByFloor).toBe(16);
    expect(stopped.reason).toBe("2 picks");
    // Two questions, kept apart. "Ask again" replays the one that was abandoned; "Show me more"
    // extends the one the grid answers. Conflating them is R1-04.
    expect(stopped.lastQuery).toBe("slow burn detective series");
    expect(stopped.pageQuery).toBe("tense heist movie");
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

  it("carries_pageOf_only_when_the_page_can_name_the_turn_it_extends", () => {
    // R3-02. Fixture variation: the same page request with and without a known turn id. With one,
    // the server extends THAT turn whatever was said after it; without one it falls back to
    // comparing the query, and a `pageOf: null` would be a fifth field on every ordinary ask
    // claiming to extend nothing.
    const named = askRequest("a 90s romantic comedy", "turn-9", "conv-1", [], "assistant-turn-1");
    expect(named.pageOf).toBe("assistant-turn-1");
    expect(Object.keys(named).sort())
      .toEqual(["clientTurnId", "conversationId", "mediaTypes", "pageOf", "query"]);

    for (const unnamed of [
      askRequest("a 90s romantic comedy", "turn-9", "conv-1", []),
      askRequest("a 90s romantic comedy", "turn-9", "conv-1", [], null),
      askRequest("a 90s romantic comedy", "turn-9", "conv-1", [], ""),
    ]) {
      expect(Object.keys(unnamed)).not.toContain("pageOf");
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

  it("never_pages_an_answer_that_is_still_being_held", () => {
    // The hold alone is disqualifying, whatever the status beside it says: those cards are not the
    // current question's answer, and a page of them would adopt them as its page one (R1-04).
    expect(canPage("streaming", true, false, 2)).toBe(false);
    expect(canPage("blocked", true, false, 2)).toBe(false);
    expect(canPage("error", true, false, 2)).toBe(false);
  });

  it("pages_the_answer_a_stop_handed_back_whole_but_never_the_partial", () => {
    // F6-06 against R1-04, which are easy to mistake for each other. R1-04 was not "a stopped
    // screen offered paging" — it was paging sending the question that had never been answered.
    // Here the Stop ends the hold, so what is on screen is a finished, server-ordered,
    // server-floored answer whose question `pageQuery` names, and extending it is honest.
    const asking = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    const stopped = chatReducer(asking, { type: "stop" });
    expect(stopped.holdingPrevious).toBe(false);
    expect(stopped.settled).toBe(stopped.order.length);
    expect(canPage(stopped.status, stopped.holdingPrevious, stopped.exhausted, stopped.order.length, stopped.settled)).toBe(true);
    expect(stopped.pageQuery).toBe("a tense heist movie");

    // …and the partial is still refused. A turn that never reached `done` was never ranked and
    // never floored; extending it compounds that, and `settled` is 0 against a non-empty grid.
    let partial = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    for (const n of [7, 8]) partial = chatReducer(partial, { type: "candidate", data: card(n) });
    const stoppedPartial = chatReducer(partial, { type: "stop" });
    expect(stoppedPartial.settled).toBe(0);
    expect(canPage(stoppedPartial.status, stoppedPartial.holdingPrevious, stoppedPartial.exhausted, stoppedPartial.order.length, stoppedPartial.settled)).toBe(false);

    // A stopped PAGE is the third shape: the answer it was extending is settled and its own cards
    // are not, so the grid is part finished work and part not. Refused.
    let paging = chatReducer(answered, { type: "moreTurn" });
    paging = chatReducer(paging, { type: "candidate", data: card(9) });
    const stoppedPage = chatReducer(paging, { type: "stop" });
    expect(stoppedPage.settled).toBe(2);
    expect(stoppedPage.order).toHaveLength(3);
    expect(canPage(stoppedPage.status, stoppedPage.holdingPrevious, stoppedPage.exhausted, stoppedPage.order.length, stoppedPage.settled)).toBe(false);
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
    // Held with the answer they describe, and released with it (F6-06): while the exhausted grid is
    // still on screen it is still exhausted, and offering to page it would be an offer the server
    // has already refused.
    const asking = chatReducer(exhaustedState, { type: "userTurn", message: "something quieter", fresh: false });
    expect(asking.exhausted).toBe(true);
    // The page that exhausted it added nothing, so the grid is still one turn's work — held
    // verbatim rather than reset, which is the property.
    expect(asking.paged).toBe(exhaustedState.paged);
    const swapped = chatReducer(asking, { type: "candidate", data: card(8) });
    expect(swapped.exhausted).toBe(false);
    expect(swapped.paged).toBe(false);
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
/**
 * F4-05. The accumulated footnote and the live region both counted the outside-request channel as
 * picks — the cards the server's own sentence, in the same line, had just called not-matching.
 */
describe("pickSummary and the footnote's count", () => {
  it("counts_picks_and_names_the_outside_channel_separately", () => {
    // R3's turn 10, verbatim: 9 grid + 3 outside, announced as "12 picks".
    expect(pickSummary(9, 3)).toBe("9 picks ready. 3 closest-but-outside shown separately.");
    expect(footnote("3 picks matched everything you asked · 3 closest-but-outside shown separately", 9, true))
      .toBe("9 picks so far · last page: 3 picks matched everything you asked · 3 closest-but-outside shown separately");
  });

  it("an_answer_with_no_outside_channel_reads_exactly_as_it_did", () => {
    // Fixture variation, one field: the same answer with the outside count at zero — which is most
    // turns, and where the two counts were indistinguishable and the defect survived.
    expect(pickSummary(21, 0)).toBe("21 picks ready.");
    expect(pickSummary(1, 0)).toBe("1 pick ready.");
    expect(footnote("7 picks ranked for you, from 47 pulled", 21, true))
      .toBe("21 picks so far · last page: 7 picks ranked for you, from 47 pulled");
  });

  it("says_nothing_matched_only_when_nothing_is_on_screen", () => {
    // "No matches" over three outside cards is the same conflation in reverse — the empty state is
    // a claim about the screen, and the screen is not empty.
    expect(pickSummary(0, 0)).toBe("No matches for that one.");
    expect(pickSummary(0, 3)).toBe("No picks matched everything you asked. 3 closest-but-outside shown separately.");
    expect(pickSummary(0, 1)).toBe("No picks matched everything you asked. 1 closest-but-outside shown separately.");
  });
});

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
    // The frame is the running turn's until that turn completes, and then it is the answer's.
    expect(conflicted.turnConflict).toBe("Your conditions permanently exclude comedy and anime.");
    expect(conflicted.turnConflictTerms).toEqual(["comedy", "anime"]);
    const answered = chatReducer(conflicted, { type: "done", data: { results: [card(1)], reason: "1 pick" } });
    expect(answered.conflict).toBe("Your conditions permanently exclude comedy and anime.");
    expect(answered.conflictTerms).toEqual(["comedy", "anime"]);
    // A note belongs to the answer that produced it — both halves go when that answer is replaced,
    // which is the first candidate of the next turn and not the act of asking (R5-01).
    const next = chatReducer(answered, { type: "userTurn", message: "something else", fresh: false });
    expect(next.conflict).toBe("Your conditions permanently exclude comedy and anime.");
    const replaced = chatReducer(next, { type: "candidate", data: card(9) });
    expect(replaced.conflict).toBeNull();
    expect(replaced.conflictTerms).toEqual([]);
  });
});

/**
 * The excluded terms, as the notice states them. Genres and avoid-keywords are two server-side
 * lists and one fact for the reader: "your conditions rule out comedy and anime" is a sentence
 * about both, and until FX3 the keyword half was not on the wire at all.
 */
/**
 * F4-06's first half. The pill renders off the `conflict` flag, and the server's rewrite of the
 * reply ENDS in the very clause the pill carries — so the bubble printed the same three words
 * twice, running together with no space: "…titles from 2000 on.under your conditions".
 */
describe("showsConflictPill", () => {
  it("stands_down_when_the_reply_already_says_it", () => {
    // The server's own wording, from `AskReelStandingFilters` — both shapes it produces.
    expect(showsConflictPill("Coming up — under your conditions that means no comedy and titles from 2000 on."))
      .toBe(false);
    expect(showsConflictPill("Coming up — under your conditions that means no comedy or anime."))
      .toBe(false);
  });

  it("still_marks_a_bubble_that_was_qualified_without_saying_so", () => {
    // Fixture variation: the same flag over a reply that does NOT carry the clause — an older
    // server, and a row stored before the rewrite existed. Dropping the pill outright would leave
    // those bubbles unmarked, which is the state F4-06's OTHER half was about.
    expect(showsConflictPill("I can’t recommend a 90s romantic comedy.")).toBe(true);
    expect(showsConflictPill("")).toBe(true);
  });

  it("does_not_care_where_in_the_sentence_the_clause_sits_or_how_it_is_cased", () => {
    // It is mid-sentence today and was sentence-final before. A rule anchored to either end would
    // put the duplicate back the next time the copy moves.
    expect(showsConflictPill("Under your conditions, here is the closest fit.")).toBe(false);
    expect(showsConflictPill("Here is the closest fit, UNDER YOUR CONDITIONS.")).toBe(false);
    // …and a sentence about something else entirely keeps its pill.
    expect(showsConflictPill("Under your bar, these were the only ones left.")).toBe(true);
  });
});

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

  it("describes_the_answer_a_stop_handed_back_whole", () => {
    // F6-06's third casualty. A Stop over a held answer withdrew the provenance line along with
    // the tray and the paging — from an answer nothing had replaced, whose sentence ("8 picks
    // ranked for you, from 42 pulled · 16 under your 6.0 bar — shown below") was still true, and
    // which came back in full on a reload. Fixture variation on one flag, because a stopped
    // PARTIAL is the state the silence above is for and looks identical without it.
    expect(showsFootnote("stopped", false, 8, true)).toBe(true);
    expect(showsFootnote("stopped", false, 8, false)).toBe(false);
    // …and the flag never overrides the other two conditions.
    expect(showsFootnote("stopped", false, 0, true)).toBe(false);
    expect(showsFootnote("stopped", true, 8, true)).toBe(false);
    // Nor does it reach a state with its own explanation on screen.
    expect(showsFootnote("error", false, 8, true)).toBe(false);
    expect(showsFootnote("blocked", false, 8, true)).toBe(false);
  });
});

/**
 * `pageQuery` — the question the GRID answers, which after a Stop is not the last thing the user
 * said. R1-04 was these two being one field: "Show me more" replayed the abandoned question and
 * silently adopted the earlier question's cards as its page one.
 */
describe("chatReducer — the question a page replays", () => {
  const answered = chatReducer(
    chatReducer(initialChatState, { type: "userTurn", message: "a tense heist movie", fresh: true }),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
  );

  it("tracks_the_last_completed_answer_not_the_last_question", () => {
    expect(answered.pageQuery).toBe("a tense heist movie");
    // Asking does not change it — the grid on screen still answers the old question.
    const asking = chatReducer(answered, { type: "userTurn", message: "slow burn detective series", fresh: false });
    expect(asking.pageQuery).toBe("a tense heist movie");
    expect(asking.lastQuery).toBe("slow burn detective series");
    // The swap does, because that is the moment the grid changes hands.
    expect(chatReducer(asking, { type: "candidate", data: card(7) }).pageQuery).toBe("slow burn detective series");
    // …and so does a completed answer.
    expect(chatReducer(asking, { type: "done", data: { results: [card(7)], reason: "1 pick" } }).pageQuery)
      .toBe("slow burn detective series");
  });

  it("a_page_extends_an_answer_rather_than_becoming_one", () => {
    // A page replays the question it is paging, so it must not overwrite the field it reads.
    const paged = chatReducer(
      chatReducer(answered, { type: "moreTurn" }),
      { type: "done", data: { results: [card(3)], reason: "1 more", kept: 1 } },
    );
    expect(paged.pageQuery).toBe("a tense heist movie");
  });

  it("a_resume_takes_it_from_the_stored_thread_and_a_fresh_turn_clears_it", () => {
    const resumed = chatReducer(initialChatState, {
      type: "resume",
      payload: {
        id: "conv-9",
        turns: [{ sequence: 0, role: "user", text: "something surreal", kind: "ask", status: "Completed" }],
        cards: [card(1)],
        lastQuery: "something surreal",
      },
    });
    expect(resumed.pageQuery).toBe("something surreal");
    expect(chatReducer(answered, { type: "userTurn", message: "anything", fresh: true }).pageQuery).toBe("");
    expect(chatReducer(answered, { type: "reset" }).pageQuery).toBe("");
  });
});

/**
 * R3-02. The question a page replays is only half of what a page has to say about itself.
 *
 * `kind` was derived server-side by comparing the query to the conversation's LAST USER TURN, and
 * the Stop path makes those two disagree: settle an answer, ask a follow-up, press Stop, press
 * "Show me more". The page correctly replays the answered question — and the server, seeing a
 * question that is not the last thing the user said, recorded a second `ask`. Sixty-six cards on
 * screen came back as twenty-four after a reload, the transcript grew a question nobody typed, and
 * the stopped turn lost its own explanation.
 *
 * So the page names the turn as well: the question AND the row it was asked in, which between them
 * leave the server nothing to infer.
 */
describe("chatReducer — the turn a page extends", () => {
  const conversation = (turnId: string) => ({ type: "conversation" as const, data: { id: "conv-1", turnId } });

  const answered = chatReducer(
    chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "a 90s romantic comedy", fresh: true }),
      conversation("turn-1"),
    ),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks" } },
  );

  it("adopts_the_completed_turns_id_alongside_its_question", () => {
    expect(answered.pageTurnId).toBe("turn-1");
    expect(answered.pageQuery).toBe("a 90s romantic comedy");
  });

  it("a_turn_that_never_completes_is_not_an_answer_to_page", () => {
    // Fixture variation on the test above: the same frames, stopped instead of finished. The
    // `conversation` frame arrives before the turn has produced anything, so adopting the id THERE
    // would let a page name an abandoned turn — which is the whole failure, in mirror image.
    const asking = chatReducer(answered, { type: "userTurn", message: "something tense set at sea", fresh: false });
    const opened = chatReducer(asking, conversation("turn-2"));
    expect(opened.activeTurnId).toBe("turn-2");
    const stopped = chatReducer(opened, { type: "stop" });
    // The grid is still the FIRST answer, and so is everything a page of it would say.
    expect(stopped.pageTurnId).toBe("turn-1");
    expect(stopped.pageQuery).toBe("a 90s romantic comedy");
    expect(stopped.lastQuery).toBe("something tense set at sea");
  });

  it("a_page_extends_the_answer_it_found_rather_than_renaming_it", () => {
    const paged = chatReducer(
      chatReducer(chatReducer(answered, { type: "moreTurn" }), conversation("turn-3")),
      { type: "done", data: { results: [card(3)], reason: "1 more", kept: 1 } },
    );
    expect(paged.pageTurnId).toBe("turn-1");
    expect(paged.pageQuery).toBe("a 90s romantic comedy");
  });

  it("a_server_that_names_no_turn_leaves_the_field_null_rather_than_guessing", () => {
    // The frame carries `id` alone on an older server. A page then sends no `pageOf` and the
    // server falls back to comparing the query — right on every screen except the stopped one.
    const older = chatReducer(
      chatReducer(
        chatReducer(initialChatState, { type: "userTurn", message: "a tense heist", fresh: true }),
        { type: "conversation", data: { id: "conv-2" } },
      ),
      { type: "done", data: { results: [card(1)], reason: "1 pick" } },
    );
    expect(older.pageTurnId).toBeNull();
  });

  it("a_resume_reads_it_off_the_transcript_the_server_replays_by", () => {
    const resumed = chatReducer(initialChatState, {
      type: "resume",
      payload: {
        id: "conv-3",
        turns: [
          { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
          { id: "turn-9", sequence: 1, role: "assistant", text: "Here you go.", kind: "ask", status: "Completed" },
          { id: "turn-10", sequence: 2, role: "assistant", text: "", kind: "page", status: "Completed" },
          { sequence: 3, role: "user", text: "something tense set at sea", kind: "ask", status: "Completed" },
          { id: "turn-11", sequence: 4, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
        ],
        cards: [card(1)],
        lastQuery: "a 90s romantic comedy",
      },
    });
    // The last completed assistant ASK row — not the page after it, and not the failed tail. The
    // same row `answeredQuestion` reads the question off, so the two halves cannot name different
    // turns.
    expect(resumed.pageTurnId).toBe("turn-9");
    expect(resumed.answeredQuery).toBe("a 90s romantic comedy");
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

  it("forgets_a_page_that_a_later_question_replaced", () => {
    // F4-03. `paged` was `turns.some(kind === "page")` — true if the thread was EVER paged, for
    // the rest of its life. So a fresh question asked in a thread that had been paged three turns
    // earlier resumed reading "6 picks so far · last page: 6 picks ranked for you, from 40
    // pulled": the same count stated twice in one sentence, and "last page" on an answer with one
    // page. Worse, it read differently before and after the reload — live, the same answer said
    // the sentence verbatim, because the LIVE `paged` is per-answer and is cleared by a new ask.
    //
    // The grid a resume restores is the last completed ASK plus whatever paged onto it, so the
    // question is not "did this thread ever page" but "did anything page onto the answer on
    // screen".
    const repaged = {
      ...base,
      turns: [
        ...askTurns,
        { sequence: 2, role: "assistant", text: "Three more.", kind: "page", status: "Completed" },
        { sequence: 3, role: "user", text: "something tenser", kind: "ask", status: "Completed" },
        { sequence: 4, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
      ],
    };
    const state = chatReducer(initialChatState, { type: "resume", payload: repaged });
    expect(state.paged).toBe(false);
    expect(footnote(state.reason, 3, state.paged)).toBe("2 picks ranked for you, from 65 pulled");
  });

  it("keeps_the_running_count_when_the_question_after_the_page_was_stopped", () => {
    // R6-01, through the reducer. Fixture variation, one field against
    // `forgets_a_page_that_a_later_question_replaced` above: the tail ask's status. There, a
    // COMPLETED question replaced the answer and the page went with it. Here it was stopped, so
    // nothing replaced anything — the server replays the paged block, hands back the same grid,
    // and the line under it must still account for all of it.
    const stoppedAfterPage = {
      ...base,
      turns: [
        ...askTurns,
        { sequence: 2, role: "assistant", text: "Three more.", kind: "page", status: "Completed" },
        { sequence: 3, role: "user", text: "something tenser", kind: "ask", status: "Failed" },
        { sequence: 4, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
      ],
    };
    const state = chatReducer(initialChatState, { type: "resume", payload: stoppedAfterPage });
    expect(state.paged).toBe(true);
    // The grid is the three cards the payload carries, and the count in the line is that grid —
    // not the 2 the server's own sentence, written for one page, happens to name.
    expect(state.order).toHaveLength(3);
    expect(footnote(state.reason, 3, state.paged))
      .toBe("3 picks so far · last page: 2 picks ranked for you, from 65 pulled");
  });

  it("still_accounts_for_the_grid_when_the_newest_answer_is_the_paged_one", () => {
    // Fixture variation, one row's position: the page row moves to AFTER the second ask. The grid
    // on screen really is several pages now, so the composite line is the true one again.
    const pagedAgain = {
      ...base,
      turns: [
        ...askTurns,
        { sequence: 2, role: "user", text: "something tenser", kind: "ask", status: "Completed" },
        { sequence: 3, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
        { sequence: 4, role: "assistant", text: "Two more.", kind: "page", status: "Completed" },
      ],
    };
    expect(chatReducer(initialChatState, { type: "resume", payload: pagedAgain }).paged).toBe(true);
  });
});

/**
 * The rule on its own, because the reducer case can only exercise it through a whole payload.
 *
 * An `ask` writes a user row and an assistant row together; a `page` writes an assistant row
 * alone. So "the answer on screen was paged" is: a `page` row after the last `ask` row — and
 * `kind` is optional on the wire, where absent has always meant `ask`.
 */
describe("wasPaged", () => {
  const ask = (sequence: number) => [
    { sequence, role: "user", text: "q", kind: "ask", status: "Completed" },
    { sequence: sequence + 1, role: "assistant", text: "a", kind: "ask", status: "Completed" },
  ];
  const page = (sequence: number) => ({ sequence, role: "assistant", text: "a", kind: "page", status: "Completed" });
  /** A stopped question: the user row and the assistant row that never got written (R5-02). */
  const stoppedAsk = (sequence: number) => [
    { sequence, role: "user", text: "q", kind: "ask", status: "Failed" },
    { sequence: sequence + 1, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
  ];
  const pendingAsk = (sequence: number) => [
    { sequence, role: "user", text: "q", kind: "ask", status: "Pending" },
    { sequence: sequence + 1, role: "assistant", text: "", kind: "ask", status: "Pending" },
  ];

  it("is_false_on_an_empty_or_single_ask_transcript", () => {
    expect(wasPaged([])).toBe(false);
    expect(wasPaged(ask(0))).toBe(false);
  });

  it("is_true_only_while_the_page_belongs_to_the_newest_ask", () => {
    expect(wasPaged([...ask(0), page(2)])).toBe(true);
    expect(wasPaged([...ask(0), page(2), ...ask(3)])).toBe(false);
    expect(wasPaged([...ask(0), page(2), ...ask(3), page(5)])).toBe(true);
  });

  it("anchors_on_the_last_COMPLETED_ask,_because_that_is_the_block_the_server_replays", () => {
    // R6-01. The anchor was the last `ask` row of ANY status, and the server replays from the last
    // COMPLETED one — so stopping a question on a paged thread moved the cursor past the answer
    // that is actually on screen, found no page after it, and dropped the running count. The same
    // 49-card grid then printed "4 picks ranked for you, from 79 pulled" as a description of it.
    //
    // The two fixtures differ in ONE field — the tail ask's status — and that field alone must not
    // change what the grid underneath it is. Completed: the page belongs to the replaced answer
    // and the thread is un-paged again (F4-03). Failed: nothing replaced anything, the paged
    // answer is still the one on screen, and the prefix stays.
    expect(wasPaged([...ask(0), page(2), ...stoppedAsk(3)])).toBe(true);
    expect(wasPaged([...ask(0), page(2), ...ask(3)])).toBe(false);
  });

  it("reads_a_pending_tail_the_same_way,_and_ignores_a_page_that_never_landed", () => {
    // The probe on the same field, through its other two values. `Pending` is "not yet", not
    // "instead of": the grid on screen still belongs to the completed ask above it, so the page
    // that landed on it still counts.
    expect(wasPaged([...ask(0), page(2), ...pendingAsk(3)])).toBe(true);
    // …and the filter runs on the page rows too, for the same reason it runs on the ask row: the
    // server's block is COMPLETED assistant rows from the anchor on, so a page that failed added
    // no cards and the grid is one turn's work. Anchoring right while counting loose would put the
    // prefix back on a single-page answer.
    expect(wasPaged([...ask(0), { ...page(2), status: "Failed" }])).toBe(false);
  });

  it("treats_a_missing_kind_as_an_ask", () => {
    // The field is optional on the wire and an older server omits it. Reading absent as "not an
    // ask" would leave the last-ask cursor at -1 and make every page row count.
    const untyped = [
      { sequence: 0, role: "user", text: "q", status: "Completed" },
      { sequence: 1, role: "assistant", text: "a", status: "Completed" },
    ];
    expect(wasPaged(untyped)).toBe(false);
    expect(wasPaged([...untyped, page(2)])).toBe(true);
    expect(wasPaged([page(0), ...untyped])).toBe(false);
  });
});

/**
 * F4-06 — the explanation was the one part of a turn that did not survive a reload.
 *
 * Ask "a 90s romantic comedy" against standing exclusions on both genres and the reply is honest,
 * the notice names the collision and the bubble carries a pill. Reload, and the same question comes
 * back over four post-2000 dramas with `askreel-conflict` and `askreel-reply-conflict` both absent
 * — a screen with nothing left on it saying why the answer looks nothing like the question. The
 * cards were restored from rows and the reason line from a column; the account of WHY was stream-
 * only.
 *
 * The server now stores it on the turn and hands it back on the resume payload. These tests pin
 * the client half — including the shapes an older or half-deployed server sends, because the field
 * is optional and a client that trusts it will render `undefined` into a warning card.
 */
describe("resumedConflict", () => {
  it("reads_the_stored_notice_and_names_both_kinds_of_excluded_term", () => {
    const found = resumedConflict({
      conflict: {
        message: "Your conditions permanently exclude comedy and anime.",
        excludedGenres: ["comedy"],
        excludedKeywords: ["anime"],
        minYear: 2000,
        maxYear: null,
      },
    });
    expect(found).toEqual({
      message: "Your conditions permanently exclude comedy and anime.",
      terms: ["comedy", "anime"],
    });
  });

  it("is_null_when_the_thread_had_no_collision", () => {
    // Which is most threads, and is also every payload from a server that predates the column.
    expect(resumedConflict({})).toBeNull();
    expect(resumedConflict({ conflict: null })).toBeNull();
  });

  it("refuses_a_malformed_or_empty_notice_rather_than_rendering_it", () => {
    // Fixture variation on the one field the card actually prints. A warning-styled box reading
    // "undefined" is worse than no box: it looks like the product failing at the exact moment it
    // is explaining itself.
    expect(resumedConflict({ conflict: {} as never })).toBeNull();
    expect(resumedConflict({ conflict: { message: "   " } })).toBeNull();
    expect(resumedConflict({ conflict: { message: 7 } as never })).toBeNull();
    expect(resumedConflict({ conflict: "no" as never })).toBeNull();
  });

  it("drops_junk_out_of_the_term_list_without_dropping_the_message", () => {
    // The terms are a sentence the page builds; the message is the server's own. One being
    // unusable must not take the other down with it.
    const found = resumedConflict({
      conflict: { message: "Your conditions rule that out.", excludedGenres: ["comedy", "", 4] as never, excludedKeywords: "anime" as never },
    });
    expect(found).toEqual({ message: "Your conditions rule that out.", terms: ["comedy"] });
  });
});

describe("chatReducer — a resumed thread restores its collision notice", () => {
  const payload = {
    id: "conv-9",
    lastCardsAt: "2026-09-11T09:00:00Z",
    cards: [card(1)],
    reason: "1 pick ranked for you, from 40 pulled",
    lastQuery: "a 90s romantic comedy",
    turns: [
      { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "I can't recommend that.", kind: "ask", status: "Completed" },
    ],
    conflict: { message: "Your conditions exclude comedy and romance.", excludedGenres: ["comedy", "romance"] },
  };

  it("puts_the_message_the_terms_and_the_bubble_pill_back", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload });
    expect(state.conflict).toBe("Your conditions exclude comedy and romance.");
    expect(state.conflictTerms).toEqual(["comedy", "romance"]);
    // The pill belongs on the bubble the notice qualifies, which is the newest assistant row —
    // the same row `reason` and the notice itself are about.
    expect(state.turns.at(-1)).toEqual({
      role: "assistant",
      text: "I can't recommend that.",
      conflict: true,
    });
  });

  it("leaves_a_collision_free_thread_unmarked", () => {
    // Fixture variation, one field: the notice removed. A pill on every resumed answer would be
    // worse than none, because it is a warning.
    const clean = { ...payload, conflict: null };
    const state = chatReducer(initialChatState, { type: "resume", payload: clean });
    expect(state.conflict).toBeNull();
    expect(state.conflictTerms).toEqual([]);
    expect(state.turns.at(-1)?.conflict).toBeUndefined();
  });

  it("goes_with_the_answer_it_explains_rather_than_with_the_question", () => {
    // A conflict describes the question that produced it — and it stays on screen exactly as long
    // as that question's ANSWER does. Asking again holds the restored grid (it is all that is on
    // screen for the length of the new turn), so the notice is held with it and released at the
    // same instant: the first candidate of the new turn. Clearing it at ask-time was R5-01 —
    // the grid, the tray and the footnote stayed and the explanation left.
    const resumed = chatReducer(initialChatState, { type: "resume", payload });
    const asking = chatReducer(resumed, { type: "userTurn", message: "something else entirely", fresh: false });
    expect(asking.holdingPrevious).toBe(true);
    expect(asking.conflict).toBe(payload.conflict.message);

    const replaced = chatReducer(asking, { type: "candidate", data: card(9) });
    expect(replaced.conflict).toBeNull();
    expect(replaced.conflictTerms).toEqual([]);
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
/**
 * R3-03. What counts as "the thread just changed" — the question the RTK cache needed answering and
 * nobody was asking, so a completed `/ask` (which streams outside RTK Query entirely) invalidated
 * nothing and every client-side remount of /search re-rendered the payload from the last fetch.
 */
describe("turnSettled", () => {
  it("fires_on_every_way_out_of_a_turn_not_just_the_happy_one", () => {
    // Fixture variation across the four exits: an answer, a Stop, a failure and a 409. Each leaves
    // a row on the server the cached payload does not describe — the Stop and the failure write a
    // `Failed` row with a code, and the 409 means another turn is writing the thread right now.
    for (const next of ["done", "stopped", "error", "blocked", "idle"] as const) {
      expect(turnSettled("streaming", next)).toBe(true);
    }
  });

  it("is_silent_when_no_turn_was_running_and_while_one_still_is", () => {
    // A resume, a mount and a thread switch all change `status` without a turn having finished —
    // invalidating there would refetch the payload the page has only just applied.
    for (const previous of ["idle", "resumed", "done", "stopped", "error", "blocked"] as const) {
      expect(turnSettled(previous, "done")).toBe(false);
    }
    expect(turnSettled("streaming", "streaming")).toBe(false);
  });
});

/**
 * F6-18 + the 44 px audit. The composer was a single-line `<input>` 37 px tall — under the touch
 * minimum at every width, about 36 characters wide on a phone, for a question the server accepts up
 * to 1 000 of. The arithmetic that replaced it is here because the element is not testable in
 * vitest: this runner is node with no renderer.
 */
describe("composerHeight", () => {
  it("never_goes_under_the_touch_minimum_or_over_four_lines", () => {
    // Fixture variation across the three regimes: below the floor, between, above the ceiling.
    expect(composerHeight(20)).toBe(COMPOSER_MIN_PX);
    expect(composerHeight(COMPOSER_MIN_PX)).toBe(COMPOSER_MIN_PX);
    expect(composerHeight(68)).toBe(68);
    expect(composerHeight(400)).toBe(COMPOSER_MAX_PX);
    expect(COMPOSER_MIN_PX).toBeGreaterThanOrEqual(44);
  });

  it("rounds_up_and_survives_a_measurement_that_is_not_a_number", () => {
    // Sub-pixel line heights are ordinary; a NaN is what an element that has not laid out yet
    // reports, and falling back to the resting height is better than writing "NaNpx".
    expect(composerHeight(67.2)).toBe(68);
    expect(composerHeight(Number.NaN)).toBe(COMPOSER_MIN_PX);
    expect(composerHeight(Number.POSITIVE_INFINITY)).toBe(COMPOSER_MIN_PX);
  });
});

describe("composerSends", () => {
  it("sends_on_Enter_and_breaks_the_line_on_Shift_Enter", () => {
    expect(composerSends("Enter", false, false)).toBe(true);
    expect(composerSends("Enter", true, false)).toBe(false);
    expect(composerSends("a", false, false)).toBe(false);
  });

  it("never_sends_the_Enter_that_confirms_an_IME_candidate", () => {
    // One field different from the sending case. On a Japanese, Chinese or Korean keyboard that
    // Enter commits the candidate the user is still choosing; sending there submits half a word.
    expect(composerSends("Enter", false, true)).toBe(false);
  });
});

/**
 * F6-15. `askreel-status` was `""` on every resumed landing — six sampled in R3's session — while a
 * seam, a grid, a tray, a footnote and a conflict notice were on screen. Correct on a completed
 * turn and after a Stop; silent on the one state where the most appears unprompted.
 */
describe("resumeAnnouncement", () => {
  it("says_what_came_back_and_how_much_of_it", () => {
    expect(resumeAnnouncement(21, 0)).toBe("Picked up where you left off · 21 picks.");
    expect(resumeAnnouncement(1, 0)).toBe("Picked up where you left off · 1 pick.");
  });

  it("counts_the_outside_channel_where_it_is_named", () => {
    // The same rule as the live answer (F4-05): a closest-but-outside card is on screen and is
    // explicitly not a pick, so it is announced as what it is.
    expect(resumeAnnouncement(9, 3))
      .toBe("Picked up where you left off · 9 picks. 3 closest-but-outside shown separately.");
  });

  it("a_thread_with_nothing_stored_says_so_rather_than_announcing_a_zero", () => {
    // Fixture variation: the transcript-only thread, which is a fact about the THREAD and not an
    // empty answer — "no matches" would be a verdict on a question this visit never asked.
    expect(resumeAnnouncement(0, 0)).toBe("Picked up where you left off · no saved picks.");
  });
});

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

  it("reports_an_abandoned_turn_without_claiming_the_user_pressed_anything", () => {
    const stopped = [
      ...completed,
      { sequence: 2, role: "user", text: "something surreal and dreamlike", kind: "ask", status: "Failed" },
      { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
    ];
    expect(lastTurnOutcome(stopped)).toEqual({ status: "failed", failureCode: "client_aborted", turnId: null });
    // F6-09. `client_aborted` is written for a Stop AND for a reload, a slept phone and a
    // discarded tab — the abort reaches the server identically and it cannot tell them apart. The
    // copy said "you stopped it" for all four, and a finder who only reloaded read it as a lie.
    expect(failedTurnMessage("client_aborted")).toBe("That one didn’t finish before you left.");
    expect(failedTurnMessage("client_aborted")).not.toContain("you stopped it");
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
    expect(failedTurnMessage("HttpRequestException")).toBe("That one hit a snag.");
  });

  it("keeps_the_three_messages_distinguishable_from_each_other", () => {
    // Three codes, three sentences, and none of them a prefix of another: the whole value of this
    // function is that a reader can tell which of the three things happened to them.
    const said = [failedTurnMessage("client_aborted"), failedTurnMessage("timeout"), failedTurnMessage(null)];
    expect(new Set(said).size).toBe(3);
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

describe("answeredTurnId", () => {
  it("names_the_row_answeredQuestion_reads_the_question_off", () => {
    const rows = [
      { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
      { id: "turn-9", sequence: 1, role: "assistant", text: "Here.", kind: "ask", status: "Completed" },
      { id: "turn-10", sequence: 2, role: "assistant", text: "", kind: "page", status: "Completed" },
    ];
    expect(answeredTurnId(rows)).toBe("turn-9");
    expect(answeredQuestion(rows)).toBe("a 90s romantic comedy");
  });

  it("accepts_either_spelling_and_says_null_when_the_server_sends_neither", () => {
    // Fixture variation: the SSE frame calls it `turnId` and the resume payload calls it `id`, and
    // an older payload carries neither — where null is the honest answer, because a guessed id
    // would make the server extend a turn the user is not looking at.
    const base = [
      { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
    ];
    expect(answeredTurnId([...base, { turnId: "turn-4", sequence: 1, role: "assistant", text: "", kind: "ask", status: "Completed" }]))
      .toBe("turn-4");
    expect(answeredTurnId([...base, { sequence: 1, role: "assistant", text: "", kind: "ask", status: "Completed" }]))
      .toBeNull();
    // Nothing completed at all: there is no answer, so there is nothing to be a page of.
    expect(answeredTurnId([...base, { id: "turn-5", sequence: 1, role: "assistant", text: "", kind: "ask", status: "Failed" }]))
      .toBeNull();
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
    // …and the unanswered question is still in the transcript, because it is still what was asked —
    // followed by the turn that never answered it. That row used to be dropped for having no text
    // (R5-02), which left the failure visible only while it was the tail.
    expect(state.turns.map((t) => t.text)).toEqual([
      "quiet character dramas", "Three that fit.", "something surreal and dreamlike", "",
    ]);
    expect(state.turns.at(-1)).toEqual({ role: "assistant", text: "", failed: true, failureCode: "client_aborted" });
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

/**
 * The tray survives a reload, or the floor is decorative across one.
 *
 * `belowFloor` has been on the resume payload since the turn record landed — the server stores the
 * cards its floor cut as rows of their own, precisely so a reloaded thread comes back whole — and
 * the reducer dropped it. What the user saw was a "Show the 3 under your 6.0 bar" fence on a live
 * answer and nothing at all on the same answer one reload later: the one cut on the screen they
 * can act on (it quotes a bar THEY set) quietly stopped being accounted for.
 *
 * Three fixtures, one field apart, because absent / empty / populated are three different
 * statements and only the middle one is a zero. `floorHeldBack` renders the fence off the count,
 * so an absent field becoming `0` would suppress a fence an older server still owes us, and an
 * empty array becoming `null` would make the tray fall back to a scalar that is not there either.
 */
describe("chatReducer — the tray comes back on a resume", () => {
  const base = {
    id: "conv-t",
    lastCardsAt: "2026-09-11T09:00:00Z",
    cards: [card(1), card(2)],
    turns: [
      { sequence: 0, role: "user", text: "something tense", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "Two of those.", kind: "ask", status: "Completed" },
    ],
    reason: "2 picks ranked for you, from 40 pulled.",
    lastQuery: "something tense",
    shown: [1, 2, 8, 9, 10],
  };

  it("resume_restores_the_tray_and_keeps_it_out_of_the_grid", () => {
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...base, belowFloor: [card(8), card(9), card(10)] },
    });
    expect(state.belowFloor.map((c) => c.titleId)).toEqual(["t8", "t9", "t10"]);
    // The contract's own line: never merged into `cards`. The grid is the two that cleared the bar.
    expect(state.order).toEqual(["t1", "t2"]);
    expect(state.byId.t8).toBeUndefined();
    // The label the fence prints. It is the tray's own contents on a resume, because the server
    // hands the whole stored block back — a number the user can open the fence and count.
    expect(state.droppedByFloor).toBe(3);
    expect(floorHeldBack(state.belowFloor.length, state.droppedByFloor)).toBe(3);
  });

  it("the_trays_cards_go_through_the_same_guard_every_streamed_card_does", () => {
    // Fixture variation inside the tray: one good row, one that is not a card at all. A stored row
    // reaching `ResultCard` without a titleId renders a keyless undefined; the guard is the one the
    // stream already applies to `done.belowFloor`, so both paths reject the same shapes.
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...base, belowFloor: [card(8), { titleId: "t9" } as unknown as AskCard] },
    });
    expect(state.belowFloor.map((c) => c.titleId)).toEqual(["t8"]);
    // …and the count follows the contents rather than the payload's length, so the fence cannot
    // promise a card it just threw away.
    expect(state.droppedByFloor).toBe(1);
  });

  it("an_empty_tray_is_a_zero_and_an_absent_one_is_silence", () => {
    // The probe: the same payload with `belowFloor: []`, and with the field gone entirely.
    const empty = chatReducer(initialChatState, { type: "resume", payload: { ...base, belowFloor: [] } });
    expect(empty.belowFloor).toEqual([]);
    // A server that sent the field and no cards has TOLD us the floor cut nothing.
    expect(empty.droppedByFloor).toBe(0);
    expect(floorHeldBack(empty.belowFloor.length, empty.droppedByFloor)).toBe(0);

    const silent = chatReducer(initialChatState, { type: "resume", payload: base });
    expect(silent.belowFloor).toEqual([]);
    // A server that sent no field has told us nothing, which is not a zero — see `floorHeldBack`.
    expect(silent.droppedByFloor).toBeNull();
    expect(floorHeldBack(silent.belowFloor.length, silent.droppedByFloor)).toBe(0);
  });

  it("asking_anything_clears_the_resumed_tray", () => {
    // Last question's cut must never label this question's fence. Same rule the live `done` path
    // keeps; the resumed tray is now subject to it because it is now set at all.
    const resumed = chatReducer(initialChatState, {
      type: "resume",
      payload: { ...base, belowFloor: [card(8), card(9), card(10)] },
    });
    // …at the moment the grid it labels is replaced, not at the moment the question is asked: the
    // resumed answer is still on screen while the new turn works, and its fence is part of it
    // (F6-06). A fresh turn takes the whole screen and clears at once.
    const follow = chatReducer(resumed, { type: "userTurn", message: "something else", fresh: false });
    expect(follow.belowFloor).toHaveLength(3);
    expect(chatReducer(follow, { type: "candidate", data: card(1) }).belowFloor).toEqual([]);
    const reasked = chatReducer(resumed, { type: "reaskTurn" });
    expect(reasked.belowFloor).toHaveLength(3);
    expect(chatReducer(reasked, { type: "candidate", data: card(1) }).belowFloor).toEqual([]);
    expect(chatReducer(resumed, { type: "userTurn", message: "brand new", fresh: true }).droppedByFloor).toBeNull();
  });
});

/**
 * F6-01 — a frame from a run the screen has walked away from.
 *
 * The observed damage was durable, not cosmetic. `Recent ▾` stayed live while a turn streamed, so
 * opening another thread mid-stream put that thread's question and reply on screen above the
 * running turn's banner and 34 cards from the question just left; fourteen seconds later the heist
 * results landed IN the rom-com thread, replaced its answer, and pushed its turn count from 2 to 3.
 * Re-opening it from a clean context still showed ten heist cards under a rom-com question, and the
 * heist question existed nowhere at all.
 */
describe("chatReducer — a stale stream's frames change nothing", () => {
  const A = "conv-a";
  const B = "conv-b";
  const tagA: StreamTag = { id: 101, conversationId: A };

  /** Thread A, mid-stream: asked, conversation frame in, two candidates on screen. */
  function streamingA() {
    let state = chatReducer(initialChatState, { type: "userTurn", message: "a heist with a great ensemble", fresh: false, stream: tagA });
    state = chatReducer(state, { type: "conversation", data: { id: A }, stream: tagA });
    state = chatReducer(state, { type: "candidate", data: card(1), stream: tagA });
    return chatReducer(state, { type: "candidate", data: card(2), stream: tagA });
  }

  it("drops_every_frame_of_the_abandoned_run_after_a_thread_switch", () => {
    const live = streamingA();
    expect(live.conversationId).toBe(A);
    expect(live.order).toEqual(["t1", "t2"]);

    // The switch: thread B is put on screen whole, exactly as `resume` does it.
    const switched = chatReducer(live, {
      type: "resume",
      payload: {
        id: B,
        turns: [
          { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "I can't recommend those.", kind: "ask", status: "Completed" },
        ],
        cards: [],
        lastQuery: "a 90s romantic comedy",
      },
    });
    expect(switched.conversationId).toBe(B);

    // …and now every frame the abandoned heist run is still producing. Each is asserted against
    // the SAME baseline, so a guard that only happens to catch `done` fails here.
    const after = [
      chatReducer(switched, { type: "phase", data: { stage: "ranking", found: 34, scored: 34 }, stream: tagA }),
      chatReducer(switched, { type: "assistant", data: { text: "Seven heists that fit." }, stream: tagA }),
      chatReducer(switched, { type: "candidate", data: card(9), stream: tagA }),
      chatReducer(switched, { type: "scored", data: { titleId: "t9", predictedRating: 8.4 }, stream: tagA }),
      chatReducer(switched, { type: "reranked", data: { titleId: "t9", fit: 0.9, why: "heist" }, stream: tagA }),
      chatReducer(switched, { type: "conflict", data: { message: "…" }, stream: tagA }),
      chatReducer(switched, { type: "conversation", data: { id: A }, stream: tagA }),
      chatReducer(switched, { type: "done", data: { results: [card(9), card(10)], reason: "10 picks" }, stream: tagA }),
    ];
    for (const state of after) expect(state).toBe(switched);
  });

  it("drops_a_run_started_before_any_conversation_existed", () => {
    // The harder half: a FRESH turn is tagged with a null conversation id, so the id comparison
    // alone cannot see it. Only the run number can — which is why `initialChatState.stream` is 0
    // and minted ids start at 1.
    const fresh: StreamTag = { id: 202, conversationId: null };
    let live = chatReducer(initialChatState, { type: "userTurn", message: "a heist", fresh: true, stream: fresh });
    live = chatReducer(live, { type: "candidate", data: card(1), stream: fresh });
    const switched = chatReducer(live, {
      type: "resume",
      payload: {
        id: B,
        turns: [{ sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" }],
        cards: [],
      },
    });
    expect(switched.stream).toBe(0);
    expect(chatReducer(switched, { type: "done", data: { results: [card(9)], reason: "1 pick" }, stream: fresh })).toBe(switched);
  });

  it("drops_the_previous_run_when_a_new_turn_takes_over_the_same_thread", () => {
    // Fixture variation on the route OUT of a run: not a switch but a re-ask. The first run's
    // `done` must not resurrect the answer the second run is replacing.
    const live = streamingA();
    const tagB: StreamTag = { id: 303, conversationId: A };
    const reasked = chatReducer(live, { type: "reaskTurn", stream: tagB });
    expect(reasked.stream).toBe(303);
    expect(chatReducer(reasked, { type: "done", data: { results: [card(5)], reason: "stale" }, stream: tagA })).toBe(reasked);
    // …and the run that IS current still lands.
    expect(chatReducer(reasked, { type: "done", data: { results: [card(5)], reason: "live" }, stream: tagB }).reason).toBe("live");
  });

  it("still_accepts_every_frame_of_the_run_that_is_current", () => {
    // The guard has to be inert on the happy path, which is every other test in this file: an
    // untagged dispatch and a correctly-tagged one both go through.
    const live = streamingA();
    expect(chatReducer(live, { type: "candidate", data: card(3), stream: tagA }).order).toEqual(["t1", "t2", "t3"]);
    expect(chatReducer(live, { type: "candidate", data: card(3) }).order).toEqual(["t1", "t2", "t3"]);
  });
});

describe("staleFrame", () => {
  it("is_inert_without_a_tag_and_strict_with_one", () => {
    const base = { ...initialChatState, stream: 7, conversationId: "conv-a" };
    // No tag at all — the reducer's own tests dispatch this way and must not be turned into no-ops.
    expect(staleFrame(base, undefined)).toBe(false);
    // The run this state is listening to.
    expect(staleFrame(base, { id: 7, conversationId: "conv-a" })).toBe(false);
    // A different run.
    expect(staleFrame(base, { id: 6, conversationId: "conv-a" })).toBe(true);
    // The right run number naming the wrong thread — the durable half of F6-01. Cannot happen
    // through `run()`, and is checked anyway because it is the fact that actually matters.
    expect(staleFrame(base, { id: 7, conversationId: "conv-b" })).toBe(true);
    // A run that has not learnt its conversation yet is judged on its number alone.
    expect(staleFrame(base, { id: 7, conversationId: null })).toBe(false);
    // …and so is a state that has not learnt one.
    expect(staleFrame({ ...base, conversationId: null }, { id: 7, conversationId: "conv-b" })).toBe(false);
  });
});

describe("mintStreamId", () => {
  it("never_returns_zero_and_never_repeats", () => {
    // `0` is the "no run" sentinel every reset lands on, so a minted id colliding with it would
    // make an abandoned stream's frames live again.
    const ids = [mintStreamId(), mintStreamId(), mintStreamId()];
    expect(new Set(ids).size).toBe(3);
    for (const id of ids) expect(id).toBeGreaterThan(0);
    expect(ids[1]).toBeGreaterThan(ids[0]);
  });
});

/**
 * F6-02 — the page that never scrolled. `scrollY` stayed at 0 for a whole 19.4-second turn, so a
 * 1 000-character question filled the viewport and the banner, the narration, the progress bar and
 * every card sat below the fold for the entire time.
 */
describe("scrollTargetFor", () => {
  it("moves_the_page_when_a_turn_starts_and_when_a_stop_ends_one", () => {
    expect(scrollTargetFor("streaming")).toBe("live");
    expect(scrollTargetFor("stopped")).toBe("settled");
  });

  it("leaves_the_scroll_alone_everywhere_else", () => {
    // `done` is the one that matters here: by then the reader IS reading, and snapping the page
    // under them at the moment the answer settles is the opposite of the fix. The rest are states
    // the page enters without changing what it is about.
    for (const status of ["idle", "done", "resumed", "blocked", "error"] as const) {
      expect(scrollTargetFor(status)).toBeNull();
    }
  });
});

describe("bubbleOverflows", () => {
  it("ignores_sub_pixel_rounding_and_reports_a_real_cut", () => {
    // A bubble showing everything, with `line-height` rounding against it.
    expect(bubbleOverflows(240, 240)).toBe(false);
    expect(bubbleOverflows(243, 240)).toBe(false);
    // The 1 000-character question: ~1 100 px of text inside six lines of box.
    expect(bubbleOverflows(1104, 144)).toBe(true);
    // One line over — the smallest cut a reader would notice, and still a cut.
    expect(bubbleOverflows(168, 144)).toBe(true);
  });
});

describe("prefersReducedMotion", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("answers_no_where_matchMedia_is_absent_or_throws", () => {
    // vitest runs in node: `window` may be missing entirely, present without `matchMedia`, or —
    // in an older embedded webview — present with one that throws on an unknown feature query.
    // A scroll must never be taken down by any of the three.
    expect(prefersReducedMotion()).toBe(false);
    vi.stubGlobal("window", {});
    expect(prefersReducedMotion()).toBe(false);
    vi.stubGlobal("window", { matchMedia: () => { throw new Error("nope"); } });
    expect(prefersReducedMotion()).toBe(false);
    vi.stubGlobal("window", { matchMedia: (q: string) => ({ matches: q.includes("reduce") }) });
    expect(prefersReducedMotion()).toBe(true);
  });
});

/**
 * F6-04 — the grid was zero-height until the first card, so the document went 837 px → 4 482 px in
 * one frame and the turn scored CLS 0.672 against a 0.25 "poor" boundary.
 */
describe("skeletonCount", () => {
  it("reserves_eight_by_default_and_never_more", () => {
    // Nothing found yet — which is most of a turn, and the whole of it before `discovered` lands.
    expect(skeletonCount(null)).toBe(SKELETON_CAP);
    expect(skeletonCount({ stage: "searching", found: 0, scored: 0 })).toBe(SKELETON_CAP);
    // A big turn is capped: reserving 24 rows for an answer that will keep ten is the same shift
    // pointing downward.
    expect(skeletonCount({ stage: "discovered", found: 24, scored: 0 })).toBe(SKELETON_CAP);
    expect(skeletonCount({ stage: "discovered", found: 94, scored: 0 })).toBe(SKELETON_CAP);
  });

  it("reserves_only_what_a_small_turn_will_fill", () => {
    // Fixture variation across the cap: a narrow first turn genuinely does find one title.
    expect(skeletonCount({ stage: "discovered", found: 1, scored: 0 })).toBe(1);
    expect(skeletonCount({ stage: "discovered", found: 3, scored: 0 })).toBe(3);
    expect(skeletonCount({ stage: "scoring", found: 8, scored: 4 })).toBe(8);
    expect(skeletonCount({ stage: "scoring", found: 9, scored: 4 })).toBe(SKELETON_CAP);
  });
});

/**
 * F4-08 — 23–24 posters sat on screen for 11.5 to 18.6 seconds and then collapsed to 10, with no
 * transition and no announcement, the remainder folded into a collapsed tray. The client cannot
 * rank, but it can apply the one standing condition the user set, at the moment it learns the
 * score — and it must apply it to that turn's cards ONLY.
 */
describe("floorPartition — bounded to the turn still streaming", () => {
  const settled = card(1, { predictedRating: 4.0 });
  const streamingUnder = card(2, { predictedRating: 5.2 });
  const streamingOver = card(3, { predictedRating: 7.4 });
  const all = [settled, streamingUnder, streamingOver];
  const unsettled = new Set(["t2", "t3"]);

  it("holds_back_only_the_cards_the_server_has_not_ranked", () => {
    // `t1` is under the bar and stays: it is finished work the server already floored and
    // committed, which is exactly what a stopped PAGE's earlier answer and a held-then-restored
    // previous answer both are. Re-flooring them takes cards out of a grid the server shipped.
    const bounded = floorPartition(all, 6, unsettled);
    expect(bounded.kept.map((c) => c.titleId)).toEqual(["t1", "t3"]);
    expect(bounded.held.map((c) => c.titleId)).toEqual(["t2"]);
  });

  it("is_unchanged_without_a_bound", () => {
    // The absent-set case is the whole grid, which is what a turn with nothing settled behind it
    // wants — and is the behaviour every existing caller had.
    const whole = floorPartition(all, 6);
    expect(whole.kept.map((c) => c.titleId)).toEqual(["t3"]);
    expect(whole.held.map((c) => c.titleId)).toEqual(["t1", "t2"]);
  });

  it("keeps_badge_precision_and_unscored_cards_inside_the_bound", () => {
    // 5.96 prints as "6.0", so holding it back would put a card badged 6.0 inside a tray labelled
    // "under your 6.0 bar"; an unscored card is evidence of nothing. Both rules have to survive
    // the new parameter rather than being bypassed by it.
    const rounds = card(4, { predictedRating: 5.96 });
    const unscored = card(5, { predictedRating: null });
    const bounded = floorPartition([rounds, unscored], 6, new Set(["t4", "t5"]));
    expect(bounded.held).toEqual([]);
    expect(bounded.kept.map((c) => c.titleId)).toEqual(["t4", "t5"]);
  });

  it("an_empty_bound_holds_nothing_back_at_all", () => {
    // The held-previous-answer state: every card on screen is settled, so the client's own pass is
    // a no-op without the page having to test `holdingPrevious` separately.
    expect(floorPartition(all, 6, new Set()).held).toEqual([]);
  });
});

/**
 * R5-03 — the two halves of a page request must name ONE turn, on a resume as much as live.
 *
 * `pageTurnId` came off the last completed ask (correct) while `pageQuery` fell back to
 * `lastQuery`, which the contract defines as the last USER row — so on any thread whose last
 * question was stopped, "Show me more" went out carrying the kept answer's turn id and the
 * ABANDONED question. The server refused it `409 page_query_mismatch`, correctly, and paging was
 * permanently dead on that thread: every reload reproduced it.
 *
 * The two are read off one row here, so the shape that produced R5-03 is unrepresentable rather
 * than merely absent.
 */
describe("resumedPageTarget — the question and the row the grid belongs to", () => {
  const rows = [
    { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "I can’t do a 90s romantic comedy.", kind: "ask", status: "Completed", id: "turn-romcom" },
    { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted", id: "turn-sea" },
  ];

  it("reads_both_halves_off_the_completed_ask_when_the_tail_failed", () => {
    expect(resumedPageTarget(rows, "something tense set at sea")).toEqual({
      query: "a 90s romantic comedy",
      turnId: "turn-romcom",
    });
  });

  it("is_the_last_question_on_an_ordinary_resume", () => {
    // Fixture variation, ONE field: the tail's status. Nothing else about the thread moves, so a
    // pass here cannot come from the row's position or its text.
    const settled = [
      ...rows.slice(0, 3).map((r, i) => (i === 2 ? { ...r, status: "Completed" } : r)),
      { ...rows[3], text: "Two that fit.", status: "Completed", failureCode: null },
    ];
    expect(resumedPageTarget(settled, "something tense set at sea")).toEqual({
      query: "something tense set at sea",
      turnId: "turn-sea",
    });
  });

  it("falls_back_to_the_server_s_lastQuery_when_no_ask_completed", () => {
    // An older server sends no `status` at all, so no row can be read as the completed ask. The
    // fallback is the previous behaviour exactly — the server then derives `kind` by comparison.
    const old = rows.map(({ status: _status, ...rest }) => rest);
    expect(resumedPageTarget(old, "something tense set at sea")).toEqual({
      query: "something tense set at sea",
      turnId: null,
    });
  });
});

describe("chatReducer — a resumed page names the answer's own question", () => {
  const rows = [
    { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "I can’t do a 90s romantic comedy.", kind: "ask", status: "Completed", id: "turn-romcom" },
    { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted", id: "turn-sea" },
  ];
  const payload = {
    id: "conv-r5",
    lastCardsAt: "2026-09-11T09:00:00Z",
    turns: rows,
    cards: [card(1), card(2)],
    reason: "2 picks ranked for you, from 40 pulled",
    lastQuery: "something tense set at sea",
  };

  it("pages_the_answered_question_and_re_asks_the_stopped_one", () => {
    const state = chatReducer(initialChatState, { type: "resume", payload });
    // "Ask again" replays the question that never got an answer…
    expect(state.lastQuery).toBe("something tense set at sea");
    // …and "Show me more" pages the one the cards on screen belong to, naming its row.
    expect(state.pageQuery).toBe("a 90s romantic comedy");
    expect(state.pageTurnId).toBe("turn-romcom");
  });

  it("keeps_the_two_halves_on_one_row_when_the_tail_is_pending", () => {
    // Fixture variation, one field: `Failed` → `Pending`. A turn still running is no more the
    // thing a page extends than a failed one is.
    const pending = {
      ...payload,
      turns: [
        ...rows.slice(0, 3).map((r, i) => (i === 2 ? { ...r, status: "Pending" } : r)),
        { ...rows[3], status: "Pending", failureCode: null },
      ],
    };
    const state = chatReducer(initialChatState, { type: "resume", payload: pending });
    expect(state.pageQuery).toBe("a 90s romantic comedy");
    expect(state.pageTurnId).toBe("turn-romcom");
  });
});

/**
 * R5-04 — a 409 is four different refusals, and the client read all of them as one.
 *
 * `page_query_mismatch` was announced to the live region as "That question is still being
 * answered." — nothing was running — and the `blocked` status it landed on withdrew the footnote
 * AND the paging control, so the user could not even retry. Only a reload restored them, which
 * reproduced R5-03.
 */
describe("failureFor — a refused page is not a turn in progress", () => {
  it("gives_page_query_mismatch_its_own_action", () => {
    expect(failureFor(new AskStreamError(409, "page_query_mismatch"))).toEqual({
      type: "pageRefused", refusal: "mismatch",
    });
  });

  it("leaves_turn_in_progress_alone", () => {
    // The other 409 is unchanged: something really is running, and "Check again" is the right offer.
    expect(failureFor(new AskStreamError(409, "turn_in_progress"))).toEqual({ type: "turnInProgress" });
    // …and an uncoded 409 still falls back to it rather than claiming a refusal it cannot name.
    expect(failureFor(new AskStreamError(409, null))).toEqual({ type: "turnInProgress" });
  });
});

describe("chatReducer — a refused page keeps the answer whole", () => {
  const resumed = chatReducer(initialChatState, {
    type: "resume",
    payload: {
      id: "conv-r5b",
      lastCardsAt: "2026-09-11T09:00:00Z",
      turns: [
        { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
        { sequence: 1, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed", id: "turn-romcom" },
      ],
      cards: [card(1), card(2)],
      belowFloor: [card(9)],
      reason: "2 picks ranked for you, from 40 pulled",
      lastQuery: "a 90s romantic comedy",
    },
  });

  it("puts_the_screen_back_where_it_was_with_its_footnote_and_its_control", () => {
    const paging = chatReducer(resumed, { type: "moreTurn" });
    const refused = chatReducer(paging, { type: "pageRefused", refusal: "mismatch" });
    // Back to the state the page was started from — NOT `blocked`, which is what withdrew the
    // footnote and the control, and not `error`, which would claim the answer had gone.
    expect(refused.status).toBe("resumed");
    expect(refused.pageRefusal).toBe("mismatch");
    expect(refused.appending).toBe(false);
    // The answer is untouched, all the way down to the tray and the sentence under it.
    expect(refused.order).toEqual(resumed.order);
    expect(refused.reason).toBe(resumed.reason);
    expect(refused.belowFloor).toEqual(resumed.belowFloor);
    expect(refused.exhausted).toBe(false);
    // …and the control is offered again, so the refusal is recoverable without a reload.
    expect(canPage(refused.status, refused.holdingPrevious, refused.exhausted, refused.order.length, refused.settled)).toBe(true);
  });

  it("clears_the_refusal_the_moment_anything_is_asked", () => {
    const refused = chatReducer(chatReducer(resumed, { type: "moreTurn" }), { type: "pageRefused", refusal: "mismatch" });
    for (const action of [
      { type: "userTurn", message: "something else", fresh: false },
      { type: "reaskTurn" },
      { type: "moreTurn" },
    ] as const) {
      expect(chatReducer(refused, action).pageRefusal).toBeNull();
    }
  });

  it("says_what_happened_in_words_the_refusal_can_support", () => {
    expect(pageRefusalMessage("mismatch")).toBe("That page no longer matches its answer — ask again.");
    expect(pageRefusalMessage(null)).toBeNull();
  });
});

/**
 * R5-02 — a turn that did not finish says so wherever it sits, not only at the end.
 *
 * `transcriptOf` dropped every empty-text row on the floor, and a failed turn keeps its empty text
 * for good — so the only thing that ever named a failure was `resumedTail`, which reads the LAST
 * row. Page the kept answer, or ask anything else, and the stopped turn stopped being the tail: the
 * reload then rendered a bare user bubble with nothing under it at all, and live it rendered the
 * `assistant-message` that had streamed before the abort, presented as that turn's answer. The
 * database said `Failed` / `client_aborted` / zero cards; the product showed a promise and never
 * said it was not kept.
 */
describe("transcriptOf — a failed turn is a row, not an absence", () => {
  const rows = [
    { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
    { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
    { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted" },
    { sequence: 4, role: "user", text: "something older", kind: "ask", status: "Completed" },
    { sequence: 5, role: "assistant", text: "Two older ones.", kind: "ask", status: "Completed" },
  ];

  it("keeps_a_failed_row_in_the_middle_of_a_thread", () => {
    const turns = transcriptOf(rows);
    expect(turns.map((t) => t.text)).toEqual([
      "a 90s romantic comedy", "Three that fit.", "something tense set at sea", "", "something older", "Two older ones.",
    ]);
    expect(turns[3]).toEqual({ role: "assistant", text: "", failed: true, failureCode: "client_aborted" });
    // …and only that one. A completed answer is not a failure however quiet its sentence is.
    expect(turns.filter((t) => t.failed)).toHaveLength(1);
  });

  it("says_nothing_of_the_kind_when_that_turn_completed", () => {
    // Fixture variation, ONE field: the middle assistant row's status. It gains the text a
    // completed turn would have, which is exactly what makes the old behaviour indistinguishable —
    // dropping empty rows happened to be right whenever the turn had finished.
    const settled = rows.map((r, i) =>
      i === 2 ? { ...r, status: "Completed" }
        : i === 3 ? { ...r, text: "Here’s a tense sea-set pick.", status: "Completed", failureCode: null }
          : r);
    const turns = transcriptOf(settled);
    expect(turns.filter((t) => t.failed)).toHaveLength(0);
    expect(turns[3].text).toBe("Here’s a tense sea-set pick.");
  });

  it("still_drops_a_page_row_and_a_turn_with_nothing_to_say", () => {
    // The two rules this must not have broken: a `page` writes an assistant row for a question two
    // bubbles up (SB-04), and a `Pending` row has no text YET rather than for good.
    const turns = transcriptOf([
      ...rows.slice(0, 2),
      { sequence: 2, role: "assistant", text: "Three more.", kind: "page", status: "Completed" },
      { sequence: 3, role: "user", text: "something older", kind: "ask", status: "Pending" },
      { sequence: 4, role: "assistant", text: "", kind: "ask", status: "Pending" },
    ]);
    expect(turns.map((t) => t.text)).toEqual(["a 90s romantic comedy", "Three that fit.", "something older"]);
  });
});

describe("stoppedTranscript — the reply that streamed before the abort is not the answer", () => {
  it("turns_the_half_written_reply_into_the_failure_it_is", () => {
    // R5-02, live: the `assistant-message` arrived at 2 578 ms and the abort at 4 118 ms, and the
    // transcript read "Reel said: Here's a tense sea-set pick…" over a turn with zero cards.
    const turns: ChatTurn[] = [
      { role: "user", text: "a 90s romantic comedy" },
      { role: "assistant", text: "Three that fit." },
      { role: "user", text: "something tense set at sea" },
      { role: "assistant", text: "Here’s a tense sea-set pick." },
    ];
    expect(stoppedTranscript(turns, false)).toEqual([
      ...turns.slice(0, 3),
      { role: "assistant", text: "", failed: true, failureCode: "client_aborted" },
    ]);
  });

  it("records_the_failure_even_when_nothing_was_said_at_all", () => {
    const turns: ChatTurn[] = [{ role: "user", text: "something tense set at sea" }];
    expect(stoppedTranscript(turns, false)).toEqual([
      ...turns,
      { role: "assistant", text: "", failed: true, failureCode: "client_aborted" },
    ]);
  });

  it("leaves_the_transcript_alone_when_a_page_was_stopped", () => {
    // A page adds no transcript row, so the trailing assistant row belongs to the answer it was
    // extending — finished work, and not the thing that was abandoned.
    const turns: ChatTurn[] = [
      { role: "user", text: "a 90s romantic comedy" },
      { role: "assistant", text: "Three that fit." },
    ];
    expect(stoppedTranscript(turns, true)).toEqual(turns);
  });
});

describe("chatReducer — a stopped turn's reply is not presented as an answer", () => {
  const asked = chatReducer(initialChatState, { type: "userTurn", message: "something tense set at sea", fresh: true });

  it("replaces_the_reply_with_the_failure_row", () => {
    const replied = chatReducer(asked, { type: "assistant", data: { text: "Here’s a tense sea-set pick.", conflict: false } });
    expect(replied.turns.at(-1)).toEqual({ role: "assistant", text: "Here’s a tense sea-set pick.", conflict: false });
    const stopped = chatReducer(replied, { type: "stop" });
    expect(stopped.status).toBe("stopped");
    expect(stopped.turns.map((t) => t.text)).toEqual(["something tense set at sea", ""]);
    expect(stopped.turns.at(-1)?.failed).toBe(true);
  });

  it("does_not_touch_a_previous_answer_when_a_page_is_stopped", () => {
    // Fixture variation: the same Stop, one field of state apart — `appending`.
    const paging = chatReducer(
      { ...asked, turns: [{ role: "user", text: "a 90s romantic comedy" }, { role: "assistant", text: "Three that fit." }], order: ["a"], settled: 1 },
      { type: "moreTurn" },
    );
    const stopped = chatReducer(paging, { type: "stop" });
    expect(stopped.turns.map((t) => t.text)).toEqual(["a 90s romantic comedy", "Three that fit."]);
    expect(stopped.turns.some((t) => t.failed)).toBe(false);
  });
});

describe("showsInlineFailure", () => {
  const ok: ChatTurn = { role: "assistant", text: "Three that fit." };
  const failed: ChatTurn = { role: "assistant", text: "", failed: true, failureCode: "client_aborted" };

  it("renders_a_failed_row_wherever_it_sits", () => {
    expect(showsInlineFailure(failed, false, "resumed")).toBe(true);
    expect(showsInlineFailure(failed, true, "resumed")).toBe(true);
    expect(showsInlineFailure(failed, true, "done")).toBe(true);
  });

  it("stands_down_while_the_stopped_card_is_saying_the_same_thing", () => {
    // A live Stop puts `askreel-stopped` on screen — its own row, its own count, its own retry.
    // A second copy of that in the transcript is two "Ask again" buttons for one gesture.
    expect(showsInlineFailure(failed, true, "stopped")).toBe(false);
    // …but only for the turn that card is about. An older failure is still the transcript's.
    expect(showsInlineFailure(failed, false, "stopped")).toBe(true);
  });

  it("never_fires_on_a_turn_that_finished", () => {
    expect(showsInlineFailure(ok, false, "resumed")).toBe(false);
    expect(showsInlineFailure(ok, true, "done")).toBe(false);
  });
});

/**
 * R5-01 — the collision notice belongs to the answer whose cards are on screen.
 *
 * Two wrong states on one thread, one gesture apart. While the kept answer was held after a Stop
 * the notice was **gone** — `userTurn` cleared it at ask-time while deliberately holding the grid
 * it explains, so the one part of the turn F4-06 was filed to preserve was the one part the held
 * state dropped. And once the page landed it came back attached to the **wrong bubble**: "Your
 * conditions permanently exclude comedy and romance" annotating a sea-thriller question that
 * collided with nothing, because the anchor was "the newest assistant row" — right on a resume,
 * wrong the moment a failed turn sits between the answer and the page.
 */
describe("answerRowIndex — the row the grid belongs to", () => {
  it("skips_a_failed_row_to_reach_the_answer", () => {
    const turns: ChatTurn[] = [
      { role: "user", text: "a 90s romantic comedy" },
      { role: "assistant", text: "I can’t do a 90s romantic comedy." },
      { role: "user", text: "something tense set at sea" },
      { role: "assistant", text: "", failed: true, failureCode: "client_aborted" },
    ];
    expect(answerRowIndex(turns)).toBe(1);
  });

  it("is_the_newest_assistant_row_when_nothing_failed", () => {
    // Fixture variation, one field: the tail's `failed` flag. Same rows, same order.
    const turns: ChatTurn[] = [
      { role: "user", text: "a 90s romantic comedy" },
      { role: "assistant", text: "I can’t do a 90s romantic comedy." },
      { role: "user", text: "something tense set at sea" },
      { role: "assistant", text: "Here’s a tense sea-set pick." },
    ];
    expect(answerRowIndex(turns)).toBe(3);
  });

  it("is_minus_one_when_no_answer_is_on_screen", () => {
    expect(answerRowIndex([{ role: "user", text: "a 90s romantic comedy" }])).toBe(-1);
    expect(answerRowIndex([{ role: "assistant", text: "", failed: true, failureCode: null }])).toBe(-1);
  });
});

describe("chatReducer — the notice moves with the grid it explains", () => {
  /** A settled, colliding answer: two cards, a notice, and the terms behind it. */
  const answered = chatReducer(
    chatReducer(
      chatReducer(initialChatState, { type: "userTurn", message: "a 90s romantic comedy", fresh: true }),
      { type: "conflict", data: { message: "Your conditions permanently exclude comedy and romance.", excludedGenres: ["comedy", "romance"] } },
    ),
    { type: "done", data: { results: [card(1), card(2)], reason: "2 picks ranked for you, from 40 pulled." } },
  );

  it("lands_on_the_answer_slot_only_when_the_turn_completes", () => {
    // While the turn is in flight the notice is the TURN's, not the answer's — which is what lets a
    // held previous answer keep its own.
    const asking = chatReducer(initialChatState, { type: "userTurn", message: "a 90s romantic comedy", fresh: true });
    const colliding = chatReducer(asking, { type: "conflict", data: { message: "…comedy and romance." } });
    expect(colliding.conflict).toBeNull();
    expect(colliding.turnConflict).toBe("…comedy and romance.");
    expect(answered.conflict).toBe("Your conditions permanently exclude comedy and romance.");
    expect(answered.conflictTerms).toEqual(["comedy", "romance"]);
    expect(answered.turnConflict).toBeNull();
  });

  it("is_held_with_the_answer_while_the_next_question_runs", () => {
    // R5-01's first state. The grid, the tray and the footnote are deliberately HELD while a new
    // turn works; the notice was cleared at ask-time and so left the answer it explains.
    const asked = chatReducer(answered, { type: "userTurn", message: "something tense set at sea", fresh: false });
    expect(asked.holdingPrevious).toBe(true);
    expect(asked.conflict).toBe("Your conditions permanently exclude comedy and romance.");
    expect(asked.conflictTerms).toEqual(["comedy", "romance"]);
  });

  it("survives_the_stop_that_never_replaced_the_answer", () => {
    const asked = chatReducer(answered, { type: "userTurn", message: "something tense set at sea", fresh: false });
    const stopped = chatReducer(asked, { type: "stop" });
    expect(stopped.conflict).toBe("Your conditions permanently exclude comedy and romance.");
    // …and a page of that kept answer does not move it either: the answer did not change.
    const paged = chatReducer(
      chatReducer(stopped, { type: "moreTurn" }),
      { type: "done", data: { results: [card(3)], kept: 1, reason: "1 more from the same angle." } },
    );
    expect(paged.conflict).toBe("Your conditions permanently exclude comedy and romance.");
  });

  it("keeps_the_stopped_turn_s_own_collision_off_the_kept_answer", () => {
    // Both turns collided. The kept answer keeps its own notice; the abandoned turn's stays in the
    // turn slot, which is where the stopped card reads it — never anchored under the answer.
    const asked = chatReducer(answered, { type: "userTurn", message: "something anime", fresh: false });
    const collided = chatReducer(asked, { type: "conflict", data: { message: "Your conditions exclude anime.", excludedKeywords: ["anime"] } });
    const stopped = chatReducer(collided, { type: "stop" });
    expect(stopped.conflict).toBe("Your conditions permanently exclude comedy and romance.");
    expect(stopped.turnConflict).toBe("Your conditions exclude anime.");
    expect(stopped.turnConflictTerms).toEqual(["anime"]);
  });

  it("goes_when_the_answer_it_explains_is_replaced", () => {
    // The release, and the reason the hold is safe: the first candidate of the new turn is the
    // moment the grid stops being the old answer, and the accounting goes with it.
    const asked = chatReducer(answered, { type: "userTurn", message: "something tense set at sea", fresh: false });
    const replaced = chatReducer(asked, { type: "candidate", data: card(9) });
    expect(replaced.conflict).toBeNull();
    expect(replaced.conflictTerms).toEqual([]);
    expect(replaced.reason).toBeNull();
  });

  it("never_hangs_over_an_answer_that_collided_with_nothing", () => {
    // Fixture variation: the same follow-up, but it LANDS instead of being stopped. A new answer
    // with no conflict frame replaces the notice with nothing at all.
    const asked = chatReducer(answered, { type: "userTurn", message: "something tense set at sea", fresh: false });
    const landed = chatReducer(
      chatReducer(asked, { type: "candidate", data: card(9) }),
      { type: "done", data: { results: [card(9)], reason: "1 pick ranked for you, from 40 pulled." } },
    );
    expect(landed.conflict).toBeNull();
    expect(landed.conflictTerms).toEqual([]);
  });
});

describe("chatReducer — a resumed notice pins to the answer, not the failed tail", () => {
  it("marks_the_completed_reply_and_leaves_the_stopped_row_alone", () => {
    const state = chatReducer(initialChatState, {
      type: "resume",
      payload: {
        id: "conv-r501",
        lastCardsAt: "2026-09-11T09:00:00Z",
        turns: [
          { sequence: 0, role: "user", text: "a 90s romantic comedy", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "I can’t do a 90s romantic comedy.", kind: "ask", status: "Completed", id: "turn-romcom" },
          { sequence: 2, role: "user", text: "something tense set at sea", kind: "ask", status: "Failed" },
          { sequence: 3, role: "assistant", text: "", kind: "ask", status: "Failed", failureCode: "client_aborted", id: "turn-sea" },
        ],
        cards: [card(1)],
        reason: "1 pick ranked for you, from 40 pulled",
        lastQuery: "something tense set at sea",
        conflict: { message: "Your conditions permanently exclude comedy and romance.", excludedGenres: ["comedy", "romance"] },
      },
    });
    // The pill goes on the answer's bubble — the row `answerRowIndex` names — and nowhere near the
    // turn that never produced one.
    expect(state.turns[1].conflict).toBe(true);
    expect(state.turns[3].conflict).toBeUndefined();
    expect(state.conflict).toBe("Your conditions permanently exclude comedy and romance.");
  });
});
