import { useEffect, useRef, type ReactNode } from "react";
import type { FeedCard } from "../../store/feedTypes";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow, type RowContext } from "../../lib/useInfiniteRow";
import { sortForMood, type TonightMood } from "../../lib/tonightMood";
import RecRow from "./RecRow";
import { useNearViewport } from "./useNearViewport";

interface Props {
  title: ReactNode;
  context: RowContext;
  /** First page from the feed snapshot — high-quality seed; /more extends it forever. */
  seed: FeedCard[];
  filter: BrowseFilter;
  /** The tonight-mood chip in force. Re-sorts this rail in place; removes nothing. */
  mood?: TonightMood | null;
  /** A time limit per request; past it the rail shows its Retry tile (see `useInfiniteRow`). */
  timeoutMs?: number;
  /** Skeleton cards held while the first page loads (see `RecRow`). */
  skeletonCount?: number;
  /** Opens the page's conditions editor from the rail's "See why" (RET-07); else the rail mounts one. */
  onSeeWhy?: () => void;
  /** The anchor's name, for an anchored rail's empty line (HREV5-RET-01). */
  anchorName?: string | null;
}

/**
 * A personalised feed row (because-you-loved / deep-cuts / new / more-like-this) wired for infinite
 * scroll. The parent re-keys this on the feed's generatedAt (or the anchor title id), so a rebuild
 * or a title switch remounts it with the fresh seed. It fetches on its own — a first page when it
 * has no seed (the detail page's "more like this"), a top-up when its seed is thin — only once it
 * nears the viewport; a seeded row renders its snapshot slice immediately either way.
 */
export default function InfiniteRecRow({ title, context, seed, filter, mood = null, timeoutMs, skeletonCount, onSeeWhy, anchorName = null }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const near = useNearViewport(ref);
  // Thread the browse-filter medium to the server so the paged window + TMDB deepen are media-specific;
  // the parent re-keys this row on filter.media, so a switch remounts it with the correct-medium seed.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, retry, loading, failed, idle, exhausted, supply, anchoredSupply } = useInfiniteRow(
    { ...context, mediaType },
    seed,
    { active: near, timeoutMs }
  );

  const visible = sortForMood(items.filter((c) => matchesBrowseFilter(c, filter)), mood);

  // Auto-fill an empty filtered view (a seedless row's first page, or a media switch that left the
  // loaded slice with no matching cards). Near and idle only — see GenreRow.
  useEffect(() => {
    if (near && idle && visible.length === 0 && !exhausted) {
      loadMore();
    }
  }, [near, idle, visible.length, exhausted, loadMore]);

  return (
    <div ref={ref}>
      <RecRow
        title={title}
        items={visible}
        loadMore={near ? loadMore : undefined}
        loading={loading || (!near && items.length === 0 && !exhausted)}
        failed={failed}
        onRetry={retry}
        exhausted={exhausted}
        skeletonCount={skeletonCount}
        supply={supply}
        anchoredSupply={anchoredSupply}
        anchorName={anchorName}
        hiddenByView={items.length - visible.length}
        onSeeWhy={onSeeWhy}
        // Anchored rows carry evidence chips (because-you-loved, more-like-this).
        reserveEvidence={context.anchorTitleId != null}
      />
    </div>
  );
}
