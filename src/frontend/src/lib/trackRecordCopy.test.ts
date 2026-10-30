import { describe, expect, it } from "vitest";
import {
  counterfactualMatchLine,
  counterfactualWindowLabel,
  distinctCounterfactualWindows,
  calibrationChartLabel,
  offeredWindows,
  singleWindowNote,
  biasNote,
  headlineSentence,
  learnedSentence,
  outcomeDelay,
  NO_MODEL_SOURCE_NOTE,
  num,
  pct,
  signed,
  sourceNote,
  PROVENANCE_NOTE,
} from "./trackRecordCopy";
import type { AccuracyPayload, CounterfactualWindow } from "../store/api";

const payload = (overrides: Partial<AccuracyPayload> = {}): AccuracyPayload => ({
  window: "90d",
  from: "2026-06-12T00:00:00Z",
  to: "2026-09-10T00:00:00Z",
  source: "exposures",
  headline: {
    withinOnePoint: 0.26,
    n: 116,
    meanAbsoluteError: 1.94,
    bias: 0.37,
    correlation: -0.004,
    meanPredicted: 6.81,
    meanActual: 5.78,
  },
  calibration: [],
  moments: [],
  conversion: {
    shown: 0,
    saved: 104,
    watched: 73,
    rated: 73,
    shownToSaved: null,
    savedToWatched: 0.7019,
    shownToRated: null,
    medianDaysToWatch: 11,
  },
  counterfactual: [],
  learnedThisWeek: null,
  // Present and empty, which is what the endpoint sends for an account whose deep pass has never
  // run — the block itself is never absent, so the page never has to guess whether it exists.
  crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
  ...overrides,
});

describe("formatting", () => {
  it("renders an absent number as an em-dash rather than a fabricated zero", () => {
    // "0%" is a claim about the product's performance; "—" is the absence of one. On a brand-new
    // account every rate is unknown, and reporting them as zero would say the loop failed when it
    // simply has not run.
    expect(pct(null)).toBe("—");
    expect(num(null)).toBe("—");
    expect(signed(null)).toBe("—");
    expect(pct(0)).toBe("0%");
  });

  it("keeps the sign on a bias so an over-promise cannot hide behind an absolute value", () => {
    expect(signed(1.03)).toBe("+1.03");
    expect(signed(-0.42)).toBe("-0.42");
  });
});

describe("headlineSentence", () => {
  it("states the founder's real number without softening it", () => {
    expect(headlineSentence(payload())).toBe("Within one point on 26% of the predictions you've rated");
  });

  it("says there is nothing to score rather than claiming a perfect or a zero record", () => {
    const blank = payload({ source: "none", headline: { ...payload().headline, n: 0, withinOnePoint: 0 } });
    expect(headlineSentence(blank)).toBe("Nothing to score yet.");
    expect(headlineSentence(blank)).not.toMatch(/0%/);
  });
});

describe("sourceNote", () => {
  it("claims the card was on screen only when the exposure log says so", () => {
    expect(sourceNote(payload())).toContain("actually put in front of you");
  });

  it("admits the retro fallback proves a score existed, not that it was seen", () => {
    const note = sourceNote(payload({ source: "retro" }));
    expect(note).toContain("not that you saw it");
    expect(note).not.toContain("put in front of you");
  });

  it("tells a new account what to do rather than reporting a failure", () => {
    const note = sourceNote(payload({ source: "none", headline: { ...payload().headline, n: 0 } }));
    expect(note).toContain("Rate a few titles");
  });
});

describe("biasNote", () => {
  it("names which way the model is wrong", () => {
    expect(biasNote(0.37)).toBe("Reel promises more than you give");
    expect(biasNote(-0.4)).toBe("Reel sells titles short");
  });
});

describe("outcomeDelay (HREV1-D09)", () => {
  it("says what the day count measures, in words", () => {
    expect(outcomeDelay(41)).toBe("rated 41 days after it was shown");
    expect(outcomeDelay(3)).toBe("rated 3 days after it was shown");
  });

  it("singular for one day, and the same day for zero", () => {
    expect(outcomeDelay(1)).toBe("rated 1 day after it was shown");
    expect(outcomeDelay(0)).toBe("rated the day it was shown");
  });

  it("groups a long wait with an invariant separator", () => {
    expect(outcomeDelay(1200)).toBe("rated 1,200 days after it was shown");
  });
});


