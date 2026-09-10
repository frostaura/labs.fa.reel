import { useCallback, useEffect, useReducer, useRef } from "react";
import { useSelector } from "react-redux";
import { applyScoreOverrides, selectScoreOverrides } from "../store/scoreOverridesSlice";
import { AskStreamError, isFailedDone, streamAskReel, type AskCard, type DoneData, type PhaseData } from "./askReelStream";

/**
 * `resumed` is a fourth state, not a flavour of `done`: the transcript came back from the server
 * but this visit has not run a search, so nothing on screen was produced by a query in this
 * session. Reporting it as `done` is what rendered "No matches for that one" over a conversation
 * that had matched plenty — the cards simply were not part of what `/conversations/latest`
 * returned. Callers that want "there is a conversation here" should test `turns.length`, not the
 * status.
 */
type Status = "idle" | "streaming" | "done" | "resumed" | "error";

/** Why the stream ended badly, where the client can tell. `null` = an unclassified failure. */
export type ErrorReason = "session" | "timeout" | null;

export interface ChatTurn {
  role: "user" | "assistant";
  text: string;
}

/**
 * Conversation payload caps. Every request replays the transcript and every tmdbId already
 * surfaced, and both grow for the life of the thread — a refine-and-scroll session adds a page of
 * ids on every sentinel hit, so an afternoon on one question would eventually push the request
 * past sane body limits and the model's context window. Both caps keep the MOST RECENT entries,
 * because recency is what the next turn is about ("more like the first one" reaches back a couple
 * of exchanges, never twenty). Trimming `shown` means a very long thread may eventually re-offer
 * a title dropped off the tail — a far cheaper failure than the request falling over.
 */
export const MAX_HISTORY_TURNS = 12;
export const MAX_SHOWN_IDS = 300;

/** Inter-frame silence that means the stream is dead rather than thinking, in milliseconds. */
export const STREAM_IDLE_TIMEOUT_MS = 30_000;

/** How long a backgrounded tab may hold an open stream before it is abandoned, in milliseconds. */
export const HIDDEN_ABORT_MS = 60_000;

/** The transcript as the wire wants it, trimmed to the most recent {@link MAX_HISTORY_TURNS}. */
export function capHistory(turns: ChatTurn[]): { role: string; text: string }[] {
  return turns.slice(-MAX_HISTORY_TURNS).map((t) => ({ role: t.role, text: t.text }));
}

/** The no-repeat exclusion list, trimmed to the most recent {@link MAX_SHOWN_IDS}. */
export function capShown(shown: number[]): number[] {
  return shown.slice(-MAX_SHOWN_IDS);
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

/** The text of the last thing the USER said — what a page or a re-ask has to replay. */
export function lastUserQuery(turns: ChatTurn[]): string {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].role === "user") return turns[i].text;
  }
  return "";
}

interface State {
  turns: ChatTurn[];
  status: Status;
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
  /** Set alongside `status: "error"` when the client can name the cause. */
  errorReason: ErrorReason;
}

