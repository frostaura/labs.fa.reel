import { describe, expect, it } from "vitest";
import {
  SAVE_FAILED_TEXT,
  SAVE_LOCKED_TEXT,
  SAVE_REJECTED_FALLBACK,
  emailSaveMessage,
  humaniseReason,
  pinFailureText,
  refusedOutright,
  saveFailureMessage,
  savedMessage,
} from "./save-outcome";

describe("saveFailureMessage", () => {
  it("a 423 is the PIN message, in danger tone, with no retry (retrying cannot unlock)", () => {
    const m = saveFailureMessage({ status: 423, data: {} });
    expect(m).toMatchObject({ tone: "danger", text: SAVE_LOCKED_TEXT, persistent: true, retryable: false, kind: "locked" });
  });

  it("a 400 shows the server's own reason as a sentence", () => {
    const m = saveFailureMessage({ status: 400, data: { error: "minYear must be between 1900 and 2027" } });
    expect(m.text).toBe("The earliest year must be between 1900 and 2027.");
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

describe("humaniseReason — the server's 400 text as the screen says it", () => {
  it.each([
    ["minYear must be between 1870 and 2100", "The earliest year must be between 1870 and 2100."],
    ["maxYear must be between 1870 and 2100", "The latest year must be between 1870 and 2100."],
    ["pin must be 4-8 digits", "The PIN must be 4-8 digits."],
    ["eveningBudgetMinutes must be positive", "Evening budget minutes must be positive."],
    ["Tag must be 2–120 characters.", "Tag must be 2–120 characters."],
    ["billing isn't switched on", "Billing isn't switched on."],
  ])("%s", (reason, text) => {
    expect(humaniseReason(reason)).toBe(text);
  });

  it("never shows a raw camelCase identifier", () => {
    expect(humaniseReason("minCrowdRating must be between 0 and 10")).not.toMatch(/[a-z][A-Z]/);
  });
});

describe("pinFailureText — only a 423 means the PIN was wrong", () => {
  it.each([
    ["a 423", { status: 423 }, "Wrong current PIN — nothing changed."],
    ["a 400", { status: 400, data: { error: "pin must be 4-8 digits" } }, "The PIN must be 4-8 digits."],
    ["a 500", { status: 500 }, SAVE_FAILED_TEXT],
    ["a dropped connection", { status: "FETCH_ERROR" }, SAVE_FAILED_TEXT],
  ])("%s", (_label, error, text) => {
    expect(pinFailureText(error, "Wrong current PIN — nothing changed.")).toBe(text);
  });
});

describe("refusedOutright — the copy may say 'nothing changed' only for a 4xx", () => {
  it.each([
    [{ status: 400 }, true],
    [{ status: 409 }, true],
    [{ status: 500 }, false],
    [{ status: 503 }, false],
    [{ status: "FETCH_ERROR" }, false],
    [{ status: "TIMEOUT_ERROR" }, false],
    [new Error("boom"), false],
    [null, false],
  ])("%o → %s", (error, expected) => {
    expect(refusedOutright(error)).toBe(expected);
  });
});
