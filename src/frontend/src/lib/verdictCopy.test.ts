import { describe, expect, it } from "vitest";
import {
  type RatingVerdict,
  verdictBand,
  verdictCopy,
  verdictFollowUp,
  verdictHeadline,
  verdictSurface,
  verdictWhen,
} from "./verdictCopy";

const NOW = new Date("2026-09-10T20:00:00Z");

const verdict = (overrides: Partial<RatingVerdict> = {}): RatingVerdict => ({
  predictedBefore: 8.4,
  surface: "FeedHero",
  shownAt: "2026-08-29T20:00:00Z",
  delta: 3.4,
  ...overrides,
});

describe("verdictBand", () => {
  it("calls a half-point miss spot on, and a full point close", () => {
    expect(verdictBand(0)).toBe("spot-on");
    expect(verdictBand(-0.5)).toBe("spot-on");
    expect(verdictBand(0.9)).toBe("close");
    expect(verdictBand(-1)).toBe("close");
  });

  it("names the DIRECTION of a real miss rather than its size", () => {
    // Over and under are different admissions. "I promised more than you got" and "I sold this
    // short" call for different copy, and collapsing them into one apology hides which way the
    // model is broken — which, on this data, is consistently over.
    expect(verdictBand(3.4)).toBe("over");
    expect(verdictBand(-2.1)).toBe("under");
  });
});

describe("verdictHeadline", () => {
  it("puts both numbers first, in second person, with the prediction at one decimal", () => {
    expect(verdictHeadline(8.42, 5)).toBe("Reel said 8.4 · you said 5");
  });

  it("renders a whole-number prediction with its decimal so the two numbers read as a pair", () => {
    expect(verdictHeadline(9, 9)).toBe("Reel said 9.0 · you said 9");
  });
});

describe("verdictFollowUp", () => {
  it("promises a retrain only when the rating actually disagreed", () => {
    // A line saying "that goes into your next retrain" after a rating that confirmed the model is
    // the first string a founder catches out.
    expect(verdictFollowUp("over")).toContain("retrain");
    expect(verdictFollowUp("under")).toContain("retrain");
    expect(verdictFollowUp("spot-on")).not.toContain("retrain");
    expect(verdictFollowUp("close")).not.toContain("retrain");
    // R1-40: the nightly job retrains only when RetrainGate says one is owed (10 new ratings, a
    // 14-day-old model or a calibration breach); one rating changes none of those. The rating is
    // used at the next retrain, whenever that is — never promise "tonight".
    for (const band of ["over", "under", "spot-on", "close"] as const) {
      expect(verdictFollowUp(band)).not.toMatch(/tonight/i);
    }
  });

  it("never apologises, exclaims or claims it will do better", () => {
    const all = (["spot-on", "close", "over", "under"] as const).map(verdictFollowUp);
    for (const line of all) {
      expect(line).not.toMatch(/!/);
      expect(line.toLowerCase()).not.toMatch(/sorry|oops|our ai|we'll do better|improve/);
    }
  });
});

describe("verdictWhen", () => {
  it("counts days, then months, in plain language", () => {
    expect(verdictWhen("2026-09-10T09:00:00Z", NOW)).toBe("shown today");
    expect(verdictWhen("2026-09-09T09:00:00Z", NOW)).toBe("shown yesterday");
    expect(verdictWhen("2026-08-29T20:00:00Z", NOW)).toBe("shown 12 days ago");
    expect(verdictWhen("2026-08-01T20:00:00Z", NOW)).toBe("shown last month");
    expect(verdictWhen("2026-06-10T20:00:00Z", NOW)).toBe("shown 3 months ago");
  });

  it("says nothing when it cannot prove the card was ever on screen", () => {
    // The retro fallback knows a score existed, not that anyone saw it. Deriving "shown 12 days
    // ago" from a scoring timestamp would be the one dishonest string on the page.
    expect(verdictWhen(null, NOW)).toBeNull();
    expect(verdictWhen("not a date", NOW)).toBeNull();
    expect(verdictWhen("2026-09-11T20:00:00Z", NOW)).toBeNull();
  });
});

describe("verdictSurface", () => {
  it("names surfaces the way the product does", () => {
    expect(verdictSurface("FeedHero")).toBe("tonight's picks");
    expect(verdictSurface("Digest")).toBe("the weekly digest");
  });

  it("stays silent on an unknown or absent surface rather than leaking an enum name", () => {
    expect(verdictSurface(null)).toBeNull();
    expect(verdictSurface("SomethingNew")).toBeNull();
  });
});

describe("verdictCopy", () => {
  it("reads as the founder's worst case, plainly", () => {
    expect(verdictCopy(verdict(), 5, NOW)).toBe(
      "Reel said 8.4 · you said 5 — shown 12 days ago on tonight's picks — noted — that goes into your next retrain"
    );
  });

  it("celebrates nothing when it was right — it just says so", () => {
    expect(verdictCopy(verdict({ predictedBefore: 8.4, delta: -0.6, surface: null, shownAt: null }), 9, NOW)).toBe(
      "Reel said 8.4 · you said 9 — close enough to count"
    );
  });

  it("drops the where-and-when clause entirely on the retro fallback", () => {
    const copy = verdictCopy(verdict({ surface: null, shownAt: null }), 5, NOW);
    expect(copy).toBe("Reel said 8.4 · you said 5 — noted — that goes into your next retrain");
    expect(copy).not.toMatch(/shown/);
  });

  it("drops only the surface when the shown-at is known but the surface is not", () => {
    expect(verdictCopy(verdict({ surface: "SomethingNew" }), 5, NOW)).toBe(
      "Reel said 8.4 · you said 5 — shown 12 days ago — noted — that goes into your next retrain"
    );
  });
});
