// @vitest-environment jsdom
import { useEffect } from "react";
import { act, cleanup, render, screen } from "@testing-library/react";
import { Provider } from "react-redux";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { store } from "../../store";
import { AskStreamError, type AskCard, type AskEvent, type DoneData } from "../../lib/askReelStream";
import type { ConversationPayload } from "../../lib/askReel/transcript";
import { useAskReelChat, type AskReelChat } from "../../lib/askReel/useAskReelChat";
import AnswerStrip from "./AnswerStrip";
import { deriveAskView } from "./view";

/**
 * R1-49 — the claims register's `ask-reel-thread-state` family, held to the states that print it.
 *
 * Every sentence below is something the answer strip says about a THREAD: that it is gone, that it
 * predates saved results, that a question did not replace the answer on screen, that paging has run
 * out, that a page no longer matches its answer. Each is driven through the real hook and the real
 * reducer — the stream is the only thing replaced — and each is pinned from both sides: the state
 * that makes it true prints it, and the nearest state that does not (one field away) does not.
 */

type Turn = (on: (ev: AskEvent) => void, signal: AbortSignal) => Promise<void>;
const script: Turn[] = [];
vi.mock("../../lib/askReelStream", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../lib/askReelStream")>();
  return {
    ...actual,
    streamAskReel: vi.fn((_req: unknown, on: (ev: AskEvent) => void, signal: AbortSignal) =>
      (script.shift() ?? (async () => {}))(on, signal)),
  };
});

const T = {
  gone: "It was deleted, or it aged out.",
  transcriptOnly: "Transcript only — this thread predates saved results.",
  neverReplaced: "the last question never replaced it.",
  stoppedKept: "the question you stopped never replaced it.",
  exhausted: "That's everything on this thread",
  mismatch: "That page no longer matches its answer",
};

function card(n: number): AskCard {
  return {
    titleId: `t${n}`, mediaType: "Movie", tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7,
  };
}

function done(extra: Partial<DoneData> = {}): DoneData {
  return { results: [], reason: null, mode: "Picks", degraded: [], ...extra };
}

/** A turn that answers with these cards and finishes. */
function answers(cards: AskCard[], extra: Partial<DoneData> = {}): Turn {
  return async (on) => {
    on({ event: "conversation", data: { id: "c1", turnId: `turn-${cards[0]?.titleId ?? "none"}` } });
    on({ event: "assistant-message", data: { text: "Here you go." } });
    if (cards.length > 0) on({ event: "picks", data: { cards } });
    on({ event: "done", data: done({ results: cards, kept: cards.length, ...extra }) });
  };
}

/** A turn that fails with this error before producing anything. */
function fails(err: unknown): Turn {
  return async () => {
    throw err;
  };
}

/** A turn that produces nothing until it is aborted — the turn the user presses Stop on. */
const hangs: Turn = (_on, signal) =>
  new Promise((_resolve, reject) => {
    signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
  });

/** The hook as the page last rendered it — published from an effect, so tests drive the same object. */
let rendered: AskReelChat | null = null;
const live = (): AskReelChat => {
  if (!rendered) throw new Error("mount() first");
  return rendered;
};
function Harness() {
  const chat = useAskReelChat();
  useEffect(() => {
    rendered = chat;
  });
  const view = deriveAskView(chat, { removedKeys: [], floorBar: null });
  return <AnswerStrip chat={chat} view={view} onSaveTapped={() => {}} />;
}

function mount() {
  render(
    <Provider store={store}>
      <MemoryRouter>
        <Harness />
      </MemoryRouter>
    </Provider>,
  );
}

/** Run a gesture and let its stream settle. */
async function settle(gesture: () => void) {
  await act(async () => {
    gesture();
    await new Promise((r) => setTimeout(r, 0));
  });
}

const says = (text: string) => document.body.textContent?.includes(text) ?? false;

beforeEach(() => {
  script.length = 0;
  rendered = null;
});
afterEach(cleanup);

