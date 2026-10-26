import { configureStore } from "@reduxjs/toolkit";
import { render, type RenderResult } from "@testing-library/react";
import type { ReactElement } from "react";
import { Provider } from "react-redux";
import { api } from "../store/api";
import { rootReducer } from "../store";
import type { SyncStatus } from "../store/api/sync";

/** A fresh store per test, built exactly like the app's (store/index.ts). */
export function makeStore() {
  return configureStore({ reducer: rootReducer, middleware: (gDM) => gDM().concat(api.middleware) });
}

export type TestStore = ReturnType<typeof makeStore>;

/** Seeds the status query's cache, so a component reads it without a request leaving the test. */
export async function seedSyncStatus(store: TestStore, status: SyncStatus): Promise<void> {
  await store.dispatch(api.util.upsertQueryData("getSyncStatus", undefined, status));
}

export function renderWithStore(ui: ReactElement, store: TestStore = makeStore()): RenderResult & { store: TestStore } {
  return { store, ...render(<Provider store={store}>{ui}</Provider>) };
}

/** A complete v2 status payload with nothing wrong and nothing running. */
export function idleStatus(overrides: Partial<SyncStatus> = {}): SyncStatus {
  return {
    contractVersion: 2,
    pipelineStage: "FeedReady",
    connectionStatus: "Active",
    lastDeltaSyncAt: new Date(Date.now() - 3 * 60_000).toISOString(),
    lastFullReconcileAt: null,
    activeJob: null,
    recentJobs: [],
    outboxPending: 0,
    outboxDeadLetters: 0,
    lastListSyncAt: null,
    outboxLastError: null,
    retrain: { minNewRatings: 10, newRatingsSinceTrain: 0, hasActiveModel: true, lastTrainedAt: null },
    lastFailures: [],
    providerHealth: { state: "Unmonitored", checkedAt: null, breakers: [] },
    degradedReason: null,
    notEnoughRatings: null,
    ...overrides,
  } as SyncStatus;
}
