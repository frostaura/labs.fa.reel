import { describe, expect, it } from "vitest";
import { isGuardedNavigation, leaveGuardMessage } from "./useLeaveGuard";

const HERE = { href: "http://127.0.0.1:5211/settings", origin: "http://127.0.0.1:5211", pathname: "/settings", search: "" };
const CLICK = { defaultPrevented: false, button: 0, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false };
const link = (href: string, extra: Partial<{ target: string; hasDownload: boolean }> = {}) => ({ href, target: "", hasDownload: false, ...extra });

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

describe("isGuardedNavigation", () => {
  it("questions a plain in-app link to another page", () => {
    expect(isGuardedNavigation(CLICK, link("http://127.0.0.1:5211/rate"), HERE)).toBe(true);
  });

  it.each([
    ["a modified click (new tab)", { ...CLICK, metaKey: true }, link("http://127.0.0.1:5211/rate")],
    ["a middle click", { ...CLICK, button: 1 }, link("http://127.0.0.1:5211/rate")],
    ["a click already handled", { ...CLICK, defaultPrevented: true }, link("http://127.0.0.1:5211/rate")],
    ["a click that is not on a link", CLICK, null],
    ["a link to this same page", CLICK, link("http://127.0.0.1:5211/settings#email")],
    ["a link that opens elsewhere", CLICK, link("http://127.0.0.1:5211/rate", { target: "_blank" })],
    ["a download", CLICK, link("http://127.0.0.1:5211/export.csv", { hasDownload: true })],
    ["another site (beforeunload covers it)", CLICK, link("https://trakt.tv/users/x")],
  ])("leaves %s alone", (_label, event, anchor) => {
    expect(isGuardedNavigation(event, anchor, HERE)).toBe(false);
  });

  it("questions a link that only changes the query", () => {
    expect(isGuardedNavigation(CLICK, link("http://127.0.0.1:5211/settings?tab=prefs"), HERE)).toBe(true);
  });
});
