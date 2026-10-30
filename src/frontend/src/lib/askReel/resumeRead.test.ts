import { describe, expect, it } from "vitest";
import { isConversationGone, resumeFailureShown } from "./resumeRead";

const base = { skipped: false, turnCount: 0, streaming: false };

describe("isConversationGone — only the server saying so forgets the thread (HREV8-PH-01)", () => {
  it.each([404, 410])("a %i is gone", (status) => {
    expect(isConversationGone({ status, data: { code: "conversation_not_found" } })).toBe(true);
  });

  it.each([500, 502, 503, 429, 401, 400])("a %i is not", (status) => {
    expect(isConversationGone({ status, data: {} })).toBe(false);
  });

  it("a dropped connection, a timeout and a parse failure are not", () => {
    expect(isConversationGone({ status: "FETCH_ERROR", error: "TypeError: Failed to fetch" })).toBe(false);
    expect(isConversationGone({ status: "TIMEOUT_ERROR", error: "timed out" })).toBe(false);
    expect(isConversationGone({ status: "PARSING_ERROR", originalStatus: 200, data: "<html>", error: "bad json" })).toBe(false);
    expect(isConversationGone({ message: "aborted" })).toBe(false);
    expect(isConversationGone(undefined)).toBe(false);
  });
});

describe("resumeFailureShown — a failed read is a third state, not a cold start", () => {
  it("shows for a 503 with nothing on screen", () => {
    expect(resumeFailureShown({ ...base, error: { status: 503, data: {} } })).toBe(true);
  });

  it("shows for a dropped connection", () => {
    expect(resumeFailureShown({ ...base, error: { status: "FETCH_ERROR", error: "offline" } })).toBe(true);
  });

  it("does not show for a thread the server says is gone — that is the cold start", () => {
    expect(resumeFailureShown({ ...base, error: { status: 404, data: {} } })).toBe(false);
    expect(resumeFailureShown({ ...base, error: { status: 410, data: {} } })).toBe(false);
  });

  it("does not show without an error, or when the page is not asking for a thread", () => {
    expect(resumeFailureShown({ ...base, error: undefined })).toBe(false);
    expect(resumeFailureShown({ ...base, skipped: true, error: { status: 503, data: {} } })).toBe(false);
  });

  it("does not show once this visit has a turn on screen or one running", () => {
    expect(resumeFailureShown({ ...base, turnCount: 1, error: { status: 503, data: {} } })).toBe(false);
    expect(resumeFailureShown({ ...base, streaming: true, error: { status: 503, data: {} } })).toBe(false);
  });
});
