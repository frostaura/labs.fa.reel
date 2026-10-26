import { AskStreamError, isCard, isFailedDone, isKnockout, judgeDegraded, type AnswerData, type AskCard, type AskKnockout, type AssistantMessageData, type ClarifyData, type ConflictData, type ConversationData, type DegradedData, type DoneData, type NoneFoundData, type PhaseData, type PicksData, type PlanData, type TrayData } from "../askReelStream";
import { excludedTerms } from "./copy";
import { answeredQuestion, pageIsExhausted, resumedPageTarget, wasPaged, type PageRefusal } from "./paging";
import {
  answerRowIndex, lastTurnOutcome, lastUserQuery, markTurnReply, mergeCards, resumedConflict, stoppedTranscript,
  transcriptOf, type ChatTurn, type ConversationPayload, type ResumedTail, type TurnAnswer,
} from "./transcript";

/**
 * The Ask Reel state machine: what one screen holds, the actions a stream and the page dispatch,
 * and the pure reducer between them. Split out of the hook (WS-2 task 5) so the state machine is
 * readable — and testable — without the effects that drive it.
 */
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
  /**
   * The same fact for the turn IN FLIGHT — the collision the running (or abandoned) question
   * produced, held apart from the answer's until that turn completes.
   *
   * R5-01. One slot could not hold both, so a colliding follow-up overwrote the kept answer's
   * notice and a Stop then left the survivor anchored under the wrong bubble. The turn's notice
   * renders with the turn — under its own reply while it streams, and with the stopped card once
   * it is abandoned — and becomes {@link State.conflict} at `done`, when the answer it explains is
   * what the grid holds.
   */
  turnConflict: string | null;
  /** The terms behind {@link State.turnConflict}, on the same terms as {@link State.conflictTerms}. */
  turnConflictTerms: string[];
  /** The cards the quality floor cut on the last completed turn. Their own tray, never the grid. */
  belowFloor: AskCard[];
  /**
   * The judge's partial matches on the last completed turn — the collapsed "close, but not quite"
   * tray. Never the grid, never counted as picks.
   */
  nearMiss: AskCard[];
  /**
   * The cards nobody could check on the last completed turn — the collapsed "not checked against
   * your request" tray of a none-found answer. Never the grid, never counted as picks.
   */
  unchecked: AskCard[];
  /** "Picks" or "NoneFound" for the answer on screen; null before any answer. */
  mode: string | null;
  /** What turning one condition off would unlock, on a none-found answer. Empty otherwise. */
  knockouts: AskKnockout[];
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
  /** How many of {@link removed} were already acted on when the answer was given — see {@link removedNote}. */
  removedEarlier: number;
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
   * The server's refusal of the last page of this answer, where it named one — see
   * {@link PageRefusal}. Cleared by anything that starts a turn, and by a page that lands.
   *
   * It is beside the answer rather than instead of it: a refused page changed nothing on screen,
   * so the grid, the tray, the footnote and the control all stay exactly where they are (R5-04).
   */
  pageRefusal: PageRefusal;
  /**
   * The status the screen was in when the page in flight started — what it returns to if that page
   * is refused. A page is the one turn that can fail without costing the user anything: it extends
   * a finished answer and never replaces it, so "back where you were" is a real destination, and
   * it is the only way to put the footnote and the control back without a reload.
   */
  pagePriorStatus: ChatStatus;
  /**
   * The turn the stream in flight is writing, as its own `conversation` frame named it. Only a
   * landing place: it becomes {@link State.pageTurnId} at `done`, because a turn that never
   * completed is not an answer anything can be a page of.
   */
  activeTurnId: string | null;
  /**
   * How many leading ids of `order` the SERVER ordered while the turn was still streaming — v2's
   * `picks` frames, which arrive past the gate and in their final order (§4.8: "appended per batch,
   * never reordered once shown"). A phase boundary may settle only what comes after them, and the
   * client's floor never touches them: the server already put sub-bar cards in the tray.
   */
  pinned: number;
  /**
   * The transcript row the live grid belongs to, or null for "the newest row" — the turn that is
   * producing it. Chat UI v2 puts every answer's cards under the reply that produced it, so the grid
   * has to know whose it is: a held previous answer stays under ITS reply while a new question
   * runs, and an item question's answer (§4.9 `answer`) leaves the grid under the answer it asked
   * about. See {@link gridRowOf}.
   */
  gridTurn: number | null;
  /** The planner's reading of the newest turn (§4.9 `plan`) — what the refine chips are built from. */
  plan: PlanData | null;
  /**
   * What the v2 `degraded` frames of the turn in flight said. Applied to that turn's reply as soon as
   * there is one, and merged with `done.degraded` at the end — so a degraded reading is marked on
   * screen while the turn is still running, not eighteen seconds later.
   */
  turnDegraded: string[];
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
  | { type: "plan"; data: PlanData; stream?: StreamTag }
  | { type: "picks"; data: PicksData; stream?: StreamTag }
  | { type: "tray"; data: TrayData; stream?: StreamTag }
  | { type: "answer"; data: AnswerData; stream?: StreamTag }
  | { type: "clarify"; data: ClarifyData; stream?: StreamTag }
  | { type: "noneFound"; data: NoneFoundData; stream?: StreamTag }
  | { type: "degraded"; data: DegradedData; stream?: StreamTag }
  | { type: "stop" }
  | { type: "reset" }
  | { type: "turnInProgress" }
  | { type: "pageRefused"; refusal: PageRefusal }
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
    // …and inside the 409 family, the refusal that changed nothing. R5-04: this fell through to
    // `turn_in_progress`, so a refused page announced that a turn was running when none was, and
    // took the footnote and the paging control down with it.
    if (err.code === "page_query_mismatch") return { type: "pageRefused", refusal: "mismatch" };
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
  conflict: null, conflictTerms: [], turnConflict: null, turnConflictTerms: [],
  belowFloor: [], nearMiss: [], unchecked: [], mode: null, knockouts: [], droppedByFloor: null, errorReason: null,
  holdingPrevious: false, arrived: 0, removed: 0, removedEarlier: 0, lastCardsAt: null, settled: 0,
  lastTurnAt: null, resumedTail: null, answeredQuery: "", stream: 0, pageQuery: "",
  pageTurnId: null, activeTurnId: null, pageRefusal: null, pagePriorStatus: "idle",
  pinned: 0, gridTurn: null, plan: null, turnDegraded: [],
};

