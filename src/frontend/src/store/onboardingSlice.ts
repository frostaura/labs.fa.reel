import { createSlice, type PayloadAction } from "@reduxjs/toolkit";
import {
  FIRST_MODEL_TIMING,
  NEXT_MODEL_TIMING,
  RECONNECT_REASONS,
  isAwaitingModel,
  type ReadinessInput,
  type RatingsFloor,
  type StatusFailure,
} from "../lib/modelReadiness";
import { UNRECOGNISED_STATE_COPY } from "../lib/syncCopy";

export { FIRST_MODEL_TIMING, ratingsNeededCopy } from "../lib/modelReadiness";
export type { RatingsFloor, StatusFailure } from "../lib/modelReadiness";

/**
 * The onboarding live build-up state machine.
 *
 * Fed by `/sse/pipeline` named events (see lib/onboardingStream.ts). Two hard rules keep the
 * show robust against SSE replays and out-of-order delivery:
 *   1. seq-dedupe — every event carries a monotonic per-account `seq`; stale ones are dropped.
 *   2. stage monotonicity — the show only advances (except `failed`, which always applies).
 * The 15s sync/status poll is authoritative: pollAdvance() can move the stage even when SSE
 * stalls, so the show never wedges on a dropped connection.
 */
export type ShowStage =
  | "connecting"
  | "ingesting"
  | "profiling"
  | "fitting"
  | "reveal"
  | "needsRatings"
  | "awaitingModel"
  | "failed";

/**
 * Three stops sit off the forward path.
 *  - `needsRatings`: the trainer's "too few ratings to fit a model" (fewer than its floor of usable
 *    movie and show ratings). Not a failure. Applies from any stage, like `failed`.
 *  - `awaitingModel`: the same park once the account HAS rated enough. The server still stores the
 *    trainer's Degraded until the next Train runs (the nightly enrichment chains one). It reports it
 *    as `AwaitingFirstModel` once CR-6 lands, and as `PipelineFailed` with no floor until then; the
 *    poll reads both as "waiting for the model", not a failure (lib/modelReadiness.ts decides).
 *  - `failed`: a real stop.
 * The authoritative poll owns every stop it set: it moves the show off them in either direction.
 * Stream frames never lift one, so a late forward frame cannot paper over a stop.
 */
const STAGE_ORDER: Record<ShowStage, number> = {
  connecting: 0,
  ingesting: 1,
  profiling: 2,
  fitting: 3,
  reveal: 4,
  awaitingModel: 97,
  needsRatings: 98,
  failed: 99,
};

/** The status poll's fields the show reads. Everything past `pipelineStage` is optional (v2). */
export interface StatusSnapshot extends ReadinessInput {
  lastFailures?: StatusFailure[] | null;
}

export type IngestKind = "movies" | "shows" | "episodes" | "ratings";

export interface Insight {
  id: string;
  kind: "library" | "genre" | "era" | "creator" | "tone" | "stat";
  text: string;
  payload?: Record<string, unknown>;
}

export interface ModelProgress {
  phase: string;
  processed: number;
  total: number;
}

export interface PipelineEvent {
  type: string;
  data: {
    seq: number;
    [key: string]: unknown;
  };
}

export interface OnboardingState {
  stage: ShowStage;
  counters: Partial<Record<IngestKind, number>>;
  insights: Insight[];
  modelProgress: ModelProgress | null;
  traktUser: string | null;
  avatarUrl: string | null;
  lastSeq: number;
  lastEventAt: number | null;
  stalled: boolean;
  error: string | null;
  /** The status API's `degradedReason` (a stable code), when the account is degraded. */
  degradedReason: string | null;
  /** Counts behind `needsRatings`; null when the server did not send them. */
  ratingsFloor: RatingsFloor | null;
  /** The status API's `retrain.hasActiveModel`; null when the server did not send it. */
  hasActiveModel: boolean | null;
}

const initialState: OnboardingState = {
  stage: "connecting",
  counters: {},
  insights: [],
  modelProgress: null,
  traktUser: null,
  avatarUrl: null,
  lastSeq: 0,
  lastEventAt: null,
  stalled: false,
  error: null,
  degradedReason: null,
  ratingsFloor: null,
  hasActiveModel: null,
};

