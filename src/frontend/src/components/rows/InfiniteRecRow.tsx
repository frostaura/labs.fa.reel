import { useEffect, type ReactNode } from "react";
import type { FeedCard } from "../../store/feedTypes";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow, type RowContext } from "../../lib/useInfiniteRow";
import { sortForMood, type TonightMood } from "../../lib/tonightMood";
import RecRow from "./RecRow";

interface Props {
  title: ReactNode;
  context: RowContext;
  /** First page from the feed snapshot — high-quality seed; /more extends it forever. */
  seed: FeedCard[];
  filter: BrowseFilter;
  /** The tonight-mood chip in force. Re-sorts this rail in place; removes nothing. */
  mood?: TonightMood | null;
}

/**
 * A personalised feed row (because-you-loved / deep-cuts / new / more-like-this) wired for infinite
 * scroll. The parent re-keys this on the feed's generatedAt (or the anchor title id), so a rebuild
 * or a title switch remounts it with the fresh seed. When it mounts with no seed (e.g. the detail
 * page's "more like this" rail), it self-loads its first page like the genre rails do.
 */
export default function InfiniteRecRow({ title, context, seed, filter, mood = null }: Props) {
  // Thread the browse-filter medium to the server so the paged window + TMDB deepen are media-specific;
  // the parent re-keys this row on filter.media, so a switch remounts it with the correct-medium seed.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, loading, exhausted } = useInfiniteRow({ ...context, mediaType }, seed);

  // Seedless rows have nothing to render — and no cards means no scroll to trigger paging — so kick
  // the first page on mount. Seeded home rows already show their snapshot slice and page on scroll,
  // so this only fires for the empty-seed case. (loadMore's identity only shifts when the lane
  // exhausts, and its own in-flight/exhausted guards keep this from over-fetching.)
  useEffect(() => {
    if (seed.length === 0) {
      void loadMore();
    }
  }, [loadMore, seed.length]);

  const visible = sortForMood(items.filter((c) => matchesBrowseFilter(c, filter)), mood);

  // Auto-fill an empty filtered view: a media switch can leave the loaded slice with no matching
  // cards. Page the next (media-specific) slice instead of showing an empty row; exhausted latches
  // the stop when the medium is genuinely dry.
  useEffect(() => {
    if (visible.length === 0 && !loading && !exhausted) {
      void loadMore();
    }
  }, [visible.length, loading, exhausted, loadMore]);

  return (
    <RecRow title={title} items={visible} loadMore={loadMore} loading={loading} exhausted={exhausted} />
  );
}
