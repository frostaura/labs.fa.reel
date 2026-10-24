import type { RecentlyWatchedResponse, SavedQueueResponse } from "../savedTypes";
import { api } from "./base";

export const savedApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    // The queue, in the order the user asked for. `sort` is part of the cache key, so switching
    // orders is a fresh request rather than a client-side re-sort — the server owns the comparator
    // (and the pinned pick), and two implementations of it would drift within a release.
    getSaved: b.query<SavedQueueResponse, { sort?: string } | void>({
      query: (args) => (args && args.sort ? `saved?sort=${encodeURIComponent(args.sort)}` : "saved"),
      providesTags: ["Saved"],
    }),
    // Watched-via-Reel titles that are still unrated. Kept under its own tag: rating one of these
    // must retire that prompt without refetching the whole shelf, and saving a title must not
    // disturb the strip the user is halfway through answering.
    getRecentlyWatched: b.query<RecentlyWatchedResponse, void>({
      query: () => "saved/recently-watched",
      providesTags: ["RecentlyWatched"],
    }),
  }),
});

export const {
  useGetSavedQuery,
  useGetRecentlyWatchedQuery,
} = savedApi;
