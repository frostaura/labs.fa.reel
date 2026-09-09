import { describe, expect, it } from "vitest";
import reducer, {
  applyScoreOverrides,
  scoreOverridesCleared,
  scoresRecalibrated,
  type ScoreOverridesState,
} from "./scoreOverridesSlice";

/**
 * The client half of visible recalibration.
 *
 * A Preserve build can now change what the model thinks of tonight's picks without changing which
 * picks they are — it updates the snapshot's rows in place and publishes `feed-scores-updated` with
 * the UNCHANGED `generatedAt`. Every row on Home is keyed on that value, so nothing here may cause
 * a remount: the RTK `getFeed` entry is patched directly, and this slice is what the two surfaces
 * RTK cannot reach (the infinite rows' local state, Ask Reel's reducer) filter through instead.
 */
const empty: ScoreOverridesState = { byTitleId: {} };

describe("scoreOverridesSlice", () => {
  it("records the new number for each changed title", () => {
    const state = reducer(empty, scoresRecalibrated({ changed: [{ titleId: "a", predictedRating: 6.4 }] }));

    expect(state.byTitleId).toEqual({ a: 6.4 });
  });

  it("accumulates across frames and lets a later frame win", () => {
    const first = reducer(
      empty,
      scoresRecalibrated({ changed: [{ titleId: "a", predictedRating: 6.4 }, { titleId: "b", predictedRating: 8.1 }] })
    );
    const second = reducer(first, scoresRecalibrated({ changed: [{ titleId: "a", predictedRating: 7.0 }] }));

    expect(second.byTitleId).toEqual({ a: 7.0, b: 8.1 });
  });

  it("ignores malformed entries rather than showing NaN for you", () => {
    const state = reducer(
      empty,
      scoresRecalibrated({
        changed: [
          { titleId: "a", predictedRating: 6.4 },
          { titleId: "b", predictedRating: Number.NaN },
          { titleId: "", predictedRating: 5 },
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          null as any,
        ],
      })
    );

    expect(state.byTitleId).toEqual({ a: 6.4 });
  });

  it("survives a frame with no changes at all", () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    expect(reducer(empty, scoresRecalibrated({ changed: undefined as any })).byTitleId).toEqual({});
  });

  it("clears when a fresh snapshot is adopted", () => {
    const populated = reducer(empty, scoresRecalibrated({ changed: [{ titleId: "a", predictedRating: 6.4 }] }));

    // Adopting re-seeds every row from the server, whose numbers are current by construction — a
    // surviving override could only mask a fresher value.
    expect(reducer(populated, scoreOverridesCleared()).byTitleId).toEqual({});
  });
});

describe("applyScoreOverrides", () => {
  const cards = [
    { titleId: "a", predictedRating: 6.9 },
    { titleId: "b", predictedRating: 8.1 },
  ];

  it("replaces only the ratings that moved", () => {
    const next = applyScoreOverrides(cards, { a: 5.6 });

    expect(next.map((c) => c.predictedRating)).toEqual([5.6, 8.1]);
  });

  it("returns the SAME array when nothing matches", () => {
    // Referential stability is load-bearing: these lists feed memoised rows, and a fresh array on
    // every render would re-render the whole reel continuously.
    expect(applyScoreOverrides(cards, { z: 1 })).toBe(cards);
    expect(applyScoreOverrides(cards, {})).toBe(cards);
  });

  it("returns the same array when the override equals what is already shown", () => {
    expect(applyScoreOverrides(cards, { a: 6.9 })).toBe(cards);
  });

  it("keeps the identity of every untouched card", () => {
    const next = applyScoreOverrides(cards, { a: 5.6 });

    expect(next).not.toBe(cards);
    expect(next[1]).toBe(cards[1]);
    expect(next[0]).not.toBe(cards[0]);
  });

  it("handles an empty list", () => {
    const none: typeof cards = [];
    expect(applyScoreOverrides(none, { a: 1 })).toBe(none);
  });

  it("covers Ask Reel cards, whose rating may be null", () => {
    const askCards = [{ titleId: "a", predictedRating: null as number | null }];

    expect(applyScoreOverrides(askCards, { a: 7.2 })[0].predictedRating).toBe(7.2);
  });
});
