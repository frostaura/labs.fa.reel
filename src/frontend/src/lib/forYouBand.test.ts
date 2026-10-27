import { describe, expect, it } from "vitest";
import { bandText, firstUsableBand, usableBand } from "./forYouBand";

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

describe("firstUsableBand", () => {
  // /api/feed now carries the calibrator's band itself (WS-5), the same number Saved shows; the
  // recently-watched read stays the fallback for a page that never loaded the feed.
  it("prefers the caller's band, then the feed's, then the recently-watched read", () => {
    expect(firstUsableBand(0.9, 1.1, 1.3)).toBe(0.9);
    expect(firstUsableBand(undefined, 1.1, 1.3)).toBe(1.1);
    expect(firstUsableBand(null, null, 1.3)).toBe(1.3);
  });

  it("skips an unmeasured source rather than printing it", () => {
    expect(firstUsableBand(0, Number.NaN, 1.3)).toBe(1.3);
    expect(firstUsableBand(undefined, undefined, undefined)).toBeNull();
  });
});
