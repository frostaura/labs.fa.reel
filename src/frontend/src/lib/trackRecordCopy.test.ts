import { describe, expect, it } from "vitest";
import {
  agreementNote,
  offeredWindows,
  singleWindowNote,
  biasNote,
  headlineSentence,
  learnedSentence,
  NO_MODEL_SOURCE_NOTE,
  num,
  pct,
  signed,
  sourceNote,
  PROVENANCE_NOTE,
} from "./trackRecordCopy";
import type { AccuracyPayload } from "../store/api";

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

describe("biasNote and agreementNote", () => {
  it("names which way the model is wrong", () => {
    expect(biasNote(0.37)).toBe("Reel promises more than you give");
    expect(biasNote(-0.4)).toBe("Reel sells titles short");
  });

  it("calls a near-zero correlation what it is instead of 'still learning'", () => {
    // The live value is −0.004. Dressing that up as progress is the single most tempting
    // dishonesty available on this page.
    expect(agreementNote(-0.004)).toBe("no relationship worth the name");
    expect(agreementNote(0.42)).toBe("does the order match yours?");
    expect(agreementNote(null)).toBe("not measurable yet");
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
