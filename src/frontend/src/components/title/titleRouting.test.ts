import { describe, expect, it } from "vitest";
import { canGoBack, titleFailure } from "./titleRouting";

describe("titleFailure (F-9)", () => {
  it("names a 404 as not found", () => {
    expect(titleFailure({ status: 404, data: null })).toBe("not-found");
  });

  it("every other failure is an error, never 'not found'", () => {
    for (const error of [{ status: 500 }, { status: 403 }, { status: "FETCH_ERROR" }, { status: "PARSING_ERROR" }, null, undefined, "boom"]) {
      expect(titleFailure(error)).toBe("error");
    }
  });
});

describe("canGoBack (F-8)", () => {
  it("steps back when a card opened the modal over its page", () => {
    expect(canGoBack({ backgroundLocation: { pathname: "/home" } }, { idx: 0 })).toBe(true);
  });

  it("steps back after any in-app navigation", () => {
    expect(canGoBack(null, { idx: 3 })).toBe(true);
  });

  it("a deep link — no background, first history entry — cannot step back", () => {
    expect(canGoBack(null, { idx: 0 })).toBe(false);
    expect(canGoBack(undefined, null)).toBe(false);
    expect(canGoBack({}, {})).toBe(false);
  });
});
