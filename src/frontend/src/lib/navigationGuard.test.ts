import { NavigationType } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import {
  LEAVE_CONFIRMED,
  departureNeedsAsking,
  guardNavigation,
  leavesPage,
  pageLocation,
  pendingLeaveQuestion,
} from "./navigationGuard";

const at = (pathname: string, search = "", state: unknown = null) => ({ pathname, search, state });

describe("pendingLeaveQuestion — what the shell's one blocker asks", () => {
  const removers: Array<() => void> = [];
  afterEach(() => {
    while (removers.length > 0) removers.pop()!();
  });

  it("asks nothing when nothing is registered", () => {
    expect(pendingLeaveQuestion()).toBeNull();
  });

  it("asks nothing while every registered question has nothing at stake", () => {
    removers.push(guardNavigation(() => null), guardNavigation(() => null));
    expect(pendingLeaveQuestion()).toBeNull();
  });

  it("asks the question of the first registration that has something at stake", () => {
    removers.push(
      guardNavigation(() => null),
      guardNavigation(() => "Leave your conditions?"),
      guardNavigation(() => "Leave your email?"),
    );
    expect(pendingLeaveQuestion()).toBe("Leave your conditions?");
  });

  it("reads the question at the moment of leaving, not at registration", () => {
    let dirty = false;
    removers.push(guardNavigation(() => (dirty ? "Leave without saving?" : null)));
    expect(pendingLeaveQuestion()).toBeNull();
    dirty = true;
    expect(pendingLeaveQuestion()).toBe("Leave without saving?");
  });

  it("forgets a question once it is removed, and removing twice is harmless", () => {
    const remove = guardNavigation(() => "Leave?");
    remove();
    remove();
    expect(pendingLeaveQuestion()).toBeNull();
  });

  it("holds one entry per function, so registering the same function twice is one registration", () => {
    const question = () => "Leave?";
    const removeA = guardNavigation(question);
    const removeB = guardNavigation(question);
    removeA();
    // A Set holds one entry per function, so the shared function is gone after the first removal.
    // Callers register one closure per draft owner; this pins that behaviour so nobody relies on
    // reference-counting that is not there.
    expect(pendingLeaveQuestion()).toBeNull();
    removeB();
  });
});

describe("pageLocation — the page a location shows", () => {
  it("is the location itself for an ordinary page", () => {
    const here = at("/settings");
    expect(pageLocation(here)).toBe(here);
  });

  it("is the background page for the title modal opened over it", () => {
    const background = at("/settings", "?tab=prefs");
    expect(pageLocation(at("/title/movie/603", "", { backgroundLocation: background }))).toBe(background);
  });

  it("ignores a background on any route other than the title modal", () => {
    const person = at("/person/31", "", { backgroundLocation: at("/settings") });
    expect(pageLocation(person)).toBe(person);
  });

  it("is the title page itself when it was opened directly, with no background", () => {
    const title = at("/title/tv/1399");
    expect(pageLocation(title)).toBe(title);
  });
});

describe("leavesPage — which navigations can lose a draft", () => {
  it.each([
    ["a different page", at("/settings"), at("/home")],
    ["the same page with a different query", at("/settings"), at("/settings", "?tab=prefs")],
    ["Back from the page to the one before it", at("/settings", "?tab=prefs"), at("/settings")],
    ["a full-page person view", at("/settings"), at("/person/31", "", { backgroundLocation: at("/settings") })],
    ["a title modal over a different page", at("/settings"), at("/title/movie/603", "", { backgroundLocation: at("/home") })],
    ["a title page opened directly", at("/settings"), at("/title/movie/603")],
  ])("leaves for %s", (_label, from, to) => {
    expect(leavesPage(from, to)).toBe(true);
  });

  it.each([
    ["a hash jump on the same page", at("/settings"), at("/settings")],
    ["a title modal opened over this page", at("/settings"), at("/title/movie/603", "", { backgroundLocation: at("/settings") })],
    [
      "closing the title modal back onto its page",
      at("/title/movie/603", "", { backgroundLocation: at("/settings") }),
      at("/settings"),
    ],
    [
      "one title modal to another over the same page",
      at("/title/movie/603", "", { backgroundLocation: at("/saved") }),
      at("/title/tv/1399", "", { backgroundLocation: at("/saved") }),
    ],
  ])("stays for %s", (_label, from, to) => {
    expect(leavesPage(from, to)).toBe(false);
  });
});

describe("departureNeedsAsking — sign-out's own question is not asked twice", () => {
  const settings = at("/settings");

  it("asks on a push away from the page", () => {
    expect(departureNeedsAsking({ currentLocation: settings, nextLocation: at("/"), historyAction: NavigationType.Push })).toBe(true);
  });

  it.each([NavigationType.Push, NavigationType.Replace])("lets a %s its caller already asked about through", (historyAction) => {
    expect(departureNeedsAsking({ currentLocation: settings, nextLocation: at("/", "", LEAVE_CONFIRMED), historyAction })).toBe(false);
  });

  it("asks on Back or Forward onto an entry that once carried the confirmation", () => {
    expect(departureNeedsAsking({ currentLocation: settings, nextLocation: at("/", "", LEAVE_CONFIRMED), historyAction: NavigationType.Pop })).toBe(true);
  });

  it("asks on Back to the page before", () => {
    expect(departureNeedsAsking({ currentLocation: settings, nextLocation: at("/home"), historyAction: NavigationType.Pop })).toBe(true);
  });

  it("does not ask when the page stays mounted under a title modal", () => {
    expect(
      departureNeedsAsking({
        currentLocation: settings,
        nextLocation: at("/title/movie/603", "", { backgroundLocation: settings }),
        historyAction: NavigationType.Push,
      }),
    ).toBe(false);
  });
});
