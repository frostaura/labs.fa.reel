import { useCallback, useEffect, useReducer, useRef } from "react";
import { streamAskReel, type AskCard, type PhaseData } from "./askReelStream";

type Status = "idle" | "streaming" | "done" | "error";

export interface ChatTurn {
  role: "user" | "assistant";
  text: string;
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
  | { type: "phase"; data: PhaseData }
  | { type: "conversation"; data: { id: string } }
  | { type: "resume"; turns: ChatTurn[]; conversationId: string }
  | { type: "assistant"; data: { text: string } }
  | { type: "candidate"; data: AskCard }
  | { type: "scored"; data: { titleId: string; predictedRating: number | null } }
  | { type: "reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { type: "done"; data: { results: AskCard[]; reason: string | null } }
  | { type: "error" };

const initial: State = {
  turns: [], status: "idle", phase: null, byId: {}, order: [], reason: null, shown: [],
  conversationId: null, appending: false, lastQuery: "", exhausted: false,
};

function reducer(state: State, action: Action): State {
  switch (action.type) {
    case "userTurn": {
      const turn: ChatTurn = { role: "user", text: action.message };
      return action.fresh
        ? { ...initial, turns: [turn], status: "streaming", lastQuery: action.message }
        : {
            ...state, turns: [...state.turns, turn], status: "streaming", phase: null,
            byId: {}, order: [], reason: null, appending: false, lastQuery: action.message, exhausted: false,
          };
    }
    case "moreTurn":
      // A page, not a turn: no transcript entry, and the existing cards stay put.
      return { ...state, status: "streaming", phase: null, appending: true };
    case "phase":
      return { ...state, status: "streaming", phase: action.data };
    case "conversation":
      return { ...state, conversationId: action.data.id };
    case "resume":
      return { ...initial, turns: action.turns, conversationId: action.conversationId, status: "done" };
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
      const byId = { ...state.byId };
      const newTmdb: number[] = [];
      for (const c of action.data.results) {
        byId[c.titleId] = { ...byId[c.titleId], ...c };
        if (c.tmdbId != null) newTmdb.push(c.tmdbId);
      }
      const shown = Array.from(new Set([...state.shown, ...newTmdb]));
      const incoming = action.data.results.map((c) => c.titleId);
      // Appending keeps what is on screen and adds only genuinely new ids; a page that adds
      // nothing latches `exhausted` so the scroll sentinel stops asking for more.
      const order = state.appending
        ? [...state.order, ...incoming.filter((id) => !state.order.includes(id))]
        : incoming;
      const gained = order.length - state.order.length;
      return {
        ...state, byId, order, status: "done", shown, appending: false,
        reason: state.appending && gained === 0 ? state.reason : action.data.reason,
        exhausted: state.appending ? gained === 0 : false,
      };
    }
    case "error":
      return { ...state, status: "error" };
  }
}

function blend(c: AskCard): number {
  return (c.similarity ?? 0) * 0.35 + ((c.predictedRating ?? 6) / 10) * 0.25 + (c.fit ?? 0.5) * 0.4;
}

/**
 * Drives a multi-turn Ask Reel conversation: each turn streams from /api/search/ask with the
 * prior transcript + the tmdbIds already shown (so nothing repeats), accumulates cards (re-sorted
 * live by the personal blend), and records the reply. `fresh` starts a new conversation.
 */
export function useAskReelChat() {
  const [state, dispatch] = useReducer(reducer, initial);
  const stateRef = useRef(state);
  stateRef.current = state;
  const abortRef = useRef<AbortController | null>(null);

  // The active media filter, kept in a ref so `send`/`loadMore` stay referentially stable while
  // still reading the current value — a filter change re-sends rather than hiding cards locally.
  const mediaRef = useRef<string[]>([]);
  const setMediaTypes = useCallback((types: string[]) => {
    mediaRef.current = types;
  }, []);

  const run = useCallback((msg: string, fresh: boolean, more: boolean) => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    const history = fresh ? [] : stateRef.current.turns.map((t) => ({ role: t.role, text: t.text }));
    const shownTmdbIds = fresh ? [] : [...stateRef.current.shown];
    const conversationId = fresh ? null : stateRef.current.conversationId;
    dispatch(more ? { type: "moreTurn" } : { type: "userTurn", message: msg, fresh });

    streamAskReel({ query: msg, history, shownTmdbIds, conversationId, mediaTypes: mediaRef.current }, (ev) => {
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
    run(msg, fresh, false);
  }, [run]);

  /**
   * Next page of the SAME question. Replays the last message with every tmdbId already shown
   * excluded, so the server returns fresh picks that append to the reel. No transcript entry.
   */
  const loadMore = useCallback(() => {
    const s = stateRef.current;
    if (s.status === "streaming" || s.exhausted || !s.lastQuery || s.order.length === 0) return;
    run(s.lastQuery, false, true);
  }, [run]);

  const resume = useCallback((turns: ChatTurn[], conversationId: string) => {
    if (turns.length > 0) dispatch({ type: "resume", turns, conversationId });
  }, []);

  useEffect(() => () => abortRef.current?.abort(), []);

  const cards = state.order.map((id) => state.byId[id]).filter(Boolean).sort((a, b) => blend(b) - blend(a));
  return {
    turns: state.turns, status: state.status, phase: state.phase, cards, reason: state.reason,
    send, resume, loadMore, setMediaTypes, exhausted: state.exhausted, appending: state.appending,
  };
}