const MAX_INSIGHTS = 50;

function advance(state: OnboardingState, target: ShowStage) {
  if (target === "failed" || target === "needsRatings" || STAGE_ORDER[target] > STAGE_ORDER[state.stage]) {
    state.stage = target;
  }
}

/** The status API's reason code for "below the training floor". */
const NOT_ENOUGH_RATINGS = "NotEnoughRatings";

/** Stops the poll owns, and so may move the show off. */
const POLL_STOPS = new Set<ShowStage>(["needsRatings", "awaitingModel", "failed"]);

/** Backend PipelineStage → show stage, used by both stage-changed events and the status poll. */
export function stageFromPipeline(pipelineStage: string): ShowStage | null {
  switch (pipelineStage) {
    case "Ingesting": return "ingesting";
    case "Extracting": return "profiling";
    case "Training": return "fitting";
    case "Evaluated": return "fitting";
    case "FeedReady": return "reveal";
    case "NotEnoughRatings": return "needsRatings";
    case "Degraded": return "failed";
    default: return null;
  }
}

/**
 * The show stage a status poll stands for. The reason the server ranked first decides, before the
 * stage does:
 *  1. a revoked or failed Trakt link is a failure with a reconnect, even below the floor — the
 *     server computes the floor whatever the link's state, and no rating helps until it is fixed;
 *  2. below the floor (the reason, or counts with no other reason) is `needsRatings`;
 *  3. the floor-met wait — `AwaitingFirstModel`, or the trainer's Degraded park with no floor left
 *     and no failed Train — is `awaitingModel`; a Train that failed recently is a real failure;
 *  4. otherwise the stage maps as usual.
 */
export function stageFromStatus(status: StatusSnapshot): ShowStage | null {
  const reason = status.degradedReason ?? null;
  const floor = status.notEnoughRatings ?? null;
  if (reason !== null && RECONNECT_REASONS.has(reason)) return "failed";
  if (reason === NOT_ENOUGH_RATINGS || (reason === null && floor !== null)) return "needsRatings";
  if (isAwaitingModel(status)) return "awaitingModel";
  return stageFromPipeline(status.pipelineStage);
}

