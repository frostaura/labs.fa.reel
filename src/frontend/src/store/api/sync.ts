import { api } from "./base";
import type { PipelineStage } from "./auth";

/**
 * `GET /api/sync/status`, contract v2 (`Application/Pipeline/SyncStatusContract.cs`).
 *
 * Hand-written rather than aliased to the generated schema because the generated document types
 * every integer as `number | string` (the API reads numbers from strings), which no consumer
 * should have to narrow. The key-set check in `sync.contract.test.ts` fails `tsc` the moment the
 * two drift apart, so the hand-written shape can never silently lag the contract.
 *
 * Every enum below travels as a string and the backend appends values without notice: readers map
 * a value they do not know to a defined fallback, never to a crash or a blank.
 */

/** How the header treats a job — computed server-side by `JobVisibilityPolicy`. */
export type JobVisibility = "Background" | "UserVisible" | "NamedPass";

export type JobStatus = "Pending" | "Running" | "Succeeded" | "Failed" | "Cancelled";

export type ConnectionStatus = "Active" | "RefreshFailed" | "Revoked";

export type DegradedReason =
  | "TraktRevoked"
  | "TraktRefreshFailed"
  | "TraktWritesDeadLettered"
  | "NotEnoughRatings"
  | "ProviderDown"
  | "PipelineFailed"
  // Status v2 (WS-6): the stored state was written by a newer build this one cannot name. It
  // raises no issue of its own — no reconnect, no failure copy.
  | "UnrecognisedState";

export type ProviderHealthState = "Unmonitored" | "Healthy" | "Degraded" | "Down";

export interface SyncJobSummary {
  /** The SyncJob id — tells a new run of a kind from the previous one. */
  id: string;
  kind: string;
  /** The short kind its SSE frames carry; a live frame applies only when it matches. */
  sseKind: string;
  visibility: JobVisibility;
  status: JobStatus;
  /** Null whenever no honest percentage exists: queued, retrying, and always for the named pass. */
  progressPct: number | null;
  /** The handler's own progress line. Free text — never rendered as the pill's label. */
  progressMessage: string | null;
  enqueuedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  attempt: number;
  maxAttempts: number;
  retrying: boolean;
  /** When retrying, why the last attempt failed — a stable `JobFailureReason` code. */
  retryReasonCode: string | null;
}

/** The latest unrecovered failure of one kind; drops out as soon as that kind next succeeds. */
export interface JobFailure {
  kind: string;
  sseKind: string;
  visibility: JobVisibility;
  failedAt: string;
  consecutiveFailures: number;
  attempts: number;
  /** A stable `JobFailureReason` code — never free text. */
  reasonCode: string;
}

export interface ProviderBreaker {
  provider: string;
  /** Null when the whole provider is affected. */
  purpose: string | null;
  state: "Open" | "Degraded";
  /** A stable machine code (`payment_required`, `truncation_breaker`, …) — never free text. */
  reasonCode: string;
  since: string;
  retryAt: string | null;
}

export interface ProviderHealth {
  state: ProviderHealthState;
  checkedAt: string | null;
  breakers: ProviderBreaker[];
}

export interface SyncStatus {
  contractVersion: number;
  pipelineStage: PipelineStage;
  connectionStatus: ConnectionStatus;
  lastDeltaSyncAt: string | null;
  lastFullReconcileAt: string | null;
  /** Only ever user-visible work or the named cross-reference pass — never background upkeep. */
  activeJob: SyncJobSummary | null;
  recentJobs: SyncJobSummary[];
  outboxPending: number;
  outboxDeadLetters: number;
  /** When Trakt last accepted a list/write-back (max SentAt across the outbox). */
  lastListSyncAt: string | null;
  /** Most recent write-back error text. Free text — kept off the header. */
  outboxLastError: string | null;
  /**
   * Progress toward the next drift retrain, so a screen that asks for ratings can say what they
   * buy. `newRatingsSinceTrain` counts UserRatings whose immutable `FirstRatedAt` is newer than the
   * Active artifact's `TrainedAt` — the same two inputs the nightly reconcile's gate reads.
   */
  retrain: {
    minNewRatings: number;
    newRatingsSinceTrain: number;
    /** False before the first model exists: the next train is the FIRST, not a drift retrain. */
    hasActiveModel: boolean;
    lastTrainedAt: string | null;
  };
  lastFailures: JobFailure[];
  providerHealth: ProviderHealth;
  degradedReason: DegradedReason | null;
  /** The training floor, present only while the account is below it. */
  notEnoughRatings: { usableRatings: number; required: number } | null;
  /**
   * Operator alerts (WS-6 task 6, FD-20 default destination): a provider-wide 401/402/403 breaker
   * (`provider_refused`) or repeated job failures (`job_failures`). Founder tier only; absent or
   * empty for everyone else. Typed here so the contract check holds; rendering is WS-9's.
   */
  alerts?: OperatorAlert[];
}

/** One raised operator alert. `code` is a closed vocabulary; unknown codes must be ignored. */
export interface OperatorAlert {
  code: string;
  since: string;
  raisedAt: string;
  reasonCode: string | null;
  provider: "Trakt" | "Tmdb" | "OpenRouter" | null;
  count: number | null;
  threshold: number | null;
  kinds: { kind: string; count: number }[] | null;
}

export const syncApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
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

