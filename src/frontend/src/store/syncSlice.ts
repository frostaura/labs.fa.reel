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

/** The latest live `job-progress` frame. Numbers only — its free-text message is never kept. */
export interface LiveProgress {
  /** SSE short kind ("delta", "xref", …); applied only while it matches `activeJob.sseKind`. */
  kind: string | null;
  pct: number | null;
  current: number | null;
  total: number | null;
}

/**
 * Live-sync state for the header pill. The status API is the authority on WHETHER something is
 * running; this slice only carries what the poll cannot: the live frame between polls, and what
 * the last sync did.
 */
export interface SyncUiState {
  /**
   * True while the status API reports user-visible work in flight (a job the user started or is
   * waiting on). Set from the poll on every response, never from a live frame, so it cannot
   * outlive the job (H-17).
   */
  active: boolean;
  live: LiveProgress | null;
  /**
   * The last completed sync's outcome, kept after the job goes idle so the pill can say what the
   * sync actually did instead of reverting to a timestamp. Cleared when the next job starts.
   */
  summary: SyncSummary | null;
}

const initialState: SyncUiState = { active: false, live: null, summary: null };

export const syncSlice = createSlice({
  name: "syncUi",
  initialState,
  reducers: {
    syncProgress(
      state,
      action: PayloadAction<{
        kind: string | null;
        pct: number | null;
        current?: number | null;
        total?: number | null;
      }>
    ) {
      state.live = {
        kind: action.payload.kind,
        pct: action.payload.pct,
        current: action.payload.current ?? null,
        total: action.payload.total ?? null,
      };
    },
    /**
     * The status poll answered. `jobChanged` is true when the active job's id differs from the
     * previous answer's (including going idle): any live frame then belongs to a previous run, so
     * it is dropped rather than shown against the new one.
     */
    activeJobObserved(state, action: PayloadAction<{ userVisible: boolean; jobChanged: boolean }>) {
      state.active = action.payload.userVisible;
      if (action.payload.jobChanged) state.live = null;
      // A new job the user is watching makes the last sync's outcome history — leaving it up would
      // report a result while a fresh sync runs. Background frames never clear it.
      if (action.payload.jobChanged && action.payload.userVisible) state.summary = null;
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

export const { syncProgress, activeJobObserved, syncCompleted, picksChangedReported, syncIdle } = syncSlice.actions;
export default syncSlice.reducer;
