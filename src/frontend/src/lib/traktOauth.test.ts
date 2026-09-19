import { describe, expect, it } from "vitest";
import { describeCallbackRejection, parseCallbackParams } from "./traktOauth";

describe("parseCallbackParams", () => {
  it("pulls code, state and error out of the query string", () => {
    expect(parseCallbackParams("?code=abc&state=xyz")).toEqual({
      code: "abc",
      state: "xyz",
      error: null,
    });
  });

  it("returns nulls for an empty query string", () => {
    expect(parseCallbackParams("")).toEqual({ code: null, state: null, error: null });
  });
});

describe("describeCallbackRejection", () => {
  const ok = { code: "abc", state: "nonce", error: null };

  it("accepts a well-formed callback whose state matches", () => {
    expect(describeCallbackRejection(ok, "nonce")).toBeNull();
  });

  it("rejects a state that does not match the stored nonce", () => {
    expect(describeCallbackRejection({ ...ok, state: "other" }, "nonce")).toBe(
      "Sign-in state mismatch. Start again from the sign-in page."
    );
  });

  it("accepts when no nonce is stored, since sessionStorage is per-tab", () => {
    // The server still verifies its own HMAC-signed state, so this is not the security boundary.
    expect(describeCallbackRejection(ok, null)).toBeNull();
  });

  it("reports a declined authorization in the user's language, not Trakt's", () => {
    expect(describeCallbackRejection({ ...ok, error: "access_denied" }, "nonce")).toBe(
      "Trakt access was declined."
    );
  });

  it("passes through any other provider error verbatim", () => {
    expect(describeCallbackRejection({ ...ok, error: "server_error" }, "nonce")).toBe(
      "Trakt returned: server_error"
    );
  });

  it("prefers the provider error over a missing code", () => {
    const rejection = describeCallbackRejection(
      { code: null, state: null, error: "access_denied" },
      "nonce"
    );
    expect(rejection).toBe("Trakt access was declined.");
  });

  it("rejects a callback missing the code", () => {
    expect(describeCallbackRejection({ ...ok, code: null }, "nonce")).toBe(
      "Missing authorization code — start again from the sign-in page."
    );
  });

  it("rejects a callback missing the state", () => {
    expect(describeCallbackRejection({ ...ok, state: null }, "nonce")).toBe(
      "Missing authorization code — start again from the sign-in page."
    );
  });
});
