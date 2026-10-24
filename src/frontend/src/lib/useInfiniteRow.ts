import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSelector } from "react-redux";
import {
  INITIAL_RAIL_PAGING,
  canAutoPage,
  nextRailPaging,
  type RailPaging,
  type RailPagingEvent,
} from "../components/rows/railPaging";
import { useRailPage } from "../components/rows/railPage";
import { useLoadMoreFeedMutation } from "../store/api";
import { selectRemovedKeys } from "../store/removedTitlesSlice";
import { applyScoreOverrides, selectScoreOverrides } from "../store/scoreOverridesSlice";
import type { FeedCard, MoreFeedRequest, MoreFeedResponse } from "../store/feedTypes";

export interface RowContext {
  row: string;
  anchorTitleId?: string | null;
  genre?: string | null;
  /** Active browse-filter medium ("movie" | "show"); omit for "all". Threaded to the server so the
   * paged window + TMDB deepen are media-specific. The owning component re-keys on this, so a switch
   * remounts the row with a fresh (correct-medium) seed rather than mutating in place. */
  mediaType?: string;
}

const PAGE_SIZE = 12;

/**
 * Visible-card floor before the row pulls another page. Acting on a card (rate / save / not
 * interested) hides it locally, so a user queueing several titles from one row drains it. Topping
 * up at half a page keeps the row full as they work, which is what makes "act, and the next one
 * slides in" true rather than "act, and the row quietly empties".
 */
const TOPUP_THRESHOLD = 6;

export interface InfiniteRowOptions {
  /**
   * Whether the row is near enough to the viewport to fetch on its own. Rows below the fold pass
   * false until they approach it, so a cold Home does not fire a billed curation call (and log
   * exposures) for every rail on the page (I-2, F-13). The hero is always active. A user-driven
   * scroll near a rail's end only happens on a rail that is on screen, so it needs no gate.
   */
  active?: boolean;
}

/**
 * Drives one row's infinite scroll: seeds from the snapshot's first page, then appends
 * server-scored pages on demand, excluding everything already shown so a title never repeats
 * within the row — and, through the page's {@link useRailPage} registry, never repeats a title
 * another rail or the hero already shows (F+3). The owning component is re-keyed on the feed's
 * generatedAt, so a rebuild remounts the row with the new seed — no in-place reset needed here.
 *
 * Failures follow lib-free rules in `components/rows/railPaging.ts`: backoff at 1, 2 and 4 s, then
 * `failed` until the user retries. `exhausted` latches only on the server's word that the lane is
 * dry (or an all-seen page) — never on a caught error, or one blip would kill the row for good.
 */
