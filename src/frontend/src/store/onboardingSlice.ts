import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

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
export type ShowStage = "connecting" | "ingesting" | "profiling" | "fitting" | "reveal" | "needsRatings" | "failed";

/**
 * `needsRatings` is not a failure and is not on the forward path either: it is the trainer saying
 * "too few ratings to fit a model" (fewer than its floor of usable movie and show ratings). It
 * applies from any stage, like `failed`, and only the authoritative poll moves the show off it —
 * once the account has rated enough, the next train runs and the poll reports the stage it reached.
 */
const STAGE_ORDER: Record<ShowStage, number> = {
  connecting: 0,
  ingesting: 1,
  profiling: 2,
  fitting: 3,
  reveal: 4,
  needsRatings: 98,
  failed: 99,
};

/** The trainer's floor as the status API reports it (`notEnoughRatings`). */
export interface RatingsFloor {
  usableRatings: number;
  required: number;
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
};

const MAX_INSIGHTS = 50;

function advance(state: OnboardingState, target: ShowStage) {
  if (target === "failed" || target === "needsRatings" || STAGE_ORDER[target] > STAGE_ORDER[state.stage]) {
    state.stage = target;
  }
}

/** The status API's reason code for "below the training floor". */
const NOT_ENOUGH_RATINGS = "NotEnoughRatings";

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
     * carries the v2 status fields that explain a stop: `degradedReason` and, below the training
     * floor, `notEnoughRatings`. Both are optional — a backend that predates them still advances.
     */
    pollAdvance(
      state,
      action: PayloadAction<{
        pipelineStage: string;
        degradedReason?: string | null;
        notEnoughRatings?: RatingsFloor | null;
      }>
    ) {
      const { pipelineStage, degradedReason, notEnoughRatings } = action.payload;
      state.degradedReason = degradedReason ?? null;
      state.ratingsFloor = notEnoughRatings ?? null;
      const mapped =
        degradedReason === NOT_ENOUGH_RATINGS || notEnoughRatings ? "needsRatings" : stageFromPipeline(pipelineStage);
      if (!mapped) return;
      // The poll is authoritative, so it is also the one thing that can lift the show off
      // `needsRatings` once enough ratings exist — but only forward, never into another stop.
      if (state.stage === "needsRatings" && mapped !== "needsRatings" && mapped !== "failed") {
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
  },
});

export const { eventReceived, pollAdvance, markStalled, reset } = onboardingSlice.actions;
export default onboardingSlice.reducer;

/** "4 of 10 ratings — rate a few": what the show says below the training floor. */
export function ratingsNeededCopy(floor: RatingsFloor | null): string {
  if (!floor) return "Reel needs a few more of your ratings before it can build your model — rate a few.";
  const { usableRatings: have, required: need } = floor;
  const short = Math.max(0, need - have);
  return `${have} of ${need} ratings — rate ${short === 1 ? "one more" : `${short} more`} and Reel builds your model.`;
}

/**
 * Why the show stopped, by the status API's `degradedReason` code. A free-text `failed` event
 * wins when there is one; this is what renders instead of the old blank "Something went wrong.".
 * Unknown codes get the generic line, so a reason appended on the server ships safely ahead of this.
 */
export function failureCopy(degradedReason: string | null, error: string | null): string {
  if (error && error.trim().length > 0) return error.trim();
  switch (degradedReason) {
    case "TraktRevoked":
      return "Reel no longer has access to your Trakt account. Reconnect Trakt to pick up where you left off.";
    case "TraktRefreshFailed":
      return "Reel couldn't renew its access to your Trakt account. Reconnect Trakt to pick up where you left off.";
    case "TraktWritesDeadLettered":
      return "Reel couldn't write your changes back to Trakt after several tries.";
    case "ProviderDown":
      return "The model service Reel uses is refusing requests, so your profile can't be built right now. Nothing is lost — it resumes when the service is back.";
    default:
      return "Building your profile stopped on Reel's side. Nothing is lost — try again.";
  }
}

/** Whether the way forward from this failure is signing in with Trakt again. */
export function needsReconnect(degradedReason: string | null): boolean {
  return degradedReason === "TraktRevoked" || degradedReason === "TraktRefreshFailed";
}
