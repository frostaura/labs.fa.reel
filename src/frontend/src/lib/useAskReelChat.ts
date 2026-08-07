import { useCallback, useEffect, useReducer, useRef } from "react";
import { isFailedDone, streamAskReel, type AskCard, type PhaseData } from "./askReelStream";

type Status = "idle" | "streaming" | "done" | "error";

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

/** The transcript as the wire wants it, trimmed to the most recent {@link MAX_HISTORY_TURNS}. */
export function capHistory(turns: ChatTurn[]): { role: string; text: string }[] {
  return turns.slice(-MAX_HISTORY_TURNS).map((t) => ({ role: t.role, text: t.text }));
}

/** The no-repeat exclusion list, trimmed to the most recent {@link MAX_SHOWN_IDS}. */
export function capShown(shown: number[]): number[] {
  return shown.slice(-MAX_SHOWN_IDS);
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
}

type Action =
  | { type: "userTurn"; message: string; fresh: boolean }
  | { type: "moreTurn" }
  | { type: "reaskTurn" }
  | { type: "phase"; data: PhaseData }
  | { type: "conversation"; data: { id: string } }
  | { type: "resume"; turns: ChatTurn[]; conversationId: string }
  | { type: "assistant"; data: { text: string } }
  | { type: "candidate"; data: AskCard }
  | { type: "scored"; data: { titleId: string; predictedRating: number | null } }
  | { type: "reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { type: "done"; data: { results: AskCard[]; reason: string | null } }
  | { type: "error" };

export const initialChatState: State = {
  turns: [], status: "idle", phase: null, byId: {}, order: [], reason: null, shown: [],
  conversationId: null, appending: false, lastQuery: "", exhausted: false,
};

function blend(c: AskCard): number {
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
          };
    }
    case "moreTurn":
      // A page, not a turn: no transcript entry, and the existing cards stay put.
      return { ...state, status: "streaming", phase: null, appending: true };
    case "reaskTurn": {
      // The same question asked again — a media-filter change, or a retry after a failure. Like a
      // page it adds no transcript entry (the user said it once; echoing their bubble on every
      // toggle click was the bug), but unlike a page it REPLACES the answer, so the stale reply is
      // dropped and the cards are cleared for the incoming set.
      const turns = state.turns.at(-1)?.role === "assistant" ? state.turns.slice(0, -1) : state.turns;
      return {
        ...state, turns, status: "streaming", phase: null,
        byId: {}, order: [], reason: null, appending: false, exhausted: false,
      };
    }
    case "phase":
      // A phase boundary is the ONLY mid-stream moment the rows may re-order (see
      // partitionedOrder) — a settle-then-shuffle rhythm instead of jumping on every event.
      return { ...state, status: "streaming", phase: action.data, order: partitionedOrder(state.order, state.byId) };
    case "conversation":
      return { ...state, conversationId: action.data.id };
    case "resume":
      return { ...initialChatState, turns: action.turns, conversationId: action.conversationId, status: "done" };
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
      };
    }
    case "error":
      return { ...state, status: "error" };
  }
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
  // `send`/`loadMore`/`reask` must stay referentially stable (an effect and an IntersectionObserver
  // depend on them) while still reading current state. Synced in an effect rather than during
  // render: writing a ref mid-render is what the React Compiler rule flags, and the callbacks only
  // ever read it from an event or an observer — long after commit.
  const stateRef = useRef(state);
  useEffect(() => {
    stateRef.current = state;
  }, [state]);
  const abortRef = useRef<AbortController | null>(null);

  // The active media filter, kept in a ref so `send`/`loadMore` stay referentially stable while
  // still reading the current value — a filter change re-sends rather than hiding cards locally.
  const mediaRef = useRef<string[]>([]);
  const setMediaTypes = useCallback((types: string[]) => {
    mediaRef.current = types;
  }, []);

  const run = useCallback((msg: string, fresh: boolean, kind: "turn" | "more" | "reask") => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

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
      switch (ev.event) {
        case "phase": dispatch({ type: "phase", data: ev.data }); break;
        case "conversation": dispatch({ type: "conversation", data: ev.data }); break;
        case "assistant-message": dispatch({ type: "assistant", data: ev.data }); break;
        case "candidate": dispatch({ type: "candidate", data: ev.data }); break;
        case "candidate-scored": dispatch({ type: "scored", data: ev.data }); break;
        case "candidate-reranked": dispatch({ type: "reranked", data: ev.data }); break;
        case "done": dispatch({ type: "done", data: ev.data }); break;
        default: break;
      }
    }, controller.signal).catch((err) => {
      if (!controller.signal.aborted) dispatch({ type: "error" });
      void err;
    });
  }, []);

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

  const resume = useCallback((turns: ChatTurn[], conversationId: string) => {
    if (turns.length > 0) dispatch({ type: "resume", turns, conversationId });
  }, []);

  useEffect(() => () => abortRef.current?.abort(), []);

  // NO render-time sort: `order` is the state's stream-stable order, so rows only ever move when
  // the reducer re-sorts it (phase / done) — sorting here is what made cards jump on every event.
  const all = state.order.map((id) => state.byId[id]).filter(Boolean);
  const cards = all.filter((c) => !c.outsideRequest);
  const outsideCards = all.filter((c) => c.outsideRequest);
  return {
    turns: state.turns, status: state.status, phase: state.phase, cards, outsideCards, reason: state.reason,
    send, resume, loadMore, reask, setMediaTypes, exhausted: state.exhausted, appending: state.appending,
    /** True once the last question has been asked, so a retry has something to replay. */
    canRetry: state.lastQuery.length > 0,
  };
}