describe("learnedSentence", () => {
  const learned = (overrides: Partial<NonNullable<AccuracyPayload["learnedThisWeek"]>> = {}) => ({
    trainedAt: "2026-09-09T03:00:00Z",
    newRatings: 14,
    calibrationBias: 0.2,
    calibrationMae: 1.1,
    spearman: 0.31,
    picksChanged: 3,
    ...overrides,
  });

  it("says what the retrain learned from and what it changed", () => {
    expect(learnedSentence(learned())).toBe("Learned from 14 new ratings. 3 picks changed.");
  });

  it("makes no closeness claim — the number behind it is retired and no longer sent (R1-56)", () => {
    // The old clause read "It did not get closer on your scores" whenever the field was missing,
    // which after WS-5's change was every retrain.
    expect(learnedSentence(learned())).not.toMatch(/closer/);
    const stale = { ...learned(), maeImprovement: 0.4 } as NonNullable<AccuracyPayload["learnedThisWeek"]>;
    expect(learnedSentence(stale)).not.toMatch(/closer/);
  });

  it("says when nothing changed, and is singular where it should be", () => {
    expect(learnedSentence(learned({ picksChanged: 0 }))).toBe("Learned from 14 new ratings. No picks changed.");
    expect(learnedSentence(learned({ newRatings: 1, picksChanged: 1 }))).toBe("Learned from 1 new rating. 1 pick changed.");
  });
});

describe("offeredWindows / singleWindowNote (G-9)", () => {
  it("offers only windows that hold a different set of scored calls", () => {
    const windows = [
      { key: "90d", pairs: 12, sameAs: null },
      { key: "365d", pairs: 40, sameAs: null },
      { key: "all", pairs: 40, sameAs: "365d" },
    ];
    expect(offeredWindows(windows)).toEqual(["90d", "365d"]);
    expect(singleWindowNote(windows)).toBeNull();
  });

  it("collapses to one window, and says why, when everything scored is recent", () => {
    const windows = [
      { key: "90d", pairs: 65, sameAs: null },
      { key: "365d", pairs: 65, sameAs: "90d" },
      { key: "all", pairs: 65, sameAs: "90d" },
    ];
    expect(offeredWindows(windows)).toEqual(["90d"]);
    expect(singleWindowNote(windows)).toBe("All 65 scored calls fall in the last 90 days, so a longer window would show the same.");
  });

  it("offers every window when the server sends no summary (a server from before the change)", () => {
    expect(offeredWindows(undefined)).toEqual(["90d", "365d", "all"]);
    expect(singleWindowNote(undefined)).toBeNull();
  });

  it("says nothing about windows on an empty page", () => {
    const windows = [
      { key: "90d", pairs: 0, sameAs: null },
      { key: "365d", pairs: 0, sameAs: "90d" },
      { key: "all", pairs: 0, sameAs: "90d" },
    ];
    expect(offeredWindows(windows)).toEqual(["90d"]);
    expect(singleWindowNote(windows)).toBeNull();
  });
});

