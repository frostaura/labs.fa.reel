import { describe, expect, it } from "vitest";
import {
  MAX_HISTORY_TURNS,
  MAX_SHOWN_IDS,
  capHistory,
  capShown,
  chatReducer,
  initialChatState,
  nextBootstrap,
  type ChatTurn,
} from "./useAskReelChat";

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

/** Minimal card — only the fields the reducer's no-repeat bookkeeping actually reads. */
function card(n: number) {
  return {
    titleId: `t${n}`, mediaType: "Movie" as const, tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7,
  };
}
