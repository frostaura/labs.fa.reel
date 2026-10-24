import { describe, expect, it } from "vitest";
import { refreshesOn401, savedAtFromDateHeader } from "./base";

describe("refreshesOn401", () => {
  it("refreshes for the session probe and every non-auth endpoint (H+1)", () => {
    expect(refreshesOn401("auth/me")).toBe(true);
    expect(refreshesOn401("saved")).toBe(true);
    expect(refreshesOn401("sync/status")).toBe(true);
  });

  it("never refreshes around refresh itself, OAuth or logout", () => {
    expect(refreshesOn401("auth/refresh")).toBe(false);
    expect(refreshesOn401("auth/logout")).toBe(false);
    expect(refreshesOn401("auth/trakt/start")).toBe(false);
    expect(refreshesOn401("auth/trakt/callback")).toBe(false);
  });
});

describe("savedAtFromDateHeader", () => {
  it("parses the HTTP Date header into ISO", () => {
    expect(savedAtFromDateHeader("Sat, 24 Oct 2026 10:15:00 GMT")).toBe("2026-10-24T10:15:00.000Z");
  });

  it("is null for an absent or unparseable header", () => {
    expect(savedAtFromDateHeader(null)).toBeNull();
    expect(savedAtFromDateHeader("yesterday-ish")).toBeNull();
  });
});
