import { describe, expect, it } from "vitest";
import { scoreBadgesPlan } from "./scoreBadges";

describe("scoreBadgesPlan", () => {
  it("renders both pills when prediction and crowd score are present", () => {
    expect(scoreBadgesPlan({ predictedRating: 8.4, crowdRating: 7.9 })).toEqual({
      empty: false,
      showUserRating: false,
      showPredicted: true,
      showCrowd: true,
    });
  });

  it("renders only the prediction when the title has no community score", () => {
    const plan = scoreBadgesPlan({ predictedRating: 8.4, crowdRating: null });
    expect(plan.showPredicted).toBe(true);
    expect(plan.showCrowd).toBe(false);
    expect(plan.empty).toBe(false);
  });

  it("renders only the crowd pill when there is no prediction", () => {
    const plan = scoreBadgesPlan({ predictedRating: null, crowdRating: 6.1 });
    expect(plan.showPredicted).toBe(false);
    expect(plan.showCrowd).toBe(true);
    expect(plan.empty).toBe(false);
  });

  it("is empty when there is nothing to show — the wrapper (and scrim) must not render", () => {
    expect(scoreBadgesPlan({}).empty).toBe(true);
    expect(scoreBadgesPlan({ predictedRating: null, crowdRating: null, userRating: null }).empty).toBe(true);
  });

  it("treats NaN as absent, matching hasPredictedRating on every surface", () => {
    const plan = scoreBadgesPlan({ predictedRating: NaN, crowdRating: NaN, userRating: NaN });
    expect(plan.empty).toBe(true);
  });

  it("lets an explicit user rating replace the prediction, not join it", () => {
    const plan = scoreBadgesPlan({ predictedRating: 8.4, crowdRating: 7.9, userRating: 9 });
    expect(plan.showUserRating).toBe(true);
    expect(plan.showPredicted).toBe(false);
    expect(plan.showCrowd).toBe(true);
  });

  it("shows the prediction when the user rating is null (Person filmography without a rating)", () => {
    const plan = scoreBadgesPlan({ predictedRating: 7.2, userRating: null });
    expect(plan.showUserRating).toBe(false);
    expect(plan.showPredicted).toBe(true);
  });
});