describe("what the page claims about its numbers (R1-47)", () => {
  // The family track-record-is-the-internal-measure: promotion reads NDCG@10, which this page does
  // not show, and the retrain check reads the active model's own calibration pairs. The page may
  // claim only what its numbers are — predictions against the ratings that followed them.
  it("the provenance line says what the numbers are, and claims no internal judgement", () => {
    expect(PROVENANCE_NOTE).toMatch(/^Every number here is Reel's prediction against the rating you then gave/);
    expect(PROVENANCE_NOTE).not.toMatch(/judged|internal|same numbers/i);
  });

  it("the headline names its population on either evidence base, never every rating", () => {
    for (const source of ["exposures", "retro"] as const) {
      for (const n of [1, 116]) {
        const p = payload({ source, headline: { ...payload().headline, n } });
        expect(headlineSentence(p)).toMatch(/of the predictions you've rated$/);
        expect(headlineSentence(p)).not.toMatch(/of what you rate/);
      }
    }
  });

  it("the no-model note promises the page fills in, not that every scored title lands here", () => {
    expect(NO_MODEL_SOURCE_NOTE).not.toMatch(/\bevery\b/);
    expect(NO_MODEL_SOURCE_NOTE).toMatch(/rate titles after it has scored them/);
  });
});

/**
 * PH2-02: the calibration chart is SVG, so its numbers reach a screen reader only through the name
 * of the `role="img"` that wraps it. The sentence reads every band the chart draws — and only those.
 */
describe("calibrationChartLabel", () => {
  const band = (low: number, n: number, meanPredicted: number | null, meanActual: number | null) => ({
    bucket: low, low, high: low + 1, n, meanPredicted, meanActual,
  });

  it("reads each drawn band as what Reel said against what you said", () => {
    expect(
      calibrationChartLabel([band(5, 12, 5.6, 6.4), band(8, 1, 8.64, 4.4), band(9, 0, null, null)])
    ).toBe(
      "Where the number lands, Reel's average prediction against your average rating in each band: " +
        "5 to 6, Reel said 5.6, you said 6.4, from 12 ratings; " +
        "8 to 9, Reel said 8.6, you said 4.4, from 1 rating."
    );
  });

  it("leaves out a band the chart does not draw, wherever it sits", () => {
    const label = calibrationChartLabel([band(4, 0, null, null), band(6, 61, 6.7, 5.9)]);
    expect(label).toContain("6 to 7, Reel said 6.7, you said 5.9, from 61 ratings.");
    expect(label).not.toContain("4 to 5");
  });

  it("never prints a fabricated number for a band with no mean", () => {
    expect(calibrationChartLabel([band(7, 3, null, 5)])).toContain("7 to 8, Reel said —, you said 5.0, from 3 ratings.");
  });

  it("says when there is nothing to draw", () => {
    expect(calibrationChartLabel([band(5, 0, null, null)])).toBe("Where the number lands: no bands yet.");
  });
});

describe("counterfactualMatchLine", () => {
  it("states the matching band, and the via-Reel ratings left out only when there are any", () => {
    expect(counterfactualMatchLine({ matchDays: 14, viaReelUnmatchedCount: 0 })).toBe("matched within 14 days");
    expect(counterfactualMatchLine({ matchDays: 14, viaReelUnmatchedCount: 3 })).toBe("matched within 14 days · 3 unmatched");
    expect(counterfactualMatchLine({ matchDays: 1, viaReelUnmatchedCount: 1 })).toBe("matched within 1 day · 1 unmatched");
  });
});

/**
 * HREV5-DESK-04 (G-9): "last 365 days" and "all time" printed identical figures twice. A wider
 * window that matches a narrower one prints once, under the widest name; any one number that
 * differs keeps both rows.
 */
describe("distinctCounterfactualWindows", () => {
  const year: CounterfactualWindow = {
    window: "last365Days", days: 365, viaReelCount: 120, viaReelMeanRating: 6.07, otherCount: 152,
    otherMeanRating: 6.48, delta: -0.41, viaReelUnmatchedCount: 0, matchDays: 14,
  };
  const all: CounterfactualWindow = { ...year, window: "allTime", days: null };

  it("prints identical windows once, under the widest window's name", () => {
    const rows = distinctCounterfactualWindows([year, all]);
    expect(rows).toHaveLength(1);
    expect(rows[0].window.window).toBe("allTime");
    expect(rows[0].label).toBe("all time (the same ratings as the last 365 days)");
  });

  it("collapses whatever order the windows arrive in", () => {
    const rows = distinctCounterfactualWindows([all, year]);
    expect(rows.map((r) => r.window.window)).toEqual(["allTime"]);
  });

  // One field at a time: each is enough to make the windows hold different ratings.
  const differing: [string, Partial<CounterfactualWindow>][] = [
    ["found-yourself count", { otherCount: 2022 }],
    ["via-Reel count", { viaReelCount: 121 }],
    ["via-Reel mean", { viaReelMeanRating: 6.08 }],
    ["found-yourself mean", { otherMeanRating: 6.77 }],
    ["gap", { delta: -0.42 }],
    ["unmatched count", { viaReelUnmatchedCount: 1 }],
    ["matching band", { matchDays: 7 }],
    ["a missing mean", { otherMeanRating: null }],
  ];
  for (const [field, change] of differing) {
    it(`keeps both rows, narrowest first, when only the ${field} differs`, () => {
      const rows = distinctCounterfactualWindows([{ ...all, ...change }, year]);
      expect(rows.map((r) => r.label)).toEqual(["last 365 days", "all time"]);
    });
  }

  it("folds a run of matching windows into the widest, and keeps a different one apart", () => {
    const quarter: CounterfactualWindow = { ...year, window: "last90Days", days: 90, otherCount: 40 };
    const rows = distinctCounterfactualWindows([all, quarter, year]);
    expect(rows.map((r) => r.label)).toEqual(["last 90 days", "all time (the same ratings as the last 365 days)"]);
  });

  it("matches two empty windows, whose means are both missing", () => {
    const empty = { ...year, viaReelCount: 0, otherCount: 0, viaReelMeanRating: null, otherMeanRating: null, delta: null };
    expect(distinctCounterfactualWindows([empty, { ...empty, window: "allTime", days: null }])).toHaveLength(1);
  });

  it("passes a single window through under its own name, and nothing for nothing", () => {
    expect(distinctCounterfactualWindows([year]).map((r) => r.label)).toEqual(["last 365 days"]);
    expect(distinctCounterfactualWindows([])).toEqual([]);
  });

  it("names a window by its days, and the open window as all time", () => {
    expect(counterfactualWindowLabel({ days: 365 })).toBe("last 365 days");
    expect(counterfactualWindowLabel({ days: null })).toBe("all time");
  });
});
