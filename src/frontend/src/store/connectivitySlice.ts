import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

/**
 * Whether what is on screen came from the service worker's offline cache rather than the server.
 * Set by the base query from the worker's `X-Reel-From-Cache` mark and cleared by the next live
 * answer, so a fallback is never presented as live data (I-11).
 */
export interface ConnectivityState {
  fromCache: boolean;
  /** When the cached answer was saved (the server's `Date` header), when it said. */
  savedAt: string | null;
}

const initialState: ConnectivityState = { fromCache: false, savedAt: null };

export const connectivitySlice = createSlice({
  name: "connectivity",
  initialState,
  reducers: {
    servedFromCache(state, action: PayloadAction<{ savedAt: string | null }>) {
      state.fromCache = true;
      // Keep the OLDEST saved time on screen: the banner speaks for everything shown, and the
      // stalest answer is the one the user needs warning about.
      const next = action.payload.savedAt;
      if (next && (!state.savedAt || next < state.savedAt)) state.savedAt = next;
    },
    servedLive() {
      return initialState;
    },
  },
});

export const { servedFromCache, servedLive } = connectivitySlice.actions;
export default connectivitySlice.reducer;
