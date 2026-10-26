import type { AskCard } from "../askReelStream";
import { excludedTerms } from "./copy";
import type { ChatStatus } from "./reducer";

/** The transcript: the rows a conversation is made of, as stored and as rendered. */
export interface ChatTurn {
  role: "user" | "assistant";
  text: string;
  /**
   * Assistant rows only: the reply is the server's degraded copy — every attempt to read the
   * question failed — so it is shown as degraded rather than as an answer (A-2, B-5).
   */
  degraded?: boolean;
  /**
   * Assistant rows only: at least one judge batch failed, so some of this answer's cards were never
   * checked against the request. Marked on the bubble so the reader knows (acceptance D3).
   */
  unchecked?: boolean;
  /**
   * Assistant rows only: the server rewrote this sentence because the question collided with a
   * standing exclusion. The reply is written before a single candidate exists, so an unqualified
   * one promised "gritty 90s action picks" above seven post-2000 titles — the server qualifies it
   * at the source and flags it here, which is what lets the page mark THIS bubble and anchor the
   * full notice to it. Absent on a resumed transcript and on an older server: the stored row keeps
   * the qualified text, which is the part that had to survive.
   */
  conflict?: boolean;
  /**
   * Assistant rows only: this turn did not finish. The row has no answer and never will, so it is
   * rendered as the failure it is rather than as a bubble — see {@link showsInlineFailure}.
   *
   * R5-02. Only the TAIL of a resumed thread was ever named as failed (`resumedTail`), so a
   * stopped turn with anything after it — a page of the kept answer, a later question — came back
   * as a bare user bubble with nothing under it, and live it kept the `assistant-message` that had
   * streamed before the abort, presented as that turn's answer. A turn nobody waited for is still
   * a turn that happened.
   */
  failed?: boolean;
  /** The server's `FailureCode` for a {@link ChatTurn.failed} row. See {@link failedTurnMessage}. */
  failureCode?: string | null;
}

/**
 * Has a clamped question bubble actually been cut short?
 *
 * Measured rather than guessed from the character count, because the same 400 characters are four
 * lines on a desktop bubble and eight on a phone — a length threshold would offer "Show more" on a
 * bubble showing everything, and withhold it on one that is cut. The tolerance absorbs the
 * sub-pixel rounding `line-height` leaves behind, which otherwise reports a one-line bubble as
 * overflowing on some zoom levels.
 */
export function bubbleOverflows(scrollHeight: number, clientHeight: number): boolean {
  return scrollHeight - clientHeight > 4;
}

/**
 * One conversation as `GET /api/search/conversations/{id}` returns it — the transcript AND the
 * answer as it should look now, which is the whole of what Phase 1 added to a resume.
 *
 * Every field below `turns` is optional because `/conversations/latest` on an older server, and a
 * thread stored before cards were kept, answer with less. The client's job is to render what it
 * was given without inventing the rest: `lastCardsAt: null` is "transcript only", which is a
 * different fact from a turn that honestly returned nothing.
 */
