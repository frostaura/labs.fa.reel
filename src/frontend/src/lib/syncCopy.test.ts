import { describe, expect, it } from "vitest";
import {
  PIPELINE_DEGRADED_FALLBACK,
  PIPELINE_STAGE_FALLBACK,
  isBackgroundKind,
  jobKindToSseKind,
  pipelineStageCopy,
  syncExplainer,
  syncSummaryCopy,
  type SyncSummaryPayload,
} from "./syncCopy";

describe("syncExplainer", () => {
  it("maps known job kinds to their explainers", () => {
    expect(syncExplainer("hydrate").title).toBe("Enriching your library");
    expect(syncExplainer("delta").title).toBe("Syncing with Trakt");
    expect(syncExplainer("reconcile").title).toBe("Nightly reconcile");
    expect(syncExplainer("train").title).toBe("Training your model");
    expect(syncExplainer("feed").title).toBe("Building tonight's picks");
    expect(syncExplainer("xref").title).toBe("Cross-referencing against your history");
    expect(syncExplainer("narrative").title).toBe("Writing your taste profile");
    expect(syncExplainer("lens").title).toBe("Writing tonight's notes");
  });

  it("falls back generically for unknown or missing kinds (backend-first jobs are safe)", () => {
    expect(syncExplainer("embeddings-v2").title).toBe("Background sync");
    expect(syncExplainer(null).title).toBe("Background sync");
    expect(syncExplainer(undefined).title).toBe("Background sync");
  });

  it("every explainer has at least one line", () => {
    for (const kind of ["ingest", "hydrate", "delta", "reconcile", "train", "feed", "xref", "narrative", "lens", "unknown"]) {
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
    expect(jobKindToSseKind("CrossReference")).toBe("xref");
    // Both of these used to fall through to "Background sync" while the user watched them run.
    expect(jobKindToSseKind("BuildTasteNarrative")).toBe("narrative");
    expect(jobKindToSseKind("PersonaliseTitles")).toBe("lens");
    expect(jobKindToSseKind("SomethingNew")).toBeNull();
    expect(jobKindToSseKind(null)).toBeNull();
  });

  it("seeded kinds round-trip into real explainers", () => {
    expect(syncExplainer(jobKindToSseKind("HydrateCatalog")).title).toBe("Enriching your library");
    expect(syncExplainer(jobKindToSseKind("FullIngest")).title).toBe("First sync with Trakt");
    expect(syncExplainer(jobKindToSseKind("EnrichCatalog")).title).toBe("Reading every title");
    expect(syncExplainer(jobKindToSseKind("CrossReference")).title).toBe(
      "Cross-referencing against your history"
    );
    // The regression this pair exists to prevent: a real job rendering as the generic fallback.
    for (const kind of ["BuildTasteNarrative", "PersonaliseTitles", "CrossReference"]) {
      expect(syncExplainer(jobKindToSseKind(kind)).title).not.toBe("Background sync");
    }
  });

  it("classifies background polls so they cannot hijack the pill from a primary job", () => {
    expect(isBackgroundKind("delta")).toBe(true);
    expect(isBackgroundKind("reconcile")).toBe(true);
    expect(isBackgroundKind("enrich")).toBe(false);
    expect(isBackgroundKind("ingest")).toBe(false);
    expect(isBackgroundKind("train")).toBe(false);
    // A ten-minute pass owns the pill for its whole run; a delta poll must not take it away.
    expect(isBackgroundKind("xref")).toBe(false);
    expect(isBackgroundKind(null)).toBe(false);
  });
});

/**
 * The post-sync sentence. Mirrors `SyncSummaryCopy` in
 * `src/backend/FrostAura.Reel.Application/Sync/SyncSummary.cs`, whose own tests assert these same
 * examples — the header pill has two sources (this SSE payload and the 5s poll of the persisted
 * ProgressMessage) and they must not disagree.
 *
 * The numbers are the fix, not the wording: the count the sync used to have in hand was every
 * rating row walked from Trakt on a full paginated refetch, so this would have read "Pulled 3,412
 * new ratings" after every single sync, forever.
 */
describe("syncSummaryCopy", () => {
  const summary = (overrides: Partial<SyncSummaryPayload> = {}): SyncSummaryPayload => ({
    ratingsAdded: 0,
    ratingsChanged: 0,
    watchedAdded: 0,
    retrainQueued: false,
    retrainAt: null,
    picksChanged: null,
    ...overrides,
  });

  it("renders the three canonical sentences", () => {
    expect(syncSummaryCopy(summary({ ratingsAdded: 12, picksChanged: 3 }))).toBe(
      "Pulled 12 new ratings from Trakt — 3 picks changed"
    );
    expect(
      syncSummaryCopy(summary({ ratingsAdded: 12, retrainQueued: true, retrainAt: "2026-09-10T03:00:00Z" }))
    ).toBe("Pulled 12 new ratings from Trakt — retraining tonight at 03:00 UTC");
    expect(syncSummaryCopy(summary())).toBe("Already up to date");
  });

  it("keeps the retrain hour in UTC whatever the browser's timezone is", () => {
    // A browser in Johannesburg rendering "05:00 UTC" for an 03:00 job is a lie the local timezone
    // would tell for free.
    expect(
      syncSummaryCopy(summary({ ratingsAdded: 1, retrainQueued: true, retrainAt: "2026-09-10T03:00:00+00:00" }))
    ).toBe("Pulled 1 new rating from Trakt — retraining tonight at 03:00 UTC");
  });

  it("says the model is still moving even when the library is not", () => {
    expect(syncSummaryCopy(summary({ retrainQueued: true, retrainAt: "2026-09-10T03:00:00Z" }))).toBe(
      "Already up to date — retraining tonight at 03:00 UTC"
    );
  });

  it("distinguishes a re-rate from a new rating", () => {
    expect(syncSummaryCopy(summary({ ratingsChanged: 2 }))).toBe("Updated 2 ratings from Trakt");
  });

  it("falls back to watches when no rating moved", () => {
    expect(syncSummaryCopy(summary({ watchedAdded: 3 }))).toBe("Pulled 3 new watches from Trakt");
    expect(syncSummaryCopy(summary({ watchedAdded: 1 }))).toBe("Pulled 1 new watch from Trakt");
  });

  it("reports an in-flight retrain as now, not tonight", () => {
    expect(syncSummaryCopy(summary({ ratingsAdded: 4, retrainQueued: true }))).toBe(
      "Pulled 4 new ratings from Trakt — retraining now"
    );
  });

  it("prefers a concrete change to the page over a promise about tonight", () => {
    expect(
      syncSummaryCopy(summary({ ratingsAdded: 12, retrainQueued: true, retrainAt: "2026-09-10T03:00:00Z", picksChanged: 3 }))
    ).toBe("Pulled 12 new ratings from Trakt — 3 picks changed");
  });

  it("treats zero picks changed as knowledge, not as a clause", () => {
    expect(syncSummaryCopy(summary({ ratingsAdded: 12, picksChanged: 0 }))).toBe("Pulled 12 new ratings from Trakt");
  });

  it("renders nothing when no sync has reported", () => {
    expect(syncSummaryCopy(null)).toBeNull();
    expect(syncSummaryCopy(undefined)).toBeNull();
  });

  it("degrades to the scheduled hour rather than NaN on a malformed timestamp", () => {
    expect(syncSummaryCopy(summary({ retrainQueued: true, retrainAt: "not-a-date" }))).toBe(
      "Already up to date — retraining tonight at 03:00 UTC"
    );
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
