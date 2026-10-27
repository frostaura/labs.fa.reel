import { describe, expect, it } from "vitest";
import { isAwaitingModel, modelReadiness, noModelSentence, pipelineSentence, rateProgress, type RateProgressInput, type ReadinessInput } from "./modelReadiness";

/**
 * Fixtures are payloads the status API sends (SyncStatusContractTests), varied one field at a time:
 * the floor-met wait is the trainer's Degraded park + PipelineFailed + no counts + no failed Train,
 * and `retrain.hasActiveModel` is false until the first model exists.
 */
const NO_MODEL = { hasActiveModel: false };
const FLOOR_MET: ReadinessInput = {
  pipelineStage: "Degraded",
  degradedReason: "PipelineFailed",
  notEnoughRatings: null,
  lastFailures: [],
  retrain: NO_MODEL,
};
const TRAIN_FAILED = [{ kind: "Train" }];

describe("isAwaitingModel", () => {
  it("is the floor-met park, with or without the PipelineFailed reason", () => {
    expect(isAwaitingModel(FLOOR_MET)).toBe(true);
    expect(isAwaitingModel({ ...FLOOR_MET, degradedReason: null })).toBe(true);
  });

  it("is whatever the server's AwaitingFirstModel reason says, on any stage", () => {
    expect(isAwaitingModel({ ...FLOOR_MET, degradedReason: "AwaitingFirstModel", lastFailures: TRAIN_FAILED })).toBe(true);
    expect(isAwaitingModel({ pipelineStage: "FeedReady", degradedReason: "AwaitingFirstModel" })).toBe(true);
  });

  it("is not a wait when a single field says otherwise", () => {
    expect(isAwaitingModel({ ...FLOOR_MET, lastFailures: TRAIN_FAILED })).toBe(false);
    expect(isAwaitingModel({ ...FLOOR_MET, notEnoughRatings: { usableRatings: 9, required: 10 } })).toBe(false);
    expect(isAwaitingModel({ ...FLOOR_MET, pipelineStage: "Training" })).toBe(false);
    expect(isAwaitingModel({ ...FLOOR_MET, degradedReason: "ProviderDown" })).toBe(false);
    expect(isAwaitingModel({ ...FLOOR_MET, degradedReason: "SomethingNewer" })).toBe(false);
  });

  it("an unrelated failed job does not make the wait a failure", () => {
    expect(isAwaitingModel({ ...FLOOR_MET, lastFailures: [{ kind: "DeltaSync" }] })).toBe(true);
  });
});

describe("modelReadiness", () => {
  it("is null before the status loads, so a page says nothing it cannot know", () => {
    expect(modelReadiness(undefined)).toBeNull();
    expect(noModelSentence(modelReadiness(null))).toBeNull();
  });

  it("an account with a model is ready whatever else is parked", () => {
    expect(modelReadiness({ ...FLOOR_MET, retrain: { hasActiveModel: true } })).toEqual({ kind: "ready" });
    expect(modelReadiness({ pipelineStage: "FeedReady", retrain: { hasActiveModel: true } })).toEqual({ kind: "ready" });
  });

  it("an older payload with no retrain block trusts a ready stage", () => {
    expect(modelReadiness({ pipelineStage: "FeedReady" })).toEqual({ kind: "ready" });
  });

  it("reads each no-model state from the field that decides it", () => {
    expect(modelReadiness(FLOOR_MET)).toEqual({ kind: "awaiting" });
    expect(modelReadiness({ ...FLOOR_MET, degradedReason: "AwaitingFirstModel" })).toEqual({ kind: "awaiting" });
    expect(modelReadiness({ ...FLOOR_MET, lastFailures: TRAIN_FAILED })).toEqual({ kind: "stopped" });
    expect(modelReadiness({ ...FLOOR_MET, degradedReason: "TraktRevoked" })).toEqual({ kind: "reconnect" });
    expect(modelReadiness({ ...FLOOR_MET, degradedReason: "ProviderDown", pipelineStage: "Extracting" })).toEqual({ kind: "stopped" });
    expect(modelReadiness({ ...FLOOR_MET, pipelineStage: "Ingesting", degradedReason: null })).toEqual({ kind: "building" });
    expect(modelReadiness({ ...FLOOR_MET, pipelineStage: "FeedReady", degradedReason: null })).toEqual({ kind: "awaiting" });
  });

  it("below the floor carries the counts; a revoked link still wins over them", () => {
    const floor = { usableRatings: 4, required: 10 };
    expect(modelReadiness({ ...FLOOR_MET, pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: floor })).toEqual({
      kind: "needsRatings",
      floor,
    });
    expect(modelReadiness({ ...FLOOR_MET, pipelineStage: "NotEnoughRatings", degradedReason: "TraktRevoked", notEnoughRatings: floor })).toEqual({
      kind: "reconnect",
    });
  });

  it("a state a newer build wrote raises nothing", () => {
    expect(modelReadiness({ ...FLOOR_MET, degradedReason: "UnrecognisedState" })).toEqual({ kind: "ready" });
  });
});

