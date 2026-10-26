/**
 * The paging state machine every infinite rail shares, kept free of React so its rules can be
 * asserted without a DOM.
 *
 * WHY IT EXISTS (O-7). A failed `/feed/more` used to return without recording anything, and the
 * rails' auto-fill effects re-fired every time `loading` flipped back to false — 4,326 POSTs in ten
 * seconds against a 503, with shimmer placeholders that never resolved and no error on the page.
 * An empty collection is not an answer, and neither is a spinner that cannot end.
 *
 * The rule now: a failure is remembered. The rail waits 1 s, 2 s, then 4 s between automatic
 * retries — four requests in all — and then stops and says so with a "Couldn't load — Retry" tile.
 * Nothing automatic fires while the rail is backing off or has failed; only the Retry button (one
 * more request) or a success brings it back to idle.
 */

/** Delays before automatic retries 1, 2 and 3. Their count is also the number of automatic retries. */
export const RAIL_BACKOFF_MS: readonly number[] = [1000, 2000, 4000];

/**
 * `idle` may page; `loading` has a request in flight; `backoff` is waiting `retryInMs` before the
 * next automatic attempt; `failed` waits for the user.
 */
export type RailPhase = "idle" | "loading" | "backoff" | "failed";

export interface RailPaging {
  phase: RailPhase;
  /** Consecutive failures since the last success. */
  failures: number;
  /** How long to wait before the next automatic attempt; set only in `backoff`. */
  retryInMs: number | null;
}

export type RailPagingEvent =
  | { type: "start" }
  | { type: "success" }
  | { type: "failure" }
  /** The user pressed Retry: one more attempt, and straight back to `failed` if it fails too. */
  | { type: "retry" }
  /**
   * The request outlived the rail's time limit. Straight to `failed`, no automatic retries: a
   * request that hung once is the slow path, and three more of them is the "shimmer that cannot
   * end" this machine exists to stop (the title page's Similar rail, F-14).
   */
  | { type: "timeout" };

export const INITIAL_RAIL_PAGING: RailPaging = { phase: "idle", failures: 0, retryInMs: null };

export function nextRailPaging(state: RailPaging, event: RailPagingEvent): RailPaging {
  switch (event.type) {
    case "start":
      return { ...state, phase: "loading", retryInMs: null };
    case "success":
      return INITIAL_RAIL_PAGING;
    case "failure": {
      const failures = state.failures + 1;
      const delay = RAIL_BACKOFF_MS[failures - 1];
      return delay === undefined
        ? { phase: "failed", failures, retryInMs: null }
        : { phase: "backoff", failures, retryInMs: delay };
    }
    case "timeout":
      return { phase: "failed", failures: Math.max(state.failures + 1, RAIL_BACKOFF_MS.length), retryInMs: null };
    case "retry":
      // A manual retry is one request, not a fresh round of three: the user is watching the tile,
      // and a second seven-second shimmer before the same failure would be the old lie again.
      return state.phase === "failed"
        ? { phase: "loading", failures: RAIL_BACKOFF_MS.length, retryInMs: null }
        : state;
  }
}

/** True only when nothing is in flight, nothing is scheduled and the user is not being asked to retry. */
export function canAutoPage(state: RailPaging): boolean {
  return state.phase === "idle";
}

/** Upper bound on the page-wide exclusion list one request carries — the most recently shown win. */
export const PAGE_EXCLUDE_MAX = 300;

/**
 * Everything one page view has put on screen, so a rail never serves a title another rail (or the
 * hero) already shows (F+3). One instance per page view: Home re-creates it whenever the rows
 * re-seed, which is exactly when "already on the page" stops being true.
 *
 * A class with methods rather than a context-held Set because rails register into it from effects
 * and async callbacks; its token is minted lazily, on the first request, so nothing impure runs
 * during render.
 */
export class RailPageRegistry {
  private readonly shown = new Set<string>();
  private token: string | null = null;

  /** Records titles as on the page. Idempotent; insertion order is "first shown". */
  register(titleIds: Iterable<string>): void {
    for (const id of titleIds) {
      if (this.shown.has(id)) continue;
      this.shown.add(id);
    }
  }

  /**
   * The page-wide exclusions for one rail's request: everything on the page that the rail has not
   * itself shown (its own titles already travel in `excludeTitleIds`, which is also its depth).
   * Capped at {@link PAGE_EXCLUDE_MAX}, keeping the most recently shown.
   */
  excludeFor(rowSeen: ReadonlySet<string>, max = PAGE_EXCLUDE_MAX): string[] {
    const others: string[] = [];
    for (const id of this.shown) {
      if (!rowSeen.has(id)) others.push(id);
    }
    return others.length > max ? others.slice(others.length - max) : others;
  }

  /** One opaque nonce per page view (≤ 64 chars), sent on every rail request of that view. */
  pageToken(): string {
    if (this.token === null) {
      this.token =
        typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
          ? crypto.randomUUID().replace(/-/g, "")
          : `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 12)}`;
    }
    return this.token;
  }
}
