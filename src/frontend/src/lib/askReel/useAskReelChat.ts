import { useCallback, useEffect, useReducer, useRef } from "react";
import { useSelector } from "react-redux";
import { applyScoreOverrides, selectScoreOverrides } from "../../store/scoreOverridesSlice";
import { streamAskReel, type AskReelRequest, type PhaseData } from "../askReelStream";
import { canPage } from "./paging";
import { chatReducer, failureFor, gridRowOf, initialChatState, mintStreamId, type ChatAction, type StreamTag } from "./reducer";
import type { ConversationPayload } from "./transcript";

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

/**
 * The longest one turn may stream, however lively, in milliseconds. The idle watchdog re-arms on
 * every byte — keepalive comments and heartbeats included — so on its own it never fires on a turn
 * the server keeps alive while a provider hangs (D7). This cap does, independent of activity. The
 * server's own judging deadline ends a healthy slow turn long before it.
 */
export const STREAM_HARD_CAP_MS = 120_000;

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
 * The hand-offs this DOCUMENT has minted and not yet acted on.
 *
 * Module state, deliberately: it dies with the document, which is the whole point. A hand-off is
 * carried in `location.state`, and `location.state` lives in the HISTORY ENTRY — so it survives a
 * reload, and it survives Back onto the entry the search box pushed. The page then received a
 * question nobody had just typed and asked it again; the server, seeing the same text as the
 * conversation's last user turn, filed the result as a PAGE of it, and a phantom billed turn
 * appended cards to an answer the reader was in the middle of.
 *
 * A restored entry cannot be in this set — the set was created with the document that is reading
 * it — so `claimHandoff` answers false and the visit resumes instead of asking.
 */
const documentHandoffs = new Set<string>();

/**
 * Mint the id for a question being handed to /search, and record it as this document's.
 *
 * The id is the turn's own idempotency key as well: a hand-off that does get re-sent (a genuine
 * remount inside the same document) is replayed by the server from the stored turn rather than run
 * and billed twice.
 */
export function mintHandoff(registry: Set<string> = documentHandoffs): string {
  const id = mintTurnId();
  registry.add(id);
  return id;
}

/**
 * Is this hand-off live — minted by this document and not yet acted on? True at most ONCE per
 * minted id; every later reader (a reload, a Back, a second mount reading the same entry) gets
 * false and must treat the question as a leftover rather than an instruction.
 */
