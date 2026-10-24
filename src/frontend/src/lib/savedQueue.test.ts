import { describe, expect, it } from "vitest";
import {
  isSavedSort,
  listSyncLabel,
  pinnedPick,
  predictionWithBand,
  queueBelowPin,
  ratingDelta,
  savedAgo,
  tonightCandidates,
} from "./savedQueue";
import type { SavedQueueItem } from "../store/savedTypes";

function item(overrides: Partial<SavedQueueItem> & { titleId: string }): SavedQueueItem {
  return {
    mediaType: "Movie",
    tmdbId: 1,
    name: overrides.titleId,
    year: 2020,
    posterPath: null,
    runtimeMinutes: 100,
    predictedRating: 7,
    crowdRating: null,
    crowdVotes: null,
    providers: [],
    availableNow: true,
    savedAt: "2026-09-01T00:00:00Z",
    daysSaved: 8,
    fitsEveningBudget: true,
    onManagedList: true,
    watchTonight: false,
    watchTonightReason: null,
    ...overrides,
  };
}

describe("watch-tonight candidates", () => {
  it("ranks by prediction, then by shortest, and mirrors the server's pick at the head", () => {
    const winner = item({ titleId: "a", predictedRating: 8.4, runtimeMinutes: 118, watchTonight: true });
    const alsoFits = item({ titleId: "b", predictedRating: 7.1, runtimeMinutes: 92 });
    const sameScoreShorter = item({ titleId: "c", predictedRating: 8.4, runtimeMinutes: 95 });

    const candidates = tonightCandidates([alsoFits, winner, sameScoreShorter], 150);

    expect(candidates.map((c) => c.titleId)).toEqual(["c", "a", "b"]);
    // The client's head and the server's flag are two implementations of one rule; if they can
    // disagree, "not tonight" starts from a different place than the pinned card.
    expect(tonightCandidates([winner, alsoFits], 150)[0].titleId).toBe(
      [winner, alsoFits].find((i) => i.watchTonight)!.titleId
    );
  });

  it("refuses anything it cannot honestly pin", () => {
    const notStreaming = item({ titleId: "a", predictedRating: 9.9, availableNow: false });
    // The pin's whole claim is "this fits your evening" — a title of unknown length cannot make it.
    const unknownRuntime = item({ titleId: "b", predictedRating: 9.9, runtimeMinutes: null });
    const unscored = item({ titleId: "c", predictedRating: null });
    const tooLong = item({ titleId: "d", predictedRating: 9.9, runtimeMinutes: 151 });

    expect(tonightCandidates([notStreaming, unknownRuntime, unscored, tooLong], 150)).toEqual([]);
    // Exactly at the budget is inside it.
    expect(tonightCandidates([item({ titleId: "e", runtimeMinutes: 150 })], 150)).toHaveLength(1);
  });

  it("orders totally, so the pinned card cannot swap between refetches", () => {
    const a = item({ titleId: "aaa", predictedRating: 7, runtimeMinutes: 100 });
    const b = item({ titleId: "bbb", predictedRating: 7, runtimeMinutes: 100 });

    expect(tonightCandidates([a, b], 150).map((c) => c.titleId)).toEqual(
      tonightCandidates([b, a], 150).map((c) => c.titleId)
    );
  });

  it("prefers the newer save when score and runtime tie", () => {
    const older = item({ titleId: "a", savedAt: "2026-01-01T00:00:00Z" });
    const newer = item({ titleId: "b", savedAt: "2026-09-01T00:00:00Z" });

    expect(tonightCandidates([older, newer], 150).map((c) => c.titleId)).toEqual(["b", "a"]);
  });
});

