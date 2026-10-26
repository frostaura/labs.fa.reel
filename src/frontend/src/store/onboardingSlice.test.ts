import { describe, expect, it } from "vitest";
import reducer, {
  eventReceived,
  failureCopy,
  markStalled,
  needsReconnect,
  ratingsNeededCopy,
  pollAdvance,
  reset,
  retryStarted,
  awaitingModelTiming,
  stageFromPipeline,
  stageFromStatus,
  type OnboardingState,
} from "./onboardingSlice";

const initial = (): OnboardingState => reducer(undefined, { type: "@@init" });

const ev = (type: string, data: Record<string, unknown>) => eventReceived({ type, data: { seq: 0, ...data } });

describe("onboardingSlice", () => {
  it("dedupes replayed events by seq", () => {
    let s = initial();
    s = reducer(s, ev("ingest-progress", { seq: 5, kind: "movies", found: 100 }));
    expect(s.counters.movies).toBe(100);
    // Replay of an older frame after EventSource reconnect must be dropped.
    s = reducer(s, ev("ingest-progress", { seq: 3, kind: "movies", found: 50 }));
    expect(s.counters.movies).toBe(100);
    expect(s.lastSeq).toBe(5);
  });

  it("treats seq 0 as unsequenced and always applies it", () => {
    let s = initial();
    s = reducer(s, ev("ingest-progress", { seq: 10, kind: "shows", found: 20 }));
    s = reducer(s, ev("heartbeat", { seq: 0 }));
    expect(s.stalled).toBe(false);
  });

  it("advances stage monotonically and never regresses", () => {
    let s = initial();
    s = reducer(s, ev("insight", { seq: 1, id: "a", kind: "genre", text: "You love sci-fi" }));
    expect(s.stage).toBe("profiling");
    // A late ingest-progress (lower stage) must not pull the show backwards.
    s = reducer(s, ev("ingest-progress", { seq: 2, kind: "ratings", found: 3000 }));
    expect(s.stage).toBe("profiling");
    expect(s.counters.ratings).toBe(3000);
  });

  it("failed always applies regardless of current stage", () => {
    let s = initial();
    s = reducer(s, ev("feed-ready", { seq: 1 }));
    expect(s.stage).toBe("reveal");
    s = reducer(s, ev("failed", { seq: 2, error: "token revoked" }));
    expect(s.stage).toBe("failed");
    expect(s.error).toBe("token revoked");
  });

  it("counters only count up (out-of-order progress frames)", () => {
    let s = initial();
    s = reducer(s, ev("ingest-progress", { seq: 0, kind: "episodes", found: 500 }));
    s = reducer(s, ev("ingest-progress", { seq: 0, kind: "episodes", found: 400 }));
    expect(s.counters.episodes).toBe(500);
  });

  it("dedupes insights by id and caps the list", () => {
    let s = initial();
    s = reducer(s, ev("insight", { seq: 0, id: "x", kind: "creator", text: "Villeneuve 9.1" }));
    s = reducer(s, ev("insight", { seq: 0, id: "x", kind: "creator", text: "Villeneuve 9.1" }));
    expect(s.insights).toHaveLength(1);
  });

  it("poll data advances a stalled show (poll is authoritative)", () => {
    let s = initial();
    s = reducer(s, ev("ingest-progress", { seq: 1, kind: "movies", found: 10 }));
    s = reducer(s, markStalled());
    expect(s.stalled).toBe(true);
    s = reducer(s, pollAdvance({ pipelineStage: "Training" }));
    expect(s.stage).toBe("fitting");
  });

  it("maps backend pipeline stages to show stages", () => {
    expect(stageFromPipeline("Ingesting")).toBe("ingesting");
    expect(stageFromPipeline("Extracting")).toBe("profiling");
    expect(stageFromPipeline("Training")).toBe("fitting");
    expect(stageFromPipeline("Evaluated")).toBe("fitting");
    expect(stageFromPipeline("FeedReady")).toBe("reveal");
    expect(stageFromPipeline("Degraded")).toBe("failed");
    expect(stageFromPipeline("Linked")).toBeNull();
  });

  it("reset returns to the initial state", () => {
    let s = initial();
    s = reducer(s, ev("feed-ready", { seq: 9 }));
    s = reducer(s, reset());
    expect(s.stage).toBe("connecting");
    expect(s.lastSeq).toBe(0);
  });
});

