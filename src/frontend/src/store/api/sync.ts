import { api } from "./base";
import type { PipelineStage } from "./auth";

export interface SyncJobSummary {
  kind: string;
  status: "Pending" | "Running" | "Succeeded" | "Failed" | "Cancelled";
  progressPct: number | null;
  progressMessage: string | null;
  enqueuedAt: string;
  completedAt: string | null;
}

export interface SyncStatus {
  pipelineStage: PipelineStage;
  connectionStatus: "Active" | "RefreshFailed" | "Revoked";
  lastDeltaSyncAt: string | null;
  lastFullReconcileAt: string | null;
  activeJob: SyncJobSummary | null;
  recentJobs: SyncJobSummary[];
  outboxPending: number;
  outboxDeadLetters: number;
  /** When Trakt last accepted a list/write-back (max SentAt across the outbox). */
  lastListSyncAt: string | null;
  /** Most recent write-back error text, for the indicator's hover detail. */
  outboxLastError: string | null;
  /**
   * Progress toward the next drift retrain, so a screen that asks for ratings can say what they
   * buy. `newRatingsSinceTrain` counts UserRatings whose immutable `FirstRatedAt` is newer than the
   * Active artifact's `TrainedAt` — the same two inputs the nightly reconcile's gate reads. Absent
   * on a response from a backend that predates the field.
   */
  retrain?: {
    minNewRatings: number;
    newRatingsSinceTrain: number;
    /** False before the first model exists: the next train is the FIRST, not a drift retrain. */
    hasActiveModel: boolean;
    lastTrainedAt: string | null;
  };
}

export const syncApi = api.injectEndpoints({
  endpoints: (b) => ({
    getSyncStatus: b.query<SyncStatus, void>({
      query: () => "sync/status",
      providesTags: ["Sync"],
    }),
    triggerSync: b.mutation<void, void>({
      query: () => ({ url: "sync/now", method: "POST" }),
      invalidatesTags: ["Sync"],
    }),
  }),
});

export const {
  useGetSyncStatusQuery,
  useTriggerSyncMutation,
} = syncApi;
