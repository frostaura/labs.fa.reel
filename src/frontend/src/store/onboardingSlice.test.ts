import { describe, expect, it } from "vitest";
import reducer, {
  eventReceived,
  failureCopy,
  markStalled,
  needsReconnect,
  ratingsNeededCopy,
  pollAdvance,
  reset,
  stageFromPipeline,
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

  it("the authoritative poll lifts it once the account has rated enough", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings" }));
    s = reducer(s, pollAdvance({ pipelineStage: "Training", degradedReason: null, notEnoughRatings: null }));
    expect(s.stage).toBe("fitting");
    expect(s.degradedReason).toBeNull();
    expect(s.ratingsFloor).toBeNull();
  });

  it("a real failure still replaces needsRatings", () => {
    let s = reducer(initial(), pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings" }));
    s = reducer(s, pollAdvance({ pipelineStage: "Degraded", degradedReason: "TraktRevoked" }));
    expect(s.stage).toBe("failed");
    expect(s.degradedReason).toBe("TraktRevoked");
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

  it("never goes negative when the counts race past the floor", () => {
    expect(ratingsNeededCopy({ usableRatings: 12, required: 10 })).toBe("12 of 10 ratings — rate 0 more and Reel builds your model.");
  });

  it("without counts it still says what to do", () => {
    expect(ratingsNeededCopy(null)).toMatch(/rate a few/);
  });
});

describe("failureCopy — never the blank 'Something went wrong.'", () => {
  it("a free-text failed event wins", () => {
    expect(failureCopy("TraktRevoked", "  Sync crashed  ")).toBe("Sync crashed");
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
