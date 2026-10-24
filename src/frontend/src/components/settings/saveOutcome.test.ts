import { describe, expect, it } from "vitest";
import {
  SAVE_FAILED_TEXT,
  SAVE_LOCKED_TEXT,
  SAVE_REJECTED_FALLBACK,
  emailSaveMessage,
  saveFailureMessage,
  savedMessage,
} from "./saveOutcome";

describe("saveFailureMessage", () => {
  it("a 423 is the PIN message, in danger tone, with no retry (retrying cannot unlock)", () => {
    const m = saveFailureMessage({ status: 423, data: {} });
    expect(m).toMatchObject({ tone: "danger", text: SAVE_LOCKED_TEXT, persistent: true, retryable: false, kind: "locked" });
  });

  it("a 400 shows the server's own reason as a sentence", () => {
    const m = saveFailureMessage({ status: 400, data: { error: "minYear must be between 1900 and 2027" } });
    expect(m.text).toBe("MinYear must be between 1900 and 2027.");
    expect(m).toMatchObject({ tone: "danger", persistent: true, retryable: false, kind: "rejected" });
  });

  it("a 400 reason that already ends a sentence is not double-punctuated", () => {
    expect(saveFailureMessage({ status: 400, data: { error: "Bad range!" } }).text).toBe("Bad range!");
  });

  it.each([
    ["no body", { status: 400 }],
    ["a string body", { status: 400, data: "Bad Request" }],
    ["an empty reason", { status: 400, data: { error: "   " } }],
    ["a non-string reason", { status: 400, data: { error: 42 } }],
  ])("a 400 with %s falls back to a generic refusal", (_label, error) => {
    expect(saveFailureMessage(error).text).toBe(SAVE_REJECTED_FALLBACK);
  });

  it.each([
    ["a 500", { status: 500, data: { error: "boom" } }],
    ["a 502", { status: 502 }],
    ["a network failure", { status: "FETCH_ERROR", error: "TypeError: Failed to fetch" }],
    ["a timeout", { status: "TIMEOUT_ERROR" }],
    ["a 401 that survived the refresh", { status: 401 }],
    ["something that is not an RTK error", new Error("x")],
    ["null", null],
  ])("%s is the generic failure — retryable, never the PIN message, never green", (_label, error) => {
    const m = saveFailureMessage(error);
    expect(m).toMatchObject({ tone: "danger", text: SAVE_FAILED_TEXT, persistent: true, retryable: true, kind: "failed" });
    expect(m.text).not.toMatch(/PIN/);
  });
});

describe("savedMessage", () => {
  it("is the only green message, and it fades", () => {
    expect(savedMessage("Saved.")).toEqual({ tone: "success", text: "Saved.", persistent: false, retryable: false, kind: "saved" });
  });
});

describe("emailSaveMessage", () => {
  it("is a success when the stored address is the one sent", () => {
    expect(emailSaveMessage(" me@example.com ", "me@example.com")).toMatchObject({ tone: "success", text: "Saved." });
  });

  it("says 'Removed.' when an empty box cleared the stored address", () => {
    expect(emailSaveMessage("", null)).toMatchObject({ tone: "success", text: "Removed." });
  });

  it("is a refusal when the server kept the old address (it answers 200 for an address it rejects)", () => {
    expect(emailSaveMessage("not-an-email@x", "old@example.com")).toMatchObject({
      tone: "danger",
      persistent: true,
      kind: "rejected",
    });
  });

  it("is a refusal when the server kept nothing for a new address", () => {
    expect(emailSaveMessage("a@b", null).tone).toBe("danger");
  });
});
