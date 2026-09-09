import { createSlice, type PayloadAction } from "@reduxjs/toolkit";
import type { RatingVerdict } from "../lib/verdictCopy";

/** The moment currently on screen, plus what the user actually said. */
export interface ShownVerdict extends RatingVerdict {
  /** The rating the user just gave — the toast needs both halves of the comparison. */
  actual: number;
  /** Distinguishes two verdicts on the same title, so the toast re-animates rather than sitting still. */
  shownKey: number;
}

interface VerdictState {
  current: ShownVerdict | null;
}

const initialState: VerdictState = { current: null };

/**
 * The after-rating moment, held in the store rather than at the call sites.
 *
 * There are three places a rating can be given — the title modal, the rate picker, and the
 * post-watch strip on Saved — and there will be more. Wiring the toast into each one means three
 * copies of the same conditional and a fourth surface that silently forgets. So the RTK Query
 * mutation dispatches here on success, one place, and the toast is mounted once in the layout:
 * every rating path gets the moment for free, including ones that do not exist yet.
 */
const verdictSlice = createSlice({
  name: "verdict",
  initialState,
  reducers: {
    verdictShown(state, action: PayloadAction<{ verdict: RatingVerdict; actual: number }>) {
      state.current = {
        ...action.payload.verdict,
        actual: action.payload.actual,
        shownKey: Date.now(),
      };
    },
    verdictDismissed(state) {
      state.current = null;
    },
  },
});

export const { verdictShown, verdictDismissed } = verdictSlice.actions;
export default verdictSlice.reducer;
