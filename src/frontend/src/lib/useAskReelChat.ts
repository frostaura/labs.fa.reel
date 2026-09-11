import { useCallback, useEffect, useReducer, useRef } from "react";
import { useSelector } from "react-redux";
import { applyScoreOverrides, selectScoreOverrides } from "../store/scoreOverridesSlice";
import { AskStreamError, isCard, isFailedDone, streamAskReel, type AskCard, type AskReelRequest, type AssistantMessageData, type ConflictData, type ConversationData, type DoneData, type PhaseData } from "./askReelStream";

/**
 * `resumed` is a fourth state, not a flavour of `done`: the transcript came back from the server
 * but this visit has not run a search, so nothing on screen was produced by a query in this
 * session. Reporting it as `done` is what rendered "No matches for that one" over a conversation
 * that had matched plenty — the cards simply were not part of what `/conversations/latest`
 * returned. Callers that want "there is a conversation here" should test `turns.length`, not the
 * status.
 *
 * `stopped` is a fifth, for the same reason: the user abandoned the turn, so what is on screen is
 * neither a completed answer nor a failure. It used to collapse into `done` (when something had
 * streamed) or `idle` (when nothing had) — and `idle` over a transcript renders NOTHING at all, so
 * pressing Stop before the first card landed the user on a blank page: no message, no retry, and
 * no way back to the answer the new turn had already cleared.
 *
 * `blocked` is a sixth: the server answered 409 `turn_in_progress`, meaning this exact question is
 * already being answered — by another tab, by a retry that got there first, or by a turn this tab
 * abandoned without the server noticing yet. Nothing on screen is wrong in that state, so nothing
 * on screen is cleared; it is not an error, because no request failed and no answer was lost.
 */
export type ChatStatus = "idle" | "streaming" | "done" | "resumed" | "stopped" | "blocked" | "error";

/**
 * Why the stream ended badly, where the client can tell. `null` = an unclassified failure.
 *
 * `conversation` is the remembered thread being gone (a 404): recoverable by starting a new one,
 * which is a different offer from "try again". `request` is a 400 — the client sent something the
 * server refuses, and repeating it verbatim cannot work.
 */
export type ErrorReason = "session" | "timeout" | "conversation" | "request" | null;

export interface ChatTurn {
  role: "user" | "assistant";
  text: string;
  /**
   * Assistant rows only: the server rewrote this sentence because the question collided with a
   * standing exclusion. The reply is written before a single candidate exists, so an unqualified
   * one promised "gritty 90s action picks" above seven post-2000 titles — the server qualifies it
   * at the source and flags it here, which is what lets the page mark THIS bubble and anchor the
   * full notice to it. Absent on a resumed transcript and on an older server: the stored row keeps
   * the qualified text, which is the part that had to survive.
   */
  conflict?: boolean;
}

/**
 * Everything this client is allowed to say about a turn.
 *
 * The caps that used to live here — twelve turns of transcript, three hundred shown ids, both
 * replayed on every single request — are gone with the fields they trimmed. The server derives the
 * history the model reads and the no-repeat set from rows it wrote, so there is nothing left to
 * cap: a client-carried transcript was unverifiable, capped (so a long thread re-offered titles it
 * had already shown), lost on reload and wrong the moment a second tab existed.
 *
 * `clientTurnId` is the one thing the client contributes to the record, and it is an idempotency
 * key: the same id twice is answered from the stored turn rather than billed twice.
 */
export function askRequest(
  query: string,
  clientTurnId: string,
  conversationId: string | null,
  mediaTypes: string[],
  pageOf?: string | null,
): AskReelRequest {
  // Added rather than always present: R3 measured every body as exactly these four keys, and a
  // `pageOf: null` on a fresh ask would be a fifth field on every turn claiming to extend nothing.
  // The field is the page's, and only when the client can actually name the turn.
  return pageOf
    ? { query, clientTurnId, conversationId, mediaTypes, pageOf }
    : { query, clientTurnId, conversationId, mediaTypes };
}

/**
 * What this tab stores instead of an id when the user has explicitly finished with a thread.
 *
 * "I pressed `+ New`" and "I have never asked anything here" are different facts, and until SB-02
 * they were the same absent key: `+ New` cleared the id, the reload fell through to
 * `/conversations/latest`, and the account's newest thread — the one just left — came straight
 * back. Not a guid, so it can never collide with a real id and can never be sent as one.
 */
export const FRESH_CONVERSATION = "new";

/** {@link resumeTarget}'s answer when nothing should be fetched at all. */
export const NO_RESUME = "none";

/**
 * Which conversation a cold /search should resume — and whether it should resume one.
 *
 * Three states, not two. This tab's own remembered id; nothing at all after an explicit `+ New`;
 * and `/conversations/latest` only when there is nothing remembered whatsoever. `/latest` is the
 * account's newest thread, not this tab's: with two tabs open, or a phone and a laptop, reloading
 * tab A dropped the reader into tab B's conversation and the thread they were reading was gone
 * from the UI. Preferring the stored id is the whole of that fix, and it is why `latest` is
 * deprecated rather than merely unused — SB-02 was ordinary use reaching it anyway, because a
 * cleared key and a never-set one were indistinguishable.
 */
export function resumeTarget(storedId: string | null): string {
  if (storedId === FRESH_CONVERSATION) return NO_RESUME;
  return storedId && storedId.length > 0 ? storedId : "latest";
}

/**
 * Where this tab remembers which Ask Reel thread it is on.
 *
 * A reload used to resume `/conversations/latest`, which is the account's newest thread and not
 * necessarily this tab's: two tabs, or a phone and a laptop, silently converged — reloading tab A
 * dropped you into tab B's conversation and the thread you were reading was gone from the UI. Both
 * halves are here now: this is written as the `conversation` frame arrives and read back by
 * {@link resumeTarget} on every cold load, so a tab resumes ITS thread and `/latest` is only ever
 * the answer for a browser that has never asked anything.
 */
export const CONVERSATION_ID_KEY = "reel.ask.conversationId";

/** `localStorage`, or null wherever it is absent or throws (SSR, private mode, blocked origins). */
function storage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * Remembers (or forgets) this tab's conversation id. Every failure is swallowed on purpose: a
 * remembered id is a convenience, never a precondition, and a full quota or a blocked origin must
 * not take the turn down with it.
 */
export function storeConversationId(id: string | null): void {
  const store = storage();
  if (!store) return;
  try {
    if (id) store.setItem(CONVERSATION_ID_KEY, id);
    else store.removeItem(CONVERSATION_ID_KEY);
  } catch {
    // See above — writing this is best-effort.
  }
}

/** The conversation id this tab last saw, or null. */
export function readStoredConversationId(): string | null {
  const store = storage();
  if (!store) return null;
  try {
    const id = store.getItem(CONVERSATION_ID_KEY);
    return id && id.length > 0 ? id : null;
  } catch {
    return null;
  }
}

/** Inter-frame silence that means the stream is dead rather than thinking, in milliseconds. */
export const STREAM_IDLE_TIMEOUT_MS = 30_000;

/** How long a backgrounded tab may hold an open stream before it is abandoned, in milliseconds. */
export const HIDDEN_ABORT_MS = 60_000;

/**
 * One exchange's idempotency key. `crypto.randomUUID` is present in every browser this app
 * supports and in node 19+, which is what the tests run on; the fallback exists so a page served
 * over plain HTTP (where `crypto` is not a secure context on some browsers) still gets an id
 * rather than sending an empty one and taking a 400.
 */
