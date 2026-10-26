import { describe, expect, it } from "vitest";
import { scoreBadgesPlan, scoreChipDescription } from "./scoreBadges";

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

describe("scoreChipDescription", () => {
  it("names each number and the band behind the prediction", () => {
    expect(scoreChipDescription({ predictedRating: 8.4, crowdRating: 7.9, crowdVotes: 40210, band: 1.14 })).toBe(
      "Predicted 8.4 for you, give or take 1.1 — what predictions for you have typically been off by. Crowd score 7.9 from 40,210 votes"
    );
  });

  it("says nothing about a band it does not have, or votes it does not know", () => {
    expect(scoreChipDescription({ predictedRating: 8.4, crowdRating: 7.9, band: null })).toBe(
      "Predicted 8.4 for you from your ratings. Crowd score 7.9"
    );
  });

  it("leads with the user's own rating where it replaces the prediction", () => {
    expect(scoreChipDescription({ predictedRating: 8.4, userRating: 9, band: 1.1 })).toBe("You rated it 9");
  });
});
