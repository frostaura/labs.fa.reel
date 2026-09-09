import { createSlice, type PayloadAction } from "@reduxjs/toolkit";
import type { RootState } from "./index";
import type { FeedCard } from "./feedTypes";

/**
 * Predicted ratings the server has recalibrated since the page was seeded.
 *
 * A Preserve-mode build can now change what the model thinks of tonight's picks WITHOUT changing
 * which picks they are: it updates the Active snapshot's rows in place and publishes
 * `feed-scores-updated` carrying the snapshot's unchanged `generatedAt`. Unchanged is the whole
 * point — every row on Home is keyed on `generatedAt`, so anything that moved it would remount the
 * rows and throw away the scroll the contract exists to protect.
 *
 * That leaves three surfaces holding numbers, only one of which is in the RTK cache:
 *   1. `getFeed` — patched directly via `updateQueryData`, never invalidated (invalidating the Feed
 *      tag is the remount this whole mechanism is avoiding).
 *   2. the infinite rows — local `useInfiniteRow` state, fed by `loadMoreFeed`, a mutation with no
 *      cache tag, so nothing in RTK can reach it.
 *   3. Ask Reel results — a local reducer, likewise unreachable.
 *
 * This slice is what (2) and (3) read, exactly as `removedTitlesSlice` is what they read for
 * removals: one shared map they filter their own state through at render time. Without it half the
 * page would update and half would not.
 *
 * Cleared when the user adopts a fresh snapshot — at that point the server's seed already carries
 * the current numbers and an override could only mask them.
 */
export interface ScoreOverridesState {
  /** titleId → the recalibrated predicted rating. */
  byTitleId: Record<string, number>;
}

const initialState: ScoreOverridesState = { byTitleId: {} };

/** One card's new number, as it arrives on the `feed-scores-updated` frame. */
export interface ScoreChange {
  titleId: string;
  predictedRating: number;
}

export const scoreOverridesSlice = createSlice({
  name: "scoreOverrides",
  initialState,
  reducers: {
    scoresRecalibrated(state, action: PayloadAction<{ changed: ScoreChange[] }>) {
      for (const change of action.payload.changed ?? []) {
        // Defensive on both fields: this data comes off the wire, and a malformed frame must leave
        // the page showing the last number it trusted rather than "NaN for you". The empty-string
        // guard is not theoretical — removedTitlesSlice was keyed in a way that let two un-tmdb'd
        // titles collapse onto one key, and removing one hid the other.
        if (typeof change?.titleId === "string" && change.titleId !== "" && Number.isFinite(change?.predictedRating)) {
          state.byTitleId[change.titleId] = change.predictedRating;
        }
      }
    },
    scoreOverridesCleared() {
      return initialState;
    },
  },
});

export const { scoresRecalibrated, scoreOverridesCleared } = scoreOverridesSlice.actions;

export const selectScoreOverrides = (state: RootState): Record<string, number> =>
  state.scoreOverrides.byTitleId;

/**
 * Applies the overrides to a list of cards, returning the SAME array when nothing matches and the
 * same object for every untouched card. Referential stability is load-bearing: these lists feed
 * memoised rows, and returning fresh objects on every render would re-render the whole reel on
 * every keystroke elsewhere in the app.
 */
export function applyScoreOverrides<T extends Pick<FeedCard, "titleId"> & { predictedRating: number | null }>(
  cards: T[],
  overrides: Record<string, number>
): T[] {
  if (cards.length === 0) return cards;
  let touched = false;
  const next = cards.map((card) => {
    const override = overrides[card.titleId];
    if (override === undefined || override === card.predictedRating) return card;
    touched = true;
    return { ...card, predictedRating: override };
  });
  return touched ? next : cards;
}

export default scoreOverridesSlice.reducer;
