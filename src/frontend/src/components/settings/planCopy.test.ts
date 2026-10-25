import { describe, expect, it } from "vitest";
import { FREE_INCLUDES, PAID_ADDS, isPaidTier, planLine, proseList } from "./planCopy";

describe("planLine — the plan card says only what the server enforces", () => {
  it("Free lists what Free has and what Paid adds", () => {
    expect(planLine("Free")).toBe(
      "Free includes taste DNA, the track record, a daily three-pick shortlist, Ask Reel and your Saved queue. " +
        "Paid adds every recommendation row, rows that keep loading as you scroll, genre rails and rebuilding your picks on demand.",
    );
  });

  it("never claims Paid unlocks Ask Reel or the Trakt queue — neither is gated on the server", () => {
    expect(PAID_ADDS.join(" ")).not.toMatch(/ask reel|queue|saved|trakt/i);
    expect(FREE_INCLUDES).toContain("Ask Reel");
  });

  it("Paid and Founder each get their own line", () => {
    expect(planLine("Paid")).toMatch(/^Paid — every recommendation row/);
    expect(planLine("Founder")).toBe("Founder access — everything unlocked.");
  });

  it("an unknown tier reads as Free, the server's default", () => {
    expect(planLine("Enterprise")).toBe(planLine("Free"));
  });

  it("isPaidTier is true only for tiers with nothing left to buy", () => {
    expect([isPaidTier("Free"), isPaidTier("Paid"), isPaidTier("Founder"), isPaidTier("")]).toEqual([false, true, true, false]);
  });

  it("joins lists as prose", () => {
    expect(proseList([])).toBe("");
    expect(proseList(["a"])).toBe("a");
    expect(proseList(["a", "b"])).toBe("a and b");
    expect(proseList(["a", "b", "c"])).toBe("a, b and c");
  });

  it("no plan copy promises a future", () => {
    for (const tier of ["Free", "Paid", "Founder"]) {
      expect(planLine(tier)).not.toMatch(/coming soon|soon|when .* land/i);
    }
  });
});
