import { describe, expect, it } from "vitest";
import { ratingPopoverNote } from "./ratingNote";

describe("ratingPopoverNote (R1-40: the play line follows ReactionEndpoints, not the media type alone)", () => {
  // The server logs a play (AddToHistory) only for a movie with no play yet; a movie already in the
  // Trakt history gets the rating alone. The line used to promise the play for every movie.
  it("promises a logged play only for a movie with no play yet", () => {
    expect(ratingPopoverNote("Movie", 0)).toBe("Also logs a play on your Trakt history — cleared if you remove the rating.");
  });

  it("an already-watched movie is told the rating syncs alone", () => {
    for (const plays of [1, 2, 7]) {
      const note = ratingPopoverNote("Movie", plays);
      expect(note).not.toMatch(/logs a play/i);
      expect(note).toBe("Ratings sync to Trakt — this movie is already in your history, so no play is added.");
    }
  });

  it("a show never touches the episode history, watched or not", () => {
    for (const plays of [0, 3]) {
      expect(ratingPopoverNote("Show", plays)).toBe("Ratings sync to Trakt — your episode history is never touched.");
    }
  });
});