type Action =
  | { type: "userTurn"; message: string; fresh: boolean }
  | { type: "moreTurn" }
  | { type: "reaskTurn" }
  | { type: "phase"; data: PhaseData }
  | { type: "conversation"; data: { id: string } }
  | { type: "resume"; turns: ChatTurn[]; conversationId: string; shown?: number[] }
  | { type: "assistant"; data: { text: string } }
  | { type: "candidate"; data: AskCard }
  | { type: "scored"; data: { titleId: string; predictedRating: number | null } }
  | { type: "reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { type: "conflict"; message: string }
  | { type: "done"; data: DoneData }
  | { type: "stop" }
  | { type: "error"; reason?: ErrorReason };

export const initialChatState: State = {
  turns: [], status: "idle", phase: null, byId: {}, order: [], reason: null, shown: [],
  conversationId: null, appending: false, lastQuery: "", exhausted: false,
  conflict: null, belowFloor: [], errorReason: null,
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

export function chatReducer(state: State, action: Action): State {
  switch (action.type) {
    case "userTurn": {
      const turn: ChatTurn = { role: "user", text: action.message };
      return action.fresh
        ? { ...initialChatState, turns: [turn], status: "streaming", lastQuery: action.message }
        : {
            ...state, turns: [...state.turns, turn], status: "streaming", phase: null,
            byId: {}, order: [], reason: null, appending: false, lastQuery: action.message, exhausted: false,
            // A conflict describes the question that produced it. A new question gets a clean
            // slate, or last turn's "horror is excluded" note hangs over an unrelated answer.
            conflict: null, belowFloor: [], errorReason: null,
          };
    }
    case "moreTurn":
      // A page, not a turn: no transcript entry, and the existing cards stay put.
      return { ...state, status: "streaming", phase: null, appending: true, errorReason: null };
    case "reaskTurn": {
      // The same question asked again — a media-filter change, or a retry after a failure. Like a
      // page it adds no transcript entry (the user said it once; echoing their bubble on every
      // toggle click was the bug), but unlike a page it REPLACES the answer, so the stale reply is
      // dropped and the cards are cleared for the incoming set.
      const turns = state.turns.at(-1)?.role === "assistant" ? state.turns.slice(0, -1) : state.turns;
      return {
        ...state, turns, status: "streaming", phase: null,
        byId: {}, order: [], reason: null, appending: false, exhausted: false,
        belowFloor: [], errorReason: null,
      };
    }
    case "phase":
      // A phase boundary is the ONLY mid-stream moment the rows may re-order (see
      // partitionedOrder) — a settle-then-shuffle rhythm instead of jumping on every event.
      return { ...state, status: "streaming", phase: action.data, order: partitionedOrder(state.order, state.byId) };
    case "conversation":
      return { ...state, conversationId: action.data.id };
    case "resume":
      // NOT `done`: see the Status doc-comment. `lastQuery` comes back with the transcript so the
      // media toggle and the retry have something to replay — without it, a resumed thread's chips
      // were inert until the user typed again.
      return {
        ...initialChatState,
        turns: action.turns,
        conversationId: action.conversationId,
        status: "resumed",
        lastQuery: lastUserQuery(action.turns),
        shown: action.shown ? capShown(action.shown) : [],
      };
    case "assistant":
      // Paging replays the last message, so the model replies again — suppress it rather than
      // stuttering the same answer into the transcript on every scroll.
      return state.appending
        ? state
        : { ...state, turns: [...state.turns, { role: "assistant", text: action.data.text }] };
    case "candidate": {
      const c = action.data;
      if (state.byId[c.titleId]) return state;
      return { ...state, byId: { ...state.byId, [c.titleId]: c }, order: [...state.order, c.titleId] };
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
      // Capped here as well as on the wire, so the state this hook carries cannot grow unbounded
      // either. Newest ids win — see MAX_SHOWN_IDS.
      const shown = capShown(Array.from(new Set([...state.shown, ...newTmdb])));
      const incoming = action.data.results.map((c) => c.titleId);
      // Appending keeps what is on screen and adds only genuinely new ids; a page that adds
      // nothing latches `exhausted` so the scroll sentinel stops asking for more.
      const order = state.appending
        ? [...state.order, ...incoming.filter((id) => !state.order.includes(id))]
        : incoming;
      const gained = order.length - state.order.length;
      return {
        ...state, byId, order: partitionedOrder(order, byId), status: "done", shown, appending: false,
        reason: state.appending && gained === 0 ? state.reason : action.data.reason,
        exhausted: state.appending ? gained === 0 : false,
        // The under-floor tray belongs to the turn that produced it. A page appends its own tray
        // to the previous one; a fresh answer replaces it.
        belowFloor: state.appending ? [...state.belowFloor, ...(action.data.belowFloor ?? [])] : action.data.belowFloor ?? [],
      };
    }
    case "stop":
      // The user pressed Stop. Whatever streamed so far is the answer if there is one; otherwise
      // this is a cold page again. Never an error — they asked for this.
      return {
        ...state, status: state.order.length > 0 ? "done" : "idle", phase: null, appending: false,
      };
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

  const run = useCallback((msg: string, fresh: boolean, kind: "turn" | "more" | "reask") => {
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

    // Both payloads are capped — see MAX_HISTORY_TURNS / MAX_SHOWN_IDS. Everything except a fresh
    // start still carries the no-repeat list, re-ask included: the conversation's promise is that
    // a title is never offered twice on the same thread.
    const history = fresh ? [] : capHistory(stateRef.current.turns);
    const shownTmdbIds = fresh ? [] : capShown(stateRef.current.shown);
    const conversationId = fresh ? null : stateRef.current.conversationId;
    dispatch(
      kind === "more" ? { type: "moreTurn" }
        : kind === "reask" ? { type: "reaskTurn" }
          : { type: "userTurn", message: msg, fresh },
    );

    streamAskReel({ query: msg, history, shownTmdbIds, conversationId, mediaTypes: mediaRef.current, continuation: kind !== "turn" }, (ev) => {
      armWatchdog();
      switch (ev.event) {
        case "phase": dispatch({ type: "phase", data: ev.data }); break;
        case "conversation": dispatch({ type: "conversation", data: ev.data }); break;
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
        // A 401 here already survived one refresh-and-retry inside streamAskReel, so the session
        // is genuinely gone rather than merely stale — say so, and let the page offer a sign-in
        // rather than a "try again" that cannot work.
        const reason: ErrorReason = err instanceof AskStreamError && err.status === 401 ? "session" : null;
        dispatch({ type: "error", reason });
      });
  }, [clearWatchdog]);

  const send = useCallback((message: string, fresh = false) => {
    const msg = message.trim();
    if (!msg) return;
    run(msg, fresh, "turn");
  }, [run]);

  /**
   * Next page of the SAME question. Replays the last message with every tmdbId already shown
   * excluded, so the server returns fresh picks that append to the reel. No transcript entry.
   */
  const loadMore = useCallback(() => {
    const s = stateRef.current;
    if (s.status === "streaming" || s.exhausted || !s.lastQuery || s.order.length === 0) return;
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

  /** Abandon the turn in flight, keeping whatever it already produced. */
  const stop = useCallback(() => {
    clearWatchdog();
    abortRef.current?.abort();
    abortRef.current = null;
    dispatch({ type: "stop" });
  }, [clearWatchdog]);

  const resume = useCallback((turns: ChatTurn[], conversationId: string, shown?: number[]) => {
    if (turns.length > 0) dispatch({ type: "resume", turns, conversationId, shown });
  }, []);

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
    send, resume, loadMore, reask, stop, setMediaTypes, exhausted: state.exhausted, appending: state.appending,
    /** The server's note that this question collides with a standing exclusion; not an error. */
    conflict: state.conflict,
    /** Cards the quality floor cut on the last turn — their own tray, never the grid. */
    belowFloor,
    /** Why the last failure happened, where the client can name it. */
    errorReason: state.errorReason,
    /** True once the last question has been asked, so a retry has something to replay. */
    canRetry: state.lastQuery.length > 0,
  };
}
