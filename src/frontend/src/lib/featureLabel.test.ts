import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { FEATURE_LABELS, featureLabel } from "./featureLabel";

const backend = (path: string) => readFileSync(new URL(`../../../backend/FrostAura.Reel.Application/${path}`, import.meta.url), "utf8");

/** FeatureSchema.Scalars, read from the C# source so a new scalar cannot ship without a label. */
function schemaScalars(): string[] {
  const source = backend("Ml/FeatureSchema.cs");
  const block = source.match(/public static readonly string\[\] Scalars =\s*\[([\s\S]*?)\];/);
  if (!block) throw new Error("FeatureSchema.Scalars not found — the parser needs updating");
  return Array.from(block[1].replace(/\/\/.*$/gm, "").matchAll(/"([A-Za-z]+)"/g), (m) => m[1]);
}

/** The walk's synthetic step names (OnDemandScorer's PredictionWaterfall constants). */
function waterfallSteps(): string[] {
  const source = backend("Ml/OnDemandScorer.cs");
  return Array.from(source.matchAll(/public const string (?:OtherFeatures|RecentRatingsCorrection|Calibration) = "([A-Za-z]+)";/g), (m) => m[1]);
}

describe("featureLabel", () => {
  it("names the release decade neutrally, never as the viewer's favourite (R1-24)", () => {
    expect(featureLabel("decadeAffinity")).toBe("release era");
    expect(featureLabel("decadeAffinity")).not.toMatch(/favou?rite|your/i);
  });

  it("keeps the one-hot setting block distinct from the release era", () => {
    expect(featureLabel("era:victorian")).toBe("victorian setting");
    expect(featureLabel("hasAttributesEra")).toBe("setting data");
  });

  it("maps the prefixed blocks to their human word", () => {
    expect(featureLabel("genre:science-fiction")).toBe("science fiction");
    expect(featureLabel("tone:slow-burn")).toBe("slow burn tone");
    expect(featureLabel("subgenreBucket:heist")).toBe("theme match");
  });

  it("never shows a raw token for an unmapped feature", () => {
    expect(featureLabel("someFutureFeature")).toBe("some future feature");
  });

  it("no label claims an era is the viewer's", () => {
    const features = ["decadeAffinity", "releaseAgeLog", "era:1980s", "hasAttributesEra"];
    for (const f of features) expect(featureLabel(f), f).not.toMatch(/favou?rite era|your era/i);
  });

  // R1-41 (WS-5 CR-5): knownPeopleFraction and its v5 siblings fell through to the camelCase
  // humanizer ("known people fraction"), a raw token in words. Every scalar gets a written label.
  it("labels every FeatureSchema scalar explicitly, never by the fallback", () => {
    const scalars = schemaScalars();
    expect(scalars.length).toBeGreaterThanOrEqual(40);
    expect(scalars).toContain("knownPeopleFraction");
    const missing = scalars.filter((name) => !(name in FEATURE_LABELS));
    expect(missing).toEqual([]);
  });

  it("labels every synthetic step of the title page's walk", () => {
    const steps = waterfallSteps();
    expect(steps).toEqual(["otherFeatures", "recentRatingsCorrection", "calibrationToYourScale"]);
    for (const step of steps) expect(FEATURE_LABELS[step], step).toBeTruthy();
  });

  it("names the people-coverage features for what they measure", () => {
    expect(featureLabel("knownPeopleFraction")).toBe("cast & crew you know from your ratings");
    expect(featureLabel("knownDirectorOrWriter")).toBe("a director or writer you know from your ratings");
    expect(featureLabel("isUnscripted")).toBe("unscripted format");
  });
});