/**
 * The transcript row the live grid renders under — the reply that produced it. `-1` before any row
 * exists. Null `gridTurn` means "the newest row": the turn on screen is the one producing the grid,
 * so its cards sit under its own reply (or, before the reply lands, under its question).
 */
export function gridRowOf(state: Pick<State, "gridTurn" | "turns">): number {
  if (state.gridTurn != null && state.gridTurn >= 0 && state.gridTurn < state.turns.length) return state.gridTurn;
  return state.turns.length - 1;
}

/**
 * The previous answer leaves the grid: its cards go onto the reply that produced it, as an earlier
 * answer, and the grid is cleared for this turn's. Called by every frame that brings the new turn's
 * first content — a candidate, a picks batch, a tray, a none-found — because each is the first
 * moment there is something to put in the old answer's place (the hold's whole rule).
 *
 * The freeze happens only when the grid's row is known and is a reply. A re-ask clears that row on
 * purpose: it is the same question asked again, so the answer it replaces is not an earlier answer
 * to anything — it is just gone, as it always was.
 */
function released(state: State): State {
  if (!state.holdingPrevious) return state;
  let turns = state.turns;
  const row = state.gridTurn;
  if (row != null && turns[row]?.role === "assistant") {
    const answer: TurnAnswer = {
      cards: state.order.map((id) => state.byId[id]).filter(Boolean),
      nearMiss: state.nearMiss, belowFloor: state.belowFloor, unchecked: state.unchecked, mode: state.mode,
    };
    if (answer.cards.length + answer.nearMiss.length + answer.belowFloor.length + answer.unchecked.length > 0) {
      turns = turns.slice();
      turns[row] = { ...turns[row], answer };
    }
  }
  return {
    ...state, turns, holdingPrevious: false, byId: {}, order: [], arrived: 0, settled: 0, pinned: 0,
    gridTurn: null,
    ...clearedAnswerDescription(),
    pageQuery: state.lastQuery,
  };
}

