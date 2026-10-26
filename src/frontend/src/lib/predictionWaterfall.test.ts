import { describe, expect, it } from "vitest";
import { predictionWaterfall, significantContributions, signedStep } from "./predictionWaterfall";

const payload = {
  baseline: 6.7,
  baselineLabel: "your average",
  steps: [
    { feature: "directorAffinity", value: 0.8 },
    { feature: "genreAffinity", value: 0.9 },
    { feature: "eraAffinity", value: -0.2 },
    { feature: "castAffinity", value: 0.03 },
    { feature: "runtime", value: -0.03 },
  ],
};

describe("predictionWaterfall", () => {
  it("walks from the baseline to the prediction, hiding steps under 0.05", () => {
    const walk = predictionWaterfall(8.2, payload)!;
    expect(walk.start).toEqual({ label: "your average", value: 6.7 });
    expect(walk.steps.map((s) => s.value)).toEqual([0.8, 0.9, -0.2]);
    expect(walk.end).toBe(8.2);
  });

  it("folds the small steps into one 'everything else' line when their sum is itself visible", () => {
    const walk = predictionWaterfall(8.3, {
      ...payload,
      steps: [...payload.steps, { feature: "a", value: 0.04 }, { feature: "b", value: 0.04 }, { feature: "c", value: 0.04 }],
    })!;
    // 0.03 − 0.03 + 0.12 = 0.12 hidden in small steps.
    expect(walk.steps.at(-1)).toEqual({ label: "everything else", value: expect.closeTo(0.12, 5) });
  });

  it("refuses to draw a walk that does not land on the prediction", () => {
    expect(predictionWaterfall(9.0, payload)).toBeNull();
  });

  it("draws nothing without a server walk — the bars are not additive and are never summed", () => {
    expect(predictionWaterfall(8.2, null)).toBeNull();
    expect(predictionWaterfall(8.2, undefined)).toBeNull();
  });
});

// WS-5's walk (OnDemandScorer.WaterfallAsync): up to six named features, then the rest of the
// model as "otherFeatures", then the serving-time corrections, from "where the model starts for you".
describe("predictionWaterfall over WS-5's server walk", () => {
  const server = {
    baseline: 6.512,
    baselineLabel: "where the model starts for you",
    steps: [
      { feature: "directorAffinity", value: 0.81 },
      { feature: "simLovedCentroid", value: 0.42 },
      { feature: "traktRating", value: 0.03 },
      { feature: "otherFeatures", value: 0.11 },
      { feature: "recentRatingsCorrection", value: 0.2 },
      { feature: "calibrationToYourScale", value: 0.268 },
      { feature: "dislikedSimilarity", value: -0.15 },
    ],
  };

  it("lands on the shown number and names every serving-time step in the reader's words", () => {
    const walk = predictionWaterfall(8.2, server)!;
    expect(walk.start.label).toBe("where the model starts for you");
    const labels = walk.steps.map((s) => s.label);
    expect(labels).toContain("your recent ratings");
    expect(labels).toContain("fitted to how you rate");
    expect(labels).toContain("similar to titles you passed on");
    for (const label of labels) expect(label).not.toMatch(/[A-Z]|otherFeatures/);
  });

  it("merges the server's otherFeatures with the hidden small steps into ONE 'everything else' line", () => {
    const walk = predictionWaterfall(8.2, server)!;
    const rest = walk.steps.filter((s) => s.label === "everything else");
    expect(rest).toHaveLength(1);
    expect(rest[0].value).toBeCloseTo(0.14, 5);
    expect(walk.steps.at(-1)?.label).toBe("everything else");
  });
});

describe("signedStep and significantContributions", () => {
  it("signs a step with a real minus", () => {
    expect(signedStep(0.84)).toBe("+0.8");
    expect(signedStep(-0.26)).toBe("−0.3");
    expect(signedStep(0)).toBe("+0.0");
  });

  it("drops bars under 0.05 and non-numbers", () => {
    expect(
      significantContributions([{ value: 0.4 }, { value: 0.049 }, { value: -0.05 }, { value: Number.NaN }]).map((c) => c.value)
    ).toEqual([0.4, -0.05]);
  });
});
