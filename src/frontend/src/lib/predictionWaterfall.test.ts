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