describe("onboarding below the training floor (H-5) — a stop that is not a failure", () => {
  it("a poll reporting NotEnoughRatings parks the show on needsRatings with the counts", () => {
    let s = initial();
    s = reducer(s, pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 4, required: 10 } }));
    expect(s.stage).toBe("needsRatings");
    expect(s.ratingsFloor).toEqual({ usableRatings: 4, required: 10 });
    expect(s.error).toBeNull();
  });

  it("the reason code alone is enough, even on a backend still reporting the stage as Degraded", () => {
    const s = reducer(initial(), pollAdvance({ pipelineStage: "Degraded", degradedReason: "NotEnoughRatings" }));
    expect(s.stage).toBe("needsRatings");
    expect(s.ratingsFloor).toBeNull();
  });

  it("the counts alone are enough too", () => {
    const s = reducer(initial(), pollAdvance({ pipelineStage: "Degraded", notEnoughRatings: { usableRatings: 0, required: 10 } }));
    expect(s.stage).toBe("needsRatings");
  });

  it("the trainer's stage-changed frame (Degraded + insufficient-ratings) is needsRatings, not failed", () => {
    let s = initial();
    s = reducer(s, ev("ingest-progress", { seq: 1, kind: "ratings", found: 4 }));
    s = reducer(s, ev("stage-changed", { seq: 2, stage: "Degraded", reason: "insufficient-ratings", message: "Not enough ratings" }));
    expect(s.stage).toBe("needsRatings");
  });

  it("a Degraded frame with any other reason is still a failure", () => {
    const s = reducer(initial(), ev("stage-changed", { seq: 1, stage: "Degraded", reason: "trakt-revoked" }));
    expect(s.stage).toBe("failed");
  });

  it("late forward frames do not lift the show off needsRatings", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings" }));
    s = reducer(s, ev("feed-ready", { seq: 5 }));
    s = reducer(s, ev("ingest-progress", { seq: 6, kind: "movies", found: 10 }));
    expect(s.stage).toBe("needsRatings");
  });

  // The payload the server really sends once usable ratings reach the floor and no Train has run
  // since the park (SyncStatusContractTests: stored Degraded, floor met → Degraded + PipelineFailed,
  // no counts). It is not a failure, and it is not "Training" either.
  const FLOOR_MET = { pipelineStage: "Degraded", degradedReason: "PipelineFailed", notEnoughRatings: null, lastFailures: [] };

  it("reaching the floor moves needsRatings to awaitingModel, never to failed", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 9, required: 10 } }));
    s = reducer(s, pollAdvance(FLOOR_MET));
    expect(s.stage).toBe("awaitingModel");
    expect(s.ratingsFloor).toBeNull();
  });

  it("a fresh page load on that payload is awaitingModel too", () => {
    expect(reducer(initial(), pollAdvance(FLOOR_MET)).stage).toBe("awaitingModel");
    // An older backend sends no reason at all for the same state.
    expect(reducer(initial(), pollAdvance({ pipelineStage: "Degraded" })).stage).toBe("awaitingModel");
  });

  it("the train the page was waiting for lifts awaitingModel forward", () => {
    let s = reducer(initial(), pollAdvance(FLOOR_MET));
    s = reducer(s, pollAdvance({ pipelineStage: "Training", degradedReason: null, notEnoughRatings: null }));
    expect(s.stage).toBe("fitting");
    s = reducer(s, pollAdvance({ pipelineStage: "FeedReady", degradedReason: null, notEnoughRatings: null }));
    expect(s.stage).toBe("reveal");
  });

  it("Degraded with a failed Train in lastFailures is a real failure, not a wait", () => {
    const s = reducer(
      initial(),
      pollAdvance({ ...FLOOR_MET, lastFailures: [{ kind: "DeltaSync" }, { kind: "Train" }] }),
    );
    expect(s.stage).toBe("failed");
    // An unrelated failed job does not turn the wait into a failure.
    expect(reducer(initial(), pollAdvance({ ...FLOOR_MET, lastFailures: [{ kind: "DeltaSync" }] })).stage).toBe("awaitingModel");
  });

  // CR-6: the reason the server will send for the same wait. Taken at its word — the server only
  // sends it after checking the Train history itself — whatever stage it is stored under.
  it("the server's AwaitingFirstModel reason is the wait, on any stored stage", () => {
    for (const pipelineStage of ["Degraded", "NotEnoughRatings", "FeedReady"]) {
      const s = reducer(initial(), pollAdvance({ pipelineStage, degradedReason: "AwaitingFirstModel", notEnoughRatings: null, lastFailures: [] }));
      expect(s.stage).toBe("awaitingModel");
      expect(s.degradedReason).toBe("AwaitingFirstModel");
    }
  });

  it("a reason the client does not know is never read as the wait", () => {
    expect(reducer(initial(), pollAdvance({ ...FLOOR_MET, degradedReason: "SomethingNewer" })).stage).toBe("failed");
    expect(reducer(initial(), pollAdvance({ ...FLOOR_MET, degradedReason: "ProviderDown" })).stage).toBe("failed");
  });

  it("the wait names the first model, or the next build of the one the account already has", () => {
    const first = reducer(initial(), pollAdvance({ ...FLOOR_MET, retrain: { hasActiveModel: false } }));
    expect(first.hasActiveModel).toBe(false);
    expect(awaitingModelTiming(first.hasActiveModel)).toBe("Reel builds your first model in tonight's refresh at the latest");
    const next = reducer(initial(), pollAdvance({ ...FLOOR_MET, retrain: { hasActiveModel: true } }));
    expect(next.stage).toBe("awaitingModel");
    expect(awaitingModelTiming(next.hasActiveModel)).toBe("Reel rebuilds your model in tonight's refresh at the latest");
    // An older payload without `retrain` says "first", the common case.
    expect(awaitingModelTiming(reducer(initial(), pollAdvance(FLOOR_MET)).hasActiveModel)).toMatch(/first model/);
  });

  it("the poll can put the show back below the floor (ratings were removed on Trakt)", () => {
    let s = reducer(initial(), pollAdvance(FLOOR_MET));
    s = reducer(s, pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 8, required: 10 } }));
    expect(s.stage).toBe("needsRatings");
  });

  it("a revoked link below the floor offers the reconnect: the server's first-ranked reason wins", () => {
    // ResolveStageAsync computes the floor whatever the link's state, so both arrive together.
    const s = reducer(
      initial(),
      pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "TraktRevoked", notEnoughRatings: { usableRatings: 4, required: 10 } }),
    );
    expect(s.stage).toBe("failed");
    expect(needsReconnect(s.degradedReason)).toBe(true);
    expect(stageFromStatus({ pipelineStage: "Degraded", degradedReason: "TraktRefreshFailed", notEnoughRatings: { usableRatings: 4, required: 10 } })).toBe("failed");
  });

  it("dead-lettered writes do not hide the floor: the stage still says NotEnoughRatings", () => {
    expect(
      stageFromStatus({ pipelineStage: "NotEnoughRatings", degradedReason: "TraktWritesDeadLettered", notEnoughRatings: { usableRatings: 4, required: 10 } }),
    ).toBe("needsRatings");
  });

  it("a real failure still replaces needsRatings", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings" }));
    s = reducer(s, pollAdvance({ pipelineStage: "Degraded", degradedReason: "TraktRevoked" }));
    expect(s.stage).toBe("failed");
    expect(s.degradedReason).toBe("TraktRevoked");
  });

  it("a poll-set failure is lifted by the poll once the server moves on (provider back)", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "Degraded", degradedReason: "ProviderDown" }));
    expect(s.stage).toBe("failed");
    s = reducer(s, pollAdvance({ pipelineStage: "Training", degradedReason: null }));
    expect(s.stage).toBe("fitting");
  });

  it("a stream failure with its error is not lifted by a status poll that cannot see it", () => {
    let s = reducer(initial(), ev("failed", { seq: 1, error: "DeltaSync failed after 3 attempts." }));
    s = reducer(s, pollAdvance({ pipelineStage: "Ingesting", degradedReason: null }));
    expect(s.stage).toBe("failed");
  });

  it("a poll from a backend that predates the v2 fields still advances", () => {
    const s = reducer(initial(), pollAdvance({ pipelineStage: "Ingesting" }));
    expect(s.stage).toBe("ingesting");
    expect(s.degradedReason).toBeNull();
  });
});

