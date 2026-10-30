import { createSelector, createSlice, type PayloadAction } from "@reduxjs/toolkit";
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
 *
 * TWO SETS (HREV7-DESK-01). `keys` holds what the server has CONFIRMED; `pending` holds what is in
 * flight. The rails hide both ({@link selectHiddenKeys}), so a card leaves on the tap rather than a
 * round trip later, and a failed request takes only its own pending entry away, which puts the card
 * back. Kept apart rather than merged so that (a) a failure can never un-hide a title some EARLIER
 * action had already removed, and (b) a surface that treats the removal as the server's receipt
 * (Ask Reel's save toast, F4-09) can go on reading the confirmed set alone. `pending` is a multiset:
 * two actions in flight on one title each hold, and release, their own entry.
 */
export interface RemovedTitlesState {
  keys: string[];
  pending: string[];
}

const initialState: RemovedTitlesState = { keys: [], pending: [] };

interface TitleRef {
  titleId: string;
}

function release(pending: string[], titleId: string): string[] {
  const at = pending.indexOf(titleId);
  return at < 0 ? pending : [...pending.slice(0, at), ...pending.slice(at + 1)];
}

export const removedTitlesSlice = createSlice({
  name: "removedTitles",
  initialState,
  reducers: {
    /** An action that will remove the title has been sent: hide it now. */
    titleRemovalStarted(state, action: PayloadAction<TitleRef>) {
      if (action.payload.titleId) state.pending.push(action.payload.titleId);
    },
    /** That action failed: release its hold, which shows the title again unless something else hides it. */
    titleRemovalFailed(state, action: PayloadAction<TitleRef>) {
      state.pending = release(state.pending, action.payload.titleId);
    },
    /** The server confirmed the removal (and releases the hold an optimistic start took, if any). */
    titleRemoved(state, action: PayloadAction<TitleRef>) {
      const { titleId } = action.payload;
      if (titleId && !state.keys.includes(titleId)) {
        state.keys.push(titleId);
      }
      state.pending = release(state.pending, titleId);
    },
    titleRestored(state, action: PayloadAction<TitleRef>) {
      state.keys = state.keys.filter((k) => k !== action.payload.titleId);
    },
    /**
     * A feed rebuild: the new snapshot already excludes everything confirmed. What is still in
     * flight is kept — the server has not seen it yet, so the new snapshot may well contain it.
     */
    removalsCleared(state) {
      state.keys = [];
    },
  },
});

export const { titleRemovalStarted, titleRemovalFailed, titleRemoved, titleRestored, removalsCleared } =
  removedTitlesSlice.actions;

/** What the server has confirmed removed. The receipt, for a surface that confirms on it. */
export const selectRemovedKeys = (state: RootState): string[] => state.removedTitles.keys;

/** What a suggestion surface hides: the confirmed removals plus those still in flight. */
export const selectHiddenKeys = createSelector(
  [selectRemovedKeys, (state: RootState): string[] => state.removedTitles.pending],
  (keys, pending): string[] => (pending.length === 0 ? keys : [...new Set([...keys, ...pending])])
);

export default removedTitlesSlice.reducer;