describe("noModelSentence", () => {
  it("says the wait and its outer bound, never a failure", () => {
    const text = noModelSentence({ kind: "awaiting" })!;
    expect(text).toBe("You've rated enough — Reel builds your first model in tonight's refresh at the latest.");
    expect(text).not.toMatch(/went wrong|stopped|failed/i);
  });

  it("gives the counts below the floor", () => {
    expect(noModelSentence({ kind: "needsRatings", floor: { usableRatings: 4, required: 10 } })).toBe(
      "Reel hasn't built your model yet. 4 of 10 ratings — rate 6 more and Reel builds your model."
    );
  });

  it("has a sentence for every other state and none for ready", () => {
    expect(noModelSentence({ kind: "ready" })).toBeNull();
    for (const kind of ["building", "reconnect", "stopped"] as const) {
      expect(noModelSentence({ kind })).toMatch(/\w/);
      expect(noModelSentence({ kind })).not.toMatch(/something went wrong/i);
    }
  });
});

describe("pipelineSentence", () => {
  it("never shows the raw Degraded to an account that is only waiting", () => {
    expect(pipelineSentence(FLOOR_MET)).toBe("You've rated enough — Reel builds your first model in tonight's refresh at the latest.");
    expect(pipelineSentence({ ...FLOOR_MET, retrain: { hasActiveModel: true } })).toBe(
      "Your picks are ready. Reel rebuilds your model in tonight's refresh at the latest."
    );
  });

  it("names the real stops and the ordinary stages in sentences", () => {
    expect(pipelineSentence({ ...FLOOR_MET, lastFailures: TRAIN_FAILED })).toBe(
      "Building your picks stopped on Reel's side. Nothing you've rated is lost."
    );
    expect(pipelineSentence({ ...FLOOR_MET, degradedReason: "TraktRevoked" })).toMatch(/lost access to your Trakt account/);
    expect(pipelineSentence({ pipelineStage: "NotEnoughRatings", notEnoughRatings: { usableRatings: 4, required: 10 } })).toBe(
      "4 of 10 ratings — rate 6 more and Reel builds your model."
    );
    expect(pipelineSentence({ pipelineStage: "FeedReady" })).toBe("Your picks are ready.");
    expect(pipelineSentence({ pipelineStage: "Training" })).toBe("Reel is training your model.");
    expect(pipelineSentence(undefined)).toBeNull();
  });

  it("never prints an enum, even for a stage this client has never heard of (KILL-11)", () => {
    for (const stage of ["SomeNewStage", "FeedReady", "Degraded", "Ingesting", "Extracting", "Evaluated"]) {
      expect(pipelineSentence({ pipelineStage: stage })).not.toMatch(/[a-z][A-Z]|^[A-Z][a-z]+[A-Z]/);
    }
    expect(pipelineSentence({ pipelineStage: "SomeNewStage" })).toBe("Reel is working on your picks.");
  });
});

