import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LEARNED_NEWS_MS, learnedStatusSegment } from "./learnedCopy";
import type { ModelLearned } from "../store/api";

const NOW = Date.parse("2026-10-26T12:00:00Z");

const learned = (overrides: Partial<ModelLearned> = {}): ModelLearned => ({
  trainedAt: "2026-10-26T09:00:00Z",
  newRatingsSinceLast: 14,
  calibrationBias: 0.2,
  calibrationMae: 1.1,
  spearman: 0.31,
  picksChanged: 3,
  ...overrides,
});

describe("learnedStatusSegment", () => {
  // `relTime` reads the clock itself, so the clock is pinned rather than only the `now` argument.
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("says when it retrained, what it learned from and what moved", () => {
    expect(learnedStatusSegment(learned(), NOW)).toBe("Retrained 3h ago on 14 new ratings, 3 picks changed");
  });

  it("makes no closeness claim, whatever an older server still sends", () => {
    // WS-5 retired the number; a stale payload carrying it must not bring the claim back.
    const stale = { ...learned(), maeImprovement: 0.4 } as ModelLearned;
    expect(learnedStatusSegment(stale, NOW)).not.toMatch(/closer/);
  });

  it("says nothing about picks when none changed", () => {
    expect(learnedStatusSegment(learned({ picksChanged: 0 }), NOW)).toBe("Retrained 3h ago on 14 new ratings");
  });

  it("is singular where it should be, and formats large counts with separators", () => {
    expect(learnedStatusSegment(learned({ newRatingsSinceLast: 1, picksChanged: 1 }), NOW)).toBe(
      "Retrained 3h ago on 1 new rating, 1 pick changed"
    );
    expect(learnedStatusSegment(learned({ newRatingsSinceLast: 1204, picksChanged: 0 }), NOW)).toBe(
      "Retrained 3h ago on 1,204 new ratings"
    );
  });

  it("ages out: a retrain older than two days is no longer news", () => {
    const old = learned({ trainedAt: new Date(NOW - LEARNED_NEWS_MS - 60_000).toISOString() });
    expect(learnedStatusSegment(old, NOW)).toBeNull();
  });

  it("says nothing without a retrain or with a timestamp it cannot read", () => {
    expect(learnedStatusSegment(null, NOW)).toBeNull();
    expect(learnedStatusSegment(learned({ trainedAt: "not a date" }), NOW)).toBeNull();
  });
});
