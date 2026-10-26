import { describe, expect, it } from "vitest";
import { DIGEST_TITLE_HIGH, DIGEST_TITLE_RELAXED, digestRailCopy } from "./digestCopy";

const RELAXED =
  "Only 0 of this week's arrivals cleared the 7 “high for you” bar, so it was relaxed to 6 for this digest.";

describe("digestRailCopy (IR1S-03)", () => {
  it("claims 'high for you' only when the build met the bar", () => {
    expect(digestRailCopy(null)).toEqual({ title: DIGEST_TITLE_HIGH, caption: null });
    // A digest written before the field existed has no key at all.
    expect(digestRailCopy(undefined)).toEqual({ title: DIGEST_TITLE_HIGH, caption: null });
  });

  it("retitles a relaxed digest and shows the build's reason", () => {
    const copy = digestRailCopy(RELAXED);
    expect(copy.title).toBe(DIGEST_TITLE_RELAXED);
    expect(copy.title).not.toMatch(/high for you/i);
    expect(copy.caption).toBe(RELAXED);
  });

  it("treats a blank reason as no reason, as the API does", () => {
    expect(digestRailCopy("   ")).toEqual({ title: DIGEST_TITLE_HIGH, caption: null });
  });
});
