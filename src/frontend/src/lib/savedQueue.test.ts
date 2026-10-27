import { describe, expect, it } from "vitest";
import {
  heldOutLine,
  isSavedSort,
  listSyncLabel,
  MANAGED_LIST_TOOLTIP,
  pinnedPick,
  predictionWithBand,
  queueBelowPin,
  ratingDelta,
  SAVED_INTRO,
  savedAgo,
  tonightCandidates,
  advancedPinLine,
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
  // A card as the server sends it: the label reads `listSync` only, whatever the legacy flag says.
  const card = (listSync: SavedQueueItem["listSync"], onManagedList: boolean): Pick<SavedQueueItem, "listSync" | "onManagedList"> =>
    listSync === undefined ? { onManagedList } : { listSync, onManagedList };
  const label = (listSync: SavedQueueItem["listSync"], onManagedList: boolean) => listSyncLabel(card(listSync, onManagedList));

  it("says syncing only for a pending write-back", () => {
    expect(label("pending", true)?.text).toBe("syncing…");
  });

  it("never says syncing for a card that is simply off the list", () => {
    // The 116: off the list, nothing pending. The old label called every one of them "syncing".
    expect(label("offList", false)?.text).toBe("not on Up Next");
    expect(label("onList", true)?.text).toBe("on Up Next");
  });

  it("the server's listSync wins over the legacy flag", () => {
    // onManagedList is true for pending AND onList, so it cannot tell them apart.
    expect(label("onList", false)?.text).toBe("on Up Next");
    expect(label("offList", true)?.text).toBe("not on Up Next");
  });

  it("an older server without listSync gets no line at all — the legacy flag is not evidence", () => {
    // onManagedList is true while a ListAdd is still queued, so "on Up Next" from it would claim a
    // pending write had landed. Both values of the flag say nothing.
    expect(label(undefined, true)).toBeNull();
    expect(label(undefined, false)).toBeNull();
  });

  it("an unknown value from a newer server reads as unknown, not as syncing", () => {
    for (const onManagedList of [true, false]) {
      expect(label("archived" as unknown as "pending", onManagedList)).toBeNull();
    }
  });

  it("no state of any card says syncing unless it is pending", () => {
    const states = [undefined, "onList", "offList"] as const;
    for (const listSync of states) {
      for (const onManagedList of [true, false]) {
        expect(label(listSync, onManagedList)?.text).not.toBe("syncing…");
      }
    }
  });
});

describe("the offList tooltip (WS-4 CR-4)", () => {
  // Watchlist intake (WS-4 task 8) saves titles that were never written to Up Next, so the old
  // "taken off on Trakt, or the write-back gave up" was a false story for every one of them.
  it("states the fact without guessing why", () => {
    const title = listSyncLabel({ listSync: "offList" })!.title;
    expect(title).toBe("Saved here; not on your Reel — Up Next list on Trakt.");
    expect(title).not.toMatch(/gave up|taken off/i);
  });
});

describe("heldOutLine (WS-4: Saved holds out what the conditions exclude)", () => {
  it("says how many saves the conditions hide, and nothing when none are", () => {
    expect(heldOutLine(0, 5)).toBeNull();
    expect(heldOutLine(undefined, 5)).toBeNull();
    expect(heldOutLine(1, 5)).toBe("1 save is hidden by your conditions.");
    expect(heldOutLine(2, 5)).toBe("2 saves are hidden by your conditions.");
  });

  it("says ALL of them when nothing is left to show, so a full shelf never reads as empty", () => {
    expect(heldOutLine(1, 0)).toBe("Your 1 save is hidden by your conditions.");
    expect(heldOutLine(3, 0)).toBe("All 3 of your saves are hidden by your conditions.");
  });
});

