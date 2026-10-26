// Type-only, so nothing at runtime depends on the hook module: the resume payload is defined
// beside the reducer that consumes it, and duplicating its shape here is how the two drift.
import type { ConversationPayload } from "../../lib/askReel/transcript";
import type { AskCard } from "../../lib/askReelStream";
import { api } from "./base";

/**
 * One row of Ask Reel's thread list. `lastCardsAt: null` means the thread predates stored results
 * — it is a transcript, and saying so is a different fact from a turn that returned nothing.
 */
export interface ConversationSummary {
  id: string;
  title: string | null;
  turnCount: number;
  lastTurnAt: string;
  lastCardsAt: string | null;
}

/**
 * What a turn says about itself when a client comes back to ask. `status` is the server's own
 * enum name (`Pending` / `Completed` / `Failed`); a Pending turn answers 202, so a poller can tell
 * "not yet" apart from "nothing".
 */
export interface TurnStatusPayload {
  status: string;
  reason: string | null;
  failureCode: string | null;
  cards: AskCard[];
  removed?: number;
}

export const searchApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    /**
     * Ask Reel's thread list — enough to render the `Recent` menu and nothing more.
     *
     * Bounded on purpose: the menu shows a handful, and an unbounded list would grow with the
     * account for a control nobody scrolls.
     */
    listConversations: b.query<ConversationSummary[], number | void>({
      query: (limit) => `search/conversations?limit=${typeof limit === "number" ? limit : 20}`,
      providesTags: ["Conversation"],
    }),
    /**
     * One conversation, resumed — the transcript AND the answer as it should look now.
     *
     * The id is `"latest"` only for a browser that has never asked anything: `/conversations/latest`
     * is the ACCOUNT's newest thread, not this tab's, and preferring it is how two tabs (or a phone
     * and a laptop) silently converged on one conversation. One endpoint serves both because the
     * route's `{id:guid}` constraint cannot match the word, so the page expresses the whole rule as
     * a single query argument.
     */
    getConversation: b.query<ConversationPayload, string>({
      query: (id) => `search/conversations/${encodeURIComponent(id)}`,
      providesTags: (_r, _e, id) => [{ type: "Conversation" as const, id }],
    }),
    /**
     * What became of a turn whose stream this client lost. The events are gone, but the turn is a
     * row, and a row answers the only question a disconnected client has: is it done, and what did
     * it say. `202` while it is still running, which RTK surfaces as data with `status: "Pending"`.
     */
    getTurn: b.query<TurnStatusPayload, string>({
      query: (id) => `search/turns/${encodeURIComponent(id)}`,
    }),
    /**
     * Erasure, for one conversation. Invalidates the list AND that thread's own entry: without the
     * second, the page can keep rendering a conversation the server no longer has, and without the
     * first the deleted thread stays in the menu until a reload and 404s when clicked.
     *
     * It never touches exposures — that is the server's promise, stated in its own doc-comment.
     */
    deleteConversation: b.mutation<void, string>({
      query: (id) => ({ url: `search/conversations/${encodeURIComponent(id)}`, method: "DELETE" }),
      invalidatesTags: (_r, _e, id) => ["Conversation", { type: "Conversation" as const, id }],
    }),
  }),
});

export const {
  useListConversationsQuery,
  useGetConversationQuery,
  useGetTurnQuery,
  useDeleteConversationMutation,
} = searchApi;
