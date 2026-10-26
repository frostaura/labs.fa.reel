import { describe, expect, it } from "vitest";
import {
  INITIAL_RAIL_PAGING,
  PAGE_EXCLUDE_MAX,
  RAIL_BACKOFF_MS,
  RailPageRegistry,
  canAutoPage,
  nextRailPaging,
  type RailPaging,
  type RailPagingEvent,
} from "./railPaging";

function run(events: RailPagingEvent["type"][], from: RailPaging = INITIAL_RAIL_PAGING): RailPaging {
  return events.reduce((state, type) => nextRailPaging(state, { type } as RailPagingEvent), from);
}

describe("rail paging (O-7)", () => {
  it("backs off 1 s, 2 s, 4 s, then fails — four requests in all", () => {
    let state = INITIAL_RAIL_PAGING;
    const delays: (number | null)[] = [];
    let requests = 0;
    while (state.phase !== "failed" && requests < 50) {
      state = nextRailPaging(state, { type: "start" });
      requests += 1;
      state = nextRailPaging(state, { type: "failure" });
      if (state.phase === "backoff") delays.push(state.retryInMs);
    }
    expect(requests).toBe(4);
    expect(delays).toEqual([1000, 2000, 4000]);
    expect(RAIL_BACKOFF_MS).toEqual([1000, 2000, 4000]);
  });

  it("nothing automatic pages while in flight, backing off or failed", () => {
    expect(canAutoPage(INITIAL_RAIL_PAGING)).toBe(true);
    expect(canAutoPage(run(["start"]))).toBe(false);
    expect(canAutoPage(run(["start", "failure"]))).toBe(false);
    expect(canAutoPage(run(["start", "failure", "start", "failure", "start", "failure", "start", "failure"]))).toBe(false);
  });

  it("a success anywhere in the sequence resets the count", () => {
    const state = run(["start", "failure", "start", "failure", "start", "success"]);
    expect(state).toEqual(INITIAL_RAIL_PAGING);
    // The next failure starts the backoff over at one second.
    expect(run(["start", "failure"], state).retryInMs).toBe(1000);
  });

  it("a manual retry is one request: if it fails the rail is failed again at once", () => {
    const failed = run(["start", "failure", "start", "failure", "start", "failure", "start", "failure"]);
    expect(failed.phase).toBe("failed");
    const retrying = nextRailPaging(failed, { type: "retry" });
    expect(retrying.phase).toBe("loading");
    expect(nextRailPaging(retrying, { type: "failure" }).phase).toBe("failed");
    expect(nextRailPaging(retrying, { type: "success" })).toEqual(INITIAL_RAIL_PAGING);
  });

  it("retry is ignored unless the rail has failed", () => {
    expect(nextRailPaging(INITIAL_RAIL_PAGING, { type: "retry" })).toEqual(INITIAL_RAIL_PAGING);
    const backoff = run(["start", "failure"]);
    expect(nextRailPaging(backoff, { type: "retry" })).toEqual(backoff);
  });
});

describe("RailPageRegistry (F+3)", () => {
  it("excludes what other rails show and leaves this rail's own titles to excludeTitleIds", () => {
    const page = new RailPageRegistry();
    page.register(["hero-1", "hero-2", "row-a1"]);
    page.register(["genre-x1"]);
    expect(page.excludeFor(new Set(["genre-x1"]))).toEqual(["hero-1", "hero-2", "row-a1"]);
  });

  it("registering twice is a no-op", () => {
    const page = new RailPageRegistry();
    page.register(["a", "b"]);
    page.register(["b", "a", "c"]);
    expect(page.excludeFor(new Set())).toEqual(["a", "b", "c"]);
  });

  it("caps the list, keeping the most recently shown", () => {
    const page = new RailPageRegistry();
    const ids = Array.from({ length: PAGE_EXCLUDE_MAX + 25 }, (_, i) => `t${i}`);
    page.register(ids);
    const sent = page.excludeFor(new Set());
    expect(sent).toHaveLength(PAGE_EXCLUDE_MAX);
    expect(sent[0]).toBe("t25");
    expect(sent.at(-1)).toBe(`t${PAGE_EXCLUDE_MAX + 24}`);
    expect(page.excludeFor(new Set(), 3)).toEqual(ids.slice(-3));
  });

  it("mints one token per page view, within the server's 64-character limit", () => {
    const page = new RailPageRegistry();
    const token = page.pageToken();
    expect(token).toMatch(/^[A-Za-z0-9]{1,64}$/);
    expect(page.pageToken()).toBe(token);
    expect(new RailPageRegistry().pageToken()).not.toBe(token);
  });
});

describe("a rail's time limit", () => {
  it("fails straight to the Retry tile on a timeout, with no automatic retries", () => {
    const loading = nextRailPaging(INITIAL_RAIL_PAGING, { type: "start" });
    const timedOut = nextRailPaging(loading, { type: "timeout" });
    expect(timedOut).toEqual({ phase: "failed", failures: 3, retryInMs: null });
    expect(canAutoPage(timedOut)).toBe(false);
    // Retry is one more attempt; a success clears it.
    const retried = nextRailPaging(timedOut, { type: "retry" });
    expect(retried.phase).toBe("loading");
    expect(nextRailPaging(retried, { type: "success" })).toEqual(INITIAL_RAIL_PAGING);
  });
});
