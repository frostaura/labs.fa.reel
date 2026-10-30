import { describe, expect, it } from "vitest";
import type { TraktRecommendations } from "../store/feedTypes";
import { hiddenParts, traktRecsCaption, traktRecsNotice } from "./traktRecsCopy";

function recs(over: Partial<TraktRecommendations> = {}): TraktRecommendations {
  return {
    state: "Ok",
    fetchedAt: new Date().toISOString(),
    returnedByTrakt: 0,
    unresolved: 0,
    hiddenAsSeen: 0,
    hiddenAsSetAside: 0,
    hiddenByConditions: 0,
    heldBelowBar: 0,
    bar: null,
    unscoredRemaining: 0,
    items: [],
    ...over,
  };
}

const card = {
  titleId: "t1",
  mediaType: "Movie" as const,
  tmdbId: 1,
  name: "One",
  year: 2020,
  runtimeMinutes: 100,
  posterPath: null,
  backdropPath: null,
  genres: [],
  predictedRating: null,
  crowdRating: null,
  crowdVotes: null,
  whyThis: "",
  isReleased: true,
  providers: [],
  traktRank: 1,
};

describe("traktRecsCopy", () => {
  it("says the list is Trakt's, in Trakt's order, and nothing else when nothing was taken out", () => {
    expect(traktRecsCaption(recs({ returnedByTrakt: 1, items: [card] }))).toBe("Trakt's own picks for you, in Trakt's order.");
  });

  it("counts every reason a recommendation was taken out", () => {
    const r = recs({ returnedByTrakt: 9, hiddenAsSeen: 3, hiddenAsSetAside: 2, hiddenByConditions: 1, heldBelowBar: 1, bar: 7, items: [card] });
    expect(hiddenParts(r)).toEqual([
      "3 you've already seen",
      "2 you've saved or turned down",
      "1 your conditions rule out",
      '1 under your 7.0 "for you" bar',
    ]);
    expect(traktRecsCaption(r)).toBe(
      "Trakt's own picks for you, in Trakt's order. Not shown: 3 you've already seen, 2 you've saved or turned down, 1 your conditions rule out and 1 under your 7.0 \"for you\" bar."
    );
  });

  it("a stale list says Trakt is not answering and how old the list is", () => {
    const fetchedAt = new Date(Date.now() - 3 * 3_600_000).toISOString();
    expect(traktRecsCaption(recs({ state: "Stale", fetchedAt, returnedByTrakt: 1, items: [card] }))).toBe(
      "Trakt isn't answering right now, so this is the list it gave 3h ago, in Trakt's order."
    );
  });

  it("has no notice while there are cards", () => {
    expect(traktRecsNotice(recs({ returnedByTrakt: 1, items: [card] }))).toBeNull();
  });

  it("tells the four empty states apart instead of rendering nothing", () => {
    expect(traktRecsNotice(recs({ state: "NotLinked" }))?.text).toMatch(/no Trakt link/);
    expect(traktRecsNotice(recs({ state: "RelinkRequired" }))?.text).toMatch(/sign in with it again/);
    expect(traktRecsNotice(recs({ state: "Unavailable" }))).toEqual({
      text: "Trakt isn't answering right now, so its recommendations can't be shown.",
      retry: true,
    });
    expect(traktRecsNotice(recs({ returnedByTrakt: 0 }))?.text).toBe("Trakt has no recommendations for you right now.");
    expect(traktRecsNotice(recs({ returnedByTrakt: 5, hiddenAsSeen: 4, hiddenAsSetAside: 1 }))?.text).toBe(
      "Nothing left to show from Trakt's 5: 4 you've already seen and 1 you've saved or turned down."
    );
  });

  it("only an unreachable Trakt offers Retry; a dead link and an honest empty list do not", () => {
    expect(traktRecsNotice(recs({ state: "Unavailable" }))?.retry).toBe(true);
    expect(traktRecsNotice(recs({ state: "RelinkRequired" }))?.retry).toBe(false);
    expect(traktRecsNotice(recs({ state: "NotLinked" }))?.retry).toBe(false);
    expect(traktRecsNotice(recs({ returnedByTrakt: 0 }))?.retry).toBe(false);
  });
});
