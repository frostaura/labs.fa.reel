import { createSlice, type PayloadAction } from "@reduxjs/toolkit";
import type { RootState } from "./index";

/**
 * Titles the user has just made ineligible this session (rated/seen, not-interested, saved) — they
 * must vanish from every suggestion surface immediately. The server already excludes them, but the
 * infinite-scroll rows render from local `useInfiniteRow` state and Ask Reel from a local reducer,
 * neither subscribed to the RTK cache; this shared set is what those surfaces filter against. An
 * undo (un-save / un-not-interested) restores the key. Cleared on a feed rebuild, after which the
 * server-side seed no longer contains them.
 *
 * Keyed on the title's GUID id, not (mediaType, tmdbId): a card without a tmdbId used to key as the
 * bare "movie:" / "show:" string, so two un-tmdb'd titles collided and removing one hid the other.
 * The GUID is always present and unique, so no removal ever bleeds onto another title.
 */
export interface RemovedTitlesState {
  keys: string[];
}

const initialState: RemovedTitlesState = { keys: [] };

interface TitleRef {
  titleId: string;
}

export const removedTitlesSlice = createSlice({
  name: "removedTitles",
  initialState,
  reducers: {
    titleRemoved(state, action: PayloadAction<TitleRef>) {
      const { titleId } = action.payload;
      if (titleId && !state.keys.includes(titleId)) {
        state.keys.push(titleId);
      }
    },
    titleRestored(state, action: PayloadAction<TitleRef>) {
      state.keys = state.keys.filter((k) => k !== action.payload.titleId);
    },
    removalsCleared() {
      return initialState;
    },
  },
});

export const { titleRemoved, titleRestored, removalsCleared } = removedTitlesSlice.actions;
export const selectRemovedKeys = (state: RootState): string[] => state.removedTitles.keys;
export default removedTitlesSlice.reducer;
