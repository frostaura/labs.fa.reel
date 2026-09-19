import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

/**
 * What one delta sync did, as it arrives on the `job-completed` frame. Mirrors the backend's
 * `SyncSummary`; `picksChanged` is null until the rebuild the sync enqueued reports it on its own
 * `feed-ready` frame — null means "not yet", never "zero".
 */
export interface SyncSummary {
  ratingsAdded: number;
  ratingsChanged: number;
  watchedAdded: number;
  retrainQueued: boolean;
  retrainAt: string | null;
  picksChanged: number | null;
}

/** Lightweight live-sync state for the header pill, fed by the steady-state SSE stream. */
export interface SyncUiState {
  active: boolean;
  label: string | null;
  pct: number | null;
  /** Job kind ("hydrate", "delta", …) — drives the pill's contextual tooltip copy. */
  kind: string | null;
  /** Raw item counts when the live phase has them (e.g. next-episode enrichment) — powers the "x / y". */
  current: number | null;
  total: number | null;
  /**
   * The last completed sync's outcome, kept after the job goes idle so the pill can say what the
   * sync actually did instead of reverting to "Synced 2m ago". Cleared when the next job starts.
   */
  summary: SyncSummary | null;
}

const initialState: SyncUiState = {
  active: false, label: null, pct: null, kind: null, current: null, total: null, summary: null,
};

export const syncSlice = createSlice({
  name: "syncUi",
  initialState,
  reducers: {
    syncProgress(
      state,
      action: PayloadAction<{
        label: string;
        pct: number | null;
        kind: string | null;
        current?: number | null;
        total?: number | null;
      }>
    ) {
      state.active = true;
      state.label = action.payload.label;
      state.pct = action.payload.pct;
      state.kind = action.payload.kind;
      state.current = action.payload.current ?? null;
      state.total = action.payload.total ?? null;
      // A new job is running, so last sync's outcome is history — leaving it up would report a
      // result while a fresh sync is mid-flight.
      state.summary = null;
    },
    /** A delta/reconcile finished and said what it did. Survives the job going idle. */
    syncCompleted(state, action: PayloadAction<SyncSummary>) {
      state.summary = action.payload;
    },
    /**
     * The rebuild that sync triggered has landed and knows how much of the page moved. Fills the
     * one field the sync itself could not know, without disturbing the rest.
     */
    picksChangedReported(state, action: PayloadAction<{ picksChanged: number }>) {
      if (state.summary) {
        state.summary.picksChanged = action.payload.picksChanged;
      }
    },
    syncIdle(state) {
      // Deliberately NOT a reset to initialState: the pill goes idle the moment the job finishes,
      // and the whole point of the post-sync moment is that its sentence outlives the spinner.
      return { ...initialState, summary: state.summary };
    },
  },
});

export const { syncProgress, syncCompleted, picksChangedReported, syncIdle } = syncSlice.actions;
export default syncSlice.reducer;