export function mintTurnId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") return crypto.randomUUID();
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 10)}`;
}

/**
 * Which run a frame belongs to — the label every frame of one `/ask` stream carries.
 *
 * F6-01 is what a screen looks like without one. `Recent ▾` was live while a turn streamed, so
 * opening another thread mid-stream put THAT thread's question and reply on screen above the
 * running turn's progress banner and a grid of 34 cards from the question that had just left. The
 * heist search then landed *in the rom-com thread*, replaced its answer, and pushed its turn count
 * from 2 to 3 — durably, because the server had already written the rows. Nothing on the client
 * could tell the two apart: a `done` frame carried no evidence of which conversation it had been
 * asked for.
 *
 * Two independent facts, because neither alone covers the case. `id` is minted per run and is
 * monotonic, so it separates two runs even when both started before any conversation existed;
 * `conversationId` is the thread the run was started FOR, and is what catches a frame that belongs
 * to a thread no longer on screen. Both are checked in {@link staleFrame}.
 */
export interface StreamTag {
  /** This run's own number. Monotonic within the tab; never reused. */
  id: number;
  /** The thread this run was started for, or null when the run is opening a new one. */
  conversationId: string | null;
}

let streamCounter = 0;

/**
 * The next run's id. Starts at 1 so that {@link initialChatState}'s `stream: 0` — the value a
 * reset, a `+ New` and a thread switch all land on — matches no run at all, and every frame still
 * in flight from the run that was abandoned is stale by construction.
 */
export function mintStreamId(): number {
  streamCounter += 1;
  return streamCounter;
}

/**
 * What the page should bring into view for this status, or `null` to leave the scroll where the
 * reader put it.
 *
 * F6-02: `scrollY` stayed at **0** for the whole of a 19.4-second turn. A 1 000-character question
 * makes a bubble ~1 100 px tall, so the viewport held nothing but the user's own words — no banner,
 * no narration, no progress bar, no reply, no cards — and four screenshots taken at 400 ms, 1 s,
 * 3 s and 8 s of the next turn were pixel-identical for the same reason. The only evidence anything
 * was happening was the Send arrow greying out.
 *
 * Two moments, not every render. A turn STARTING is the page changing what it is about, and a Stop
 * is the same thing in reverse — the kept answer is below a transcript that may already fill the
 * screen. `done` is deliberately absent: by then the reader is reading, and snapping the page under
 * them at the moment the answer settles is the opposite of the fix.
 */
export type ScrollTarget = "live" | "settled" | null;

export function scrollTargetFor(status: ChatStatus): ScrollTarget {
  if (status === "streaming") return "live";
  if (status === "stopped") return "settled";
  return null;
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

/** Whether this browser has asked for motion to be kept to a minimum. Absent `matchMedia` = no. */
export function prefersReducedMotion(): boolean {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return false;
  try {
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  } catch {
    return false;
  }
}

/**
 * Whether the preset mood chips ("Under 90 minutes, feel-good", …) should show: only on a
 * genuinely fresh Ask Reel — no transcript, no cards on screen, nothing streaming. The moment a
 * conversation exists (including one resumed from a previous visit) the presets yield to the
 * refine chips, which are about THIS thread rather than a cold start.
 */
export function showMoodPresets(turnCount: number, cardCount: number, streaming: boolean): boolean {
  return turnCount === 0 && cardCount === 0 && !streaming;
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
 * The browse toggle a resumed answer was produced under.
 *
 * The toggle and the grid have to agree. A restored answer that ran under "Movies" sitting beside
 * a control reading "All" is a control lying about what produced what is underneath it — and the
 * next re-ask would then go out under a constraint the user never chose. Anything unrecognised,
 * and both tokens together, mean no constraint: a guess here re-scopes someone's thread silently.
 */
export function mediaFilterFor(tokens: string[] | undefined): "all" | "movie" | "show" {
  if (!tokens || tokens.length !== 1) return "all";
  if (tokens[0] === "movie") return "movie";
  if (tokens[0] === "tv") return "show";
  return "all";
}

export type BootstrapAction = "seed" | "resume" | "none";

/**
 * How /search should start on this render, given the `?q=` seed and whatever the page has already
 * acted on (`null` = nothing yet). The page consumes the seed and clears the param, which brings
 * it straight back here with an empty seed — so "no seed" must only mean "resume the last
 * conversation" on the very first pass. Getting this wrong is what made a mid-conversation reload
 * replay turn one and throw away every refinement.
 *
 * Returns the next `handled` value alongside the action so the caller just stores what it is given.
 */
export function nextBootstrap(seed: string, handled: string | null): { action: BootstrapAction; handled: string } {
  // A seed is live until it is consumed; the only reason to skip one is that it is the exact seed
  // already in flight (a StrictMode double-invoke, or a render before the param clear lands).
  if (seed) return { action: handled === seed ? "none" : "seed", handled: seed };
  // Empty seed: either a cold landing on /search (resume) or the settling render after a consumed
  // seed. Settling resets `handled` to "", so searching the same phrase again still starts a turn.
  return { action: handled === null ? "resume" : "none", handled: "" };
}

/**
 * The plural of `word` for `n` — the noun only, so call sites read `${n} ${plural(n, "pick")}`.
 *
 * One helper rather than a ternary per site: the count copy on this page is written in four
 * places and only some of them agreed, so the live region announced "1 picks ready." to a screen
 * reader while the server's own sentence beside it was correctly singular. Regular -s plurals are
 * all this page has; anything irregular gets written out longhand rather than taught to a helper
 * that would then be trusted with words it cannot handle.
 */
export function plural(n: number, word: string): string {
  return n === 1 ? word : `${word}s`;
}

/**
 * How many the quality floor held back, as the tray must state it.
 *
 * The tray's contents are the truth when it HAS contents. It used to be labelled from
 * `droppedByFloor`, a scalar the server sends beside the cards, so a `done` carrying
 * `droppedByFloor: 2` and one card put "Show the 2 under your 6 bar" over a tray holding one
 * title. The two happened to agree on live turns, which is exactly why it survived: any server
 * path that sets one without the other prints a number the user can count and disprove.
 *
 * The scalar is still the answer when the tray is empty — a turn can report a cut without sending
 * the cards down, and "3 held back" is honest there. `null` means the server said nothing at all,
 * which is not the same as a zero.
 */
export function floorHeldBack(belowFloorCount: number, droppedByFloor: number | null): number {
  if (belowFloorCount > 0) return belowFloorCount;
  return typeof droppedByFloor === "number" && droppedByFloor > 0 ? droppedByFloor : 0;
}

/**
 * What separates clauses in an accounting line. The server's own `AskReelReason.Separator`, so a
 * client-composed clause sits in the same list as the server's without changing punctuation
 * halfway through the sentence.
 */
const REASON_SEPARATOR = " · ";

/**
 * Has this page run out? The server's answer, not an id diff.
 *
 * The old rule counted how many of a page's ids were not already in `order` and latched
 * `exhausted` on zero — but a page's `done.results` is always a subset of what already streamed as
 * `candidate` frames, so that count was usually zero on a page that had just added thirteen cards
 * (R1-03). It was not ALWAYS zero, and the difference was invisible: `candidate` early-returns on
 * a title already in `byId`, which still holds every card of every earlier turn including its
 * below-floor tray, so a page overlapping the previous tray reached `done` with genuinely new ids
 * and behaved correctly. One gesture, opposite behaviour, decided by tray overlap.
 *
 * The server is the only party that can answer this: it derives the no-repeat set from its own
 * rows, so a page it returns nothing for is a page with nothing left to return. `kept` is the
 * server's own count of what it kept and equals `results.length` on every path that sends it;
 * both are required to be empty, so a malformed frame fails towards "ask again" rather than
 * towards a latch the user cannot undo.
 */
export function pageIsExhausted(data: Pick<DoneData, "results" | "kept">): boolean {
  return data.results.length === 0 && (data.kept == null || data.kept <= 0);
}

/**
 * The provenance line under the grid.
 *
 * The server's sentence describes ONE turn — "13 picks ranked for you, from 65 pulled". On an
 * un-paged answer that is the whole truth and it is rendered verbatim. On a paged one it is true
 * of the last page and false of the grid: R1-03 watched "24 picks ranked for you, from 60 pulled"
 * sit under 37 cards, and the alternative (the newest page's sentence) would have claimed 13 of
 * the same 37. Neither is the accumulated set, so the line says both — what is on screen, and
 * where the last of it came from.
 *
 * A count of zero falls back to the sentence alone: "0 picks so far" is a claim about a screen
 * with no grid on it, and the branch that renders this only fires when there are cards anyway.
 */
export function footnote(reason: string | null, shownCount: number, paged: boolean): string | null {
  if (!reason) return null;
  if (!paged || shownCount <= 0) return reason;
  return `${shownCount} ${plural(shownCount, "pick")} so far${REASON_SEPARATOR}last page: ${reason}`;
}

/**
 * How many threads `Recent ▾` asks for on a first open, and after "Show older".
 *
 * The route is `GET /api/search/conversations?limit=N`, clamped server-side to 1..100, and it has
 * **no `offset`** — so 100 is the whole of what this control can reach, and there is no third
 * gesture to offer. Recorded here rather than discovered again: a "Show older" that keeps
 * requesting would silently return the same hundred rows forever.
 */
export const RECENT_LIMIT = 20;
export const OLDER_LIMIT = 100;

/**
 * What the thread menu should request, and what it may offer.
 *
 * SB-05 is the missing half of a control that was otherwise correct: the menu showed 20 of an
 * account's 51 threads, matching the contract's documented `?limit=20`, and said nothing about the
 * other 31 — so a menu framed as "a means of getting back to an answer" read as a complete list
 * and was not one. The first page stays cheap (this is a control almost nobody touches on a given
 * visit); asking for the rest is one deliberate gesture.
 *
 * The offer is made only over a FULL page, because a short one is the whole list and promising
 * older threads over it promises rows that do not exist. At the ceiling the menu says so instead
 * of offering a gesture the route cannot honour.
 */
export function recentListing(
  showOlder: boolean,
  rowCount: number,
): { limit: number; canShowOlder: boolean; atCeiling: boolean } {
  return {
    limit: showOlder ? OLDER_LIMIT : RECENT_LIMIT,
    canShowOlder: !showOlder && rowCount >= RECENT_LIMIT,
    atCeiling: showOlder && rowCount >= OLDER_LIMIT,
  };
}

/**
 * Whether the provenance line belongs on screen.
 *
 * `resumed` is in, and SB-06 is what happened when it was not. The resume payload carries the
 * turn's `reason` verbatim — "24 picks ranked for you, from 81 pulled · widened to find enough ·
 * only 5 picks cleared your 6.0 bar, so it was relaxed" — the reducer stores it, and the page then
 * gated the render on `status === "done"`. A resume is deliberately NOT `done` (see the status
 * doc-comment), so a restored answer lost the only on-screen account of how it was assembled,
 * including the fact that the quality floor had been relaxed to fill it.
 *
 * The other two conditions are unchanged and are about honesty of a different kind: mid-page the
 * sentence describes a turn that has not finished, and with nothing on screen it is a claim about
 * a grid that is not there.
 */
export function showsFootnote(
  status: ChatStatus,
  appending: boolean,
  shownCount: number,
  settledAnswer = false,
): boolean {
  if (appending || shownCount <= 0) return false;
  if (status === "done" || status === "resumed") return true;
  // F6-06's third casualty. A Stop over a held answer withdrew the provenance line along with the
  // tray and the paging, leaving a grid with no account of where it came from — for an answer
  // nothing had replaced and whose sentence was still perfectly true. Offered only over a settled
  // answer: a stopped turn's own partial has no finished sentence to print, and `reason` is null
  // there in any case.
  return status === "stopped" && settledAnswer;
}

/**
 * The floor, formatted for the tray label — always one decimal, always the same string.
 *
 * `toLocaleString(undefined, { maximumFractionDigits: 1 })` printed "6" in the tray while the
 * server's own sentence forty pixels below it read "6.0 bar", and on a comma-decimal locale it
 * printed "6,5" beside the server's "6.5". Both halves of that are fixed by pinning the locale
 * and the fraction digits: this is chrome, but it is chrome quoting the SAME number the server
 * quotes, so it renders as an invariant one-decimal string. Display only — it is never sent
 * anywhere, never a cache key, never a filter row.
 */
const FLOOR_FORMAT = new Intl.NumberFormat("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 });

export function formatFloor(floor: number | null | undefined): string | null {
  return typeof floor === "number" && Number.isFinite(floor) ? FLOOR_FORMAT.format(floor) : null;
}

/**
 * The account's quality bar, applied on the client to cards from a turn that never finished.
 *
 * The floor is the SERVER's job and it runs at `done`. Stop a page ten seconds in and `done` never
 * arrives, so the cards that already streamed keep whatever score they had — and nine sub-floor
 * picks sat in the recommended grid with a tray directly beneath them announcing that seventeen
 * picks had been held back for being under the 6.0 bar. Three numbers on one screen that cannot
 * all be true. This does not rank (the client cannot, and does not try): it only enforces the one
 * standing condition the user set, on the cards they can see.
 *
 * The comparison is made on the number the BADGE prints — one decimal — not on the value behind
 * it. A card scored 5.96 displays as "6.0", and holding it back would put a card badged 6.0 inside
 * a tray labelled "under your 6.0 bar", which is the same contradiction wearing the other hat.
 *
 * A card with no prediction, and an account with no bar, are both evidence of nothing: never held
 * back. Order is preserved within each half, because a stopped turn has no ranking to assert.
 *
 * `appliesTo` bounds it to the cards of a turn the server has NOT ranked — the ids after the
 * settled prefix. Without it the same call re-floors finished work: a stopped page would apply the
 * client's bar to the answer it was extending, and a Stop that hands a previous answer back whole
 * would strip cards out of a grid the server had already floored and committed. Absent = every
 * card, which is what a turn with nothing settled behind it wants (F4-08).
 */
export function floorPartition(
  cards: AskCard[],
  floor: number | null | undefined,
  appliesTo?: ReadonlySet<string>,
): { kept: AskCard[]; held: AskCard[] } {
  if (typeof floor !== "number" || !Number.isFinite(floor)) return { kept: cards, held: [] };
  const kept: AskCard[] = [];
  const held: AskCard[] = [];
  for (const c of cards) {
    if (appliesTo && !appliesTo.has(c.titleId)) {
      kept.push(c);
      continue;
    }
    const rating = c.predictedRating;
    const under = typeof rating === "number" && Number.isFinite(rating) && Math.round(rating * 10) / 10 < floor;
    (under ? held : kept).push(c);
  }
  return { kept, held };
}

/**
 * How many card-shaped placeholders the grid holds open while a turn is working.
 *
 * F6-04: the grid was zero-height until the first card, so the document went **837 px → 4 482 px in
 * a single frame** when the batch landed, and the turn scored **CLS 0.672** against Google's 0.25
 * "poor" boundary — a page with one "Show me more" on it scored 1.158, in four discrete jumps.
 * Everything the reader was reaching for moved under them.
 *
 * Eight, or the server's own count where it is smaller. Both bounds earn their place: reserving 24
 * rows for a turn that will keep ten is the same shift pointing downward, and reserving nothing
 * before `discovered` arrives leaves the first 6–10 seconds of every turn unreserved — which is
 * most of it. A turn that has found nothing yet gets the full eight, because it is going to fill
 * the grid either way.
 */
export const SKELETON_CAP = 8;

export function skeletonCount(phase: PhaseData | null): number {
  const found = phase?.found ?? 0;
  return found > 0 ? Math.min(found, SKELETON_CAP) : SKELETON_CAP;
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
    .filter((t) => t.text.trim().length > 0 && (t.role === "user" || t.role === "assistant"))
    .map((t) => ({ role: t.role as "user" | "assistant", text: t.text }));
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

/**
 * Was the answer a resume restores made of more than one page?
 *
 * F4-03: this was `turns.some((t) => t.kind === "page")` — true if the thread had EVER been paged,
 * for the rest of its life. A fresh question asked in a thread that was paged three turns earlier
 * came back reading "6 picks so far · last page: 6 picks ranked for you, from 40 pulled": the same
 * count twice in one sentence, and "last page" over an answer with one page. And it read
 * differently before and after the reload, because the LIVE `paged` is per-answer and a new ask
 * clears it — the same answer, two sentences, with a page turn in between flipping it.
 *
 * The grid a resume restores is the last completed ask plus whatever paged onto it, so the
 * question is not "did this thread ever page" but "did anything page onto the answer on screen".
 * An `ask` writes a user row and an assistant row together; a `page` writes an assistant row
 * alone — so the rule is a `page` row after the last `ask` row. `kind` is optional on the wire and
 * absent has always meant `ask`, which matters: reading absent as "not an ask" would leave the
 * cursor at -1 and make every page row in the thread count again.
 */
export function wasPaged(rows: ConversationPayload["turns"]): boolean {
  let lastAsk = -1;
  for (let i = 0; i < rows.length; i++) if ((rows[i].kind ?? "ask") === "ask") lastAsk = i;
  return rows.slice(lastAsk + 1).some((t) => t.kind === "page");
}

/**
 * The question the restored grid actually answers.
 *
 * The server replays the last COMPLETED `ask` block, so when the newest turn failed the cards on
 * screen belong to an earlier question — and the seam, which says only "picked up where you left
 * off", let them read as the answer to the one directly above them. This is the server's own rule
 * (`ResumeAsync`'s `lastAsk`) applied to the transcript it sent, so the two cannot drift: the last
 * completed assistant `ask` row, and the nearest user row above it.
 */
export function answeredQuestion(rows: ConversationPayload["turns"]): string {
  const answer = answeredTurnIndex(rows);
  for (let i = answer - 1; i >= 0; i--) {
    if (rows[i].role === "user") return rows[i].text;
  }
  return "";
}

/** Where the last completed assistant `ask` row sits, or -1. The row a resumed grid belongs to. */
function answeredTurnIndex(rows: ConversationPayload["turns"]): number {
  return rows.reduce(
    (found, t, i) => (t.role === "assistant" && (t.kind ?? "ask") === "ask" && t.status === "Completed" ? i : found),
    -1,
  );
}

/**
 * The id of the turn the restored grid belongs to — what a page of it must name as `pageOf`.
 *
 * Exactly the row {@link answeredQuestion} reads the question off, so the two halves of a page
 * request can never name different turns: the query is that row's question and this is that row's
 * id. Null where the payload carries no ids (an older server), which is why `pageOf` is optional —
 * the server then falls back to comparing the query, which is right on every screen except the one
 * R3-02 found.
 */
export function answeredTurnId(rows: ConversationPayload["turns"]): string | null {
  const row = rows[answeredTurnIndex(rows)];
  return row ? row.id ?? row.turnId ?? null : null;
}

/**
 * What a failed turn says for itself, in one line.
 *
 * Three cases, not two, and the first of them is narrower than it looks. `client_aborted` is what
 * the server writes when the client's connection went away — and it goes away identically for a
 * Stop, a reload, a slept phone and a discarded tab. The server cannot tell those apart, so
 * neither can this sentence.
 *
 * F6-09 is what saying otherwise cost: a finder hard-reloaded a running turn and the resumed
 * transcript told them "you stopped it", permanently, across two further reloads. The wording was
 * ACCURATE for a real Stop — verified in the same session — which is exactly what makes the class
 * dangerous: copy that is right most of the time and asserts a cause the client cannot know. "You
 * left" is true of all four, and it is still specific enough to be worth reading.
 *
 * Any other code is a thing that happened TO them; no code at all is neither, so it claims
 * neither. The silent case is not hypothetical: the resume payload projects `sequence`, `role`,
 * `text`, `kind` and `status` and no `failureCode`, so today every resumed failure arrives
 * unattributed. The wording lights up the moment the field does.
 */
export function failedTurnMessage(failureCode: string | null): string {
  if (failureCode === "client_aborted") return "That one didn’t finish before you left.";
  if (failureCode) return "That one hit a snag.";
  return "That one didn’t finish.";
}

/** How often a still-running turn is re-checked, and how long that is worth doing for. */
export const PENDING_TURN_POLL_MS = 5_000;
export const PENDING_TURN_BUDGET_MS = 60_000;

/**
 * The excluded terms a conflict is about — the account's excluded genres and its avoid-keywords,
 * as one list for one sentence.
 *
 * They are two lists server-side and one fact to a reader: "your conditions rule out comedy and
 * anime" does not care which list a term came from. A term can legitimately be in both (a genre
 * the account excludes AND an avoid-keyword it lists), and naming it twice reads as a bug in the
 * very place the product is explaining itself — so the merge is case-insensitively deduplicated,
 * keeping the first spelling. Blanks are dropped: "rules out  and anime" is worse than saying less.
 */
export function excludedTerms(genres: string[] | undefined, keywords: string[] | undefined): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of [...(genres ?? []), ...(keywords ?? [])]) {
    const term = raw.trim();
    const key = term.toLowerCase();
    if (!term || seen.has(key)) continue;
    seen.add(key);
    out.push(term);
  }
  return out;
}

/** "comedy" · "comedy and anime" · "comedy, anime and musical". A list a person can read. */
export function listPhrase(items: string[]): string {
  if (items.length === 0) return "";
  if (items.length === 1) return items[0];
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

/**
 * Whether "Show me more" may be offered at all — the question being, whose answer is on screen?
 *
 * A page EXTENDS an answer, so it only makes sense over one: a completed turn of this visit, or a
 * thread the server handed back whole. Every other state fails for a reason of its own, and R1-04
 * is what happens when none of them are checked. Get a 37-card answer, ask something new, press
 * Stop before the first card lands: the previous answer is correctly held and dimmed, and the page
 * control rendered underneath it with no idea the cards above it belonged to an earlier question.
 * Pressing it replayed the STOPPED question, the dim came off, and 37 held cards silently stopped
 * being "the previous answer" and became page one of a different one.
 *
 * So: `holdingPrevious` is disqualifying whatever the status — those cards are not this question's
 * answer. `stopped` is disqualifying because an abandoned turn never reached `done`, which is where
 * ranking and the quality floor run: extending an unranked, unfloored partial compounds it. `error`
 * and `blocked` have their own controls ("Ask again", "Check again") saying what to do instead, and
 * a page from either would be a second, contradictory offer beside them.
 */
export function canPage(
  status: ChatStatus,
  holdingPrevious: boolean,
  exhausted: boolean,
  cardCount: number,
  settled = 0,
): boolean {
  if (cardCount === 0 || exhausted || holdingPrevious) return false;
  if (status === "done" || status === "resumed") return true;
  // …and the one stopped screen where paging is honest: a Stop that gave a held previous answer
  // back whole (F6-06). Every card on it is settled — finished, server-ordered, server-floored work
  // — and `pageQuery` names the question it answers, so the page extends that answer rather than
  // the abandoned one. A stopped turn's OWN partial is what the rule above exists for and has
  // `settled === 0` against a non-zero grid, so it is still refused. R1-04 was not "a stopped state
  // offered paging"; it was paging sending the question that had never been answered.
  return status === "stopped" && settled === cardCount;
}

/**
 * Did a turn just finish — in any of the ways a turn can finish?
 *
 * R3-03. A completed `/ask` streams outside RTK Query and so invalidated nothing: `getConversation`
 * provides `{type:"Conversation", id}` and only `deleteConversation` ever invalidated it, so every
 * client-side remount of `/search` re-rendered the payload from the last fetch. Page an answer to
 * 21 + 21, press Home, press Back, and 14 + 10 came back — the thread as it was when the page last
 * asked the server, under the footnote of a turn two pages ago. A hard reload restored all 42,
 * which is what pinned it to the client rather than to the record.
 *
 * Every exit counts, not just `done`: a Stop and a failure both write a row (`Failed`, with its
 * code), and a `page` that completes changes what the thread's newest answer is. Each of the three
 * leaves the cached payload describing a conversation that has moved on.
 *
 * `blocked` is in as well — a 409 means another turn is writing this conversation right now, so the
 * cached copy is if anything staler than after a turn of this tab's own.
 */
export function turnSettled(previous: ChatStatus, next: ChatStatus): boolean {
  return previous === "streaming" && next !== "streaming";
}

/** The text of the last thing the USER said — what a page or a re-ask has to replay. */
export function lastUserQuery(turns: ChatTurn[]): string {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].role === "user") return turns[i].text;
  }
  return "";
}

interface State {
  turns: ChatTurn[];
  status: ChatStatus;
  phase: PhaseData | null;
  byId: Record<string, AskCard>;
  order: string[];
  reason: string | null;
  shown: number[]; // tmdbIds surfaced across the whole conversation (no-repeat)
  conversationId: string | null;
  /** True while a "load more" turn is in flight — its results append instead of replacing. */
  appending: boolean;
  /** The message to replay when paging. Keeps "more" on the same intent as the last real turn. */
  lastQuery: string;
  /**
   * Latches when the SERVER returns a page with no cards at all, so the page control stops
   * offering. Never inferred from an id diff — see {@link pageIsExhausted}.
   */
  exhausted: boolean;
  /**
   * True once this answer has had at least one page appended to it. The footnote needs it: the
   * server's sentence describes one turn, and on a paged answer that is not the grid.
   */
  paged: boolean;
  /**
   * The server's account of a collision between this question and the account's own exclusions —
   * "you asked for horror, and horror is excluded". Shown as a note beside the answer, never as an
   * error: the exclusion still holds and the turn still ran.
   */
  conflict: string | null;
  /**
   * The excluded terms behind {@link State.conflict}, genres and avoid-keywords merged. Kept as
   * data so the notice can state them as a list rather than the page re-parsing the server's
   * prose; empty when the server named none.
   */
  conflictTerms: string[];
  /** The cards the quality floor cut on the last completed turn. Their own tray, never the grid. */
  belowFloor: AskCard[];
  /**
   * The server's own count of what the quality floor cut on the last completed turn. Distinct from
   * `belowFloor.length` on purpose: a turn can report a cut without sending the cards down, and the
   * tray has to be able to say "3 held back" when it was handed none of them. `null` = the server
   * said nothing, which is not the same as zero.
   */
  droppedByFloor: number | null;
  /** Set alongside `status: "error"` when the client can name the cause. */
  errorReason: ErrorReason;
  /**
   * True while the cards on screen belong to the PREVIOUS answer and the current turn has not yet
   * produced one of its own. A new question used to blank the grid the instant it was asked, so the
   * screen went empty for the fifteen-odd seconds a turn takes — and pressing Stop inside that
   * window destroyed an answer the user was still reading, for a question they had not replaced.
   * The previous answer is now held until the first `candidate` of the new turn arrives, which is
   * the first moment there is anything to put in its place.
   */
  holdingPrevious: boolean;
  /**
   * Cards this turn has streamed so far, reset at the start of every turn and every page. It is
   * NOT `order.length`: a page appends to the previous answer, and while `holdingPrevious` is set
   * `order` belongs to the previous turn entirely. The stopped state quotes this number, so it has
   * to mean "arrived before you stopped" rather than "on screen".
   */
  arrived: number;
  /**
   * How many stored cards the server dropped from a resumed or replayed answer because the account
   * has since interacted with the title. Said out loud rather than absorbed: a card that silently
   * disappears between two views of the same answer is indistinguishable from a bug, and the
   * founder-locked "never show things I've already seen" rule is why it went.
   */
  removed: number;
  /**
   * When this thread last stored cards, as the server reports it. `null` means it never did — a
   * thread from before results were kept, which is the ONLY state that may say "transcript only".
   * Reporting that on a thread seventeen seconds old is what made the copy a lie.
   */
  lastCardsAt: string | null;
  /**
   * When this thread last had a turn of any kind. The only stamp a transcript-only thread has, so
   * it is what dates the seam where `lastCardsAt` is null. Absent on an older server.
   */
  lastTurnAt: string | null;
  /**
   * What became of the last stored turn, when it was not a completed answer — see
   * {@link ResumedTail}. Set only by a resume, and cleared by anything that starts a turn of this
   * visit, because from that moment the screen is about the new turn rather than the old one.
   */
  resumedTail: ResumedTail | null;
  /**
   * The question the restored grid answers, where that is NOT the last thing the user said. Read
   * by the seam when `resumedTail` is set; empty otherwise, because on an ordinary resume the grid
   * answers the question directly above it and naming it is noise.
   */
  answeredQuery: string;
  /**
   * How many leading ids of `order` belong to turns that already reached `done` — the part of the
   * screen that is finished work and is frozen in the order the server sent it (SB-01).
   *
   * Without this the client had one order for a live screen and another for the same answer after
   * a reload: a page's `phase` frames re-sorted the WHOLE grid by blend, including the answer the
   * page was extending, while the stored record kept `(Sequence, Rank)`. The contract's rule —
   * "array order is the contract, never re-sort by blend" — applies to every turn the moment it
   * completes; a turn still streaming has no server order yet, which is the only reason the client
   * derives one at all.
   */
  settled: number;
  /**
   * The {@link StreamTag} id of the run whose frames this state accepts. `0` — the value
   * {@link initialChatState} carries, and therefore what a `+ New`, a thread switch and a resume
   * all land on — matches no run, so every frame still arriving from an abandoned stream is
   * dropped. See {@link staleFrame}.
   */
  stream: number;
  /**
   * The question the GRID answers, which is not always the last thing the user said.
   *
   * A page replays a question, so it needs the one the cards on screen belong to — and after a Stop
   * that hands a held previous answer back whole (F6-06) those two differ: `lastQuery` is the
   * question that was abandoned and is what "Ask again" replays, while this is the question the
   * restored grid answers and is what "Show me more" extends. Conflating them is R1-04, where
   * pressing the page control sent the STOPPED question and silently adopted the earlier question's
   * cards as its page one.
   */
  pageQuery: string;
  /**
   * The id of the assistant turn the GRID is, which is what a page of it names as `pageOf`. Set
   * from the `conversation` frame of the turn that produced the answer, or from the resumed
   * transcript; null until one arrives, and on a server that sends no turn id at all.
   *
   * It moves with {@link State.pageQuery} and for the same reason — the two are the question and
   * the row it was asked in, and a request carrying one without the other is R3-02 from the other
   * side.
   */
  pageTurnId: string | null;
  /**
   * The turn the stream in flight is writing, as its own `conversation` frame named it. Only a
   * landing place: it becomes {@link State.pageTurnId} at `done`, because a turn that never
   * completed is not an answer anything can be a page of.
   */
  activeTurnId: string | null;
}

export type ChatAction =
  | { type: "userTurn"; message: string; fresh: boolean; stream?: StreamTag }
  | { type: "moreTurn"; stream?: StreamTag }
  | { type: "reaskTurn"; stream?: StreamTag }
  | { type: "phase"; data: PhaseData; stream?: StreamTag }
  | { type: "conversation"; data: ConversationData; stream?: StreamTag }
  | { type: "resume"; payload: ConversationPayload }
  | { type: "assistant"; data: AssistantMessageData; stream?: StreamTag }
  | { type: "candidate"; data: AskCard; stream?: StreamTag }
  | { type: "scored"; data: { titleId: string; predictedRating: number | null }; stream?: StreamTag }
  | { type: "reranked"; data: { titleId: string; fit: number | null; why: string | null }; stream?: StreamTag }
  | { type: "conflict"; data: ConflictData; stream?: StreamTag }
  | { type: "done"; data: DoneData; stream?: StreamTag }
  | { type: "stop" }
  | { type: "reset" }
  | { type: "turnInProgress" }
  | { type: "conversationGone" }
  | { type: "error"; reason?: ErrorReason };

/** The three actions that START a run, and so set the id whose frames the reducer will accept. */
const TURN_STARTS: ReadonlySet<ChatAction["type"]> = new Set(["userTurn", "moreTurn", "reaskTurn"]);

/**
 * Does this frame belong to a run that is no longer on screen? F6-01's guard.
 *
 * Two checks, and they catch different halves of the same defect. The **run id** catches a frame
 * from any run this state is not currently listening to — including one started before a
 * conversation existed at all, where there is no id to compare. The **conversation id** catches a
 * frame that names a thread other than the one being rendered, which is the durable half: a `done`
 * for thread A arriving while thread B is on screen is what wrote A's ten cards under B's question
 * and moved B's turn count.
 *
 * Untagged actions pass, deliberately: the reducer is a pure state machine that tests drive
 * directly, and requiring a tag would make every such dispatch a no-op rather than a test of the
 * case it is about. The hook tags every frame it forwards.
 */
export function staleFrame(state: State, tag: StreamTag | undefined): boolean {
  if (!tag) return false;
  if (tag.id !== state.stream) return true;
  return (
    tag.conversationId != null &&
    state.conversationId != null &&
    tag.conversationId !== state.conversationId
  );
}

/**
 * What a failed `/ask` means, as an action.
 *
 * The four statuses the server distinguishes are four different things for a person to do, and
 * collapsing them into one "search hit a snag" is how a 409 — which means the answer is on its
 * way — looked exactly like a crash and cost the user the grid they were reading. Pure and
 * exported so the mapping is testable without a renderer: the hook is not.
 *
 * The code is preferred over the status where both are available, because the status is shared
 * (a 404 from a proxy is not `conversation_not_found`) while the code is ours.
 */
export function failureFor(err: unknown): ChatAction {
  if (err instanceof AskStreamError) {
    if (err.code === "turn_in_progress" || err.status === 409) return { type: "turnInProgress" };
    if (err.code === "conversation_not_found" || err.status === 404) return { type: "conversationGone" };
    // A 401 here already survived one refresh-and-retry inside streamAskReel, so the session is
    // genuinely gone rather than merely stale — say so, and let the page offer a sign-in rather
    // than a "try again" that cannot work.
    if (err.status === 401) return { type: "error", reason: "session" };
    if (err.status === 400) return { type: "error", reason: "request" };
  }
  return { type: "error", reason: null };
}

export const initialChatState: State = {
  turns: [], status: "idle", phase: null, byId: {}, order: [], reason: null, shown: [],
  conversationId: null, appending: false, lastQuery: "", exhausted: false, paged: false,
  conflict: null, conflictTerms: [], belowFloor: [], droppedByFloor: null, errorReason: null,
  holdingPrevious: false, arrived: 0, removed: 0, lastCardsAt: null, settled: 0,
  lastTurnAt: null, resumedTail: null, answeredQuery: "", stream: 0, pageQuery: "",
  pageTurnId: null, activeTurnId: null,
};

/**
 * The ranking score for a card. The server sends its own `blend` and its order is authoritative,
 * so prefer it: re-deriving a second blend here from the parts produced a subtly different order
 * on the client than the one the server had already committed to storage as `Rank`.
 * The local formula remains for older servers and for cards still mid-stream, which have no
 * server blend until the turn completes.
 */
function blend(c: AskCard): number {
  if (c.blend != null) return c.blend;
  return (c.similarity ?? 0) * 0.35 + ((c.predictedRating ?? 6) / 10) * 0.25 + (c.fit ?? 0.5) * 0.4;
}

/**
 * The stream-stable order for the cards of ONE turn still in flight: clean ids before outside ids,
 * each block sorted by the personal blend. Applied only at a `phase` boundary — `candidate` appends
 * and `scored`/`reranked` update card data in place, so rows stop jumping under the cursor on every
 * SSE event (the old behaviour re-sorted at render time, live). Array.sort is stable, so equal
 * blends keep arrival order and identical inputs render identically.
 *
 * It is NOT the answer's order. A turn that has reached `done` has a server order, and this is the
 * client assembling one it does not have yet — see {@link streamOrder} for the boundary between
 * the two, and the `done` case for why the server's array wins the moment it arrives.
 */
export function partitionedOrder(order: string[], byId: Record<string, AskCard>): string[] {
  const present = order.filter((id) => byId[id]);
  const byBlend = (a: string, b: string) => blend(byId[b]) - blend(byId[a]);
  return [
    ...present.filter((id) => !byId[id].outsideRequest).sort(byBlend),
    ...present.filter((id) => byId[id].outsideRequest).sort(byBlend),
  ];
}

/**
 * The on-screen order mid-stream: a settled prefix carried verbatim, and only the in-flight tail
 * settled by blend.
 *
 * SB-01 is what happens without the split. `partitionedOrder` used to run over the whole merged
 * array on every `phase` frame, so a page's progress re-sorted the answer ALREADY on screen —
 * page-two's picks interleaved into page-one's — and the reload that followed rendered the same
 * cards in the stored `(Sequence, Rank)` order instead. One answer, two orders, one gesture apart,
 * and the contract is explicit that the array is the order: `blend` is rounded for display and
 * carries a tiebreak the rounded number cannot reproduce.
 *
 * `settled` is how many leading ids belong to turns that already have a `done`. Those are finished
 * work with a server order of their own and are never touched again; everything after them is this
 * turn's, and settling it at a phase boundary is the rhythm that keeps rows from jumping.
 */
export function streamOrder(order: string[], byId: Record<string, AskCard>, settled: number): string[] {
  const cut = Math.max(0, Math.min(settled, order.length));
  return [
    ...order.slice(0, cut).filter((id) => byId[id]),
    ...partitionedOrder(order.slice(cut), byId),
  ];
}

/**
 * The running floor count across a paged answer. Silence on both sides stays silence: `null` means
 * "the server never told us", which the tray treats differently from a genuine zero.
 */
function sumDropped(carried: number | null, incoming: number | undefined): number | null {
  if (carried == null && incoming == null) return null;
  return (carried ?? 0) + (incoming ?? 0);
}

/**
 * Everything on screen that DESCRIBES the answer rather than being it — the provenance sentence,
 * whether it is more than one turn's work, the under-floor tray, the tray's own count, and whether
 * the server has said there is no more.
 *
 * F6-06: these were cleared the instant a new question was asked, while the cards they describe
 * were deliberately held. A Stop three seconds later therefore left the user with the grid and none
 * of its accounting — 16 tray picks that had been one tap away simply ceased to exist, the footnote
 * went, and "Show me more" went with it. Reloading the page brought all of it back, which is how it
 * was known to be a client-side loss rather than a server one.
 *
 * So they now move with the grid: held while the grid is held, and released at the single moment
 * the grid is replaced (the first candidate of the new turn) or completed.
 */
function clearedAnswerDescription(): Pick<State, "reason" | "paged" | "belowFloor" | "droppedByFloor" | "exhausted"> {
  return { reason: null, paged: false, belowFloor: [], droppedByFloor: null, exhausted: false };
}

export function chatReducer(state: State, action: ChatAction): State {
  const tag = "stream" in action ? action.stream : undefined;
  // F6-01. A frame from a run this screen has moved on from changes NOTHING — not the grid, not
  // the transcript, not the conversation id, and above all not the turn count of the thread now on
  // screen. Checked once here rather than in each of the eight frame cases, because the one case
  // that forgot the check would be the one that wrote the damage.
  if (!TURN_STARTS.has(action.type) && staleFrame(state, tag)) return state;
  switch (action.type) {
    case "userTurn": {
      const turn: ChatTurn = { role: "user", text: action.message };
      // A FRESH turn is a new conversation — the transcript goes with it, so there is no previous
      // answer left to hold: cards under a blank transcript belong to a question no longer on
      // screen. A follow-up in the same thread is the opposite case, and it keeps its answer up.
      return action.fresh
        ? { ...initialChatState, turns: [turn], status: "streaming", lastQuery: action.message, stream: tag?.id ?? 0 }
        : {
            ...state, turns: [...state.turns, turn], status: "streaming", phase: null, stream: tag?.id ?? 0,
            appending: false, lastQuery: action.message,
            // Whatever turn was named last belongs to the answer BEFORE this one; this turn's own
            // id arrives on its `conversation` frame, and until it does there is none to adopt.
            activeTurnId: null,
            // A conflict describes the question that produced it. A new question gets a clean
            // slate, or last turn's "horror is excluded" note hangs over an unrelated answer.
            conflict: null, conflictTerms: [], errorReason: null,
            // The resumed thread's unanswered tail belongs to the visit BEFORE this one. Asking
            // anything replaces it — the retry offer it carries is exactly what was just taken.
            resumedTail: null, answeredQuery: "",
            // `byId` / `order` deliberately NOT cleared — see `holdingPrevious`. They are replaced
            // by the first candidate of this turn, not by the act of asking.
            holdingPrevious: state.order.length > 0, arrived: 0,
            // …and neither is the answer's own accounting, on exactly the same terms. See
            // {@link clearedAnswerDescription}.
            ...(state.order.length > 0 ? {} : clearedAnswerDescription()),
          };
    }
    case "moreTurn":
      // A page, not a turn: no transcript entry, and the existing cards stay put. It is explicitly
      // NOT a hold — a page ADDS to what is on screen, so the first candidate must append rather
      // than replace the answer it is extending.
      //
      // `holdingPrevious` is carried, never cleared. Clearing it unconditionally was R1-04's
      // mechanism: a page started over a HELD answer dropped the dim and the seam, and the cards
      // of an earlier question became page one of the stopped one without a word on screen.
      // `canPage` now stops such a page being started at all; this is the backstop, and it fails
      // the honest way — the previous answer stays labelled as previous.
      //
      // Everything on screen at the moment a page starts belongs to a turn that has finished, so
      // the whole of `order` is settled: the page's own frames may settle only what the page
      // itself brings (SB-01).
      return {
        ...state, status: "streaming", phase: null, appending: true, errorReason: null, arrived: 0,
        settled: state.order.length, resumedTail: null, answeredQuery: "", stream: tag?.id ?? 0,
        // `pageTurnId` is deliberately carried — it names the answer this page extends, which is
        // the whole point of the request about to go out. Only the in-flight handle resets.
        activeTurnId: null,
      };
    case "reaskTurn": {
      // The same question asked again — a media-filter change, or a retry after a failure. Like a
      // page it adds no transcript entry (the user said it once; echoing their bubble on every
      // toggle click was the bug), and like a follow-up it REPLACES the answer — but not until the
      // replacement exists. Toggling to Movies used to blank the grid for the whole turn, so a
      // Stop, a timeout or a failed re-ask cost the user the answer they were reading.
      const turns = state.turns.at(-1)?.role === "assistant" ? state.turns.slice(0, -1) : state.turns;
      return {
        ...state, turns, status: "streaming", phase: null, stream: tag?.id ?? 0,
        appending: false, errorReason: null, activeTurnId: null,
        resumedTail: null, answeredQuery: "",
        holdingPrevious: state.order.length > 0, arrived: 0,
        ...(state.order.length > 0 ? {} : clearedAnswerDescription()),
      };
    }
    case "phase":
      // A phase boundary is the ONLY mid-stream moment the rows may re-order (see
      // partitionedOrder) — a settle-then-shuffle rhythm instead of jumping on every event. A held
      // previous answer is exempt: it is finished work, and reshuffling it while a new turn runs
      // would animate the OLD answer in response to the NEW one's progress.
      //
      // …and so is every OTHER turn's block, held or not. `streamOrder` settles this turn's cards
      // and carries the settled prefix verbatim: a page that re-sorted the answer it was extending
      // is SB-01, and the reload afterwards proved the two disagreed.
      return {
        ...state, status: "streaming", phase: action.data,
        order: state.holdingPrevious ? state.order : streamOrder(state.order, state.byId, state.settled),
      };
    case "conversation":
      // The turn id is remembered as well as the conversation's: it is the handle a later "Show me
      // more" puts on the wire as `pageOf` (R3-02). Held aside rather than adopted, because the
      // frame arrives BEFORE the turn has produced anything — see {@link State.activeTurnId}.
      return { ...state, conversationId: action.data.id, activeTurnId: action.data.turnId ?? null };
    case "resume": {
      // NOT `done`: see the Status doc-comment. `lastQuery` comes back with the transcript so the
      // media toggle and the retry have something to replay — without it, a resumed thread's chips
      // were inert until the user typed again.
      const p = action.payload;
      // F4-06. The notice is the account of WHY the answer looks nothing like the question, and it
      // was stream-only: `askreel-conflict` and `askreel-reply-conflict` were both absent after a
      // reload, leaving a screen full of post-2000 dramas under "a 90s romantic comedy" with
      // nothing on it explaining either. Restored here, from the stored object, never recomputed —
      // the account's conditions can change between the turn and the resume (that is the whole
      // point of the Conditions screen the notice points at), and a re-derived notice would
      // describe an answer produced under yesterday's rules.
      const restoredConflict = resumedConflict(p);
      const turns = transcriptOf(p.turns);
      // …and the pill goes back on the bubble the notice qualifies, which is the newest assistant
      // row — the same row `reason` and the notice itself are about. A warning on every resumed
      // answer would be worse than none, so this is the one row and only when there is a notice.
      if (restoredConflict) {
        for (let i = turns.length - 1; i >= 0; i--) {
          if (turns[i].role === "assistant") {
            turns[i] = { ...turns[i], conflict: true };
            break;
          }
        }
      }
      const cards = p.cards ?? [];
      const byId: Record<string, AskCard> = {};
      for (const c of cards) byId[c.titleId] = c;
      // The tray, restored as a tray. It came back on the payload all along and was dropped on the
      // floor here, so a reloaded thread lost the "Show the N under your bar" fence it had been
      // left with — the picks the user's OWN bar held back simply ceased to exist across a reload,
      // which is the one cut on the screen they can act on (it names a number they chose).
      //
      // Through `isCard` rather than cast: these rows go straight into a render, the guard is the
      // same one every streamed card passes, and a malformed entry would otherwise reach
      // `ResultCard` as a keyless undefined.
      const tray = (p.belowFloor ?? []).filter(isCard);
      return {
        ...initialChatState,
        turns,
        conversationId: p.id,
        status: "resumed",
        // The server's own `lastQuery` first: it reads the last USER row, including one whose
        // answer never landed, which is exactly the question a retry has to replay.
        lastQuery: p.lastQuery ?? lastUserQuery(turns),
        // The restored grid answers the thread's last completed question, which is what a page of
        // it replays. Same source as `lastQuery` on an ordinary resume; they part company only
        // after a Stop.
        pageQuery: p.lastQuery ?? lastUserQuery(turns),
        // …and the turn that grid IS, read off the same transcript by the same rule the server
        // replays by, so a page of a resumed answer names the row it extends rather than leaving
        // the server to infer it from a question the thread may have moved past (R3-02).
        pageTurnId: answeredTurnId(p.turns),
        shown: p.shown ?? [],
        byId,
        // Verbatim. `partitionedOrder` is for a live stream, where the client is assembling an
        // answer frame by frame; a stored answer HAS an order — the ranks the server committed —
        // and re-deriving it from a rounded blend is how the same question comes back looking
        // different on every reload.
        order: cards.map((c) => c.titleId),
        // All of it is finished work — a resumed answer is never re-ordered by anything.
        settled: cards.length,
        belowFloor: tray,
        // The tray IS the count on a resume: the server hands the whole stored block back, so
        // `belowFloor.length` is what the fence can be opened and counted against. A payload that
        // carried the field and an empty array is a server saying the floor cut nothing; one that
        // carried no field at all said nothing, and `null` is how {@link floorHeldBack} tells the
        // two apart — a zero there would suppress a fence that an older server still owes us.
        droppedByFloor: p.belowFloor ? tray.length : null,
        reason: p.reason ?? null,
        conflict: restoredConflict?.message ?? null,
        conflictTerms: restoredConflict?.terms ?? [],
        // The payload's `reason` is the NEWEST turn's, verbatim — so on a thread whose answer was
        // paged it describes one page and the restored grid is several, which is R1-03 arriving by
        // the resume path. The transcript says which; see {@link wasPaged} for why "ever paged" is
        // the wrong question and what it cost (F4-03).
        paged: wasPaged(p.turns),
        removed: p.removed ?? 0,
        lastCardsAt: p.lastCardsAt ?? null,
        lastTurnAt: p.lastTurnAt ?? null,
        // What the server was left holding, and which question the grid above it belongs to.
        // Both read off the transcript the payload already sends; neither was read before SB-03.
        resumedTail: lastTurnOutcome(p.turns),
        answeredQuery: answeredQuestion(p.turns),
      };
    }
    case "assistant":
      // Paging replays the last message, so the model replies again — suppress it rather than
      // stuttering the same answer into the transcript on every scroll.
      return state.appending
        ? state
        : {
          ...state,
          turns: [...state.turns, { role: "assistant", text: action.data.text, conflict: action.data.conflict }],
        };
    case "candidate": {
      const c = action.data;
      // The swap. The previous answer stayed up while this turn was thinking; this is the first
      // moment there is something to put in its place, so it is replaced now rather than having
      // been blanked when the question was asked.
      if (state.holdingPrevious) {
        // The previous turn's block goes with it, so nothing is settled any more: what is on
        // screen from here is this turn's stream, and it settles at phase boundaries like any other.
        return {
          ...state, holdingPrevious: false, byId: { [c.titleId]: c }, order: [c.titleId], arrived: 1, settled: 0,
          // The previous answer is gone at this exact moment, so its accounting goes with it —
          // this is the release {@link clearedAnswerDescription} is held for.
          ...clearedAnswerDescription(),
          pageQuery: state.lastQuery,
        };
      }
      if (state.byId[c.titleId]) return state;
      return {
        ...state, byId: { ...state.byId, [c.titleId]: c }, order: [...state.order, c.titleId],
        arrived: state.arrived + 1,
      };
    }
    case "scored": {
      const e = state.byId[action.data.titleId];
      if (!e) return state;
      return { ...state, byId: { ...state.byId, [action.data.titleId]: { ...e, predictedRating: action.data.predictedRating } } };
    }
    case "reranked": {
      const e = state.byId[action.data.titleId];
      if (!e) return state;
      return { ...state, byId: { ...state.byId, [action.data.titleId]: { ...e, fit: action.data.fit, why: action.data.why } } };
    }
    case "conflict":
      return {
        ...state,
        conflict: action.data.message,
        // The terms travel beside the sentence rather than being parsed back out of it. Two
        // server-side lists, one fact for the reader — and the keyword half is the one a turn
        // colliding with an avoid-keyword ("anime", "musical") had no way to name at all.
        conflictTerms: excludedTerms(action.data.excludedGenres, action.data.excludedKeywords),
      };
    case "done": {
      // A caught server exception arrives as `done` with no results. Treat it as the failure it is
      // rather than as an empty answer, and keep whatever is already on screen — a page that blew
      // up should offer a retry, not wipe the reel.
      if (isFailedDone(action.data)) return { ...state, status: "error", phase: null, appending: false };
      const byId = { ...state.byId };
      const newTmdb: number[] = [];
      for (const c of action.data.results) {
        byId[c.titleId] = { ...byId[c.titleId], ...c };
        if (c.tmdbId != null) newTmdb.push(c.tmdbId);
      }
      // Kept for the page's own accounting only — the no-repeat set the SERVER applies is
      // derived from its own cards, so an uncapped list here can no longer grow a request.
      const shown = Array.from(new Set([...state.shown, ...newTmdb]));
      const incoming = action.data.results.map((c) => c.titleId);
      // THE contract line: "array order is the contract … never re-sort by it". `results` is this
      // turn's answer in the order the server committed to storage as `Rank`, so it is taken
      // verbatim — the client's own blend ordering was only ever a stand-in for a server order
      // that had not arrived yet, and keeping it past `done` is what made the same answer render
      // one way live and another way after a reload (SB-01).
      //
      // A page appends AFTER the settled block, which is carried through untouched. Only the
      // settled prefix is carried — a candidate this page streamed and its own `done` then dropped
      // (the quality floor cut it) falls out, exactly as it does on a fresh turn, so the screen and
      // the stored record hold the same cards.
      const carried = state.appending
        ? state.order.slice(0, state.settled).filter((id) => byId[id])
        : [];
      const order = state.appending
        ? [...carried, ...incoming.filter((id) => !carried.includes(id))]
        : incoming;
      // A page the server returned nothing for: it is the end of this answer, and it has nothing
      // to say ABOUT the answer. `AskReelReason.Empty` is true of the page and false of the grid
      // above it, so the accounting that was already there stands.
      const emptyPage = state.appending && pageIsExhausted(action.data);
      return {
        ...state, byId, order, status: "done", shown, appending: false,
        // This turn is finished work now, so the whole of `order` is frozen: nothing on screen may
        // be re-ordered again until a new turn replaces it.
        settled: order.length,
        // The answer landed, so there is nothing left being held: `order` above is this turn's.
        holdingPrevious: false,
        reason: emptyPage ? state.reason : action.data.reason,
        exhausted: state.appending ? pageIsExhausted(action.data) : false,
        // A page that added cards makes the answer a paged one, and it stays paged until the next
        // question replaces it — the footnote needs to know the grid is more than one turn's work.
        paged: state.appending ? state.paged || !emptyPage : false,
        // The grid now answers THIS question, so that is what a page of it must replay. A page
        // leaves it alone: it extends the answer it found, it does not become it.
        pageQuery: state.appending ? state.pageQuery : state.lastQuery,
        // …and the row that question was asked in, on exactly the same terms. Together they are
        // the whole of what a page request says about what it extends (R3-02).
        pageTurnId: state.appending ? state.pageTurnId : state.activeTurnId,
        // The under-floor tray belongs to the turn that produced it. A page appends its own tray
        // to the previous one; a fresh answer replaces it.
        belowFloor: state.appending ? [...state.belowFloor, ...(action.data.belowFloor ?? [])] : action.data.belowFloor ?? [],
        // Same rule as the tray it labels: a page ADDS its cut to the running total, a fresh answer
        // replaces it. `null` rather than `0` where neither side said anything — a server that never
        // sent the field has not told us it cut nothing, and the tray falls back to counting the
        // cards it was handed in that case, which zero would silently suppress.
        droppedByFloor: sumDropped(state.appending ? state.droppedByFloor : null, action.data.droppedByFloor),
      };
    }
    case "stop":
      // The user pressed Stop. Everything that streamed stays — including a previous answer still
      // being held, which is the whole point: stopping a turn must never cost the user the answer
      // they were already looking at. Its own status, not `done` and never `idle`: `done` would
      // claim a complete answer and print "N picks ranked for you" over a partial one, and `idle`
      // over a transcript rendered literally nothing — no cards, no message, no retry, no way back.
      // Never an error either; they asked for this.
      //
      // …and the HOLD ends here (F6-06). "Held until the new one arrives" is only true while
      // something is still coming, and after a Stop nothing is: what is on screen is the previous
      // answer, whole and finished, not a placeholder for one. Leaving the hold latched is what
      // left the kept grid at 40 % opacity as a resting state, under a label about a question that
      // would never be answered, with its tray, its footnote and its paging all withdrawn — every
      // one of which came back on a reload, because the server had them all along.
      //
      // `lastQuery` is deliberately NOT restored: the abandoned question is what "Ask again"
      // replays. `pageQuery` already holds the one the grid answers, which is what a page extends.
      return { ...state, status: "stopped", phase: null, appending: false, holdingPrevious: false };
    case "reset":
      // `+ New`. Everything goes, including the conversation id — the whole point is that the next
      // question opens a thread of its own rather than extending one the user has finished with.
      return initialChatState;
    case "turnInProgress":
      // 409. This exact question is already being answered — by another tab, or by a turn this one
      // abandoned before the server noticed. NOTHING on screen is cleared, because nothing on
      // screen is wrong: the grid, a held previous answer and the transcript all stay exactly where
      // they are, and the page offers a check-again that re-sends the SAME turn id (which replays
      // the stored answer the moment it exists) rather than a retry that would take the same 409.
      return { ...state, status: "blocked", phase: null, appending: false };
    case "conversationGone":
      // 404. The thread this tab remembered is not the caller's any more — deleted from another
      // device, or an id that outlived its retention. The id is dropped here so the next turn mints
      // a new conversation instead of asking again for one that is gone; the transcript stays on
      // screen, because it is still what the user was reading and the page says what happened.
      return { ...state, status: "error", errorReason: "conversation", conversationId: null, phase: null, appending: false };
    case "error":
      // `appending` and `phase` are cleared deliberately. A failed page used to leave `appending`
      // latched true, so the next `done` appended to a grid the user had been told was broken, and
      // the progress bar kept animating under an error banner.
      return { ...state, status: "error", appending: false, phase: null, errorReason: action.reason ?? null };
  }
}

/**
 * Mount guard for React StrictMode's deliberate mount → unmount → mount cycle in development.
 *
 * The unmount abort has to stay: leaving a stream running after the page is gone leaks a reader
 * and keeps the server's turn open. But StrictMode's fake unmount fires that same cleanup on a
 * component that is about to come straight back, killing the request the first mount had just
 * started — and `nextBootstrap` will not restart it, because the seed is already recorded as
 * handled. The result was a /search landing that showed a spinner and never resolved.
 *
 * So the abort is deferred by one turn of the event loop and cancelled if a mount lands first.
 * A real unmount is one tick later than it was; a StrictMode remount costs nothing.
 *
 * `schedule` / `cancel` are injectable so this is testable as a pure state machine — the hook
 * itself cannot be, since vitest here runs in node with no renderer.
 */
export function createMountGuard(
  abort: () => void,
  schedule: (fn: () => void) => unknown = (fn) => setTimeout(fn, 0),
  cancel: (handle: unknown) => void = (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
): { mount: () => void; unmount: () => void } {
  let mounted = false;
  let pending: unknown = null;
  return {
    mount() {
      mounted = true;
      if (pending !== null) {
        cancel(pending);
        pending = null;
      }
    },
    unmount() {
      mounted = false;
      if (pending !== null) cancel(pending);
      pending = schedule(() => {
        pending = null;
        if (!mounted) abort();
      });
    },
  };
}

/**
 * Drives a multi-turn Ask Reel conversation: each turn streams from /api/search/ask with the
 * prior transcript + the tmdbIds already shown (so nothing repeats), accumulates cards in a
 * stream-stable order (appended on arrival, re-sorted only at phase boundaries and on done —
 * see partitionedOrder), and records the reply. Cards flagged outsideRequest are returned
 * separately as `outsideCards`, never mixed into `cards`. `fresh` starts a new conversation.
 */
export function useAskReelChat() {
  const [state, dispatch] = useReducer(chatReducer, initialChatState);
  // Predicted ratings the server has recalibrated since these cards were streamed. Ask Reel's
  // results live in this reducer, not in the RTK cache, so a `feed-scores-updated` frame cannot
  // reach them any other way — the same reason removedTitlesSlice exists for removals.
  const overrides = useSelector(selectScoreOverrides);
  // `send`/`loadMore`/`reask` must stay referentially stable (an effect and an IntersectionObserver
  // depend on them) while still reading current state. Synced in an effect rather than during
  // render: writing a ref mid-render is what the React Compiler rule flags, and the callbacks only
  // ever read it from an event or an observer — long after commit.
  const stateRef = useRef(state);
  useEffect(() => {
    stateRef.current = state;
  }, [state]);
  const abortRef = useRef<AbortController | null>(null);
  // The id of the exchange in flight. Kept so a 409 can be answered by re-sending the SAME turn —
  // the server then replays the stored answer instead of running the question a second time.
  const turnIdRef = useRef<string | null>(null);
  const watchdogRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // The run this hook is still listening to, set synchronously so a side effect fired from inside
  // a frame handler (writing the remembered conversation id) can tell live from abandoned before
  // the reducer's own commit has landed. `0` = nothing is running that this screen still owns.
  const activeStreamRef = useRef(0);

  // The active media filter, kept in a ref so `send`/`loadMore` stay referentially stable while
  // still reading the current value — a filter change re-sends rather than hiding cards locally.
  const mediaRef = useRef<string[]>([]);
  const setMediaTypes = useCallback((types: string[]) => {
    mediaRef.current = types;
  }, []);

  const clearWatchdog = useCallback(() => {
    if (watchdogRef.current !== null) {
      clearTimeout(watchdogRef.current);
      watchdogRef.current = null;
    }
  }, []);

  const run = useCallback((
    msg: string,
    fresh: boolean,
    kind: "turn" | "more" | "reask",
    turnId?: string,
    pageOf?: string | null,
  ) => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    // A stream that goes quiet is indistinguishable from one that is thinking, so it needs a
    // deadline rather than a guess. Reset on every frame: a slow turn that keeps talking is fine,
    // thirty seconds of silence is not, and before this the tab simply span forever.
    const armWatchdog = () => {
      clearWatchdog();
      watchdogRef.current = setTimeout(() => {
        controller.abort();
        if (abortRef.current === controller) abortRef.current = null;
        dispatch({ type: "error", reason: "timeout" });
      }, STREAM_IDLE_TIMEOUT_MS);
    };
    armWatchdog();

    // The whole request. No transcript, no shown set, no `continuation` flag: the server reads all
    // three off its own rows, and `kind` — ask or page — is derived there from whether the query
    // equals the conversation's last user turn. A fresh start deliberately drops the conversation
    // id rather than asking the server to fork it.
    const conversationId = fresh ? null : stateRef.current.conversationId;
    // Minted here unless the caller brought one. A caller's id is a RETRY of the same exchange —
    // the reconnect after a 409, or a seeded turn whose page remounted under StrictMode — and the
    // server answers it from the stored turn instead of running and billing it twice.
    const clientTurnId = turnId ?? mintTurnId();
    turnIdRef.current = clientTurnId;
    // The label every frame of THIS run carries, so the reducer can tell them from the frames of a
    // run the user has already walked away from (F6-01). Minted before the first dispatch, because
    // the turn-start action is what tells the reducer which id to listen to.
    const stream: StreamTag = { id: mintStreamId(), conversationId };
    activeStreamRef.current = stream.id;
    dispatch(
      kind === "more" ? { type: "moreTurn", stream }
        : kind === "reask" ? { type: "reaskTurn", stream }
          : { type: "userTurn", message: msg, fresh, stream },
    );

    streamAskReel(askRequest(msg, clientTurnId, conversationId, mediaRef.current, pageOf), (ev) => {
      armWatchdog();
      switch (ev.event) {
        case "phase": dispatch({ type: "phase", data: ev.data, stream }); break;
        case "conversation":
          // Remembered as it arrives, not at the end of the turn: an abandoned or failed stream
          // still happened, and the thread it opened is still this tab's.
          //
          // …unless this run has been abandoned. A stale `conversation` frame would otherwise
          // overwrite localStorage with the thread the user just left, so the NEXT cold load
          // resumes it — the durable half of F6-01, one layer below the reducer. Read off a ref
          // set synchronously by `run` rather than off `stateRef`, which is synced in an effect
          // and so still holds the PREVIOUS run's id when this frame (the stream's first, and
          // usually its fastest) lands before the commit.
          if (activeStreamRef.current === stream.id) {
            storeConversationId(ev.data.id);
          }
          dispatch({ type: "conversation", data: ev.data, stream });
          break;
        case "assistant-message": dispatch({ type: "assistant", data: ev.data, stream }); break;
        case "candidate": dispatch({ type: "candidate", data: ev.data, stream }); break;
        case "candidate-scored": dispatch({ type: "scored", data: ev.data, stream }); break;
        case "candidate-reranked": dispatch({ type: "reranked", data: ev.data, stream }); break;
        case "conflict": dispatch({ type: "conflict", data: ev.data, stream }); break;
        case "done": dispatch({ type: "done", data: ev.data, stream }); break;
        default: break;
      }
    }, controller.signal)
      .then(() => {
        clearWatchdog();
      })
      .catch((err: unknown) => {
        clearWatchdog();
        if (controller.signal.aborted) return;
        const failure = failureFor(err);
        // Forgotten HERE rather than in the reducer, which stays pure: the stored id is what the
        // next cold load resumes from, and leaving a 404'd id in localStorage means every visit
        // from this tab starts by asking for a conversation that is gone.
        if (failure.type === "conversationGone") storeConversationId(null);
        dispatch(failure);
      });
  }, [clearWatchdog]);

  /**
   * Ask a question. `turnId` is for a caller that has already minted one and needs THIS send to be
   * the same exchange as a previous attempt — the seeded first turn, whose id rides in the router
   * state so that a StrictMode remount (which resets every ref in this component) re-sends the
   * same id and is replayed server-side rather than billed a second time.
   */
  const send = useCallback((message: string, fresh = false, turnId?: string) => {
    const msg = message.trim();
    if (!msg) return;
    run(msg, fresh, "turn", turnId);
  }, [run]);

  /**
   * Next page of the SAME question. Replays the last message with every tmdbId already shown
   * excluded, so the server returns fresh picks that append to the reel. No transcript entry.
   */
  const loadMore = useCallback(() => {
    const s = stateRef.current;
    // The authoritative guard, here rather than only in the render: this is what issues the
    // request, and a page over a held or abandoned answer is a billed turn for a question the
    // user never got an answer to (R1-04). See {@link canPage} for what each state means.
    // `pageQuery`, not `lastQuery`: after a Stop that handed the previous answer back whole they
    // are two different questions, and the one this page must replay is the one the grid answers.
    const query = s.pageQuery || s.lastQuery;
    if (!canPage(s.status, s.holdingPrevious, s.exhausted, s.order.length, s.settled) || !query) return;
    // …and `pageTurnId` names the row that question was asked in, so the server extends THAT turn
    // rather than deriving `kind` from whatever was said last. Both halves or neither: after a
    // Stop the question the grid answers is no longer the conversation's last user turn, and a
    // page sent without the id was recorded as a second ask (R3-02) — the transcript grew a
    // question nobody typed and the next reload replayed one turn's cards instead of two.
    run(query, false, "more", undefined, s.pageTurnId);
  }, [run]);

  /**
   * Ask the LAST question again and replace its answer — the media toggle, and the retry offered
   * after a failure. The user said it once, so it adds no second copy of their bubble; re-sending
   * through `send` is what stuttered a duplicate user turn into the transcript on every toggle.
   */
  const reask = useCallback(() => {
    const s = stateRef.current;
    if (!s.lastQuery) return;
    // Deliberately not gated on `streaming`, unlike `loadMore`: this one is always a direct user
    // action, and toggling the filter mid-stream should abort and re-run rather than be swallowed.
    run(s.lastQuery, false, "reask");
  }, [run]);

  /**
   * Ask for the SAME exchange again, by its own id — what the 409 state offers.
   *
   * This is the one retry that must not mint a new id. A 409 means the question is already being
   * answered, so asking it again under a new id would run and bill it twice and put two answers in
   * the transcript; re-sending the same id either takes the 409 again (still running) or is
   * replayed from the stored turn the moment it completes. A retry after a FAILURE is the opposite
   * case and goes through `reask`, because the server replays a stored failure verbatim.
   */
  const retryTurn = useCallback(() => {
    const s = stateRef.current;
    const turnId = turnIdRef.current;
    if (!turnId || !s.lastQuery) return;
    run(s.lastQuery, false, "reask", turnId);
  }, [run]);

  /** Abandon the turn in flight, keeping whatever it already produced. */
  const stop = useCallback(() => {
    clearWatchdog();
    abortRef.current?.abort();
    abortRef.current = null;
    activeStreamRef.current = 0;
    dispatch({ type: "stop" });
  }, [clearWatchdog]);

  /**
   * Put a stored conversation back on screen — transcript, cards, order, reason and all.
   *
   * Gated on there being a transcript: `/conversations/latest` answers `{id: null, turns: []}` for
   * an account that has never asked anything, and resuming that would replace a cold start's mood
   * presets with an empty thread.
   */
  const resume = useCallback((payload: ConversationPayload) => {
    if (!payload.id || payload.turns.length === 0) return;
    // Whatever was streaming was streaming for a DIFFERENT thread — this one is what the screen is
    // about from here. Aborted rather than merely ignored: the reducer's tag check already makes a
    // stale frame inert, but a run left open keeps a reader, a watchdog and the server's turn
    // alive for a conversation nobody is looking at (F6-01).
    abortRef.current?.abort();
    abortRef.current = null;
    activeStreamRef.current = 0;
    turnIdRef.current = null;
    clearWatchdog();
    // A resumed thread is this tab's thread from now on, so it is remembered on the same terms as
    // one started here — otherwise a cold visit that resumed would still have nothing stored, and
    // the NEXT reload would fall back to the account's newest thread instead of this one.
    storeConversationId(payload.id);
    dispatch({ type: "resume", payload });
  }, [clearWatchdog]);

  /**
   * Forget this thread and start over — what `+ New` does.
   *
   * The stored id is REPLACED rather than removed, by {@link FRESH_CONVERSATION}: clearing it left
   * this tab looking like a browser that had never asked anything, so the reload resumed
   * `/conversations/latest` and put the user back in the thread they had just left (SB-02). The
   * marker lives until the first `conversation` frame of the next question overwrites it with the
   * real id.
   */
  const startNew = useCallback(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    activeStreamRef.current = 0;
    turnIdRef.current = null;
    clearWatchdog();
    storeConversationId(FRESH_CONVERSATION);
    dispatch({ type: "reset" });
  }, [clearWatchdog]);

  useEffect(() => {
    const guard = createMountGuard(() => {
      abortRef.current?.abort();
      if (watchdogRef.current !== null) clearTimeout(watchdogRef.current);
    });
    guard.mount();
    return () => guard.unmount();
  }, []);

  // A backgrounded tab holds the stream open indefinitely — the server keeps a turn alive and the
  // reader keeps a connection for a page nobody is looking at. A minute out of sight is the line;
  // coming back inside it costs nothing, and beyond it the user gets a retry rather than a stale
  // answer that finished streaming into a hidden tab.
  useEffect(() => {
    if (typeof document === "undefined") return;
    let hiddenTimer: ReturnType<typeof setTimeout> | null = null;
    const onVisibility = () => {
      if (hiddenTimer !== null) {
        clearTimeout(hiddenTimer);
        hiddenTimer = null;
      }
      if (document.visibilityState !== "hidden") return;
      hiddenTimer = setTimeout(() => {
        hiddenTimer = null;
        if (stateRef.current.status !== "streaming") return;
        clearWatchdog();
        abortRef.current?.abort();
        abortRef.current = null;
        dispatch({ type: "error", reason: "timeout" });
      }, HIDDEN_ABORT_MS);
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      if (hiddenTimer !== null) clearTimeout(hiddenTimer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [clearWatchdog]);

  // NO render-time sort: `order` is the state's stream-stable order, so rows only ever move when
  // the reducer re-sorts it (phase / done) — sorting here is what made cards jump on every event.
  // Without the override pass an open Ask Reel view would keep quoting the pre-recalibration
  // number for a title the hero has already updated.
  const all = applyScoreOverrides(state.order.map((id) => state.byId[id]).filter(Boolean), overrides);
  const cards = all.filter((c) => !c.outsideRequest);
  const outsideCards = all.filter((c) => c.outsideRequest);
  const belowFloor = applyScoreOverrides(state.belowFloor, overrides);
  return {
    turns: state.turns, status: state.status, phase: state.phase, cards, outsideCards, reason: state.reason,
    send, resume, startNew, loadMore, reask, retryTurn, stop, setMediaTypes, exhausted: state.exhausted, appending: state.appending,
    /**
     * Whether "Show me more" may be offered — the same rule {@link loadMore} enforces, so the
     * control and the callback can never disagree about whose answer is on screen. False over a
     * held previous answer, a stopped turn, a failure and a 409; see {@link canPage}.
     */
    pageable: canPage(state.status, state.holdingPrevious, state.exhausted, state.order.length, state.settled),
    /**
     * Whether this answer is more than one turn's work. Read only by the footnote, which has to
     * say so: the server's sentence describes the turn that produced it, never the accumulation.
     */
    paged: state.paged,
    /** The server's note that this question collides with a standing exclusion; not an error. */
    conflict: state.conflict,
    /**
     * The terms that note is about — genres and avoid-keywords in one list, ready for
     * {@link listPhrase}. Empty when the server named none, which is every message-only conflict.
     */
    conflictTerms: state.conflictTerms,
    /** Cards the quality floor cut on the last turn — their own tray, never the grid. */
    belowFloor,
    /**
     * How many the quality floor cut on the last turn, as the SERVER counted them. Cleared on a new
     * user turn, a re-ask and a fresh conversation, so last question's number never labels this
     * question's tray. `null` when the server said nothing at all.
     */
    droppedByFloor: state.droppedByFloor,
    /** Why the last failure happened, where the client can name it. */
    errorReason: state.errorReason,
    /** True once the last question has been asked, so a retry has something to replay. */
    canRetry: state.lastQuery.length > 0,
    /**
     * True while the cards above belong to the PREVIOUS question — the current turn is still
     * working and has produced nothing yet. The page seams and dims them rather than pretending
     * they answer what was just asked.
     */
    holdingPrevious: state.holdingPrevious,
    /** Cards this turn streamed before it ended. What the stopped state counts. */
    arrived: state.arrived,
    /**
     * The ids of the cards the SERVER has not ranked yet — everything after the settled prefix.
     *
     * The page needs the boundary, not just the count: the account's quality bar may be applied on
     * the client to a turn still streaming (F4-08 — a card scored under the bar belongs in the tray
     * the moment it is scored, not eighteen seconds later at `done`), and it must never be applied
     * to the finished work above it. A stopped page's earlier answer, and a previous answer handed
     * back whole, are both already floored by the server; re-flooring them here would take cards out
     * of a grid the server committed.
     */
    unsettled: state.order.slice(state.settled),
    /**
     * True when everything on screen is a completed answer — server-ordered, server-floored,
     * nothing of it still streaming and none of it held pending a replacement. What tells a Stop
     * that gave the previous answer back whole apart from a Stop that kept a half-streamed one
     * (F6-06); `done` and `resumed` satisfy it too, which is why the footnote's own rule can stay
     * one expression.
     */
    settledAnswer: state.order.length > 0 && state.settled >= state.order.length && !state.holdingPrevious,
    /**
     * The question the grid answers — what "Show me more" replays. Equal to the last thing the user
     * said on every ordinary screen, and deliberately not after a Stop: there, `lastQuery` is the
     * abandoned question ("Ask again" replays that one) and this is the answer still on screen.
     */
    pageQuery: state.pageQuery,
    /**
     * The thread this hook is on, mirrored into `localStorage` as it changes and read back by
     * {@link resumeTarget} on the next cold load — so a tab resumes ITS conversation rather than
     * the account's newest one.
     */
    conversationId: state.conversationId,
    /**
     * How many stored cards the server dropped out of a resumed answer because the account has
     * since rated, watched, saved or dismissed the title. Rendered as a one-line note: a card that
     * silently disappears between two views of the same answer looks exactly like a bug.
     */
    removed: state.removed,
    /**
     * When this thread last stored cards. `null` is the ONLY state that may claim the thread
     * predates saved picks — it was previously the only branch a resume could reach at all, which
     * is how a seventeen-second-old conversation was told it was too old to have any.
     */
    lastCardsAt: state.lastCardsAt,
    /**
     * When this thread last had a turn of any kind — the only stamp a transcript-only thread has,
     * so it is what dates the seam where `lastCardsAt` is null. Null on an older server.
     */
    lastTurnAt: state.lastTurnAt,
    /**
     * What became of the resumed thread's last turn, when it was not a completed answer. Null on
     * every ordinary resume, and cleared the moment this visit asks anything. See
     * {@link ResumedTail} — without it, a thread whose last question was stopped or crashed came
     * back showing the PREVIOUS answer under it, with nothing on the page saying so (SB-03).
     */
    resumedTail: state.resumedTail,
    /**
     * The question the restored grid answers, when that is not the last thing the user said. Read
     * by the seam alongside {@link ResumedTail}; empty on an ordinary resume.
     */
    answeredQuery: state.answeredQuery,
    /**
     * Has a turn of THIS visit produced what is on screen?
     *
     * The page reads it before applying a conversation payload: a reducer that has run a turn is
     * ahead of anything the server has been asked for since, and R3-03's fix (invalidating the
     * `Conversation` tag when a turn settles) means a refetch can now land WHILE that answer is on
     * screen. Without the check the refetch would replace a live, paged, twenty-one-card answer
     * with the server's account of it — correct data, arriving as a state change nobody asked for.
     *
     * `stream` is the run id the reducer is listening to, and it is non-zero from the moment a turn
     * starts until a resume or a `+ New` clears it — which is exactly "this visit owns the screen".
     */
    hasLocalTurn: state.stream !== 0,
  };
}