export interface ConversationPayload {
  id: string;
  title?: string | null;
  /** When this thread last stored cards. Null = it predates stored results entirely. */
  lastCardsAt?: string | null;
  /**
   * When this thread last had a turn of any kind. Optional: it is on the conversation LIST on
   * every server and only newer ones put it in the resume payload. It is the only stamp a
   * transcript-only thread has, which is the one case `lastCardsAt` cannot date.
   */
  lastTurnAt?: string | null;
  /**
   * The transcript. `status` and `failureCode` are what SB-03 turned on: a row can be `Pending`
   * (the server is still working) or `Failed` (stopped, or a crash), and dropping those left the
   * previous answer sitting under an unanswered question with nothing saying so. `id` is optional
   * everywhere — today's payload projects no turn ids — and is what lets a pending turn be polled
   * by `GET /turns/{id}` rather than by re-reading the whole conversation.
   */
  turns: {
    sequence?: number;
    role: string;
    text: string;
    kind?: string;
    status?: string;
    failureCode?: string | null;
    /** True when the row's reply is the degraded copy. Absent on an older server. */
    degraded?: boolean;
    /** True when a judge batch failed on this row's turn. Absent on an older server. */
    judgeDegraded?: boolean;
    /** Both spellings accepted: the payload has never carried one, and the SSE frame calls it `turnId`. */
    id?: string | null;
    turnId?: string | null;
  }[];
  /** The answer, in the order the server committed to storage as `Rank`. Never re-sorted. */
  cards?: AskCard[];
  /**
   * The tray — the cards the account's own rating floor pushed under the "Show the N under your
   * bar" fence, stored as rows of their own and handed back separately. Same card shape as
   * `cards`, and **never merged into it**: these were displayed and explicitly not recommended.
   *
   * Optional because an older server sends no such field. Absent and empty are different facts:
   * absent is silence, empty is a server saying the floor cut nothing — see the `resume` case,
   * which is what keeps the tray's label from claiming a number it was never told.
   */
  belowFloor?: AskCard[];
  /** The judge's partial matches, restored as their own collapsed tray. Absent on an older server. */
  nearMiss?: AskCard[];
  /** The cards nobody could check, restored as their own collapsed tray. Absent on an older server. */
  unchecked?: AskCard[];
  /** "Picks" or "NoneFound" for the newest answer. Absent on an older server. */
  mode?: string | null;
  reason?: string | null;
  /**
   * The collision notice the newest replayed turn emitted, stored on the turn and handed back
   * with it — `null` on a thread that had no collision, which is most of them.
   *
   * F4-06: the cards were restored from rows and the reason line from a column, and the
   * EXPLANATION was the one part of the turn the server forgot. Reload a thread that asked for a
   * 90s romantic comedy against standing exclusions on both genres and it came back as four
   * post-2000 dramas under that question with nothing on the page saying why.
   *
   * Optional, and treated as untrusted: see {@link resumedConflict}. `poolSize` / `catalogueSize`
   * are deliberately absent — both count the catalogue at one instant and stop being true
   * immediately, so a resume replaying them would state a stale number as a current one.
   */
  conflict?: PersistedConflict | null;
  /** Stored cards the eligibility re-filter dropped since the turn ran, across BOTH lists. */
  removed?: number;
  /**
   * How many of `removed` the account had already rated, watched, saved or dismissed when the
   * answer was given (a person query shows seen titles). Absent on an older server, read as 0.
   */
  removedEarlier?: number;
  lastQuery?: string | null;
  /** The media filter that turn ran under, in request tokens ("movie" / "tv"). */
  mediaTypes?: string[];
  /** The whole conversation's no-repeat set — informational; the server applies its own. */
  shown?: number[];
}

/**
 * A collision notice as it is STORED — the `conflict` frame minus the two counts that go stale.
 *
 * `minYear` / `maxYear` are present only when the YEAR is what collided; an account with a standing
 * window would otherwise carry them on every turn and put a year on a screen whose collision was
 * purely a genre. They are accepted and deliberately not rendered as terms: the server's own
 * `message` already states the window, and the live `conflict` frame carries no such fields, so
 * naming them here would make a resumed notice say more than the live one it is restoring.
 */
export interface PersistedConflict {
  message: string;
  excludedGenres?: string[];
  excludedKeywords?: string[];
  minYear?: number | null;
  maxYear?: number | null;
}

/** Everything in `value` that is a non-blank string, and nothing else. */
function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
}

/**
 * The collision notice a resumed thread is entitled to render, or null.
 *
 * Top-level on the resume payload, which is where the turn record puts it: it is the NEWEST
 * replayed turn's notice, exactly as `reason` is that turn's sentence, and the two are restored
 * together or the page explains an answer with half of its own account of it.
 *
 * Validated rather than trusted, and the two halves fail independently. `message` is what the card
 * PRINTS: a warning-styled box reading "undefined" is worse than no box, because it looks like the
 * product breaking at the precise moment it is explaining itself. The term list is a sentence the
 * page builds, so junk is dropped out of it without taking the message down.
 */
export function resumedConflict(payload: { conflict?: PersistedConflict | null }): { message: string; terms: string[] } | null {
  const raw: unknown = payload.conflict;
  if (!raw || typeof raw !== "object") return null;
  const message = (raw as PersistedConflict).message;
  if (typeof message !== "string" || !message.trim()) return null;
  return {
    message,
    terms: excludedTerms(
      stringList((raw as PersistedConflict).excludedGenres),
      stringList((raw as PersistedConflict).excludedKeywords),
    ),
  };
}

