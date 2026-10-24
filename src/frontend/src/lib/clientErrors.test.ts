import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  CLIENT_ERROR_ENDPOINT,
  MAX_REPORTS_PER_PAGE,
  buildClientErrorReport,
  reportClientError,
  resetClientErrorBudget,
} from "./clientErrors";

beforeEach(() => {
  resetClientErrorBudget();
  vi.stubGlobal("window", { location: { pathname: "/saved", search: "?q=ninja films" } });
});
afterEach(() => vi.unstubAllGlobals());

describe("buildClientErrorReport", () => {
  it("carries the error, the component stack and the path — never the query string", () => {
    const report = buildClientErrorReport(new TypeError("x.map is not a function"), "caught", "  at Saved", "/saved", new Date("2026-10-24T10:00:00Z"));
    expect(report).toEqual({
      name: "TypeError",
      message: "x.map is not a function",
      stack: expect.stringContaining("TypeError"),
      componentStack: "  at Saved",
      origin: "caught",
      path: "/saved",
      occurredAt: "2026-10-24T10:00:00.000Z",
    });
  });

  it("handles a thrown non-Error and clips oversized fields", () => {
    const report = buildClientErrorReport("boom", "uncaught", "x".repeat(10_000), "/");
    expect(report.name).toBe("string");
    expect(report.message).toBe("boom");
    expect(report.stack).toBeNull();
    expect(report.componentStack).toHaveLength(4_000);
  });
});

describe("reportClientError", () => {
  it("posts one report to the sink with credentials, path only", async () => {
    const send = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    expect(reportClientError(new Error("render"), "caught", null, send)).toBe(true);
    expect(send).toHaveBeenCalledTimes(1);
    const [url, init] = send.mock.calls[0];
    expect(url).toBe(CLIENT_ERROR_ENDPOINT);
    expect(init).toMatchObject({ method: "POST", credentials: "include", keepalive: true });
    const body = JSON.parse(init.body);
    expect(body.path).toBe("/saved");
    expect(JSON.stringify(body)).not.toContain("ninja");
  });

  it("reports the same error once per page load", () => {
    const send = vi.fn().mockResolvedValue(new Response());
    reportClientError(new Error("same"), "caught", null, send);
    expect(reportClientError(new Error("same"), "caught", null, send)).toBe(false);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("stops after the per-page budget so a render loop is not a request loop", () => {
    const send = vi.fn().mockResolvedValue(new Response());
    for (let i = 0; i < MAX_REPORTS_PER_PAGE + 5; i += 1) reportClientError(new Error(`e${i}`), "caught", null, send);
    expect(send).toHaveBeenCalledTimes(MAX_REPORTS_PER_PAGE);
  });

  it("never throws when the sink is missing or the network is down", () => {
    const rejecting = vi.fn().mockRejectedValue(new TypeError("Failed to fetch"));
    const throwing = vi.fn(() => {
      throw new Error("sync throw");
    });
    expect(() => reportClientError(new Error("a"), "caught", null, rejecting)).not.toThrow();
    expect(() => reportClientError(new Error("b"), "caught", null, throwing as never)).not.toThrow();
  });
});
