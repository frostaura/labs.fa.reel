import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

/**
 * Which answers on screen came from the service worker's offline cache rather than the server, per
 * RTK Query cache key, with when each was saved (the server's `Date` header). Set by the base query
 * from the worker's `X-Reel-From-Cache` mark; an entry clears only when THAT query next answers
 * live, so a fallback is never presented as live data (I-11).
 *
 * Per key, not one flag: the 60-second status poll answering live used to clear the banner while
 * an hour-old feed from the cache was still on screen.
 */
export interface ConnectivityState {
  /** Query cache key → when the cached answer was saved (null when the response did not say). */
  cached: Record<string, string | null>;
}

const initialState: ConnectivityState = { cached: {} };

export const connectivitySlice = createSlice({
  name: "connectivity",
  initialState,
  reducers: {
    servedFromCache(state, action: PayloadAction<{ key: string; savedAt: string | null }>) {
      state.cached[action.payload.key] = action.payload.savedAt;
    },
    servedLive(state, action: PayloadAction<{ key: string }>) {
      delete state.cached[action.payload.key];
    },
  },
});

/**
 * What the offline banner says: whether anything still on screen came from the cache, and the
 * OLDEST saved time among those answers (the banner speaks for everything shown, and the stalest
 * answer is the one the user needs warning about). `onScreen` says whether a key's query still has a
 * subscriber; a cached answer for a page the user has left no longer speaks for the screen.
 */
export function offlineSnapshot(
  state: ConnectivityState,
  onScreen: (key: string) => boolean
): { fromCache: boolean; savedAt: string | null } {
  let fromCache = false;
  let savedAt: string | null = null;
  for (const [key, saved] of Object.entries(state.cached)) {
    if (!onScreen(key)) continue;
    fromCache = true;
    if (saved && (!savedAt || saved < savedAt)) savedAt = saved;
  }
  return { fromCache, savedAt };
}

export const { servedFromCache, servedLive } = connectivitySlice.actions;
export default connectivitySlice.reducer;