describe("a thread that is gone (404) — “It was deleted, or it aged out.”", () => {
  it("says so when the server answers conversation_not_found", async () => {
    mount();
    script.push(answers([card(1)]), fails(new AskStreamError(404, "conversation_not_found")));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    expect(screen.getByTestId("askreel-conversation-gone")).toBeTruthy();
    expect(says(T.gone)).toBe(true);
  });

  it("never says so for a failure that is not the thread being gone", async () => {
    // Fixture variation, one field: the status. A 500 is a snag, and says so.
    mount();
    script.push(answers([card(1)]), fails(new AskStreamError(500)));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    expect(screen.queryByTestId("askreel-conversation-gone")).toBeNull();
    expect(says(T.gone)).toBe(false);
  });
});

describe("a resumed thread with no stored results — “Transcript only — this thread predates saved results.”", () => {
  type Row = ConversationPayload["turns"][number];
  function thread(extra: Partial<ConversationPayload>, reply: Partial<Row> = {}): ConversationPayload {
    return {
      id: "c1", lastCardsAt: null, lastTurnAt: "2026-01-01T00:00:00Z",
      turns: [
        { role: "user", text: "ninja movies", status: "Completed", kind: "ask" },
        { role: "assistant", text: "Ninja picks.", status: "Completed", kind: "ask", mode: "Picks", ...reply },
      ],
      ...extra,
    };
  }

  it("says so for a thread that never stored a card under a search", async () => {
    mount();
    await settle(() => live().resume(thread({})));
    expect(screen.getByTestId("askreel-transcript-only")).toBeTruthy();
    expect(says(T.transcriptOnly)).toBe(true);
  });

  it("does not say so once the thread has stored cards — that is a different fact", async () => {
    // One field: lastCardsAt. Cards stored and all since watched is "you've watched everything".
    mount();
    await settle(() => live().resume(thread({ lastCardsAt: "2026-01-01T00:00:00Z" })));
    expect(says(T.transcriptOnly)).toBe(false);
    expect(screen.getByTestId("askreel-all-removed")).toBeTruthy();
  });

  it("does not say so when the last turn failed — the transcript names that itself", async () => {
    mount();
    await settle(() => live().resume(thread({}, { role: "assistant", text: "", status: "Failed", failureCode: "client_aborted" })));
    expect(says(T.transcriptOnly)).toBe(false);
  });

  it.each(["Answer", "Clarify"])("does not say so for a thread whose only reply was a %s — it predates nothing", async (mode) => {
    // A new v2 thread that opened with small talk, an item question or a clarifying question has no
    // cards because none were ever owed, and the reply above IS the answer. Calling it a thread
    // that "predates saved results" and offering to "bring it back" is false on both counts.
    mount();
    await settle(() => live().resume(thread({}, { role: "assistant", text: "Hi! What are you in the mood for?", mode })));
    expect(says(T.transcriptOnly)).toBe(false);
    expect(screen.queryByTestId("askreel-transcript-only")).toBeNull();
    expect(screen.queryByTestId("askreel-all-removed")).toBeNull();
    expect(screen.queryByTestId("semantic-empty")).toBeNull();
  });
});

describe("a held answer — “the last question never replaced it.”", () => {
  it("says so when the next question failed and the answer is still the previous one", async () => {
    mount();
    script.push(answers([card(1), card(2)]), fails(new AskStreamError(500)));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    expect(screen.getByTestId("askreel-previous-answer").textContent).toContain(T.neverReplaced);
  });

  it("does not say “never” while that question is still being answered elsewhere (409)", async () => {
    // One field: the failure. A 409 is the same question already running — its answer is on the
    // way and "Check again" fetches it, so the held answer is held, not final.
    mount();
    script.push(answers([card(1), card(2)]), fails(new AskStreamError(409, "turn_in_progress")));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    expect(live().status).toBe("blocked");
    expect(says(T.neverReplaced)).toBe(false);
    expect(screen.getByTestId("askreel-previous-answer").textContent).toBe("Previous answer — held until the new one arrives.");
  });

  it("does not say so once the new answer has replaced it", async () => {
    mount();
    script.push(answers([card(1)]), answers([card(3)]));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    expect(screen.queryByTestId("askreel-previous-answer")).toBeNull();
  });
});

