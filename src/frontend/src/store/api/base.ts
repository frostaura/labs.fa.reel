import { createApi, fetchBaseQuery, type BaseQueryFn, type FetchArgs, type FetchBaseQueryError } from "@reduxjs/toolkit/query/react";
import { ensureFreshSession } from "../../lib/session";
import { FROM_CACHE_HEADER } from "../../lib/swContract";
import { servedFromCache, servedLive, type ConnectivityState } from "../connectivitySlice";

/**
 * Auth rides in HttpOnly cookies (reel_at / reel_rt) — no headers to prepare; every request
 * just needs credentials included. The access cookie lives 15 minutes: on a 401 the base
 * query silently rotates the session via `ensureFreshSession` (single-flight, shared with the
 * raw-fetch callers in `lib/session.ts`) and retries once. A refreshed access cookie also heals
 * the SSE EventSource on its next auto-reconnect.
 *
 * `auth/me` — the session probe every protected route waits on — refreshes like any other call.
 * It used to be exempt with the rest of `auth/*`, so any reload more than 15 minutes after the
 * last request signed the user out with a perfectly good 30-day refresh cookie still in hand
 * (H+1). The other auth endpoints stay exempt: refreshing around `auth/refresh` itself would
 * recurse, and the OAuth and logout calls have no session to heal.
 *
 * Only a 401 that survives the refresh is terminal; it surfaces through the query's `error`, and
 * RequireAuth redirects on that alone (anything else gets a retry shell — H-15).
 */
const rawBaseQuery = fetchBaseQuery({ baseUrl: "/api", credentials: "include" });

/** The server's `Date` header as ISO-8601, or null when absent or unparseable. */
export function savedAtFromDateHeader(value: string | null): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/** Every endpoint outside `auth/*`, plus the session probe. */
export function refreshesOn401(url: string): boolean {
  return !url.startsWith("auth/") || url === "auth/me";
}

const baseQueryWithReauth: BaseQueryFn<string | FetchArgs, unknown, FetchBaseQueryError> = async (
  args,
  queryApi,
  extraOptions
) => {
  let result = await rawBaseQuery(args, queryApi, extraOptions);

  const url = typeof args === "string" ? args : args.url;
  if (result.error?.status === 401 && refreshesOn401(url)) {
    if (await ensureFreshSession()) {
      result = await rawBaseQuery(args, queryApi, extraOptions);
    }
  }

  // The service worker marks an answer it served from its offline cache. Record it so the shell
  // can say so; the next live answer clears it.
  const response = result.meta?.response;
  if (response) {
    if (response.headers.get(FROM_CACHE_HEADER) === "1") {
      queryApi.dispatch(servedFromCache({ savedAt: savedAtFromDateHeader(response.headers.get("Date")) }));
    } else if ((queryApi.getState() as { connectivity?: ConnectivityState }).connectivity?.fromCache) {
      queryApi.dispatch(servedLive());
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
