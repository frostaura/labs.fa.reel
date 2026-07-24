import { useCallback, useRef, useState } from "react";
import { useSelector } from "react-redux";
import { useLoadMoreFeedMutation } from "../store/api";
import { selectRemovedKeys } from "../store/removedTitlesSlice";
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
 * Drives one row's infinite scroll: seeds from the snapshot's first page, then appends
 * server-scored pages on demand, excluding everything already shown so a title never repeats
 * within the row. The owning component is re-keyed on the feed's generatedAt, so a rebuild
 * remounts the row with the new seed — no in-place reset needed here. A title-level seen-set plus
 * an in-flight guard keep `loadMore` idempotent under the rapid fire of a scroll listener;
 * `exhausted` latches when the catalog lane is truly dry (a page came back empty even after the
 * server tried to deepen it from TMDB).
 */
export function useInfiniteRow(context: RowContext, seed: FeedCard[]) {
  const { row, anchorTitleId, genre, mediaType } = context;
  const [items, setItems] = useState<FeedCard[]>(seed);
  const [exhausted, setExhausted] = useState(false);
  const [loadMoreFeed, { isLoading }] = useLoadMoreFeedMutation();
  const seenRef = useRef<Set<string>>(new Set(seed.map((c) => c.titleId)));
  const inFlight = useRef(false);
  // Titles the user just made ineligible (seen/rated, not-interested, saved) drop from view at
  // once. Pagination still tracks the full set via seenRef, so excluding these doesn't re-fetch them.
  const removed = useSelector(selectRemovedKeys);

  const loadMore = useCallback(async () => {
    if (inFlight.current || exhausted) return;
    inFlight.current = true;
    try {
      const body: MoreFeedRequest = {
        row,
        anchorTitleId,
        genre,
        mediaType,
        excludeTitleIds: Array.from(seenRef.current),
        limit: PAGE_SIZE,
      };
      // Distinguish a transient failure (500/429/offline) from genuine dryness: a caught error must
      // NOT latch `exhausted`, or one network blip kills the row for the whole session (it would never
      // try to page again). On success, the server's `exhausted` flag is the authoritative "lane dry"
      // signal — it already tried to deepen from TMDB; an all-seen page still latches too.
      let next: MoreFeedResponse | null;
      try {
        next = await loadMoreFeed(body).unwrap();
      } catch {
        next = null;
      }
      if (next === null) {
        return; // transient — leave the row live so the next scroll retries
      }
      const fresh = next.items.filter((c) => !seenRef.current.has(c.titleId));
      if (fresh.length > 0) {
        fresh.forEach((c) => seenRef.current.add(c.titleId));
        setItems((prev) => [...prev, ...fresh]);
      }
      if (next.exhausted || fresh.length === 0) {
        setExhausted(true);
      }
    } finally {
      inFlight.current = false;
    }
  }, [row, anchorTitleId, genre, mediaType, exhausted, loadMoreFeed]);

  const removedSet = new Set(removed);
  const visible = items.filter((c) => !removedSet.has(c.titleId));

  return { items: visible, loadMore, loading: isLoading, exhausted };
}
