import { describe, expect, it } from "vitest";
import { isSignedOutError } from "./sessionErrors";

describe("isSignedOutError", () => {
  it("is true only for a 401 (one the refresh could not heal)", () => {
    expect(isSignedOutError({ status: 401, data: {} })).toBe(true);
  });

  it.each([
    { status: 502, data: {} },
    { status: 500, data: {} },
    { status: 403, data: {} },
    { status: "FETCH_ERROR" as const, error: "TypeError: Failed to fetch" },
    { status: "TIMEOUT_ERROR" as const, error: "timeout" },
    { message: "serialized" },
    undefined,
  ])("is false for %o — the session may be fine", (error) => {
    expect(isSignedOutError(error)).toBe(false);
  });
});
