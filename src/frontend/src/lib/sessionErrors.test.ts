import { describe, expect, it } from "vitest";
import { isSignedOutError, sessionGate } from "./sessionErrors";

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

describe("sessionGate", () => {
  const unauthorized = { status: 401, data: {} };
  const badGateway = { status: 502, data: null };

  it("signs out on a terminal 401 even with a session still cached from an earlier answer", () => {
    expect(sessionGate({ isLoading: false, hasSession: true, error: unauthorized })).toBe("signed-out");
    expect(sessionGate({ isLoading: false, hasSession: false, error: unauthorized })).toBe("signed-out");
  });

  it("keeps the page when a re-check fails for any other reason", () => {
    expect(sessionGate({ isLoading: false, hasSession: true, error: badGateway })).toBe("children");
    expect(sessionGate({ isLoading: false, hasSession: true, error: { status: "FETCH_ERROR", error: "x" } })).toBe(
      "children"
    );
  });

  it("offers the retry shell when there is no session and the probe failed without a 401", () => {
    expect(sessionGate({ isLoading: false, hasSession: false, error: badGateway })).toBe("retry");
    expect(sessionGate({ isLoading: false, hasSession: false, error: { status: "FETCH_ERROR", error: "x" } })).toBe(
      "retry"
    );
  });

  it("shows the splash only while the first answer is outstanding", () => {
    expect(sessionGate({ isLoading: true, hasSession: false, error: undefined })).toBe("splash");
    expect(sessionGate({ isLoading: false, hasSession: true, error: undefined })).toBe("children");
  });
});
