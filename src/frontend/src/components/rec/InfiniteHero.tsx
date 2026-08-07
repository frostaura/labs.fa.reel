import { useEffect } from "react";
import type { FeedCard } from "../../store/feedTypes";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow } from "../../lib/useInfiniteRow";
import RecCardHero from "./RecCardHero";

interface Props {
  /** The snapshot's confident hero picks — the seed; the carousel pages on from there. */
  seed: FeedCard[];
  filter: BrowseFilter;
}

/** Tonight's picks as an endless reel: seeds from the hero snapshot, then pages forever. */
export default function InfiniteHero({ seed, filter }: Props) {
  // The medium goes to the server so a Movies view pages movies (deepening from TMDB when the
  // eligible slice thins) — the parent re-keys this component on filter.media, so a switch remounts
  // with a fresh, correct-medium seed. Availability (released/coming) stays a client-side filter.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, loading, exhausted } = useInfiniteRow({ row: "hero", mediaType }, seed);
  const hero = items.filter((c) => matchesBrowseFilter(c, filter));

  // Auto-fill an empty filtered view: with the seed being only ~5 hero picks (often all shows), a
  // freshly-selected Movies view can render zero cards. Rather than dead-ending on "Nothing matches",
  // page the next (now media-specific) slice — the exhausted latch stops this once the medium is dry.
  useEffect(() => {
    if (hero.length === 0 && !loading && !exhausted) {
      void loadMore();
    }
  }, [hero.length, loading, exhausted, loadMore]);

  if (hero.length === 0) {
    return (
      <p className="fa-body text-fa-frost-dim text-center py-8">
        {exhausted ? "Nothing matches this filter — try widening it." : "Finding more…"}
      </p>
    );
  }

  return <RecCardHero cards={hero} loadMore={loadMore} loading={loading} />;
}