describe("a Stop that kept the answer — “the question you stopped never replaced it.”", () => {
  it("names the question the kept grid answers", async () => {
    mount();
    script.push(answers([card(1), card(2)]), hangs);
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    await settle(() => live().stop());
    const kept = screen.getByTestId("askreel-kept-answer").textContent ?? "";
    expect(kept).toContain("Still your answer to “ninja movies”");
    expect(kept).toContain(T.stoppedKept);
  });

  it("does not say so when the stopped question had already put a card on screen", async () => {
    // One field: whether the stopped turn produced anything. Then the grid is ITS partial, not the
    // kept answer, and claiming otherwise would be the R1-04 lie.
    mount();
    const partial: Turn = (on, signal) => {
      on({ event: "picks", data: { cards: [card(9)] } });
      return hangs(on, signal);
    };
    script.push(answers([card(1), card(2)]), partial);
    await settle(() => live().send("ninja movies"));
    await settle(() => live().send("samurai films"));
    await settle(() => live().stop());
    expect(screen.queryByTestId("askreel-kept-answer")).toBeNull();
    expect(says(T.stoppedKept)).toBe(false);
  });
});

describe("paging — “That's everything on this thread” and “That page no longer matches its answer”", () => {
  it("says everything is shown when the server's page kept nothing", async () => {
    mount();
    script.push(answers([card(1), card(2)]), answers([], { kept: 0 }));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().loadMore());
    expect(says(T.exhausted)).toBe(true);
  });

  it("does not say so when the page added cards", async () => {
    mount();
    script.push(answers([card(1), card(2)]), answers([card(3)]));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().loadMore());
    expect(says(T.exhausted)).toBe(false);
    expect(screen.getByTestId("askreel-more")).toBeTruthy();
  });

  it("says a refused page no longer matches its answer, and keeps the answer", async () => {
    mount();
    script.push(answers([card(1), card(2)]), fails(new AskStreamError(409, "page_query_mismatch")));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().loadMore());
    expect(screen.getByTestId("askreel-page-refused").textContent).toContain(T.mismatch);
    expect(screen.getAllByTestId("askreel-card")).toHaveLength(2);
  });

  it("does not say so for a 409 that is a turn in progress", async () => {
    mount();
    script.push(answers([card(1), card(2)]), fails(new AskStreamError(409, "turn_in_progress")));
    await settle(() => live().send("ninja movies"));
    await settle(() => live().loadMore());
    expect(screen.queryByTestId("askreel-page-refused")).toBeNull();
    expect(says(T.mismatch)).toBe(false);
  });
});

describe("a reply that answered beside the grid is never announced as an empty search", () => {
  function talks(mode: "Answer" | "Clarify"): Turn {
    return async (on) => {
      on({ event: "conversation", data: { id: "c1", turnId: "turn-hi" } });
      if (mode === "Answer") on({ event: "answer", data: { text: "Hi! What are you in the mood for?" } });
      else on({ event: "clarify", data: { question: "Which one did you mean?", options: ["Heat", "Heat (1972)"] } });
      on({ event: "done", data: done({ mode }) });
    };
  }

  it.each(["Answer", "Clarify"] as const)("announces no “No matches” after a %s that opened the thread", async (mode) => {
    mount();
    script.push(talks(mode));
    await settle(() => live().send("hi"));
    expect(live().status).toBe("done");
    const view = deriveAskView(live(), { removedKeys: [], floorBar: null });
    expect(view.announcement).not.toMatch(/No matches/);
    expect(screen.queryByTestId("semantic-empty")).toBeNull();
  });

  it("still announces an empty SEARCH as one", async () => {
    // One field: the mode. A search that found nothing is "no matches", and says so.
    mount();
    script.push(answers([]));
    await settle(() => live().send("ninja movies"));
    expect(deriveAskView(live(), { removedKeys: [], floorBar: null }).announcement).toBe("No matches for that one.");
  });
});
