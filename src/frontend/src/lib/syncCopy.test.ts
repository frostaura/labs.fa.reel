import { describe, expect, it } from "vitest";
import {
  PIPELINE_DEGRADED_FALLBACK,
  PIPELINE_STAGE_FALLBACK,
  pipelineStageCopy,
} from "./syncCopy";

/**
 * Regression for the raw enum leak: Home interpolated `{session?.pipelineStage}` straight into the
 * waiting copy, so the founder's home screen read "Your picks are being assembled (Extracting)".
 * The contract these pin: every stage maps to a second-person verb clause, an unknown stage still
 * says something honest (so a new backend stage ships safely ahead of the frontend), and Degraded
 * — the one stage with a reason worth quoting — prefers the persisted reason over generic copy.
 */
describe("pipelineStageCopy", () => {
  it.each([
    ["Linked", "linking your Trakt"],
    ["Ingesting", "pulling your history"],
    ["Extracting", "reading every title"],
    ["Training", "teaching your model"],
    ["Evaluated", "checking itself"],
    ["FeedReady", "putting tonight's picks together"],
  ])("maps %s to human copy", (stage, expected) => {
    expect(pipelineStageCopy(stage)).toBe(expected);
  });

  it("never returns the enum token itself", () => {
    const stages = ["Linked", "Ingesting", "Extracting", "Training", "Evaluated", "FeedReady", "Degraded"];
    for (const stage of stages) {
      expect(pipelineStageCopy(stage)).not.toContain(stage);
    }
  });

  it("quotes the persisted reason for Degraded", () => {
    expect(pipelineStageCopy("Degraded", "Trakt revoked the token — re-link to carry on")).toBe(
      "Trakt revoked the token — re-link to carry on"
    );
  });

  it("falls back to honest copy when Degraded carries no reason", () => {
    expect(pipelineStageCopy("Degraded")).toBe(PIPELINE_DEGRADED_FALLBACK);
    expect(pipelineStageCopy("Degraded", null)).toBe(PIPELINE_DEGRADED_FALLBACK);
    expect(pipelineStageCopy("Degraded", "   ")).toBe(PIPELINE_DEGRADED_FALLBACK);
  });

  it("ignores a reason on any stage that is not Degraded", () => {
    expect(pipelineStageCopy("Training", "some stale outbox error")).toBe("teaching your model");
  });

  it("falls back for an unknown, absent or empty stage", () => {
    expect(pipelineStageCopy("SomeFutureStage")).toBe(PIPELINE_STAGE_FALLBACK);
    expect(pipelineStageCopy(undefined)).toBe(PIPELINE_STAGE_FALLBACK);
    expect(pipelineStageCopy(null)).toBe(PIPELINE_STAGE_FALLBACK);
    expect(pipelineStageCopy("")).toBe(PIPELINE_STAGE_FALLBACK);
  });
});
