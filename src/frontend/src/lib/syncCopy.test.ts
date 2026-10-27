import { describe, expect, it } from "vitest";
import {
  ACTIVE_LABEL_FALLBACK,
  FAILURE_NOUN_FALLBACK,
  PIPELINE_DEGRADED_FALLBACK,
  PURPOSE_FALLBACK,
  PIPELINE_STAGE_FALLBACK,
  REASON_FALLBACK,
  activeJobLabel,
  UNRECOGNISED_STATE_COPY,
  failureAftermath,
  failureNoun,
  isTerminalReason,
  pipelineStageCopy,
  purposeCopy,
  reasonCopy,
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

  it("no longer promises that only a model beating popularity ships", () => {
    // 0 of 101 evaluations beat popularity and every model shipped anyway.
    expect(syncExplainer("evaluate").lines.join(" ")).not.toMatch(/only a model that beats/i);
    // R1-46: the hydrate caption made the same promise ("the baseline your model must beat").
    for (const kind of BACKEND_SSE_KINDS) {
      expect(syncExplainer(kind).lines.join(" "), kind).not.toMatch(/must beat|only .* ships/i);
    }
  });

  /**
   * R1-46 (claims family pipeline-stage-descriptions): each caption sits on the stage whose handler
   * does that work, so the header never describes one job while another runs. The work itself is
   * pinned by the backend tests the claims register names: TitleHydrator writes poster, backdrop,
   * trailer and TMDB popularity for the library's titles; EnrichCatalog writes plot embeddings;
   * Train fits the artifact on the ratings; BuildFeed scores the eligible pool and orders it by
   * the calibrated prediction × freshness with genre-diversity shaping; CrossReference judges the
   * whole eligible pool beside the viewer's nearest rated titles; EvalHarness's primary baseline is
   * popularity.
   */
  it.each([
    ["hydrate", /artwork and trailers for the titles in your library/i],
    ["hydrate", /popularity — the baseline your model is measured against/i],
    ["enrich", /plot embeddings/i],
    ["train", /fitting a fresh model on your ratings/i],
    ["feed", /scoring eligible titles with your model/i],
    ["feed", /ranking by predicted rating, freshness and variety/i],
    ["xref", /every eligible title against the ones you've actually rated/i],
  ])("the %s stage, and only it, says %s", (kind, caption) => {
    expect(syncExplainer(kind).lines.some((line) => caption.test(line))).toBe(true);
    for (const other of BACKEND_SSE_KINDS.filter((k) => k !== kind)) {
      expect(syncExplainer(other).lines.some((line) => caption.test(line)), other).toBe(false);
    }
  });

  it("claims the whole catalogue for no library stage", () => {
    expect(syncExplainer("hydrate").lines.join(" ")).not.toMatch(/every title/i);
  });
});

/**
 * Every SSE kind the backend's `JobVisibilityPolicy.SseKind` can emit. A kind missing from the copy
 * tables would put the generic fallback on the header while the user watched a real job.
 */
const BACKEND_SSE_KINDS = [
  "ingest", "delta", "reconcile", "hydrate", "enrich", "train", "evaluate", "feed",
  "availability", "digest", "narrative", "lens", "seed", "xref",
];

/** Every code the backend's `JobFailureReason.Classify` can return. */
const BACKEND_REASON_CODES = [
  "stalled", "payment_required", "unauthorized", "forbidden", "rate_limited", "no_answer",
  "budget_spent", "timed_out", "error",
];

