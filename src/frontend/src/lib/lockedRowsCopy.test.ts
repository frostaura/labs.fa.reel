import { describe, expect, it } from "vitest";
import { lockedRowsCaption, lockedRowsHeadline } from "./lockedRowsCopy";
import { PAID_ADDS } from "../components/settings/planCopy";

describe("the free tier's locked-rows card (R1-40)", () => {
  // Only FeedEndpoints gates by tier. Ask Reel and Saved work on Free, so the old "the paid feed
  // unlocks … natural-language search and the managed Trakt queue" sold two things Free already has.
  it("never sells Ask Reel, search or the Trakt queue as paid", () => {
    expect(lockedRowsCaption()).not.toMatch(/ask reel|natural-language|search|queue|saved|trakt/i);
  });

  it("names exactly what Paid adds, in planCopy's words", () => {
    const caption = lockedRowsCaption();
    for (const item of PAID_ADDS) expect(caption).toContain(item);
    expect(caption).toBe(
      "The free shortlist gives you three picks a day. Paid adds every recommendation row, rows that keep loading as you scroll, genre rails and rebuilding your picks on demand."
    );
  });

  it("counts the locked rows in the headline, singular and plural", () => {
    expect(lockedRowsHeadline(1)).toBe("1 more personalised row on the full feed");
    expect(lockedRowsHeadline(3)).toBe("3 more personalised rows on the full feed");
  });
});
