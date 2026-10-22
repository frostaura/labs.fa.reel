import { createApi, fetchBaseQuery, type BaseQueryFn, type FetchArgs, type FetchBaseQueryError } from "@reduxjs/toolkit/query/react";
import { ensureFreshSession } from "../../lib/session";

/**
 * Auth rides in HttpOnly cookies (reel_at / reel_rt) — no headers to prepare; every request
 * just needs credentials included. The access cookie lives 15 minutes: on a 401 the base
 * query silently rotates the session via `ensureFreshSession` (single-flight, shared with the
 * raw-fetch callers in `lib/session.ts`) and retries once. A refreshed access cookie also heals
 * the SSE EventSource on its next auto-reconnect.
 * Terminal 401s surface through query `error` states; RequireAuth redirects to landing.
 */
const rawBaseQuery = fetchBaseQuery({ baseUrl: "/api", credentials: "include" });

const baseQueryWithReauth: BaseQueryFn<string | FetchArgs, unknown, FetchBaseQueryError> = async (
  args,
  queryApi,
  extraOptions
) => {
  let result = await rawBaseQuery(args, queryApi, extraOptions);

  const url = typeof args === "string" ? args : args.url;
  if (result.error?.status === 401 && !url.startsWith("auth/")) {
    if (await ensureFreshSession()) {
      result = await rawBaseQuery(args, queryApi, extraOptions);
    }
  }

  return result;
};

/**
 * The one RTK Query slice. It declares the transport and the cache tags and no endpoints: each
 * domain injects its own from a sibling file, and `./index` re-exports them all.
 */
export const api = createApi({
  reducerPath: "reelApi",
  baseQuery: baseQueryWithReauth,
  // "Feed" is the picks snapshot and is deliberately NOT invalidated by per-title reactions — see
  // the note on `markNotInterested` in titles.ts. "ContinueWatching" is split out so dropping a
  // show can refresh that rail without re-seeding (and so resetting) every infinite row the user
  // is scrolling.
  // "Conversation" covers Ask Reel's thread list and each resumed thread. The /ask STREAM is not in
  // here and cannot be — RTK Query cannot hold a text/event-stream body — but everything around it
  // is ordinary cached data, which is what makes a delete refresh the Recent menu on its own.
  tagTypes: ["Session", "Sync", "Feed", "ContinueWatching", "TasteDna", "Lab", "Title", "Saved", "RecentlyWatched", "Prefs", "Person", "PrefTags", "Dislikes", "Accuracy", "Narrative", "Lens", "CrossReference", "Conversation"],
  endpoints: () => ({}),
});
