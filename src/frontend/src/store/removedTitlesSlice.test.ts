import { describe, expect, it } from "vitest";
import reducer, {
  removalsCleared,
  selectHiddenKeys,
  titleRemovalFailed,
  titleRemovalStarted,
  titleRemoved,
  titleRestored,
} from "./removedTitlesSlice";

const initial = () => reducer(undefined, { type: "@@init" });

describe("removedTitlesSlice", () => {
  it("keys on the title GUID so two un-tmdb'd titles never collide", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-a" }));
    s = reducer(s, titleRemoved({ titleId: "guid-b" }));
    // Distinct GUIDs → distinct keys; removing one never hides the other.
    expect(s.keys).toEqual(["guid-a", "guid-b"]);
  });

  it("removes and restores a title exactly", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-a" }));
    expect(s.keys).toEqual(["guid-a"]);

    s = reducer(s, titleRestored({ titleId: "guid-a" }));
    expect(s.keys).toEqual([]);
  });

  it("does not duplicate a key removed twice", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-1" }));
    s = reducer(s, titleRemoved({ titleId: "guid-1" }));
    expect(s.keys).toEqual(["guid-1"]);
  });

  it("clears all removals on a feed rebuild", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-1" }));
    s = reducer(s, titleRemoved({ titleId: "guid-2" }));
    expect(s.keys).toHaveLength(2);
    s = reducer(s, removalsCleared());
    expect(s.keys).toEqual([]);
  });
});

describe("removedTitlesSlice — in flight (HREV7-DESK-01)", () => {
  const hidden = (s: ReturnType<typeof initial>) => selectHiddenKeys({ removedTitles: s } as never);

  it("hides a title while its removal is in flight, without confirming it", () => {
    const s = reducer(initial(), titleRemovalStarted({ titleId: "guid-a" }));
    expect(s.keys).toEqual([]);
    expect(hidden(s)).toEqual(["guid-a"]);
  });

  it("a confirmation moves it from in flight to confirmed", () => {
    let s = reducer(initial(), titleRemovalStarted({ titleId: "guid-a" }));
    s = reducer(s, titleRemoved({ titleId: "guid-a" }));
    expect(s).toEqual({ keys: ["guid-a"], pending: [] });
  });

  it("a failure releases only its own hold", () => {
    let s = reducer(initial(), titleRemovalStarted({ titleId: "guid-a" }));
    s = reducer(s, titleRemovalStarted({ titleId: "guid-a" }));
    s = reducer(s, titleRemovalStarted({ titleId: "guid-b" }));
    s = reducer(s, titleRemovalFailed({ titleId: "guid-a" }));
    // The second action on guid-a is still out, so guid-a stays hidden.
    expect(hidden(s)).toEqual(["guid-a", "guid-b"]);
    s = reducer(s, titleRemovalFailed({ titleId: "guid-a" }));
    expect(hidden(s)).toEqual(["guid-b"]);
    // A stray failure for a title with no hold changes nothing.
    s = reducer(s, titleRemovalFailed({ titleId: "guid-z" }));
    expect(hidden(s)).toEqual(["guid-b"]);
  });

  it("a failure never un-hides a title that was already confirmed removed", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-a" }));
    s = reducer(s, titleRemovalStarted({ titleId: "guid-a" }));
    s = reducer(s, titleRemovalFailed({ titleId: "guid-a" }));
    expect(hidden(s)).toEqual(["guid-a"]);
  });

  it("a feed rebuild clears the confirmed set but keeps what is still in flight", () => {
    let s = reducer(initial(), titleRemoved({ titleId: "guid-1" }));
    s = reducer(s, titleRemovalStarted({ titleId: "guid-2" }));
    s = reducer(s, removalsCleared());
    expect(s).toEqual({ keys: [], pending: ["guid-2"] });
    expect(hidden(s)).toEqual(["guid-2"]);
  });

  it("returns the same array while nothing changes, so a subscriber does not re-render", () => {
    const s = reducer(reducer(initial(), titleRemoved({ titleId: "guid-1" })), titleRemovalStarted({ titleId: "guid-2" }));
    expect(hidden(s)).toBe(hidden(s));
  });
});
