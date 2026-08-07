import { describe, expect, it } from "vitest";
import { hasPredictedRating } from "./predictedRating";

/**
 * The badge formats with `toFixed(1)`. The feed surfaces called it unguarded because the feed DTO
 * happens to be non-nullable, so a nullable score would have crashed the home screen rather than
 * degrading. This pins the guard those surfaces now share.
 */
describe("hasPredictedRating", () => {
  it("accepts a real score, including the edges of the scale", () => {
    expect(hasPredictedRating(0)).toBe(true);
    expect(hasPredictedRating(8.4)).toBe(true);
    expect(hasPredictedRating(10)).toBe(true);
  });

  it("rejects a missing score — an unrated title must show no badge, not crash", () => {
    expect(hasPredictedRating(null)).toBe(false);
    expect(hasPredictedRating(undefined)).toBe(false);
  });

  it("rejects a non-finite score, which would otherwise print 'NaN for you'", () => {
    expect(hasPredictedRating(Number.NaN)).toBe(false);
    expect(hasPredictedRating(Number.POSITIVE_INFINITY)).toBe(false);
  });
});