describe("skipping with “not tonight”", () => {
  const first = item({ titleId: "a", predictedRating: 9 });
  const second = item({ titleId: "b", predictedRating: 8 });
  const unwatchable = item({ titleId: "c", predictedRating: 10, availableNow: false });

  it("advances to the next candidate and stops rather than wrapping", () => {
    const shelf = [first, second, unwatchable];
    expect(pinnedPick(shelf, 150, 0)!.titleId).toBe("a");
    expect(pinnedPick(shelf, 150, 1)!.titleId).toBe("b");
    // Having declined every candidate, re-offering the first is the app arguing with the user.
    expect(pinnedPick(shelf, 150, 2)).toBeUndefined();
  });

  it("keeps the pinned title out of the grid below it, and only that one", () => {
    const shelf = [first, second, unwatchable];
    expect(queueBelowPin(shelf, pinnedPick(shelf, 150, 0)).map((i) => i.titleId)).toEqual(["b", "c"]);
    // No pin ⇒ nothing is hidden; the whole queue still renders.
    expect(queueBelowPin(shelf, undefined)).toHaveLength(3);
  });
});

describe("honest presentation", () => {
  it("prints the error band with the number, and omits it when unmeasured", () => {
    expect(predictionWithBand(8.42, 1.94)).toBe("8.4 ± 1.9");
    // No pairs yet ⇒ no band. "± 0.0" would be the least honest number on the screen.
    expect(predictionWithBand(8.42, null)).toBe("8.4");
    expect(predictionWithBand(8.42, 0)).toBe("8.4");
    expect(predictionWithBand(null, 1.9)).toBeNull();
    expect(predictionWithBand(Number.NaN, 1.9)).toBeNull();
  });

  it("states the delta plainly, including when it was badly wrong", () => {
    expect(ratingDelta(8.4, 6)).toBe("Reel said 8.4 · you said 6 — noted");
    expect(ratingDelta(9, 2)).toBe("Reel said 9.0 · you said 2 — noted");
  });

  it("ages a save in words", () => {
    expect(savedAgo(0)).toBe("saved today");
    expect(savedAgo(1)).toBe("saved yesterday");
    expect(savedAgo(41)).toBe("saved 41 days ago");
  });

  it("narrows sort values that arrive as strings", () => {
    expect(isSavedSort("tonight")).toBe(true);
    expect(isSavedSort("runtime")).toBe(true);
    expect(isSavedSort("nonsense")).toBe(false);
    expect(isSavedSort(null)).toBe(false);
  });
});

describe("listSyncLabel (G-3)", () => {
  it("says syncing only for a pending write-back", () => {
    expect(listSyncLabel({ listSync: "pending", onManagedList: true })?.text).toBe("syncing…");
  });

  it("never says syncing for a card that is simply off the list", () => {
    // The 116: off the list, nothing pending. The old label called every one of them "syncing".
    expect(listSyncLabel({ listSync: "offList", onManagedList: false })?.text).toBe("not on Up Next");
    expect(listSyncLabel({ listSync: "onList", onManagedList: true })?.text).toBe("on Up Next");
  });

  it("the server's listSync wins over the legacy flag", () => {
    // onManagedList is true for pending AND onList, so it cannot tell them apart.
    expect(listSyncLabel({ listSync: "onList", onManagedList: false })?.text).toBe("on Up Next");
    expect(listSyncLabel({ listSync: "offList", onManagedList: true })?.text).toBe("not on Up Next");
  });

  it("an older server without listSync never yields syncing", () => {
    expect(listSyncLabel({ onManagedList: true })?.text).toBe("on Up Next");
    expect(listSyncLabel({ onManagedList: false })).toBeNull();
  });

  it("an unknown value from a newer server reads as unknown, not as syncing", () => {
    const fromTheFuture = { listSync: "archived" as unknown as "pending", onManagedList: false };
    expect(listSyncLabel(fromTheFuture)).toBeNull();
  });

  it("no state of any card says syncing unless it is pending", () => {
    const states = [undefined, "onList", "offList"] as const;
    for (const listSync of states) {
      for (const onManagedList of [true, false]) {
        expect(listSyncLabel({ listSync, onManagedList })?.text).not.toBe("syncing…");
      }
    }
  });
});
