import { describe, expect, it } from "vitest";
import {
  agreementNote,
  biasNote,
  headlineSentence,
  learnedSentence,
  num,
  pct,
  signed,
  sourceNote,
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
    expect(headlineSentence(payload())).toBe("Within one point on 26% of what you rate");
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
  it("reports a real improvement", () => {
    expect(
      learnedSentence({
        trainedAt: "2026-09-09T03:00:00Z",
        newRatings: 14,
        calibrationBias: 0.2,
        calibrationMae: 1.1,
        maeImprovement: 0.4,
        spearman: 0.31,
        picksChanged: 3,
      })
    ).toBe("Learned from 14 new ratings. Runs 0.40 closer on your scores now. 3 picks changed.");
  });

  it("says outright that a retrain did NOT help, rather than going quiet", () => {
    expect(
      learnedSentence({
        trainedAt: "2026-09-09T03:00:00Z",
        newRatings: 14,
        calibrationBias: 0.2,
        calibrationMae: 1.4,
        maeImprovement: -0.3,
        spearman: 0.1,
        picksChanged: 0,
      })
    ).toBe("Learned from 14 new ratings. It did not get closer on your scores. No picks changed.");
  });

  it("treats an improvement that rounds to nothing as no improvement", () => {
    expect(
      learnedSentence({
        trainedAt: "2026-09-09T03:00:00Z",
        newRatings: 2,
        calibrationBias: null,
        calibrationMae: null,
        maeImprovement: 0.02,
        spearman: null,
        picksChanged: 1,
      })
    ).toContain("did not get closer");
  });
});