describe("ratingsNeededCopy", () => {
  it.each([
    [{ usableRatings: 4, required: 10 }, "4 of 10 ratings — rate 6 more and Reel builds your model."],
    [{ usableRatings: 9, required: 10 }, "9 of 10 ratings — rate one more and Reel builds your model."],
    [{ usableRatings: 0, required: 10 }, "0 of 10 ratings — rate 10 more and Reel builds your model."],
  ])("names the counts: %o", (floor, text) => {
    expect(ratingsNeededCopy(floor)).toBe(text);
  });

  it("at or past the floor it says that is enough, never 'rate 0 more'", () => {
    expect(ratingsNeededCopy({ usableRatings: 10, required: 10 })).toBe("10 of 10 ratings — that's enough for Reel to build your model.");
    expect(ratingsNeededCopy({ usableRatings: 12, required: 10 })).not.toMatch(/rate 0|rate -/);
  });

  it("without counts it still says what to do", () => {
    expect(ratingsNeededCopy(null)).toMatch(/rate a few/);
  });
});

describe("failureCopy — never the blank 'Something went wrong.'", () => {
  it("a known reason wins over a job runner's free text", () => {
    expect(failureCopy("TraktRevoked", "DeltaSync failed after 3 attempts.")).toMatch(/no longer has access to your Trakt account/);
    expect(failureCopy("ProviderDown", "Train failed")).toMatch(/model service/);
  });

  it("the free text is the fallback for the generic or missing reason", () => {
    expect(failureCopy("PipelineFailed", "  Sync crashed  ")).toBe("Sync crashed");
    expect(failureCopy(null, "Sync crashed")).toBe("Sync crashed");
    expect(failureCopy("PipelineFailed", "   ")).toMatch(/stopped on Reel's side/);
  });

  it.each(["TraktRevoked", "TraktRefreshFailed", "TraktWritesDeadLettered", "ProviderDown", "PipelineFailed", "SomethingNew", null])(
    "%s has a specific, non-empty sentence",
    (reason) => {
      const text = failureCopy(reason, null);
      expect(text.length).toBeGreaterThan(20);
      expect(text).not.toBe("Something went wrong.");
    },
  );

  it("only the Trakt access reasons offer a reconnect", () => {
    expect(["TraktRevoked", "TraktRefreshFailed", "ProviderDown", "PipelineFailed", null].map(needsReconnect)).toEqual([
      true, true, false, false, false,
    ]);
  });
});

describe("retryStarted — an accepted 'Try again'", () => {
  it("restarts the show but keeps the user and the stream position", () => {
    let s = reducer(initial(), ev("connected", { seq: 3, traktUser: "deanmar09", avatarUrl: "a.png" }));
    s = reducer(s, ev("failed", { seq: 4, error: "DeltaSync failed after 3 attempts." }));
    s = reducer(s, retryStarted());
    expect(s.stage).toBe("connecting");
    expect(s.error).toBeNull();
    expect(s.traktUser).toBe("deanmar09");
    expect(s.lastSeq).toBe(4);
    // A replay of the old failed frame after the restart is dropped by seq.
    s = reducer(s, ev("failed", { seq: 4, error: "DeltaSync failed after 3 attempts." }));
    expect(s.stage).toBe("connecting");
  });

  it("an unchanged 'still stopped' status re-applies after the restart", () => {
    const stopped = { pipelineStage: "Degraded", degradedReason: "PipelineFailed", lastFailures: [{ kind: "Train" }] };
    let s = reducer(initial(), pollAdvance(stopped));
    s = reducer(s, retryStarted());
    s = reducer(s, pollAdvance(stopped));
    expect(s.stage).toBe("failed");
  });
});
