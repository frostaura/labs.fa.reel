import { describe, expect, it } from "vitest";
import {
  PIPELINE_DEGRADED_FALLBACK,
  PIPELINE_STAGE_FALLBACK,
  isBackgroundKind,
  jobKindToSseKind,
  pipelineStageCopy,
  syncExplainer,
} from "./syncCopy";

describe("syncExplainer", () => {
  it("maps known job kinds to their explainers", () => {
    expect(syncExplainer("hydrate").title).toBe("Enriching your library");
    expect(syncExplainer("delta").title).toBe("Syncing with Trakt");
    expect(syncExplainer("reconcile").title).toBe("Nightly reconcile");
    expect(syncExplainer("train").title).toBe("Training your model");
    expect(syncExplainer("feed").title).toBe("Building tonight's picks");
  });

  it("falls back generically for unknown or missing kinds (backend-first jobs are safe)", () => {
    expect(syncExplainer("embeddings-v2").title).toBe("Background sync");
    expect(syncExplainer(null).title).toBe("Background sync");
    expect(syncExplainer(undefined).title).toBe("Background sync");
  });

  it("every explainer has at least one line", () => {
    for (const kind of ["ingest", "hydrate", "delta", "reconcile", "train", "feed", "unknown"]) {
      expect(syncExplainer(kind).lines.length).toBeGreaterThan(0);
    }
  });

  it("maps backend JobKinds to the SSE kind vocabulary (reload-seed path)", () => {
    expect(jobKindToSseKind("HydrateCatalog")).toBe("hydrate");
    expect(jobKindToSseKind("FullIngest")).toBe("ingest");
    expect(jobKindToSseKind("DeltaSync")).toBe("delta");
    expect(jobKindToSseKind("EnrichCatalog")).toBe("enrich");
    expect(jobKindToSseKind("Train")).toBe("train");
    expect(jobKindToSseKind("Evaluate")).toBe("evaluate");
    expect(jobKindToSseKind("SomethingNew")).toBeNull();
    expect(jobKindToSseKind(null)).toBeNull();
  });

  it("seeded kinds round-trip into real explainers", () => {
    expect(syncExplainer(jobKindToSseKind("HydrateCatalog")).title).toBe("Enriching your library");
    expect(syncExplainer(jobKindToSseKind("FullIngest")).title).toBe("First sync with Trakt");
    expect(syncExplainer(jobKindToSseKind("EnrichCatalog")).title).toBe("Reading every title");
  });

  it("classifies background polls so they cannot hijack the pill from a primary job", () => {
    expect(isBackgroundKind("delta")).toBe(true);
    expect(isBackgroundKind("reconcile")).toBe(true);
    expect(isBackgroundKind("enrich")).toBe(false);
    expect(isBackgroundKind("ingest")).toBe(false);
    expect(isBackgroundKind("train")).toBe(false);
    expect(isBackgroundKind(null)).toBe(false);
  });
});

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
