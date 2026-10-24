import { afterEach, describe, expect, it, vi } from "vitest";
import { ensureFreshSession, refreshSession } from "./session";

const answer = (status: number) => vi.fn(async () => new Response(status === 200 ? "{}" : "", { status }));

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("refreshSession", () => {
  it("is refreshed on a 2xx", async () => {
    vi.stubGlobal("fetch", answer(200));
    await expect(refreshSession()).resolves.toEqual({ outcome: "refreshed" });
  });

  it.each([401, 403])("is rejected when the server refuses the refresh token (%i)", async (status) => {
    vi.stubGlobal("fetch", answer(status));
    await expect(refreshSession()).resolves.toEqual({ outcome: "rejected", status });
  });

  // H-15: an expired access cookie is refused statelessly but the refresh needs the database, so a
  // restart answers 5xx here. None of these say anything about the session.
  it.each([500, 502, 503, 504, 429])("is unavailable — not signed out — on %i", async (status) => {
    vi.stubGlobal("fetch", answer(status));
    await expect(refreshSession()).resolves.toEqual({ outcome: "unavailable", status });
  });

  it("is unavailable with no status when the connection drops", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("Failed to fetch");
      })
    );
    await expect(refreshSession()).resolves.toEqual({ outcome: "unavailable", status: null });
  });

  it("collapses concurrent callers onto one request", async () => {
    let release: (r: Response) => void = () => undefined;
    const fetchMock = vi.fn(() => new Promise<Response>((resolve) => (release = resolve)));
    vi.stubGlobal("fetch", fetchMock);

    const both = Promise.all([refreshSession(), ensureFreshSession()]);
    release(new Response("{}", { status: 200 }));
    await expect(both).resolves.toEqual([{ outcome: "refreshed" }, true]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe("ensureFreshSession", () => {
  it.each([
    [200, true],
    [401, false],
    [503, false],
  ])("answers %i with %s", async (status, expected) => {
    vi.stubGlobal("fetch", answer(status));
    await expect(ensureFreshSession()).resolves.toBe(expected);
  });
});