/**
 * The stored rows that are actually a transcript.
 *
 * A turn in flight is a row with no text yet, and a failed one keeps its empty text for good.
 * Rendering those puts blank bubbles on screen — an assistant that said nothing, under a question
 * it never answered — so they are dropped here rather than in the page, which would have to know
 * about row lifecycles to do it. The rows still count for `lastQuery`, because the question a
 * failed turn never answered is precisely the one a retry replays.
 *
 * A `page` row goes with them, and that is SB-04. Paging replays the last question, so the model
 * replies again and the server stores a second assistant row — with no user row beside it, because
 * the user said nothing. The live reducer already refuses that reply (`case "assistant"` returns
 * unchanged while `appending`, with the same reasoning); this is the resume path catching up, and
 * without it a paged thread came back with three bubbles for two answers, the first two being the
 * identical sentence twice. The rule is `kind`, never text equality: a page that happens to word
 * itself differently is still an answer to a question two bubbles up.
 */
export function transcriptOf(rows: ConversationPayload["turns"]): ChatTurn[] {
  return rows
    .filter((t) => t.kind !== "page")
    .filter((t) => t.role === "user" || t.role === "assistant")
    // …and a FAILED assistant row survives the empty-text filter, because its emptiness is the
    // fact rather than an artefact of timing (R5-02). A `Pending` one does not: it has no text
    // YET, and rendering it would put a blank bubble under a question the server is still working
    // on — which `resumedTail` says out loud instead.
    .filter((t) => t.text.trim().length > 0 || (t.role === "assistant" && t.status === "Failed"))
    .map((t) =>
      t.role === "assistant" && t.status === "Failed"
        ? { role: "assistant" as const, text: "", failed: true, failureCode: t.failureCode ?? null }
        : t.role === "assistant" && (t.degraded || t.judgeDegraded)
          ? {
            role: "assistant" as const, text: t.text,
            ...(t.degraded ? { degraded: true } : {}),
            ...(t.judgeDegraded ? { unchecked: true } : {}),
          }
          : { role: t.role as "user" | "assistant", text: t.text },
    );
}

/**
 * The transcript with its newest assistant reply marked "some cards unchecked". The reply goes out
 * before the cards, so by the time `done` says a judge batch failed, the bubble it belongs to is
 * the last assistant row.
 */
export function markLastReplyUnchecked(turns: ChatTurn[]): ChatTurn[] {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].role === "assistant") {
      if (turns[i].unchecked) return turns;
      const next = turns.slice();
      next[i] = { ...turns[i], unchecked: true };
      return next;
    }
    if (turns[i].role === "user") return turns;
  }
  return turns;
}

/**
 * The transcript after a Stop — the abandoned turn recorded as the failure it is.
 *
 * R5-02's live half. `assistant-message` arrives long before `done` (2 578 ms against an abort at
 * 4 118 ms in R5's own capture), so a Stop left the model's opening sentence on screen as that
 * turn's answer: "Here's a tense sea-set pick that stays in your thriller/action lane." over zero
 * cards, with `Status='Failed'` in the database and nothing on the page saying the promise had not
 * been kept. The sentence is replaced rather than kept, because a reply to a question that was
 * never answered is not a reply — and the row stays, because the turn happened.
 *
 * A row is ADDED when nothing was said at all, so the stopped question is answered by something
 * even when the model never got a word out. It is the same row either way, which is what makes the
 * live screen and the next resume agree.
 *
 * `appending` is the one case that changes nothing: a page writes no transcript row, so the
 * trailing assistant row belongs to the answer the page was extending — finished work, and not the
 * thing that was abandoned.
 */
export function stoppedTranscript(turns: ChatTurn[], appending: boolean): ChatTurn[] {
  if (appending || turns.length === 0) return turns;
  const row: ChatTurn = { role: "assistant", text: "", failed: true, failureCode: "client_aborted" };
  return turns.at(-1)?.role === "assistant" ? [...turns.slice(0, -1), row] : [...turns, row];
}

