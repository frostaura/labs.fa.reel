import { describe, expect, it } from "vitest";
import reducer, { titleRemoved, titleRestored, removalsCleared } from "./removedTitlesSlice";

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
