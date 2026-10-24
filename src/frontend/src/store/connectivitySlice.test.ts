import { describe, expect, it } from "vitest";
import reducer, { offlineSnapshot, servedFromCache, servedLive } from "./connectivitySlice";

const everywhere = () => true;

describe("connectivitySlice", () => {
  it("keeps the oldest saved time across several cached answers", () => {
    let s = reducer(undefined, servedFromCache({ key: "getFeed(undefined)", savedAt: "2026-10-24T10:30:00.000Z" }));
    s = reducer(s, servedFromCache({ key: "getSaved(undefined)", savedAt: "2026-10-24T10:00:00.000Z" }));
    s = reducer(s, servedFromCache({ key: "getTasteDna(undefined)", savedAt: "2026-10-24T10:45:00.000Z" }));
    expect(offlineSnapshot(s, everywhere)).toEqual({ fromCache: true, savedAt: "2026-10-24T10:00:00.000Z" });
  });

  it("a cached answer without a date still marks the page", () => {
    const s = reducer(undefined, servedFromCache({ key: "getFeed(undefined)", savedAt: null }));
    expect(offlineSnapshot(s, everywhere)).toEqual({ fromCache: true, savedAt: null });
  });

  it("a live answer clears only its own query (I-11)", () => {
    let s = reducer(undefined, servedFromCache({ key: "getFeed(undefined)", savedAt: "2026-10-24T10:00:00.000Z" }));
    // The status poll answers live a minute later; the feed on screen is still the cached one.
    s = reducer(s, servedLive({ key: "getSyncStatus(undefined)" }));
    expect(offlineSnapshot(s, everywhere)).toEqual({ fromCache: true, savedAt: "2026-10-24T10:00:00.000Z" });

    s = reducer(s, servedLive({ key: "getFeed(undefined)" }));
    expect(offlineSnapshot(s, everywhere)).toEqual({ fromCache: false, savedAt: null });
  });

  it("an answer for a page the user has left no longer speaks for the screen", () => {
    let s = reducer(undefined, servedFromCache({ key: "getSaved(undefined)", savedAt: "2026-10-24T09:00:00.000Z" }));
    s = reducer(s, servedFromCache({ key: "getFeed(undefined)", savedAt: "2026-10-24T10:00:00.000Z" }));
    expect(offlineSnapshot(s, (key) => key === "getFeed(undefined)")).toEqual({
      fromCache: true,
      savedAt: "2026-10-24T10:00:00.000Z",
    });
    expect(offlineSnapshot(s, () => false)).toEqual({ fromCache: false, savedAt: null });
  });
});
