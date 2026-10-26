import { describe, expect, it } from "vitest";
import { isAwaitingModel, modelReadiness, noModelSentence, pipelineLabel, type ReadinessInput } from "./modelReadiness";

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

describe("pipelineLabel", () => {
  it("never shows the raw Degraded to an account that is only waiting", () => {
    expect(pipelineLabel(FLOOR_MET)).toBe("waiting for your first model — tonight's refresh at the latest");
    expect(pipelineLabel({ ...FLOOR_MET, retrain: { hasActiveModel: true } })).toBe(
      "waiting for the next model build — tonight's refresh at the latest"
    );
  });

  it("names the real stops and the ordinary stages in words", () => {
    expect(pipelineLabel({ ...FLOOR_MET, lastFailures: TRAIN_FAILED })).toBe("stopped");
    expect(pipelineLabel({ ...FLOOR_MET, degradedReason: "TraktRevoked" })).toBe("stopped — Trakt needs reconnecting");
    expect(pipelineLabel({ pipelineStage: "NotEnoughRatings", notEnoughRatings: { usableRatings: 4, required: 10 } })).toBe(
      "needs more ratings (4 of 10)"
    );
    expect(pipelineLabel({ pipelineStage: "FeedReady" })).toBe("ready");
    expect(pipelineLabel({ pipelineStage: "Training" })).toBe("training your model");
    expect(pipelineLabel({ pipelineStage: "SomeNewStage" })).toBe("SomeNewStage");
    expect(pipelineLabel(undefined)).toBeNull();
  });
});