/** Is this turn one that answers beside the grid rather than replacing it (§4.9 answer, clarify)? */
function leavesGrid(mode: string | null | undefined): boolean {
  return mode === "Answer" || mode === "Clarify";
}

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
function clearedAnswerDescription(): Pick<State, "reason" | "paged" | "belowFloor" | "nearMiss" | "unchecked" | "mode" | "knockouts" | "droppedByFloor" | "exhausted" | "conflict" | "conflictTerms"> {
  return {
    reason: null, paged: false, belowFloor: [], nearMiss: [], unchecked: [], mode: null, knockouts: [], droppedByFloor: null,
    exhausted: false, conflict: null, conflictTerms: [],
  };
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
            // The refusal belonged to a page of the answer being replaced; asking is the "ask
            // again" it offered, and the offer goes with the gesture (R5-04).
            pageRefusal: null,
            // Whatever turn was named last belongs to the answer BEFORE this one; this turn's own
            // id arrives on its `conversation` frame, and until it does there is none to adopt.
            activeTurnId: null,
            // A conflict describes the question that produced it — and so does the ANSWER it
            // explains, which is why this is no longer cleared here. R5-01: the grid, the tray and
            // the footnote are all deliberately held while the new turn works, and the notice was
            // cleared at ask-time, so the one part of the turn F4-06 exists to preserve was the one
            // part the held state dropped. It goes with the grid instead — see
            // {@link clearedAnswerDescription} — and the new turn's own collision lands in
            // `turnConflict`, which starts empty.
            turnConflict: null, turnConflictTerms: [], errorReason: null,
            // The resumed thread's unanswered tail belongs to the visit BEFORE this one. Asking
            // anything replaces it — the retry offer it carries is exactly what was just taken.
            resumedTail: null, answeredQuery: "",
            // `byId` / `order` deliberately NOT cleared — see `holdingPrevious`. They are replaced
            // by the first candidate of this turn, not by the act of asking.
            holdingPrevious: state.order.length > 0, arrived: 0,
            // …and neither is the answer's own accounting, on exactly the same terms. See
            // {@link clearedAnswerDescription}.
            ...(state.order.length > 0 ? {} : clearedAnswerDescription()),
            // The held grid stays under the reply that produced it while this question runs, and
            // is frozen there as an earlier answer the moment this turn has something to show.
            gridTurn: state.order.length > 0 ? gridRowOf(state) : null,
            turnDegraded: [],
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
        // Where the screen goes back to if this page is refused — read BEFORE the line above
        // overwrites it. A page never replaces what is on screen, so a refused one owes the reader
        // the state they were in, footnote and control included (R5-04).
        pagePriorStatus: state.status, pageRefusal: null,
        // A page extends the answer on screen, so the answer's notice stands. Only the turn slot
        // resets, ready for whatever this page's own frames say.
        turnConflict: null, turnConflictTerms: [],
        // `pageTurnId` is deliberately carried — it names the answer this page extends, which is
        // the whole point of the request about to go out. Only the in-flight handle resets.
        activeTurnId: null,
        turnDegraded: [],
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
        appending: false, errorReason: null, activeTurnId: null, pageRefusal: null,
        turnConflict: null, turnConflictTerms: [],
        resumedTail: null, answeredQuery: "",
        holdingPrevious: state.order.length > 0, arrived: 0,
        ...(state.order.length > 0 ? {} : clearedAnswerDescription()),
        // The grid's row was the reply just dropped (the same question, being asked again), so the
        // answer it replaces is not frozen anywhere. A grid under an EARLIER row — an item question's
        // answer left it there — keeps that row, and is frozen there like any other earlier answer.
        gridTurn: state.gridTurn != null && state.gridTurn < turns.length ? state.gridTurn : null,
        turnDegraded: [],
      };
    }
    case "phase":
      // A heartbeat is the server repeating the LAST phase through a silence. It is proof of life
      // (the watchdog already re-armed on its bytes), not a boundary: re-sorting on it would move
      // cards that are already on screen every five seconds.
      if (action.data.heartbeat) return state;
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
        order: state.holdingPrevious ? state.order : streamOrder(state.order, state.byId, Math.max(state.settled, state.pinned)),
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
      //
      // …the row `answerRowIndex` names, which is the newest assistant row that is not a FAILURE.
      // A thread whose tail was stopped carries a failed assistant row now (R5-02), and pinning to
      // "the newest assistant row" would put the romcom's explanation on a turn that never
      // produced an answer at all (R5-01).
      if (restoredConflict) {
        const anchor = answerRowIndex(turns);
        if (anchor >= 0) turns[anchor] = { ...turns[anchor], conflict: true };
      }
      // Chat UI v2: the grid renders under the reply that produced it, which on a resume is the
      // row the restored answer belongs to — the same row the notice above is anchored to. Its own
      // stored cards ARE the grid, so they are not also shown as an earlier answer; every other
      // reply keeps whatever the payload restored under it.
      const gridRow = answerRowIndex(turns);
      const gridRowAnswer = gridRow >= 0 ? turns[gridRow].answer : undefined;
      if (gridRowAnswer) {
        const { answer: _dropped, ...rest } = turns[gridRow];
        void _dropped;
        turns[gridRow] = rest;
      }
      // The top-level lists are the newest answer, and win; a payload with per-turn cards only
      // still restores the grid from the row's own.
      const cards = (p.cards ? p.cards.filter(isCard) : gridRowAnswer?.cards) ?? [];
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
      const tray = (p.belowFloor ?? gridRowAnswer?.belowFloor ?? []).filter(isCard);
      const pageTarget = resumedPageTarget(p.turns, p.lastQuery ?? lastUserQuery(turns));
      return {
        ...initialChatState,
        turns,
        conversationId: p.id,
        status: "resumed",
        // The server's own `lastQuery` first: it reads the last USER row, including one whose
        // answer never landed, which is exactly the question a retry has to replay.
        lastQuery: p.lastQuery ?? lastUserQuery(turns),
        // The restored grid answers the thread's last COMPLETED ask, and both halves of a page of
        // it are read off that one row — the question and the id it was asked under. Same answer
        // as `lastQuery` on an ordinary resume; they part company after a Stop, and reading them
        // from two places is R5-03. See {@link resumedPageTarget}.
        pageQuery: pageTarget.query,
        pageTurnId: pageTarget.turnId,
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
        nearMiss: (p.nearMiss ?? gridRowAnswer?.nearMiss ?? []).filter(isCard),
        unchecked: (p.unchecked ?? gridRowAnswer?.unchecked ?? []).filter(isCard),
        mode: p.mode ?? gridRowAnswer?.mode ?? null,
        // Not on the resume payload: the supply is not stored. The none-found reply restored above
        // still carries every count in words.
        knockouts: [],
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
        // Untrusted like every resume field: a non-number or a count past `removed` reads as none.
        removedEarlier:
          typeof p.removedEarlier === "number" && p.removedEarlier >= 0
            ? Math.min(p.removedEarlier, p.removed ?? 0)
            : 0,
        lastCardsAt: p.lastCardsAt ?? null,
        lastTurnAt: p.lastTurnAt ?? null,
        // What the server was left holding, and which question the grid above it belongs to.
        // Both read off the transcript the payload already sends; neither was read before SB-03.
        resumedTail: lastTurnOutcome(p.turns),
        answeredQuery: answeredQuestion(p.turns),
        gridTurn: gridRow >= 0 ? gridRow : null,
      };
    }
    case "assistant":
      // Paging replays the last message, so the model replies again — suppress it rather than
      // stuttering the same answer into the transcript on every scroll.
      return state.appending
        ? state
        : {
          ...state,
          // A v2 `degraded` frame can beat the reply to the wire; it is applied the moment the
          // reply it describes exists.
          turns: markTurnReply([...state.turns, {
            role: "assistant", text: action.data.text, conflict: action.data.conflict,
            ...(action.data.degraded ? { degraded: true } : {}),
          }], state.turnDegraded),
        };
    case "candidate": {
      const c = action.data;
      // The swap. The previous answer stayed up while this turn was thinking; this is the first
      // moment there is something to put in its place, so it is replaced now rather than having
      // been blanked when the question was asked.
      if (state.holdingPrevious) {
        // The previous turn's block goes with it, so nothing is settled any more: what is on
        // screen from here is this turn's stream, and it settles at phase boundaries like any other.
        // Its accounting goes with it too — this is the release {@link clearedAnswerDescription} is
        // held for — and its cards go onto the reply that produced them (chat UI v2, see `released`).
        const s = released(state);
        return { ...s, byId: { [c.titleId]: c }, order: [c.titleId], arrived: 1 };
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
      // The TURN's slot, not the answer's. The frame describes the question being asked right now,
      // and the answer on screen may still be the previous one — held, whole, with a collision of
      // its own that this must not overwrite (R5-01). It is promoted at `done`, which is the moment
      // this question's answer becomes the grid.
      return {
        ...state,
        turnConflict: action.data.message,
        // The terms travel beside the sentence rather than being parsed back out of it. Two
        // server-side lists, one fact for the reader — and the keyword half is the one a turn
        // colliding with an avoid-keyword ("anime", "musical") had no way to name at all.
        turnConflictTerms: excludedTerms(action.data.excludedGenres, action.data.excludedKeywords),
      };
    case "plan":
      return { ...state, plan: action.data };
    case "picks": {
      // One judge batch, past the gate, in its final order. Appended after whatever this turn has
      // already shown and pinned there: a later batch never moves an earlier one (§4.8), and a phase
      // boundary never re-sorts what the server ordered (see `pinned`).
      const s = released(state);
      const byId = { ...s.byId };
      const fresh: string[] = [];
      for (const c of action.data.cards) {
        if (byId[c.titleId]) {
          byId[c.titleId] = { ...byId[c.titleId], ...c };
          continue;
        }
        byId[c.titleId] = c;
        fresh.push(c.titleId);
      }
      const cut = Math.max(s.settled, s.pinned);
      const head = s.order.slice(0, cut);
      const tail = s.order.slice(cut).filter((id) => !action.data.cards.some((c) => c.titleId === id));
      const picked = action.data.cards.map((c) => c.titleId).filter((id) => !head.includes(id));
      return {
        ...s, byId, order: [...head, ...picked, ...tail], pinned: head.length + picked.length,
        arrived: s.arrived + fresh.length,
      };
    }
    case "tray": {
      // A tray of this turn's: the near misses and the cards under the bar arrive with their batch.
      // They are this turn's first content as much as a pick is, so the held answer is released.
      const s = released(state);
      const cards = action.data.cards;
      if (action.data.kind === "belowFloor") return { ...s, belowFloor: mergeCards(s.belowFloor, cards) };
      if (action.data.kind === "unchecked") return { ...s, unchecked: mergeCards(s.unchecked, cards) };
      return { ...s, nearMiss: mergeCards(s.nearMiss, cards) };
    }
    case "noneFound": {
      // An honest "nothing left" is this turn's answer, so the held one is released for it. The
      // relaxation offers land now; `done` confirms the mode.
      const s = released(state);
      return { ...s, mode: "NoneFound", knockouts: (action.data.supply.knockouts ?? []).filter(isKnockout) };
    }
    case "answer":
      // An item question's answer: at most two sentences from the title's facts, beside the grid.
      // The grid is NOT released — it is still the answer the question was about.
      if (state.appending) return state;
      return {
        ...state,
        turns: markTurnReply([...state.turns, { role: "assistant", text: action.data.text }], state.turnDegraded),
      };
    case "clarify":
      // A question back, with the readings to choose between. Like an answer, it leaves the grid.
      if (state.appending) return state;
      return {
        ...state,
        turns: markTurnReply(
          [...state.turns, { role: "assistant", text: action.data.question, clarifyOptions: action.data.options }],
          state.turnDegraded,
        ),
      };
    case "degraded": {
      const turnDegraded = [...state.turnDegraded, ...action.data.what.filter((w) => !state.turnDegraded.includes(w))];
      // Marked only on a reply that belongs to THIS turn: with the question still the last row,
      // there is no reply yet and the flags wait for it.
      const turns = !state.appending && state.turns.at(-1)?.role === "assistant" ? markTurnReply(state.turns, turnDegraded) : state.turns;
      return { ...state, turnDegraded, turns };
    }
    case "done": {
      // A caught server exception arrives as `done` with no results. Treat it as the failure it is
      // rather than as an empty answer, and keep whatever is already on screen — a page that blew
      // up should offer a retry, not wipe the reel.
      if (isFailedDone(action.data)) return { ...state, status: "error", phase: null, appending: false };
      // Everything this turn said about itself being degraded, from its frames and from `done`.
      const flags = [...state.turnDegraded, ...(action.data.degraded ?? []).filter((f) => !state.turnDegraded.includes(f))];
      // An item question or a clarifying question answers BESIDE the grid (§4.9 answer, clarify):
      // the held answer is not previous, it is still the answer — so the hold simply ends, and the
      // grid, its trays, its accounting and the paging that extends it all stay exactly as they were.
      // Only over a grid that exists: with nothing on screen there is nothing to leave alone, and
      // the turn's own mode has to reach the page so it does not read as an empty search.
      if (!state.appending && state.holdingPrevious && state.order.length > 0 && leavesGrid(action.data.mode)) {
        return {
          ...state, status: "done", phase: null, holdingPrevious: false, pageRefusal: null,
          turns: markTurnReply(state.turns, flags), turnDegraded: [],
          turnConflict: null, turnConflictTerms: [],
        };
      }
      // The trays this turn streamed, if it streamed any (v2 `tray` frames). A held previous answer's
      // trays are not this turn's and are never carried into it.
      const streamedTrays = state.holdingPrevious
        ? { belowFloor: [], nearMiss: [], unchecked: [], mode: null, knockouts: [] }
        : { belowFloor: state.belowFloor, nearMiss: state.nearMiss, unchecked: state.unchecked, mode: state.mode, knockouts: state.knockouts };
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
        ...state, byId, order, status: "done", shown, appending: false, pageRefusal: null,
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
        // A v2 page streams its trays as `tray` frames and `done` may repeat them, so every merge is
        // de-duplicated by title; a fresh answer's `done` lists win, falling back to what streamed.
        belowFloor: state.appending
          ? mergeCards(state.belowFloor, action.data.belowFloor ?? [])
          : mergeCards(action.data.belowFloor ?? [], streamedTrays.belowFloor),
        // The near-miss tray follows the floor tray's rule: a page adds its own, an answer replaces.
        nearMiss: state.appending
          ? mergeCards(state.nearMiss, action.data.nearMiss ?? [])
          : mergeCards(action.data.nearMiss ?? [], streamedTrays.nearMiss),
        unchecked: state.appending
          ? mergeCards(state.unchecked, action.data.unchecked ?? [])
          : mergeCards(action.data.unchecked ?? [], streamedTrays.unchecked),
        mode: state.appending ? state.mode : action.data.mode ?? streamedTrays.mode,
        // Offers are checked one by one: a malformed chip is dropped, never the answer it sits in.
        knockouts: state.appending
          ? state.knockouts
          : action.data.supply?.knockouts
            ? action.data.supply.knockouts.filter(isKnockout)
            : streamedTrays.knockouts,
        // A failed judge batch marks the reply it belongs to, so "some cards unchecked" sits on
        // the bubble the cards answer. A page's reply was suppressed, so a page marks nothing.
        turns: !state.appending && (judgeDegraded(flags) || flags.includes("plan")) ? markTurnReply(state.turns, flags) : state.turns,
        turnDegraded: [],
        // Finished work: nothing is pinned mid-stream any more, and the grid is this turn's — under
        // its own reply, the newest row — unless it is a page, which extends the grid where it is.
        pinned: 0,
        gridTurn: state.appending ? state.gridTurn : null,
        // Same rule as the tray it labels: a page ADDS its cut to the running total, a fresh answer
        // replaces it. `null` rather than `0` where neither side said anything — a server that never
        // sent the field has not told us it cut nothing, and the tray falls back to counting the
        // cards it was handed in that case, which zero would silently suppress.
        droppedByFloor: sumDropped(state.appending ? state.droppedByFloor : null, action.data.droppedByFloor),
        // The turn's collision becomes the ANSWER's the moment this turn's cards are the grid. A
        // fresh answer replaces the notice outright — including with nothing, which is what a
        // question that collided with nothing is entitled to; a page keeps the one it is extending
        // unless its own frames said something.
        conflict: state.turnConflict ?? (state.appending ? state.conflict : null),
        conflictTerms: state.turnConflict ? state.turnConflictTerms : state.appending ? state.conflictTerms : [],
        turnConflict: null,
        turnConflictTerms: [],
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
      //
      // …and the abandoned turn is recorded in the transcript as a failure rather than leaving its
      // half-written reply on screen as an answer (R5-02). See {@link stoppedTranscript}.
      return {
        ...state, status: "stopped", phase: null, appending: false, holdingPrevious: false,
        turns: stoppedTranscript(state.turns, state.appending),
      };
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
    case "pageRefused":
      // A refusal, not a failure: this page never started, and the answer it was going to extend is
      // untouched — grid, tray, footnote, paging control and all. So the screen goes back to the
      // state the page was started FROM rather than to `blocked` (which withdrew the footnote and
      // the control, so the only recovery was a reload) or to `error` (which would claim something
      // had gone wrong with the answer on screen). `done` is the fallback for a prior status that
      // could not be a page's origin; `canPage` refuses the rest anyway.
      return {
        ...state,
        status: state.pagePriorStatus === "streaming" || state.pagePriorStatus === "idle" ? "done" : state.pagePriorStatus,
        pageRefusal: action.refusal,
        appending: false,
        phase: null,
      };
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
