/**
 * Whether the account has a model yet, read off `GET /api/sync/status` — the one place the
 * "waiting for the first model" state is decided, so onboarding, Taste DNA, Track record and the
 * Settings pipeline line can never disagree about it (R1-57, CR-6).
 *
 * The status contract (store/api/sync.ts, Application/Pipeline/SyncStatusContract.cs):
 *  - the server may send `degradedReason: "AwaitingFirstModel"` — the reason CR-6 asks WS-6 to
 *    append for "the floor is met and no Train has run since the park". When it does, it decides;
 *  - until it does, the same state arrives as the trainer's stored `Degraded` park with reason
 *    `PipelineFailed` (or none), no `notEnoughRatings`, and no failed `Train` in `lastFailures`.
 *    Only the training step writes Degraded, so with no failed Train that park can only mean "too
 *    few ratings when it last ran, enough now" — a wait, never a failure;
 *  - `retrain.hasActiveModel` says whether any model exists, which decides between "your first
 *    model" and "your model's next build".
 *
 * Every field past `pipelineStage` is optional: an older payload that lacks them reads as the
 * stage says, and an unknown reason never reads as a wait.
 */

/** The trainer's floor as the status API reports it (`notEnoughRatings`). */
export interface RatingsFloor {
  usableRatings: number;
  required: number;
}

/** One entry of the status API's `lastFailures` — only the job kind is read here. */
export interface StatusFailure {
  kind: string;
}

/** The status fields readiness is decided from. */
export interface ReadinessInput {
  pipelineStage: string;
  degradedReason?: string | null;
  notEnoughRatings?: RatingsFloor | null;
  lastFailures?: StatusFailure[] | null;
  retrain?: { hasActiveModel: boolean } | null;
}

/** The reason code CR-6 asks the server to send for the floor-met wait. */
export const AWAITING_FIRST_MODEL = "AwaitingFirstModel";

/** The reason for a stored value only a newer build can read; it raises no issue of its own. */
const UNRECOGNISED_STATE = "UnrecognisedState";

/** Reasons whose way forward is signing in with Trakt again. */
export const RECONNECT_REASONS: ReadonlySet<string> = new Set(["TraktRevoked", "TraktRefreshFailed"]);

/** Stages of a pipeline run that is under way. */
const IN_PROGRESS = new Set(["Ingesting", "Extracting", "Training", "Evaluated"]);

/**
 * When the model gets built, said as it is: a new account's first Train is chained by the nightly
 * enrichment pass (rating a title does not queue one), so "tonight" is the honest outer bound.
 */
export const FIRST_MODEL_TIMING = "Reel builds your first model in tonight's refresh at the latest";

/** The same bound for an account that already has a model and is parked until the next build. */
export const NEXT_MODEL_TIMING = "Reel rebuilds your model in tonight's refresh at the latest";

/**
 * The account has rated enough and is waiting for a Train: the server says so outright
 * (`AwaitingFirstModel`), or sends the trainer's Degraded park with no floor, no other reason and no
 * failed Train. A failed Train is a real stop; so is any reason the server ranked above this one.
 */
export function isAwaitingModel(status: ReadinessInput): boolean {
  const reason = status.degradedReason ?? null;
  if (reason === AWAITING_FIRST_MODEL) return true;
  if (status.pipelineStage !== "Degraded" || (status.notEnoughRatings ?? null) !== null) return false;
  if (reason !== null && reason !== "PipelineFailed") return false;
  return !(status.lastFailures ?? []).some((failure) => failure.kind === "Train");
}

export type ModelReadiness =
  /** A model exists (or an older payload says the feed is ready). */
  | { kind: "ready" }
  /** The first pipeline run is under way. */
  | { kind: "building" }
  /** Below the training floor; `floor` carries the counts when the server sent them. */
  | { kind: "needsRatings"; floor: RatingsFloor | null }
  /** Enough ratings, waiting for the Train that builds the model. */
  | { kind: "awaiting" }
  /** The Trakt link must be reconnected before anything can be built. */
  | { kind: "reconnect" }
  /** Building stopped on Reel's side (a failed Train, a refusing provider, an unexplained park). */
  | { kind: "stopped" };

/**
 * What the status says about the account's model, or null before the status has loaded. A page
 * renders nothing extra for null and for `ready`: its own data is the honest account then.
 */
export function modelReadiness(status: ReadinessInput | null | undefined): ModelReadiness | null {
  if (!status) return null;
  const reason = status.degradedReason ?? null;
  const floor = status.notEnoughRatings ?? null;
  const hasModel = status.retrain?.hasActiveModel;

  // A stage or link written by a newer build: nothing is known to be broken and nothing is asked
  // of the user (the contract's rule for this reason), so no page says anything about it.
  if (hasModel === true || reason === UNRECOGNISED_STATE) return { kind: "ready" };
  if (reason !== null && RECONNECT_REASONS.has(reason)) return { kind: "reconnect" };
  if (reason === "NotEnoughRatings" || floor !== null || status.pipelineStage === "NotEnoughRatings") {
    return { kind: "needsRatings", floor };
  }
  if (isAwaitingModel(status)) return { kind: "awaiting" };
  // A refusing provider stalls a run that is nominally under way, so it outranks the stage.
  if (status.pipelineStage === "Degraded" || reason === "ProviderDown") return { kind: "stopped" };
  if (IN_PROGRESS.has(status.pipelineStage)) return { kind: "building" };
  // FeedReady with no `retrain` block is an older payload: trust the stage. FeedReady that says
  // outright there is no model is a park the trainer has not explained — a wait, not a failure.
  return hasModel === false ? { kind: "awaiting" } : { kind: "ready" };
}

