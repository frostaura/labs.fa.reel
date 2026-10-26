import { describe, expect, it } from "vitest";
import { leaveGuardMessage } from "./useLeaveGuard";

describe("leaveGuardMessage — one question for every dirty draft", () => {
  it("names a single draft", () => {
    expect(leaveGuardMessage(["your conditions"])).toBe("You have unsaved changes to your conditions. Leave without saving them?");
  });

  it("names two drafts in one sentence, so one click asks once", () => {
    expect(leaveGuardMessage(["your conditions", "your email address"])).toBe(
      "You have unsaved changes to your conditions and your email address. Leave without saving them?",
    );
  });

  it("names a draft once even when two editors hold the same kind", () => {
    expect(leaveGuardMessage(["your conditions", "your conditions"])).toBe(
      "You have unsaved changes to your conditions. Leave without saving them?",
    );
  });
});
