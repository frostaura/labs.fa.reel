import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { LEARNED_DISMISS_PREFIX, dismissLearned, isLearnedDismissed, learnedCopy } from "./learnedCopy";
import type { ModelLearned } from "../store/api";

const learned = (overrides: Partial<ModelLearned> = {}): ModelLearned => ({
  trainedAt: "2026-09-09T03:00:00Z",
  newRatingsSinceLast: 14,
  calibrationBias: 0.2,
  calibrationMae: 1.1,
  maeImprovement: 0.4,
  spearman: 0.31,
  picksChanged: 3,
  ...overrides,
});

describe("learnedCopy", () => {
  it("says what the retrain learned, how much closer it runs, and what moved", () => {
    expect(learnedCopy(learned())).toBe(
      "Learned from 14 new ratings — runs 0.4 closer on your scores now. 3 picks changed."
    );
  });

  it("drops the improvement clause when the model did NOT get closer", () => {
    // A retrain that made calibration worse is real and must not be dressed up.
    expect(learnedCopy(learned({ maeImprovement: -0.3 }))).toBe("Learned from 14 new ratings. 3 picks changed.");
  });

  it("drops the improvement clause when it rounds to nothing", () => {
    expect(learnedCopy(learned({ maeImprovement: 0.02 }))).toBe("Learned from 14 new ratings. 3 picks changed.");
  });

  it("drops the improvement clause when there is no previous evaluation to compare against", () => {
    expect(learnedCopy(learned({ maeImprovement: null }))).toBe("Learned from 14 new ratings. 3 picks changed.");
  });

  it("says nothing about picks when none changed", () => {
    expect(learnedCopy(learned({ picksChanged: 0 }))).toBe(
      "Learned from 14 new ratings — runs 0.4 closer on your scores now."
    );
  });

  it("is singular where it should be", () => {
    expect(learnedCopy(learned({ newRatingsSinceLast: 1, picksChanged: 1, maeImprovement: null }))).toBe(
      "Learned from 1 new rating. 1 pick changed."
    );
  });
});

/**
 * A minimal in-memory Storage, matching browseFilter.test.ts: the suite runs in node with no DOM
 * (no jsdom or happy-dom is installed, deliberately), so a stub is what lets these exercise the
 * real read/write/catch paths without pulling a DOM implementation into the project.
 */
function installStorage(options: { throwOnRead?: boolean; throwOnWrite?: boolean } = {}) {
  const map = new Map<string, string>();
  (globalThis as { localStorage?: unknown }).localStorage = {
    getItem(key: string) {
      if (options.throwOnRead) throw new Error("SecurityError");
      return map.get(key) ?? null;
    },
    setItem(key: string, value: string) {
      if (options.throwOnWrite) throw new Error("QuotaExceededError");
      map.set(key, value);
    },
    removeItem: (key: string) => void map.delete(key),
    clear: () => map.clear(),
    key: (i: number) => [...map.keys()][i] ?? null,
    get length() {
      return map.size;
    },
  };
  return map;
}

describe("learned band dismissal", () => {
  beforeEach(() => {
    installStorage();
  });

  afterEach(() => {
    delete (globalThis as { localStorage?: unknown }).localStorage;
  });

  it("is scoped to the retrain, so the next one is still news", () => {
    dismissLearned("2026-09-09T03:00:00Z");

    expect(isLearnedDismissed("2026-09-09T03:00:00Z")).toBe(true);
    expect(isLearnedDismissed("2026-09-10T03:00:00Z")).toBe(false);
    expect(localStorage.getItem(`${LEARNED_DISMISS_PREFIX}2026-09-09T03:00:00Z`)).toBe("1");
  });

  it("shows the band when storage throws on read", () => {
    // A private window or a browser blocking site data throws on the accessor itself. Showing the
    // band is the harmless direction; crashing Home is not.
    installStorage({ throwOnRead: true });

    expect(isLearnedDismissed("2026-09-09T03:00:00Z")).toBe(false);
  });

  it("swallows a throwing write", () => {
    installStorage({ throwOnWrite: true });

    expect(() => dismissLearned("2026-09-09T03:00:00Z")).not.toThrow();
  });
});