/** "4 of 10 ratings — rate a few": what Reel says below the training floor. */
export function ratingsNeededCopy(floor: RatingsFloor | null): string {
  if (!floor) return "Reel needs a few more of your ratings before it can build your model — rate a few.";
  const { usableRatings: have, required: need } = floor;
  const short = Math.max(0, need - have);
  if (short === 0) return `${have} of ${need} ratings — that's enough for Reel to build your model.`;
  return `${have} of ${need} ratings — rate ${short === 1 ? "one more" : `${short} more`} and Reel builds your model.`;
}

/**
 * One sentence on why there is no model behind a page yet, or null when there is one (or the
 * status has not loaded). Never "Something went wrong": each state says what is true and what, if
 * anything, the user can do.
 */
export function noModelSentence(readiness: ModelReadiness | null): string | null {
  if (!readiness) return null;
  switch (readiness.kind) {
    case "ready":
      return null;
    case "building":
      return "Reel is building your first model right now.";
    case "needsRatings":
      return `Reel hasn't built your model yet. ${ratingsNeededCopy(readiness.floor)}`;
    case "awaiting":
      return `You've rated enough — ${FIRST_MODEL_TIMING}.`;
    case "reconnect":
      return "Reel has lost access to your Trakt account, so it can't build your model until you reconnect Trakt.";
    case "stopped":
      return "Building your model stopped on Reel's side. Nothing you've rated is lost.";
  }
}

/**
 * The Settings status line: what Reel is doing with the account, as a sentence (KILL-11, H-11). It
 * used to read "Pipeline: FeedReady" — an enum where a sentence belongs — and an unknown stage fell
 * through to its raw name. Unknown stages now get a plain, true sentence instead.
 */
export function pipelineSentence(status: ReadinessInput | null | undefined): string | null {
  if (!status) return null;
  if (status.degradedReason === UNRECOGNISED_STATE) {
    return "This status comes from a newer version of Reel — reload the page to read it.";
  }
  const readiness = modelReadiness(status);
  if (readiness?.kind === "needsRatings") return ratingsNeededCopy(readiness.floor);
  if (isAwaitingModel(status)) {
    return status.retrain?.hasActiveModel
      ? `Your picks are ready. ${NEXT_MODEL_TIMING}.`
      : `You've rated enough — ${FIRST_MODEL_TIMING}.`;
  }
  switch (status.pipelineStage) {
    case "Ingesting":
      return "Reel is importing your Trakt history.";
    case "Extracting":
      return "Reel is reading your taste from what you've watched and rated.";
    case "Training":
    case "Evaluated":
      return "Reel is training your model.";
    case "FeedReady":
      return "Your picks are ready.";
    case "Degraded":
      return readiness?.kind === "reconnect"
        ? "Reel has lost access to your Trakt account — reconnect it below to carry on."
        : "Building your picks stopped on Reel's side. Nothing you've rated is lost.";
    default:
      return "Reel is working on your picks.";
  }
}

/** The status fields the Rate screen's payoff line reads: readiness plus the retrain gate's counts. */
export interface RateProgressInput extends ReadinessInput {
  retrain?: { hasActiveModel: boolean; minNewRatings: number; newRatingsSinceTrain: number } | null;
}

/**
 * The Rate screen's payoff line: what the ratings saved here buy, or null when the status cannot
 * say (not loaded, or an older payload with no `retrain` block) — no line beats a made-up one.
 *
 * `saved` is the ratings confirmed on this screen since the status was read. The status query is not
 * invalidated by a rating, so they are counted on top of the server's numbers; each is a title the
 * account had not rated, so it is one more row for the trainer's floor and one more new rating for
 * the retrain gate.
 *
 * With a model, it is the retrain gate's countdown. Without one it used to promise "Reel trains your
 * first model tonight" from the first rating — but the trainer refuses below its floor (R1-43), so
 * while `notEnoughRatings` holds the line says how many more the first model needs, and "tonight"
 * is promised only once the floor is met.
 */
export function rateProgress(status: RateProgressInput | null | undefined, saved: number): string | null {
  const retrain = status?.retrain;
  if (!status || !retrain) return null;

  if (retrain.hasActiveModel) {
    const sinceTrain = retrain.newRatingsSinceTrain + saved;
    const toGo = Math.max(0, retrain.minNewRatings - sinceTrain);
    if (toGo === 0) return `That's enough — Reel retrains tonight on ${sinceTrain} new ${sinceTrain === 1 ? "rating" : "ratings"}.`;
    return `${toGo} more ${toGo === 1 ? "rating" : "ratings"} and Reel retrains tonight.`;
  }

  const readiness = modelReadiness(status);
  if (!readiness || readiness.kind === "ready") return null;
  if (readiness.kind !== "needsRatings") return noModelSentence(readiness);
  if (!readiness.floor) {
    return "Reel needs a few more of your ratings before it can build your first model — keep rating.";
  }
  const have = readiness.floor.usableRatings + saved;
  const short = readiness.floor.required - have;
  if (short <= 0) return `That's enough — ${FIRST_MODEL_TIMING}.`;
  return `${have} of ${readiness.floor.required} ratings — rate ${short === 1 ? "one more" : `${short} more`} and Reel can build your first model.`;
}
