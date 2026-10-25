import { describe, expect, it } from "vitest";
import reducer, { activeJobObserved, syncCompleted, syncProgress } from "./syncSlice";

const summary = { ratingsAdded: 2, ratingsChanged: 0, watchedAdded: 0, retrainQueued: false, retrainAt: null, picksChanged: null };

describe("syncSlice", () => {
  it("keeps only numbers from a live frame", () => {
    const s = reducer(undefined, syncProgress({ kind: "delta", pct: 50, current: 1, total: 2 }));
    expect(s.live).toEqual({ kind: "delta", pct: 50, current: 1, total: 2 });
  });

  it("a background frame does not wipe the last sync's sentence", () => {
    let s = reducer(undefined, syncCompleted(summary));
    s = reducer(s, syncProgress({ kind: "enrich", pct: 10 }));
    expect(s.summary).toEqual(summary);
  });

  it("drops a stale frame when the poll reports a different job (or none)", () => {
    let s = reducer(undefined, syncProgress({ kind: "delta", pct: 100 }));
    s = reducer(s, activeJobObserved({ userVisible: false, jobChanged: true }));
    expect(s).toMatchObject({ active: false, live: null });
  });

  it("keeps the frame while the same job is still reported", () => {
    let s = reducer(undefined, syncProgress({ kind: "delta", pct: 60 }));
    s = reducer(s, activeJobObserved({ userVisible: true, jobChanged: false }));
    expect(s).toMatchObject({ active: true, live: { pct: 60 } });
  });

  it("a new job the user is watching makes the last sentence history", () => {
    let s = reducer(undefined, syncCompleted(summary));
    s = reducer(s, activeJobObserved({ userVisible: true, jobChanged: true }));
    expect(s).toMatchObject({ summary: null, summaryAt: null });
  });

  it("dates the sentence when it arrives", () => {
    const s = reducer(undefined, syncCompleted(summary, 1_700_000_000_000));
    expect(s).toMatchObject({ summary, summaryAt: 1_700_000_000_000 });
  });

  it("going idle keeps the sentence and its date", () => {
    let s = reducer(undefined, syncCompleted(summary, 42));
    s = reducer(s, activeJobObserved({ userVisible: false, jobChanged: true }));
    expect(s).toMatchObject({ summary, summaryAt: 42 });
  });
});
