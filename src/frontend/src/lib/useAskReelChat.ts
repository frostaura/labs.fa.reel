import { useCallback, useEffect, useReducer, useRef } from "react";
import { useSelector } from "react-redux";
import { applyScoreOverrides, selectScoreOverrides } from "../store/scoreOverridesSlice";
import { AskStreamError, isFailedDone, streamAskReel, type AskCard, type AskReelRequest, type ConversationData, type DoneData, type PhaseData } from "./askReelStream";

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
): AskReelRequest {
  return { query, clientTurnId, conversationId, mediaTypes };
}

/**
 * Which conversation a cold /search should resume.
 *
 * This tab's own remembered id, and `/conversations/latest` only when there is nothing remembered
 * at all. `/latest` is the account's newest thread, not this tab's: with two tabs open, or a phone
 * and a laptop, reloading tab A dropped the reader into tab B's conversation and the thread they
 * were reading was gone from the UI. Preferring the stored id is the whole of that fix, and it is
 * why `latest` is deprecated rather than merely unused.
 */
export function resumeTarget(storedId: string | null): string {
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
  turns: { sequence?: number; role: string; text: string; kind?: string; status?: string }[];
  /** The answer, in the order the server committed to storage as `Rank`. Never re-sorted. */
  cards?: AskCard[];
  reason?: string | null;
  /** Stored cards the eligibility re-filter dropped since the turn ran. */
  removed?: number;
  lastQuery?: string | null;
  /** The media filter that turn ran under, in request tokens ("movie" / "tv"). */
  mediaTypes?: string[];
  /** The whole conversation's no-repeat set — informational; the server applies its own. */
  shown?: number[];
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
 */
export function floorPartition(
  cards: AskCard[],
  floor: number | null | undefined,
): { kept: AskCard[]; held: AskCard[] } {
  if (typeof floor !== "number" || !Number.isFinite(floor)) return { kept: cards, held: [] };
  const kept: AskCard[] = [];
  const held: AskCard[] = [];
  for (const c of cards) {
    const rating = c.predictedRating;
    const under = typeof rating === "number" && Number.isFinite(rating) && Math.round(rating * 10) / 10 < floor;
    (under ? held : kept).push(c);
  }
  return { kept, held };
}

/**
 * The stored rows that are actually a transcript.
 *
 * A turn in flight is a row with no text yet, and a failed one keeps its empty text for good.
 * Rendering those puts blank bubbles on screen — an assistant that said nothing, under a question
 * it never answered — so they are dropped here rather than in the page, which would have to know
 * about row lifecycles to do it. The rows still count for `lastQuery`, because the question a
 * failed turn never answered is precisely the one a retry replays.
 */
export function transcriptOf(rows: { role: string; text: string }[]): ChatTurn[] {
  return rows
    .filter((t) => t.text.trim().length > 0 && (t.role === "user" || t.role === "assistant"))
    .map((t) => ({ role: t.role as "user" | "assistant", text: t.text }));
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
): boolean {
  if (cardCount === 0 || exhausted || holdingPrevious) return false;
  return status === "done" || status === "resumed";
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
  /** Latches when a page adds nothing new, so the scroll sentinel stops asking. */
  exhausted: boolean;
  /**
   * The server's account of a collision between this question and the account's own exclusions —
   * "you asked for horror, and horror is excluded". Shown as a note beside the answer, never as an
   * error: the exclusion still holds and the turn still ran.
   */
  conflict: string | null;
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
}

export type ChatAction =
  | { type: "userTurn"; message: string; fresh: boolean }
  | { type: "moreTurn" }
  | { type: "reaskTurn" }
  | { type: "phase"; data: PhaseData }
  | { type: "conversation"; data: ConversationData }
  | { type: "resume"; payload: ConversationPayload }
  | { type: "assistant"; data: { text: string } }
  | { type: "candidate"; data: AskCard }
  | { type: "scored"; data: { titleId: string; predictedRating: number | null } }
  | { type: "reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { type: "conflict"; message: string }
  | { type: "done"; data: DoneData }
  | { type: "stop" }
  | { type: "reset" }
  | { type: "turnInProgress" }
  | { type: "conversationGone" }
  | { type: "error"; reason?: ErrorReason };

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
  conversationId: null, appending: false, lastQuery: "", exhausted: false,
  conflict: null, belowFloor: [], droppedByFloor: null, errorReason: null,
  holdingPrevious: false, arrived: 0, removed: 0, lastCardsAt: null,
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
 * The stream-stable order: clean ids before outside ids, each block sorted by the personal blend.
 * Applied ONLY when a `phase` or `done` frame arrives — `candidate` appends and `scored`/`reranked`
 * update card data in place, so rows stop jumping under the cursor on every SSE event (the old
 * behaviour re-sorted at render time, live). Array.sort is stable, so equal blends keep arrival
 * order and identical inputs render identically.
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
 * The running floor count across a paged answer. Silence on both sides stays silence: `null` means
 * "the server never told us", which the tray treats differently from a genuine zero.
 */
function sumDropped(carried: number | null, incoming: number | undefined): number | null {
  if (carried == null && incoming == null) return null;
  return (carried ?? 0) + (incoming ?? 0);
}

export function chatReducer(state: State, action: ChatAction): State {
  switch (action.type) {
    case "userTurn": {
      const turn: ChatTurn = { role: "user", text: action.message };
      // A FRESH turn is a new conversation — the transcript goes with it, so there is no previous
      // answer left to hold: cards under a blank transcript belong to a question no longer on
      // screen. A follow-up in the same thread is the opposite case, and it keeps its answer up.
      return action.fresh
        ? { ...initialChatState, turns: [turn], status: "streaming", lastQuery: action.message }
        : {
            ...state, turns: [...state.turns, turn], status: "streaming", phase: null,
            reason: null, appending: false, lastQuery: action.message, exhausted: false,
            // A conflict describes the question that produced it. A new question gets a clean
            // slate, or last turn's "horror is excluded" note hangs over an unrelated answer.
            conflict: null, belowFloor: [], droppedByFloor: null, errorReason: null,
            // `byId` / `order` deliberately NOT cleared — see `holdingPrevious`. They are replaced
            // by the first candidate of this turn, not by the act of asking.
            holdingPrevious: state.order.length > 0, arrived: 0,
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
      return { ...state, status: "streaming", phase: null, appending: true, errorReason: null, arrived: 0 };
    case "reaskTurn": {
      // The same question asked again — a media-filter change, or a retry after a failure. Like a
      // page it adds no transcript entry (the user said it once; echoing their bubble on every
      // toggle click was the bug), and like a follow-up it REPLACES the answer — but not until the
      // replacement exists. Toggling to Movies used to blank the grid for the whole turn, so a
      // Stop, a timeout or a failed re-ask cost the user the answer they were reading.
      const turns = state.turns.at(-1)?.role === "assistant" ? state.turns.slice(0, -1) : state.turns;
      return {
        ...state, turns, status: "streaming", phase: null,
        reason: null, appending: false, exhausted: false,
        belowFloor: [], droppedByFloor: null, errorReason: null,
        holdingPrevious: state.order.length > 0, arrived: 0,
      };
    }
    case "phase":
      // A phase boundary is the ONLY mid-stream moment the rows may re-order (see
      // partitionedOrder) — a settle-then-shuffle rhythm instead of jumping on every event. A held
      // previous answer is exempt: it is finished work, and reshuffling it while a new turn runs
      // would animate the OLD answer in response to the NEW one's progress.
      return {
        ...state, status: "streaming", phase: action.data,
        order: state.holdingPrevious ? state.order : partitionedOrder(state.order, state.byId),
      };
    case "conversation":
      return { ...state, conversationId: action.data.id };
    case "resume": {
      // NOT `done`: see the Status doc-comment. `lastQuery` comes back with the transcript so the
      // media toggle and the retry have something to replay — without it, a resumed thread's chips
      // were inert until the user typed again.
      const p = action.payload;
      const turns = transcriptOf(p.turns);
      const cards = p.cards ?? [];
      const byId: Record<string, AskCard> = {};
      for (const c of cards) byId[c.titleId] = c;
      return {
        ...initialChatState,
        turns,
        conversationId: p.id,
        status: "resumed",
        // The server's own `lastQuery` first: it reads the last USER row, including one whose
        // answer never landed, which is exactly the question a retry has to replay.
        lastQuery: p.lastQuery ?? lastUserQuery(turns),
        shown: p.shown ?? [],
        byId,
        // Verbatim. `partitionedOrder` is for a live stream, where the client is assembling an
        // answer frame by frame; a stored answer HAS an order — the ranks the server committed —
        // and re-deriving it from a rounded blend is how the same question comes back looking
        // different on every reload.
        order: cards.map((c) => c.titleId),
        reason: p.reason ?? null,
        removed: p.removed ?? 0,
        lastCardsAt: p.lastCardsAt ?? null,
      };
    }
    case "assistant":
      // Paging replays the last message, so the model replies again — suppress it rather than
      // stuttering the same answer into the transcript on every scroll.
      return state.appending
        ? state
        : { ...state, turns: [...state.turns, { role: "assistant", text: action.data.text }] };
    case "candidate": {
      const c = action.data;
      // The swap. The previous answer stayed up while this turn was thinking; this is the first
      // moment there is something to put in its place, so it is replaced now rather than having
      // been blanked when the question was asked.
      if (state.holdingPrevious) {
        return { ...state, holdingPrevious: false, byId: { [c.titleId]: c }, order: [c.titleId], arrived: 1 };
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
      return { ...state, conflict: action.message };
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
      // Appending keeps what is on screen and adds only genuinely new ids; a page that adds
      // nothing latches `exhausted` so the scroll sentinel stops asking for more.
      const order = state.appending
        ? [...state.order, ...incoming.filter((id) => !state.order.includes(id))]
        : incoming;
      const gained = order.length - state.order.length;
      return {
        ...state, byId, order: partitionedOrder(order, byId), status: "done", shown, appending: false,
        // The answer landed, so there is nothing left being held: `order` above is this turn's.
        holdingPrevious: false,
        reason: state.appending && gained === 0 ? state.reason : action.data.reason,
        exhausted: state.appending ? gained === 0 : false,
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
      return { ...state, status: "stopped", phase: null, appending: false };
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

  const run = useCallback((msg: string, fresh: boolean, kind: "turn" | "more" | "reask", turnId?: string) => {
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
    dispatch(
      kind === "more" ? { type: "moreTurn" }
        : kind === "reask" ? { type: "reaskTurn" }
          : { type: "userTurn", message: msg, fresh },
    );

    streamAskReel(askRequest(msg, clientTurnId, conversationId, mediaRef.current), (ev) => {
      armWatchdog();
      switch (ev.event) {
        case "phase": dispatch({ type: "phase", data: ev.data }); break;
        case "conversation":
          // Remembered as it arrives, not at the end of the turn: an abandoned or failed stream
          // still happened, and the thread it opened is still this tab's.
          storeConversationId(ev.data.id);
          dispatch({ type: "conversation", data: ev.data });
          break;
        case "assistant-message": dispatch({ type: "assistant", data: ev.data }); break;
        case "candidate": dispatch({ type: "candidate", data: ev.data }); break;
        case "candidate-scored": dispatch({ type: "scored", data: ev.data }); break;
        case "candidate-reranked": dispatch({ type: "reranked", data: ev.data }); break;
        case "conflict": dispatch({ type: "conflict", message: ev.data.message }); break;
        case "done": dispatch({ type: "done", data: ev.data }); break;
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
    if (!canPage(s.status, s.holdingPrevious, s.exhausted, s.order.length) || !s.lastQuery) return;
    run(s.lastQuery, false, "more");
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
    // A resumed thread is this tab's thread from now on, so it is remembered on the same terms as
    // one started here — otherwise a cold visit that resumed would still have nothing stored, and
    // the NEXT reload would fall back to the account's newest thread instead of this one.
    storeConversationId(payload.id);
    dispatch({ type: "resume", payload });
  }, []);

  /**
   * Forget this thread and start over — what `+ New` does. The stored id goes with it, so a reload
   * lands on a cold start rather than resurrecting the conversation the user just left.
   */
  const startNew = useCallback(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    turnIdRef.current = null;
    clearWatchdog();
    storeConversationId(null);
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
    pageable: canPage(state.status, state.holdingPrevious, state.exhausted, state.order.length),
    /** The server's note that this question collides with a standing exclusion; not an error. */
    conflict: state.conflict,
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
  };
}
