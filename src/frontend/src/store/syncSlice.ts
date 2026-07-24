import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

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
}

const initialState: SyncUiState = { active: false, label: null, pct: null, kind: null, current: null, total: null };

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
    },
    syncIdle() {
      return initialState;
    },
  },
});

export const { syncProgress, syncIdle } = syncSlice.actions;
export default syncSlice.reducer;