describe("what Saved says about the managed list (R1-09)", () => {
  // Watchlist intake (WS-4 task 8, WatchlistIntake: read-only) puts a Trakt watchlist entry on this
  // shelf and never writes it to Up Next, so "anything saved here lands on it" was false for every
  // imported save — the same card's own tooltip said "not on your Reel — Up Next list".
  const intro = `${SAVED_INTRO.before} ${SAVED_INTRO.listName} ${SAVED_INTRO.after}`;

  it("never says everything on the shelf lands on Up Next, or that the list mirrors the shelf", () => {
    for (const copy of [MANAGED_LIST_TOOLTIP, intro]) {
      expect(copy).not.toMatch(/anything saved here|everything here|in sync with this shelf/i);
      expect(copy).not.toMatch(/what you save here syncs/i);
    }
  });

  it("scopes the list to saves made in Reel and says watchlist imports stay off it", () => {
    for (const copy of [MANAGED_LIST_TOOLTIP, intro]) {
      expect(copy).toMatch(/what you save in Reel/i);
      expect(copy).toMatch(/Trakt watchlist/);
      expect(copy).toMatch(/stays? off (that|the) list/i);
    }
  });

  it("agrees with the card that a save can be here and not on Up Next", () => {
    // The offList card line is the fact; the header must leave room for it, not contradict it.
    expect(listSyncLabel({ listSync: "offList" })!.title).toMatch(/^Saved here; not on your Reel — Up Next/);
    expect(MANAGED_LIST_TOOLTIP).toMatch(/^Reel keeps a Trakt list called “Reel — Up Next”/);
  });
});

// HREV1-D05: after one "Not tonight", Saved pinned Seized at 5.0 — under the founder's For-you bar
// of 6.0 — under "WATCH TONIGHT" with no line at all, because only the server's own pin carries a
// reason. The server's rule (SavedQueue.WatchTonightReason, R1-18): an under-bar pick says so in its
// first words. The pin the client advanced to now carries a line of its own under the same rule.
describe("advancedPinLine — the line on a pin 'Not tonight' advanced to", () => {
  const seized = item({ titleId: "seized", predictedRating: 5.0, runtimeMinutes: 110 });

  it("leads with the shortfall when the pick is under the bar", () => {
    const line = advancedPinLine(seized, { bar: 6.0, expectedAbsoluteError: 1.2, eveningBudgetMinutes: 150 });
    expect(line).toBe(
      "Under 6.0 for you — the next thing in your queue that's streaming now and fits tonight: 5.0 for you (±1.2), 1h 50m inside a 2h 30m evening."
    );
    expect(line!.startsWith("Under 6.0")).toBe(true);
  });

  it("is under the bar at the precision the badge shows — 5.96 shows as 6.0 and clears a 6.0 bar", () => {
    const line = advancedPinLine(item({ titleId: "edge", predictedRating: 5.96, runtimeMinutes: 90 }), {
      bar: 6.0, expectedAbsoluteError: null, eveningBudgetMinutes: 150,
    });
    expect(line).toBe("Next-highest-predicted thing in your queue that's streaming now and fits tonight — 6.0 for you, 1h 30m inside a 2h 30m evening.");
  });

  it("makes no quality claim at all when it cannot tell — never 'highest' or 'good', only 'next'", () => {
    const line = advancedPinLine(item({ titleId: "x", predictedRating: 6.4, runtimeMinutes: 95 }), {
      bar: null, expectedAbsoluteError: 0.9, eveningBudgetMinutes: 120,
    });
    expect(line).toBe("Next-highest-predicted thing in your queue that's streaming now and fits tonight — 6.4 for you (±0.9), 1h 35m inside a 2h evening.");
  });

  it("varies with the fixture: every under-bar rating leads with 'Under'", () => {
    for (const rating of [1.0, 4.04, 5.94, 7.44]) {
      for (const bar of [6.0, 7.5]) {
        const line = advancedPinLine(item({ titleId: "v", predictedRating: rating }), { bar, expectedAbsoluteError: null, eveningBudgetMinutes: 150 })!;
        const under = Number(rating.toFixed(1)) < bar;
        expect(line.startsWith("Under "), `${rating} vs ${bar}`).toBe(under);
      }
    }
  });

  it("never calls the evening budget 'yours' — the served budget may be the default (HREV1-D10)", () => {
    for (const minutes of [90, 120, 150, 240]) {
      const line = advancedPinLine(item({ titleId: "b", predictedRating: 6.8, runtimeMinutes: 80 }), {
        bar: 6.0, expectedAbsoluteError: null, eveningBudgetMinutes: minutes,
      })!;
      expect(line, `${minutes}`).not.toMatch(/your \d/);
      expect(line, `${minutes}`).toMatch(/inside a \dh( \d+m)? evening\.$/);
    }
  });

  it("says nothing for a card that could not have been pinned", () => {
    expect(advancedPinLine(item({ titleId: "u", predictedRating: null }), { bar: 6, expectedAbsoluteError: null, eveningBudgetMinutes: 150 })).toBeNull();
    expect(advancedPinLine(item({ titleId: "r", runtimeMinutes: null }), { bar: 6, expectedAbsoluteError: null, eveningBudgetMinutes: 150 })).toBeNull();
  });
});
