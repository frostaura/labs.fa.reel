import { describe, expect, it } from "vitest";
import reducer, { servedFromCache, servedLive } from "./connectivitySlice";

describe("connectivitySlice", () => {
  it("keeps the oldest saved time across several cached answers", () => {
    let s = reducer(undefined, servedFromCache({ savedAt: "2026-10-24T10:30:00.000Z" }));
    s = reducer(s, servedFromCache({ savedAt: "2026-10-24T10:00:00.000Z" }));
    s = reducer(s, servedFromCache({ savedAt: "2026-10-24T10:45:00.000Z" }));
    expect(s).toEqual({ fromCache: true, savedAt: "2026-10-24T10:00:00.000Z" });
  });

  it("a cached answer without a date still marks the page", () => {
    expect(reducer(undefined, servedFromCache({ savedAt: null }))).toEqual({ fromCache: true, savedAt: null });
  });

  it("the next live answer clears it", () => {
    const s = reducer(reducer(undefined, servedFromCache({ savedAt: null })), servedLive());
    expect(s).toEqual({ fromCache: false, savedAt: null });
  });
});
