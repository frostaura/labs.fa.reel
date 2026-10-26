import { describe, expect, it } from "vitest";
import { attribute, judge, parseHits, routeShape } from "./unmockedApi";

describe("routeShape", () => {
  it("collapses numeric and uuid segments so one mock shape covers every fixture id", () => {
    expect(routeShape("/api/titles/603")).toBe("/api/titles/:id");
    expect(routeShape("/api/conversations/0b6a9c2e-1d3f-4a5b-8c7d-9e0f1a2b3c4d/turns")).toBe("/api/conversations/:id/turns");
    expect(routeShape("/api/settings/filters")).toBe("/api/settings/filters");
    // A slug that merely contains digits is a route word, not an id.
    expect(routeShape("/api/people/nolan-1970")).toBe("/api/people/nolan-1970");
  });
});

describe("attribute", () => {
  const starts = [
    { key: "b.spec.ts › second", at: 200 },
    { key: "a.spec.ts › first", at: 100 },
    { key: "c.spec.ts › third", at: 300 },
  ];

  it("gives each hit to the test that most recently began, whatever order the starts arrive in", () => {
    const { byTest, beforeAnyTest } = attribute(
      [
        { at: 150, method: "GET", path: "/api/feed" },
        { at: 200, method: "GET", path: "/api/learned" },
        { at: 299, method: "POST", path: "/api/saved/12" },
        { at: 301, method: "GET", path: "/api/feed" },
      ],
      starts
    );
    expect(byTest).toEqual({
      "a.spec.ts › first": ["GET /api/feed"],
      "b.spec.ts › second": ["GET /api/learned", "POST /api/saved/:id"],
      "c.spec.ts › third": ["GET /api/feed"],
    });
    expect(beforeAnyTest).toEqual([]);
  });

  it("de-duplicates repeat calls and keeps a late call from a closing page with its own test", () => {
    const { byTest } = attribute(
      [
        { at: 120, method: "GET", path: "/api/titles/1" },
        { at: 130, method: "GET", path: "/api/titles/2" },
        // After "first" ended but before "second" began: still first's page.
        { at: 199, method: "GET", path: "/api/accuracy" },
      ],
      starts
    );
    expect(byTest).toEqual({ "a.spec.ts › first": ["GET /api/accuracy", "GET /api/titles/:id"] });
  });

  it("reports a call made before any test began instead of blaming the first test", () => {
    const { byTest, beforeAnyTest } = attribute([{ at: 50, method: "GET", path: "/api/auth/me" }], starts);
    expect(byTest).toEqual({});
    expect(beforeAnyTest).toEqual(["GET /api/auth/me"]);
  });

  it("finds nothing when no call reached the tripwire", () => {
    expect(attribute([], starts)).toEqual({ byTest: {}, beforeAnyTest: [] });
  });
});

describe("judge", () => {
  const baseline = { "x.spec.ts › known": ["GET /api/learned"] };

  it("passes a known debt and fails a new test or a new call on a known test", () => {
    const found = {
      "x.spec.ts › known": ["GET /api/learned", "GET /api/accuracy"],
      "y.spec.ts › new": ["GET /api/feed"],
    };
    const ran = new Set(Object.keys(found));
    expect(judge(found, baseline, ran).unexpected).toEqual({
      "x.spec.ts › known": ["GET /api/accuracy"],
      "y.spec.ts › new": ["GET /api/feed"],
    });
  });

  it("lists a debt that did not recur only when its test actually ran", () => {
    expect(judge({}, baseline, new Set(["x.spec.ts › known"])).unseen).toEqual(baseline);
    // A filtered run that skipped the test says nothing about it.
    expect(judge({}, baseline, new Set(["z.spec.ts › other"])).unseen).toEqual({});
  });

  it("is clean when every call is covered", () => {
    expect(judge(baseline, baseline, new Set(Object.keys(baseline)))).toEqual({ unexpected: {}, unseen: {} });
  });
});

describe("parseHits", () => {
  it("reads one JSON object per line and ignores blank lines", () => {
    expect(parseHits('{"at":1,"method":"GET","path":"/api/a"}\n\n{"at":2,"method":"PUT","path":"/sse/x"}\n')).toEqual([
      { at: 1, method: "GET", path: "/api/a" },
      { at: 2, method: "PUT", path: "/sse/x" },
    ]);
  });
});