export function claimHandoff(id: string | undefined, registry: Set<string> = documentHandoffs): boolean {
  return id != null && registry.delete(id);
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
 * How /search should start on this render, given the question handed over in the navigation and
 * whatever the page has already acted on (`null` = nothing yet). The page consumes the seed and
 * clears the router state, which brings it straight back here with an empty seed — so "no seed"
 * must only mean "resume the last conversation" on the very first pass. Getting this wrong is what
 * made a mid-conversation reload replay turn one and throw away every refinement.
 *
 * `live` answers the one question a seed cannot answer about itself: did this document navigate
 * here, or did the question merely come back with the history entry? `location.state` lives in the
 * HISTORY ENTRY, so it outlives the navigation that created it — reload /search, or press Back
 * onto the entry the search box pushed, and the same question is handed to a page that has never
 * asked it. It was fired again as though it had just been typed, and because the text matched the
 * conversation's last user turn the server filed the result as a PAGE of it: a phantom billed turn
 * appending cards nobody asked for. See {@link claimHandoff}, which is what the caller passes; it
 * answers true at most once per hand-off, so a seed can be consumed exactly once and no more.
 *
 * It is a callback rather than a boolean because claiming a hand-off SPENDS it: the predicate is
 * consulted only on the pass that actually decides, never on the re-renders around it.
 *
 * Returns the next `handled` value alongside the action so the caller just stores what it is
 * given, and `clear` — whether the router state must be wiped on this pass. Clearing is the
 * caller's first act, not its last: the state has to be gone BEFORE the request goes out, or a
 * reload landing in between inherits the question all over again.
 */
export function nextBootstrap(
  seed: string,
  handled: string | null,
  live: () => boolean = () => true,
): { action: BootstrapAction; handled: string; clear: boolean } {
  if (seed) {
    // Already acted on — a StrictMode double-invoke, or a render before the state clear lands.
    // Checked FIRST so the hand-off is not spent a second time on a pass that decides nothing.
    if (handled === seed) return { action: "none", handled: seed, clear: false };
    // A seed this document did not navigate with is a leftover, not a question. Thrown away, and
    // the visit falls back to the resume every other cold landing on /search gets.
    if (!live()) return { action: "resume", handled: seed, clear: true };
    return { action: "seed", handled: seed, clear: true };
  }
  // Empty seed: either a cold landing on /search (resume) or the settling render after a consumed
  // seed. Settling resets `handled` to "", so searching the same phrase again still starts a turn.
  return { action: handled === null ? "resume" : "none", handled: "", clear: false };
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
 * The composer's floor and ceiling in CSS pixels.
 *
 * F6-18 + the 44 px audit. The composer was a single-line `<input>` **37 px** tall: under the touch
 * minimum at every width, and — at 239 px wide on a phone — about 36 characters of a question the
 * server accepts up to 1 000 of. A 1 000-character question measured `scrollWidth 6101` in it. The
 * *bubble* has been readable back since F6-02; the field the question is typed into was not, before
 * it was sent.
 *
 * 44 is the target minimum, so it is also the resting height. The ceiling is four lines of the
 * body face plus its vertical padding: enough to read back a paragraph-length question, and short
 * enough that a growing composer never eats the answer it is asking about. Past it the field
 * scrolls rather than growing.
 */
export const COMPOSER_MIN_PX = 44;

export const COMPOSER_MAX_PX = 116;

/**
 * How tall the composer should be for content of `scrollHeight` — clamped, and total.
 *
 * A textarea reports `scrollHeight` for its content, but only once its own height has been released
 * (the caller sets `height: auto` first), and it never reports less than one line — so the clamp
 * does both ends. Pure, so the arithmetic is testable where the element is not: vitest here runs in
 * node with no renderer.
 */
export function composerHeight(scrollHeight: number): number {
  if (!Number.isFinite(scrollHeight)) return COMPOSER_MIN_PX;
  return Math.min(COMPOSER_MAX_PX, Math.max(COMPOSER_MIN_PX, Math.ceil(scrollHeight)));
}

/**
 * Does this keystroke send the question, or break the line?
 *
 * A multi-line composer has to answer that explicitly — an `<input>` answered it by having no
 * second line at all, which is why Shift+Enter "submitted" (F3-12): it never did anything, and the
 * form's default submit ran. Enter sends because that is the gesture the whole surface is built
 * around; Shift+Enter is the newline, which is the only reason the field can grow.
 *
 * `composing` is the IME guard and is not optional: on a Japanese, Chinese or Korean keyboard the
 * Enter that CONFIRMS a candidate raises `keydown` with `isComposing` true, and sending there
 * submits a half-typed word the user was still choosing.
 */
export function composerSends(key: string, shiftKey: boolean, composing: boolean): boolean {
  return key === "Enter" && !shiftKey && !composing;
}

/** How often a still-running turn is re-checked, and how long that is worth doing for. */
export const PENDING_TURN_POLL_MS = 5_000;

export const PENDING_TURN_BUDGET_MS = 60_000;

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
export interface MountGuard {
  mount: () => void;
  unmount: () => void;
}

export function createMountGuard(
  abort: () => void,
  schedule: (fn: () => void) => unknown = (fn) => setTimeout(fn, 0),
  cancel: (handle: unknown) => void = (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
): MountGuard {
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
 * The ONE guard a hook instance owns, kept in the caller's ref box.
 *
 * The deferral above only works if the mount that cancels it is the same guard that scheduled it.
 * The effect used to call {@link createMountGuard} in its own body, so StrictMode's cleanup
 * scheduled the abort on guard #1 and the remount then cancelled guard #2's empty queue — while
 * guard #1's timer, still closed over `mounted === false`, killed the request the first mount had
 * just started. That is the Home → /search hand-off: one `POST /api/search/ask` aborted by the
 * page itself, and `nextBootstrap` refusing to restart a seed already recorded as handled.
 *
 * Per INSTANCE, not per module: a genuine unmount leaves its guard behind with the component, and
 * the next mount of /search gets a fresh box and a fresh guard, so the old one's abort still runs.
 */
export function guardFor(
  box: { current: MountGuard | null },
  abort: () => void,
  schedule?: (fn: () => void) => unknown,
  cancel?: (handle: unknown) => void,
): MountGuard {
  box.current ??= createMountGuard(abort, schedule, cancel);
  return box.current;
}

/**
 * Why a run was cut short, when the page itself did the cutting.
 *
 * Every deliberate abort names itself, so the one kind that has no name — a request killed by
 * something the page did not do — can be told apart from the six that are already accounted for.
 */
export type AbortCause = "stop" | "replaced" | "resume" | "reset" | "timeout" | "hidden" | "unmount";

/**
 * What the page owes the user when its own request ends in an abort.
 *
 * The rejection used to be swallowed on `signal.aborted` alone, which is right for a Stop (the
 * reducer already has its own state), for the abort `run` fires at the head of a replacement turn,
 * for a resume, a `+ New`, the idle watchdog, the hidden-tab cut and a real unmount — and wrong
 * for everything else, because "wrong" here means the reducer stays in `streaming` under a
 * progress bar with no error, no retry and no way out but a reload. An unclaimed abort is a
 * failure and is stated as one.
 */
export function abortFallout(cause: AbortCause | null): ChatAction | null {
  return cause === null ? { type: "error", reason: null } : null;
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
  // Why each controller was aborted, read by the stream's own `catch`. A WeakMap rather than a
  // field on the ref: the rejection arrives a tick or more after `abortRef` has moved on, so the
  // answer has to be attached to the CONTROLLER that was cut, not to whatever is current.
  const abortCauses = useRef(new WeakMap<AbortController, AbortCause>());
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

  /**
   * Abort the run in flight and SAY WHY, so {@link abortFallout} can tell a deliberate cut from a
   * request that died on its own. Every abort in this hook goes through here or names its cause on
   * the controller directly; one that does not is, correctly, reported to the user as a failure.
   */
  const abortActive = useCallback((cause: AbortCause) => {
    const controller = abortRef.current;
    if (!controller) return;
    abortCauses.current.set(controller, cause);
    controller.abort();
  }, []);

  const run = useCallback((
    msg: string,
    fresh: boolean,
    kind: "turn" | "more" | "reask",
    turnId?: string,
    pageOf?: string | null,
  ) => {
    // The turn already running is being replaced by this one — a re-ask, a filter toggle, a
    // follow-up typed over a live stream. Named, so its rejection is not mistaken for a failure.
    abortActive("replaced");
    const controller = new AbortController();
    abortRef.current = controller;

    // A stream that goes quiet is indistinguishable from one that is thinking, so it needs a
    // deadline rather than a guess. Reset on every frame: a slow turn that keeps talking is fine,
    // thirty seconds of silence is not, and before this the tab simply span forever.
    const armWatchdog = () => {
      clearWatchdog();
      watchdogRef.current = setTimeout(() => {
        abortCauses.current.set(controller, "timeout");
        controller.abort();
        if (abortRef.current === controller) abortRef.current = null;
        dispatch({ type: "error", reason: "timeout" });
      }, STREAM_IDLE_TIMEOUT_MS);
    };
    armWatchdog();
    const hardCap = setTimeout(() => {
      if (abortRef.current !== controller) return;
      abortCauses.current.set(controller, "timeout");
      controller.abort();
      abortRef.current = null;
      clearWatchdog();
      dispatch({ type: "error", reason: "timeout" });
    }, STREAM_HARD_CAP_MS);
    // Whether THIS run received its terminal frame. A stream that closes cleanly without one — a
    // proxy cutting the body, or a `done` the guards rejected — used to leave the page streaming
    // forever behind a progress bar (acceptance D1).
    let sawDone = false;

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
        case "done":
          sawDone = true;
          dispatch({ type: "done", data: ev.data, stream });
          break;
        // §4.9's v2 frames. Each has its own action so the reducer, not the transport, decides
        // what it means for the screen.
        case "plan": dispatch({ type: "plan", data: ev.data, stream }); break;
        case "picks": dispatch({ type: "picks", data: ev.data, stream }); break;
        case "tray": dispatch({ type: "tray", data: ev.data, stream }); break;
        case "answer": dispatch({ type: "answer", data: ev.data, stream }); break;
        case "clarify": dispatch({ type: "clarify", data: ev.data, stream }); break;
        case "none-found": dispatch({ type: "noneFound", data: ev.data, stream }); break;
        case "degraded": dispatch({ type: "degraded", data: ev.data, stream }); break;
        default: break;
      }
    }, controller.signal, armWatchdog)
      .then(() => {
        clearWatchdog();
        clearTimeout(hardCap);
        if (!sawDone && abortRef.current === controller) {
          abortRef.current = null;
          dispatch({ type: "error", reason: null });
        }
      })
      .catch((err: unknown) => {
        clearWatchdog();
        clearTimeout(hardCap);
        if (controller.signal.aborted) {
          // An abort this page asked for already has its own state; one it did not ask for used to
          // leave the reducer streaming forever behind a progress bar. See {@link abortFallout}.
          const fallout = abortFallout(abortCauses.current.get(controller) ?? null);
          if (fallout) dispatch(fallout);
          return;
        }
        const failure = failureFor(err);
        // Forgotten HERE rather than in the reducer, which stays pure: the stored id is what the
        // next cold load resumes from, and leaving a 404'd id in localStorage means every visit
        // from this tab starts by asking for a conversation that is gone.
        if (failure.type === "conversationGone") storeConversationId(null);
        dispatch(failure);
      });
  }, [clearWatchdog, abortActive]);

  /**
   * Ask a question. `turnId` is for a caller that has already minted one and needs THIS send to be
   * the same exchange as a previous attempt — the reconnect after a 409, and the seeded first
   * turn, whose id rides in the router state so that any send that does go out twice is replayed
   * server-side from the stored turn rather than run and billed again.
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
    abortActive("stop");
    abortRef.current = null;
    activeStreamRef.current = 0;
    dispatch({ type: "stop" });
  }, [clearWatchdog, abortActive]);

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
    abortActive("resume");
    abortRef.current = null;
    activeStreamRef.current = 0;
    turnIdRef.current = null;
    clearWatchdog();
    // A resumed thread is this tab's thread from now on, so it is remembered on the same terms as
    // one started here — otherwise a cold visit that resumed would still have nothing stored, and
    // the NEXT reload would fall back to the account's newest thread instead of this one.
    storeConversationId(payload.id);
    dispatch({ type: "resume", payload });
  }, [clearWatchdog, abortActive]);

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
    abortActive("reset");
    abortRef.current = null;
    activeStreamRef.current = 0;
    turnIdRef.current = null;
    clearWatchdog();
    storeConversationId(FRESH_CONVERSATION);
    dispatch({ type: "reset" });
  }, [clearWatchdog, abortActive]);

  // ONE guard for the life of this hook instance — see {@link guardFor}. Rebuilding it inside the
  // effect body is what made StrictMode's fake unmount fatal: the remount cancelled a different
  // guard's empty queue while the first guard's timer aborted the request the first mount had just
  // started, and the Home → /search hand-off never recovered from it.
  const guardBox = useRef<MountGuard | null>(null);
  useEffect(() => {
    const guard = guardFor(guardBox, () => {
      abortActive("unmount");
      if (watchdogRef.current !== null) clearTimeout(watchdogRef.current);
    });
    guard.mount();
    return () => guard.unmount();
  }, [abortActive]);

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
        abortActive("hidden");
        abortRef.current = null;
        dispatch({ type: "error", reason: "timeout" });
      }, HIDDEN_ABORT_MS);
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      if (hiddenTimer !== null) clearTimeout(hiddenTimer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [clearWatchdog, abortActive]);

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
    /** The server's note that the question the GRID answers collides with a standing exclusion. */
    conflict: state.conflict,
    /**
     * The same note for the turn in flight — or, after a Stop, for the one that was abandoned. Read
     * beside that turn (its own reply while it streams, the stopped card once it ends) and never
     * under the answer, which has its own (R5-01).
     */
    turnConflict: state.turnConflict,
    /** The terms behind {@link turnConflict}, ready for {@link listPhrase}. */
    turnConflictTerms: state.turnConflictTerms,
    /**
     * The terms that note is about — genres and avoid-keywords in one list, ready for
     * {@link listPhrase}. Empty when the server named none, which is every message-only conflict.
     */
    conflictTerms: state.conflictTerms,
    /** Cards the quality floor cut on the last turn — their own tray, never the grid. */
    belowFloor,
    /** The judge's partial matches on the last turn — the collapsed "close, but not quite" tray. */
    nearMiss: applyScoreOverrides(state.nearMiss, overrides),
    /** The cards nobody could check on the last turn — the collapsed "not checked" tray. */
    unchecked: applyScoreOverrides(state.unchecked, overrides),
    /** "NoneFound" when the answer on screen is an honest "nothing left", "Picks" otherwise. */
    mode: state.mode,
    /** The relaxation offers of a none-found answer. */
    knockouts: state.knockouts,
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
     * The server's refusal of the last page, where it named one — see {@link PageRefusal}. Read
     * beside the paging control, which stays exactly where it is: a refused page changed nothing
     * on screen, and withdrawing the control left the reader with no way to try again (R5-04).
     */
    pageRefusal: state.pageRefusal,
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
    /** How many of `removed` were already acted on when the answer was given. */
    removedEarlier: state.removedEarlier,
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
    /**
     * The transcript row the live grid renders under — the reply that produced it (chat UI v2).
     * Every other reply carries its own earlier answer, collapsed, in `turns[i].answer`.
     */
    gridRow: gridRowOf(state),
    /** The planner's reading of the newest turn, where the v2 engine sent one. Refine chips read it. */
    plan: state.plan,
    /**
     * The ids the client's own floor may touch: neither finished work (`settled`) nor a v2 pick the
     * server already gated (`pinned`).
     */
    clientFloorable: state.order.slice(Math.max(state.settled, state.pinned)),
  };
}
