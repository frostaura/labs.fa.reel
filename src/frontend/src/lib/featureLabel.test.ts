import { describe, expect, it } from "vitest";
import { featureLabel } from "./featureLabel";

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
});