export const onboardingSlice = createSlice({
  name: "onboarding",
  initialState,
  reducers: {
    eventReceived(state, action: PayloadAction<PipelineEvent>) {
      const { type, data } = action.payload;
      const seq = typeof data.seq === "number" ? data.seq : 0;
      if (seq !== 0 && seq <= state.lastSeq) return; // replayed event after reconnect — drop
      if (seq !== 0) state.lastSeq = seq;
      state.lastEventAt = Date.now();
      state.stalled = false;

      switch (type) {
        case "connected":
          state.traktUser = (data.traktUser as string) ?? state.traktUser;
          state.avatarUrl = (data.avatarUrl as string) ?? state.avatarUrl;
          break;
        case "ingest-progress": {
          const kind = data.kind as IngestKind;
          const found = data.found as number;
          if (kind != null && typeof found === "number") {
            state.counters[kind] = Math.max(state.counters[kind] ?? 0, found);
          }
          advance(state, "ingesting");
          break;
        }
        case "insight": {
          advance(state, "profiling");
          const insight: Insight = {
            id: String(data.id ?? seq),
            kind: (data.kind as Insight["kind"]) ?? "stat",
            text: String(data.text ?? ""),
            payload: data.payload as Record<string, unknown> | undefined,
          };
          if (!state.insights.some((i) => i.id === insight.id)) {
            state.insights.push(insight);
            if (state.insights.length > MAX_INSIGHTS) state.insights.shift();
          }
          break;
        }
        case "model-progress":
          advance(state, "fitting");
          state.modelProgress = {
            phase: String(data.phase ?? ""),
            processed: (data.processed as number) ?? 0,
            total: (data.total as number) ?? 0,
          };
          break;
        case "stage-changed": {
          // The trainer parks a too-few-ratings account as Degraded and says why in `reason`; that
          // is the NotEnoughRatings case, not a failure. The counts arrive with the next poll.
          const insufficient = data.reason === "insufficient-ratings";
          const mapped = insufficient ? "needsRatings" : stageFromPipeline(String(data.stage ?? ""));
          if (mapped) advance(state, mapped);
          break;
        }
        case "feed-ready":
          advance(state, "reveal");
          break;
        case "failed":
          state.error = String(data.error ?? "Something went wrong during sync.");
          advance(state, "failed");
          break;
        case "heartbeat":
        default:
          break; // heartbeats only refresh lastEventAt/stalled above
      }
    },
    /**
     * The authoritative sync/status poll can advance the show when SSE is stalled or lost. It also
     * carries the v2 status fields that explain a stop (`degradedReason`, `notEnoughRatings`,
     * `lastFailures`), all optional — a backend that predates them still advances.
     *
     * A stop the poll set (or a stream `Degraded` frame, which carries no error text) belongs to the
     * poll: the next poll replaces it with whatever the server now says, forward or into another
     * stop. A stream `failed` frame — a job that died, with its error — is not something a status
     * poll can see, so only a retry clears it.
     */
    pollAdvance(state, action: PayloadAction<StatusSnapshot>) {
      state.degradedReason = action.payload.degradedReason ?? null;
      state.ratingsFloor = action.payload.notEnoughRatings ?? null;
      state.hasActiveModel = action.payload.retrain?.hasActiveModel ?? null;
      const mapped = stageFromStatus(action.payload);
      if (!mapped) return;
      if (POLL_STOPS.has(state.stage) && state.error === null) {
        state.stage = mapped;
        return;
      }
      advance(state, mapped);
    },
    markStalled(state) {
      state.stalled = true;
    },
    reset() {
      return initialState;
    },
    /**
     * "Try again" was accepted by the server: start the show over, but keep who the user is and the
     * last stream `seq` — a replay of the old `failed` frame must not stop the new run at once.
     */
    retryStarted(state) {
      return { ...initialState, traktUser: state.traktUser, avatarUrl: state.avatarUrl, lastSeq: state.lastSeq };
    },
  },
});

export const { eventReceived, pollAdvance, markStalled, reset, retryStarted } = onboardingSlice.actions;
export default onboardingSlice.reducer;

/** When the parked account's model gets built: its first, or the next build of the one it has. */
export function awaitingModelTiming(hasActiveModel: boolean | null): string {
  return hasActiveModel ? NEXT_MODEL_TIMING : FIRST_MODEL_TIMING;
}

/**
 * Why the show stopped. A known `degradedReason` says it best, so it wins; a stream `failed` frame's
 * free text is the fallback for the generic or missing reason (it is a job runner's sentence, e.g.
 * "DeltaSync failed after 3 attempts.", which would otherwise sit above a Reconnect button that it
 * does not explain). Unknown codes get the generic line, so a reason appended on the server ships
 * safely ahead of this.
 */
export function failureCopy(degradedReason: string | null, error: string | null): string {
  switch (degradedReason) {
    case "TraktRevoked":
      return "Reel no longer has access to your Trakt account. Reconnect Trakt to pick up where you left off.";
    case "TraktRefreshFailed":
      return "Reel couldn't renew its access to your Trakt account. Reconnect Trakt to pick up where you left off.";
    case "TraktWritesDeadLettered":
      return "Reel couldn't write your changes back to Trakt after several tries.";
    case "ProviderDown":
      return "The model service Reel uses is refusing requests, so your profile can't be built right now. Nothing is lost — it resumes when the service is back.";
    case "UnrecognisedState":
      return UNRECOGNISED_STATE_COPY;
    default:
      if (error && error.trim().length > 0) return error.trim();
      return "Building your profile stopped on Reel's side. Nothing is lost — try again.";
  }
}

/** Whether the way forward from this failure is signing in with Trakt again. */
export function needsReconnect(degradedReason: string | null): boolean {
  return degradedReason !== null && RECONNECT_REASONS.has(degradedReason);
}