export function useInfiniteRow(context: RowContext, seed: FeedCard[], options: InfiniteRowOptions = {}) {
  const { row, anchorTitleId, genre, mediaType } = context;
  const active = options.active ?? true;
  const page = useRailPage();
  const [items, setItems] = useState<FeedCard[]>(seed);
  const [exhausted, setExhausted] = useState(false);
  const [paging, setPaging] = useState<RailPaging>(INITIAL_RAIL_PAGING);
  const [loadMoreFeed, { isLoading }] = useLoadMoreFeedMutation();
  const seenRef = useRef<Set<string>>(new Set(seed.map((c) => c.titleId)));
  const inFlight = useRef(false);
  const exhaustedRef = useRef(false);
  const pagingRef = useRef<RailPaging>(INITIAL_RAIL_PAGING);
  const retryTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Titles the user just made ineligible (seen/rated, not-interested, saved) drop from view at
  // once. Pagination still tracks the full set via seenRef, so excluding these doesn't re-fetch them.
  const removed = useSelector(selectRemovedKeys);
  // Recalibrated numbers for cards this row is already holding. Rows render from local state fed by
  // `loadMoreFeed` — a mutation with no cache tag — so an RTK cache patch cannot reach them; without
  // this, a `feed-scores-updated` frame would update the hero and leave every paginated card stale.
  const overrides = useSelector(selectScoreOverrides);

  // Set below, once `attempt` exists; read only by the backoff timer.
  const attemptRef = useRef<() => Promise<void>>(async () => {});

  const transition = useCallback((event: RailPagingEvent): RailPaging => {
    const next = nextRailPaging(pagingRef.current, event);
    pagingRef.current = next;
    setPaging(next);
    return next;
  }, []);

  // Everything this row holds is on the page — the seed from its first render, pages as they land.
  useEffect(() => {
    page?.register(items.map((c) => c.titleId));
  }, [page, items]);

  // A scheduled retry must not fire into an unmounted row.
  useEffect(
    () => () => {
      if (retryTimer.current !== null) clearTimeout(retryTimer.current);
    },
    []
  );

  const attempt = useCallback(async () => {
    if (inFlight.current || exhaustedRef.current) return;
    inFlight.current = true;
    transition({ type: "start" });
    const body: MoreFeedRequest = {
      row,
      anchorTitleId,
      genre,
      mediaType,
      excludeTitleIds: Array.from(seenRef.current),
      limit: PAGE_SIZE,
    };
    if (page) {
      body.pageExcludeTitleIds = page.excludeFor(seenRef.current);
      body.pageToken = page.pageToken();
    }
    let next: MoreFeedResponse | null;
    try {
      next = await loadMoreFeed(body).unwrap();
    } catch {
      next = null;
    } finally {
      inFlight.current = false;
    }
    if (next === null) {
      const after = transition({ type: "failure" });
      if (after.phase === "backoff" && after.retryInMs !== null) {
        retryTimer.current = setTimeout(() => {
          retryTimer.current = null;
          void attemptRef.current();
        }, after.retryInMs);
      }
      return;
    }
    transition({ type: "success" });
    const fresh = next.items.filter((c) => !seenRef.current.has(c.titleId));
    if (fresh.length > 0) {
      fresh.forEach((c) => seenRef.current.add(c.titleId));
      setItems((prev) => [...prev, ...fresh]);
    }
    if (next.exhausted || fresh.length === 0) {
      exhaustedRef.current = true;
      setExhausted(true);
    }
  }, [row, anchorTitleId, genre, mediaType, page, loadMoreFeed, transition]);

  // The backoff timer calls whichever `attempt` is current when it fires, not the one that
  // scheduled it.
  useEffect(() => {
    attemptRef.current = attempt;
  }, [attempt]);

  /** Page on: a no-op while a request is in flight, a retry is scheduled, or the row has failed. */
  const loadMore = useCallback(() => {
    if (!canAutoPage(pagingRef.current)) return;
    void attempt();
  }, [attempt]);

  /** The Retry tile: one more request, from `failed` only. */
  const retry = useCallback(() => {
    if (pagingRef.current.phase !== "failed") return;
    transition({ type: "retry" });
    // `retry` already moved the row to loading; `attempt` re-asserts it and owns the outcome.
    void attempt();
  }, [attempt, transition]);

  const removedSet = useMemo(() => new Set(removed), [removed]);
  const visible = useMemo(
    () => applyScoreOverrides(items.filter((c) => !removedSet.has(c.titleId)), overrides),
    [items, removedSet, overrides]
  );

  const idle = canAutoPage(paging);

  // Backfill after removals. Only an ACTIVE, IDLE row tops up: idle is what stops the old retry
  // storm (a failure no longer flips straight back to "go"), and active keeps below-the-fold rows
  // quiet until the user nears them. Each pass either grows `visible` or ends the row.
  useEffect(() => {
    if (active && idle && !exhausted && visible.length < TOPUP_THRESHOLD) {
      loadMore();
    }
  }, [active, idle, visible.length, exhausted, loadMore]);

  return {
    items: visible,
    loadMore,
    retry,
    // A row waiting out a backoff is still trying, so it keeps its placeholder; a failed row is not.
    loading: isLoading || paging.phase === "loading" || paging.phase === "backoff",
    failed: paging.phase === "failed",
    idle,
    exhausted,
  };
}