describe("header pill copy", () => {
  it.each(BACKEND_SSE_KINDS)("has fixed copy for the %s kind", (kind) => {
    expect(activeJobLabel(kind)).not.toBe(ACTIVE_LABEL_FALLBACK);
    expect(failureNoun(kind)).not.toBe(FAILURE_NOUN_FALLBACK);
    expect(syncExplainer(kind).title).not.toBe("Background sync");
  });

  it("names the cross-reference pass with no denominator", () => {
    expect(activeJobLabel("xref")).toBe("Cross-referencing · first pass");
    expect(activeJobLabel("xref")).not.toMatch(/\d/);
  });

  it("falls back for an unknown or missing kind", () => {
    expect(activeJobLabel("embeddings-v2")).toBe(ACTIVE_LABEL_FALLBACK);
    expect(activeJobLabel(null)).toBe(ACTIVE_LABEL_FALLBACK);
    expect(failureNoun(undefined)).toBe(FAILURE_NOUN_FALLBACK);
  });

  it.each(BACKEND_REASON_CODES)("has a sentence for the %s reason code", (code) => {
    expect(reasonCopy(code)).toMatch(/\.$/);
  });

  it("maps an unknown reason code to the generic line, never to the code itself", () => {
    expect(reasonCopy("quota_exhausted_v2")).toBe(REASON_FALLBACK);
    expect(reasonCopy("quota_exhausted_v2")).not.toContain("quota");
    expect(reasonCopy(null)).toBe(REASON_FALLBACK);
  });

  it("treats only the credential and payment refusals as terminal", () => {
    expect(BACKEND_REASON_CODES.filter(isTerminalReason)).toEqual(["payment_required", "unauthorized", "forbidden"]);
    expect(isTerminalReason(null)).toBe(false);
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
 * is keyed on its stable reason code — never free text, never the code.
 */
describe("pipelineStageCopy", () => {
  it.each([
    ["NotEnoughRatings", "waiting for a few more ratings"],
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
    const stages = ["Linked", "Ingesting", "Extracting", "Training", "Evaluated", "FeedReady", "Degraded", "NotEnoughRatings"];
    for (const stage of stages) {
      expect(pipelineStageCopy(stage)).not.toContain(stage);
    }
  });

  it.each([
    ["TraktRevoked", "your Trakt link needs reconnecting"],
    ["TraktRefreshFailed", "your Trakt link needs reconnecting"],
    ["TraktWritesDeadLettered", "some changes couldn't reach Trakt"],
    ["NotEnoughRatings", "waiting for a few more ratings"],
    ["ProviderDown", "the model provider is down"],
    ["PipelineFailed", "a step failed and stopped your picks updating"],
  ])("keys Degraded on its reason code: %s", (reason, expected) => {
    expect(pipelineStageCopy("Degraded", reason)).toBe(expected);
  });

  it("names the Trakt link only when the reason is the Trakt link", () => {
    for (const reason of ["TraktWritesDeadLettered", "ProviderDown", "PipelineFailed", null, "SomeFutureReason"]) {
      expect(pipelineStageCopy("Degraded", reason)).not.toContain("Trakt link");
    }
  });

  it("falls back to neutral copy with no reason, an unknown one, or free text — never quoting it", () => {
    expect(pipelineStageCopy("Degraded")).toBe(PIPELINE_DEGRADED_FALLBACK);
    expect(pipelineStageCopy("Degraded", null)).toBe(PIPELINE_DEGRADED_FALLBACK);
    expect(pipelineStageCopy("Degraded", "SomeFutureReason")).toBe(PIPELINE_DEGRADED_FALLBACK);
    expect(pipelineStageCopy("Degraded", "Trakt revoked the token — re-link to carry on")).toBe(PIPELINE_DEGRADED_FALLBACK);
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

describe("purposeCopy", () => {
  it.each([
    ["cross-reference", "Cross-referencing"],
    ["rerank-feed", "Ranking your picks"],
    ["search-agent", "Ask Reel"],
    ["embed", "Search"],
  ])("names %s as a feature", (purpose, expected) => {
    expect(purposeCopy(purpose)).toBe(expected);
  });

  it("never shows an identifier: unknown, absent or whole-provider reads as the fallback", () => {
    expect(purposeCopy("some-future-purpose")).toBe(PURPOSE_FALLBACK);
    expect(purposeCopy(null)).toBe(PURPOSE_FALLBACK);
    expect(purposeCopy(undefined)).toBe(PURPOSE_FALLBACK);
  });
});

describe("failureAftermath", () => {
  it("names what still stands for kinds whose earlier results stay on the page", () => {
    expect(failureAftermath("xref")).toBe("Titles earlier passes judged keep their verdicts.");
    expect(failureAftermath("feed")).toBe("Your current picks stay up until a rebuild succeeds.");
  });

  it("says nothing for a kind with no earlier result on show, or one this build does not know", () => {
    expect(failureAftermath("delta")).toBeNull();
    expect(failureAftermath("some-future-kind")).toBeNull();
    expect(failureAftermath(null)).toBeNull();
  });
});

describe("UnrecognisedState (status v2)", () => {
  it("has its own Degraded clause instead of pointing at a header that raises nothing for it", () => {
    const clause = pipelineStageCopy("Degraded", "UnrecognisedState");
    expect(clause).toBe("a newer version of Reel recorded where your setup got to — nothing is lost");
    expect(clause).not.toBe(PIPELINE_DEGRADED_FALLBACK);
  });

  it("the onboarding sentence claims no failure and offers no reconnect", () => {
    expect(UNRECOGNISED_STATE_COPY).toMatch(/^A newer version of Reel/);
    expect(UNRECOGNISED_STATE_COPY).not.toMatch(/fail|error|stopped|try again/i);
    expect(UNRECOGNISED_STATE_COPY).toMatch(/nothing to reconnect/);
    expect(UNRECOGNISED_STATE_COPY).not.toMatch(/UnrecognisedState/);
  });
});
