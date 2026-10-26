import { describe, expect, it } from "vitest";
import { bandText, usableBand } from "./forYouBand";

describe("the for-you band", () => {
  it("prints a measured band to one decimal", () => {
    expect(bandText(1.14)).toBe("±1.1");
    expect(bandText(1.96)).toBe("±2.0");
  });

  it("prints nothing for an unmeasured band — never ±0.0", () => {
    for (const band of [null, undefined, 0, -0.3, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(bandText(band)).toBeNull();
      expect(usableBand(band)).toBeNull();
    }
  });
});