describe("rateProgress (R1-43)", () => {
  // A modelless account below the trainer's floor, as the status API sends it: the cold-start park
  // with the floor's counts. Varied one field at a time below.
  const GATE = { minNewRatings: 10, newRatingsSinceTrain: 0, hasActiveModel: false };
  const BELOW: RateProgressInput = {
    pipelineStage: "NotEnoughRatings",
    degradedReason: null,
    notEnoughRatings: { usableRatings: 4, required: 10 },
    lastFailures: [],
    retrain: GATE,
  };

  it("below the floor it counts what the first model still needs, and promises no night", () => {
    const line = rateProgress(BELOW, 0);
    expect(line).toBe("4 of 10 ratings — rate 6 more and Reel can build your first model.");
    expect(line).not.toMatch(/tonight/);
  });

  it("counts the ratings saved on this screen toward the floor", () => {
    expect(rateProgress(BELOW, 5)).toBe("9 of 10 ratings — rate one more and Reel can build your first model.");
    expect(rateProgress({ ...BELOW, notEnoughRatings: { usableRatings: 0, required: 10 } }, 1)).toBe(
      "1 of 10 ratings — rate 9 more and Reel can build your first model."
    );
  });

  it("promises tonight's refresh only once the floor is met — by the server's count or this screen's", () => {
    expect(rateProgress(BELOW, 6)).toBe("That's enough — Reel builds your first model in tonight's refresh at the latest.");
    expect(rateProgress(BELOW, 9)).toBe("That's enough — Reel builds your first model in tonight's refresh at the latest.");
    // Floor met before the screen opened: the trainer's Degraded park with no floor, no failed Train.
    expect(rateProgress({ ...BELOW, pipelineStage: "Degraded", degradedReason: "PipelineFailed", notEnoughRatings: null }, 0)).toBe(
      "You've rated enough — Reel builds your first model in tonight's refresh at the latest."
    );
  });

  it("a below-floor stage without the counts asks for more ratings and names no number it does not have", () => {
    expect(rateProgress({ ...BELOW, notEnoughRatings: null }, 3)).toBe(
      "Reel needs a few more of your ratings before it can build your first model — keep rating."
    );
  });

  it("a stopped or unlinked account is told so, not promised a model", () => {
    const failed = { ...BELOW, pipelineStage: "Degraded", degradedReason: "PipelineFailed", notEnoughRatings: null, lastFailures: [{ kind: "Train" }] };
    expect(rateProgress(failed, 0)).toBe("Building your model stopped on Reel's side. Nothing you've rated is lost.");
    expect(rateProgress({ ...BELOW, degradedReason: "TraktRevoked" }, 0)).toMatch(/^Reel has lost access to your Trakt account/);
    expect(rateProgress({ ...BELOW, pipelineStage: "Ingesting", notEnoughRatings: null }, 0)).toBe("Reel is building your first model right now.");
  });

  it("with a model it is the retrain gate's countdown, moved by this screen's ratings", () => {
    const withModel = { ...BELOW, pipelineStage: "FeedReady", notEnoughRatings: null, retrain: { minNewRatings: 10, newRatingsSinceTrain: 3, hasActiveModel: true } };
    expect(rateProgress(withModel, 0)).toBe("7 more ratings and Reel retrains tonight.");
    expect(rateProgress(withModel, 6)).toBe("1 more rating and Reel retrains tonight.");
    expect(rateProgress(withModel, 7)).toBe("That's enough — Reel retrains tonight on 10 new ratings.");
  });

  it("says nothing without the status or its retrain block", () => {
    expect(rateProgress(undefined, 2)).toBeNull();
    expect(rateProgress({ ...BELOW, retrain: null }, 2)).toBeNull();
  });
});