/**
 * Does this transcript row render its own failure line?
 *
 * Every failed row does, wherever it sits — that is R5-02 — with one exception: the turn the
 * `stopped` card is currently about. That card is the same fact with the same retry beside it, and
 * two "Ask again" buttons for one gesture is what F4-04 was filed to stop. The moment anything
 * happens after that turn (a page of the kept answer, another question, a reload) the card is gone
 * and the row speaks for itself.
 */
export function showsInlineFailure(turn: ChatTurn, isTail: boolean, status: ChatStatus): boolean {
  if (!turn.failed) return false;
  return !(isTail && status === "stopped");
}

/** The question a transcript row is about — the nearest user row above it. */
export function questionFor(turns: ChatTurn[], index: number): string {
  for (let i = index - 1; i >= 0; i--) if (turns[i].role === "user") return turns[i].text;
  return "";
}

/**
 * Which transcript row the answer on screen IS — the newest assistant row that is not a failure.
 *
 * R5-01. The anchor for the collision notice was "the newest assistant row", which is right on an
 * ordinary screen and wrong the moment a failed turn sits between the answer and what came after
 * it: "Your conditions permanently exclude comedy and romance" ended up annotating a sea-thriller
 * question that collided with nothing, live, while a hard reload put it back under the romcom
 * reply. One answer, two accounts of which question it explains, a reload apart.
 *
 * A failed row is never the answer — it has none, which is the whole point of it — so skipping it
 * lands on the row whose cards are actually in the grid. -1 when nothing on screen is an answer,
 * which is where the notice falls back to a block of its own.
 */
export function answerRowIndex(turns: ChatTurn[]): number {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].role === "assistant" && !turns[i].failed) return i;
  }
  return -1;
}

/**
 * What became of a resumed thread's LAST turn, when it was not a completed answer.
 *
 * SB-03: the resume payload has carried `turns[].status` all along and `transcriptOf` dropped it on
 * the floor, so a thread whose last turn was stopped or crashed came back reading
 *
 *   You said | **something surreal and dreamlike** | Picked up where you left off | [24 cards]
 *
 * — the previous question's answer sitting under a question that never got one, with nothing
 * anywhere on the page saying so. The grid is correct (the server replays the last COMPLETED turn);
 * what was missing is the sentence naming which question it answers and what happened to the other.
 */
export interface ResumedTail {
  /** `failed` — the turn ended badly. `pending` — the server is still working on it. */
  status: "failed" | "pending";
  /**
   * The server's own `FailureCode`, where the payload carries one. `client_aborted` means the
   * client's connection went away — a Stop, a reload, a slept phone or a discarded tab, all
   * indistinguishable from the server's side — and anything else is a snag the user did not cause.
   * See {@link failedTurnMessage}: the copy may only claim what the code can actually support.
   */
  failureCode: string | null;
  /**
   * The turn's id, where the payload carries one — what `GET /api/search/turns/{id}` polls while a
   * turn is still running. Null is the ordinary case on today's server: the resume payload projects
   * `sequence`, `role`, `text`, `kind` and `status`, and no id, so the pending state falls back to
   * re-reading the conversation itself.
   */
  turnId: string | null;
}

/**
 * The outcome of the last stored turn — `null` when it completed, which is the ordinary case.
 *
 * The LAST row is the right one to read: an `ask` writes a user row and an assistant row together
 * before the stream opens, and a `page` writes the assistant row alone, so the newest row of a
 * conversation is always the assistant's. Only that row can be in flight or failed; every earlier
 * one has already been decided, and a failure four turns back is history the transcript already
 * shows rather than a state the page is in.
 */
export function lastTurnOutcome(rows: ConversationPayload["turns"]): ResumedTail | null {
  const last = rows.at(-1);
  if (!last || last.role !== "assistant") return null;
  if (last.status !== "Failed" && last.status !== "Pending") return null;
  return {
    status: last.status === "Failed" ? "failed" : "pending",
    failureCode: last.failureCode ?? null,
    turnId: last.id ?? last.turnId ?? null,
  };
}

/** The text of the last thing the USER said — what a page or a re-ask has to replay. */
export function lastUserQuery(turns: ChatTurn[]): string {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].role === "user") return turns[i].text;
  }
  return "";
}
