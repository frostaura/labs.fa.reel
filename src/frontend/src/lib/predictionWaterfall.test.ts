import { describe, expect, it } from "vitest";
import { breakdownBars, predictionWaterfall, significantContributions, signedStep } from "./predictionWaterfall";

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
    // 0.03 − 0.03 + 0.12 = 0.12 hidden in small steps, printed as the tenth that lands the walk.
    expect(walk.steps.at(-1)).toEqual({ feature: null, label: "everything else", value: 0.1 });
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
    // 0.11 + 0.03 = 0.14 under the fold, printed as a tenth.
    expect(rest[0].value).toBe(0.1);
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

// HREV1-D06: each step was rounded on its own, so the printed chain did not add up. The Girl in the
// Spider's Web read 6.3 → +0.6 → +0.2 → +0.2 → −0.1 → −0.2 → −0.6 → −0.1 → "6.2 for you": the
// printed steps sum to 6.3, not 6.2. The founder checking the arithmetic got a different number from
// the badge.
const tenths = (v: number) => Math.round(v * 10);
const shown = (v: number) => Number(v.toFixed(1));

/** The walk as the page prints it, in whole tenths: start, steps, end. */
function printed(walk: NonNullable<ReturnType<typeof predictionWaterfall>>) {
  return {
    start: tenths(shown(walk.start.value)),
    steps: walk.steps.map((s) => tenths(Number(signedStep(s.value).replace("−", "-")))),
    end: tenths(shown(walk.end)),
  };
}

describe("the printed walk adds up (HREV1-D06)", () => {
  const spidersWeb = {
    baseline: 6.291,
    baselineLabel: "where the model starts for you",
    steps: [
      { feature: "eraAffinity", value: 0.62 },
      { feature: "directorAffinity", value: 0.18 },
      { feature: "simLovedCentroid", value: 0.16 },
      { feature: "traktRating", value: -0.078 },
      { feature: "genreAffinity", value: -0.21 },
      { feature: "dislikedSimilarity", value: -0.63 },
      { feature: "otherFeatures", value: -0.103 },
    ],
  };

  it("lands The Girl in the Spider's Web's printed steps on the printed 6.2", () => {
    const walk = predictionWaterfall(6.23, spidersWeb)!;
    const p = printed(walk);
    expect(p.start).toBe(63);
    expect(p.end).toBe(62);
    expect(p.start + p.steps.reduce((a, b) => a + b, 0)).toBe(p.end);
  });

  it("adds up on every walk, whatever the fixture — 2,000 seeded random walks", () => {
    // A seeded generator, so a failure names a reproducible case. Varies the baseline, the number of
    // steps, their size (including steps hugging the 0.05 fold and the ±x.x5 rounding edges) and sign.
    let seed = 20261027;
    const rand = () => ((seed = (seed * 1103515245 + 12345) % 2 ** 31) / 2 ** 31);
    const features = ["directorAffinity", "genreAffinity", "eraAffinity", "castAffinity", "traktRating", "runtime", "dislikedSimilarity", "otherFeatures"];
    for (let i = 0; i < 2000; i++) {
      const n = 1 + Math.floor(rand() * features.length);
      const steps = features.slice(0, n).map((feature) => {
        const edge = rand() < 0.3;
        const magnitude = edge ? 0.05 * (1 + Math.floor(rand() * 8)) + (rand() - 0.5) * 0.002 : rand() * 1.2;
        return { feature, value: (rand() < 0.5 ? -1 : 1) * magnitude };
      });
      const baseline = 3 + rand() * 5;
      const predicted = baseline + steps.reduce((sum, s) => sum + s.value, 0);
      const walk = predictionWaterfall(predicted, { baseline, baselineLabel: "start", steps });
      expect(walk, `walk ${i} refused`).not.toBeNull();
      const p = printed(walk!);
      expect(p.start + p.steps.reduce((a, b) => a + b, 0), `walk ${i}: ${JSON.stringify({ baseline, steps, predicted })}`).toBe(p.end);
      for (const step of walk!.steps) {
        // No printed step is a "+0.0" — a step that rounds away is folded, never shown as nothing.
        expect(tenths(step.value)).not.toBe(0);
      }
    }
  });

  it("keeps each named step within a tenth of its own value and never flips its sign", () => {
    const walk = predictionWaterfall(6.23, spidersWeb)!;
    for (const step of walk.steps) {
      if (step.feature === null) continue;
      const raw = spidersWeb.steps.find((s) => s.feature === step.feature)!.value;
      expect(Math.sign(step.value)).toBe(Math.sign(raw));
      expect(Math.abs(step.value - raw)).toBeLessThanOrEqual(0.1 + 1e-9);
    }
  });
});

// HREV1-D02: the walk read "−0.1 crowd rating" while the bar list beneath it drew crowd rating as
// a green bar ("pushed it up"), drew "your contrarian lens" as a big push the walk never took, and
// sized a +0.6 step full and a −0.6 step at about 60%. Two attribution sources, one panel.
describe("breakdownBars — one attribution source for the walk and its bars (HREV1-D02)", () => {
  const walkPayload = {
    baseline: 6.291,
    baselineLabel: "where the model starts for you",
    steps: [
      { feature: "eraAffinity", value: 0.62 },
      { feature: "traktRating", value: -0.078 },
      { feature: "dislikedSimilarity", value: -0.63 },
      { feature: "otherFeatures", value: -0.103 },
    ],
  };
  // FastTree's normalised contributions: a different source with different signs and features.
  const contributions = [
    { feature: "contrarianLens", value: 0.9 },
    { feature: "eraAffinity", value: 1.0 },
    { feature: "traktRating", value: 0.079 },
    { feature: "dislikedSimilarity", value: -0.6 },
  ];

  it("draws every bar from the walk's own steps when there is a walk: same features, same signs", () => {
    const walk = predictionWaterfall(6.1, walkPayload)!;
    const bars = breakdownBars(walk, contributions);
    expect(bars.map((b) => b.label)).toEqual(walk.steps.map((s) => s.label));
    for (const bar of bars) {
      const step = walk.steps.find((s) => s.label === bar.label)!;
      expect(Math.sign(bar.value)).toBe(Math.sign(step.value));
    }
    expect(bars.find((b) => b.feature === "traktRating")!.value).toBeLessThan(0);
    expect(bars.some((b) => b.feature === "contrarianLens")).toBe(false);
  });

  it("sizes bars on the walk's scale: equal steps draw equal bars", () => {
    const walk = predictionWaterfall(6.1, walkPayload)!;
    const bars = breakdownBars(walk, contributions);
    const era = bars.find((b) => b.feature === "eraAffinity")!;
    const passed = bars.find((b) => b.feature === "dislikedSimilarity")!;
    expect(era.widthPct).toBe(100);
    expect(passed.widthPct).toBe(100);
  });

  it("falls back to the model's contribution bars only when there is no walk to contradict", () => {
    const bars = breakdownBars(null, contributions);
    expect(bars.map((b) => b.feature)).toEqual(["contrarianLens", "eraAffinity", "traktRating", "dislikedSimilarity"]);
    expect(bars.find((b) => b.feature === "eraAffinity")!.widthPct).toBe(100);
  });
});
