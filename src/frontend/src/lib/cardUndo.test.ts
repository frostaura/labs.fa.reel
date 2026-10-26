import { describe, expect, it } from "vitest";
import { cardUndoCopy, type CardAction } from "./cardUndo";

const base = { titleId: "t1", mediaType: "movie", tmdbId: 7, name: "Heat" };

describe("cardUndoCopy", () => {
  it("says what happened to the title, per action", () => {
    expect(cardUndoCopy({ kind: "save", ...base }).message).toBe("Saved “Heat” to your queue.");
    expect(cardUndoCopy({ kind: "dismiss", ...base }).message).toBe("“Heat” won’t be picked for you again.");
    expect(cardUndoCopy({ kind: "rate", ...base, rating: 8 }).message).toBe("Rated “Heat” 8 — logged as seen.");
    expect(cardUndoCopy({ kind: "save", ...base }).undoLabel).toBe("Undo");
  });

  it("says a failed undo changed nothing and offers to try again", () => {
    const failed = (action: CardAction) => cardUndoCopy(action, true);
    expect(failed({ kind: "save", ...base })).toEqual({ message: "Couldn’t undo — “Heat” is still saved.", undoLabel: "Try again" });
    expect(failed({ kind: "dismiss", ...base }).message).toBe("Couldn’t undo — “Heat” is still hidden from your picks.");
    expect(failed({ kind: "rate", ...base, rating: 3 }).message).toBe("Couldn’t undo — Your 3 for “Heat” still stands.");
  });
});
