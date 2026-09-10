import { useEffect } from "react";
import type { FeedCard } from "../../store/feedTypes";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow } from "../../lib/useInfiniteRow";
import { sortForMood, type TonightMood } from "../../lib/tonightMood";
import RecCardHero from "./RecCardHero";

interface Props {
  /** The snapshot's confident hero picks — the seed; the carousel pages on from there. */
  seed: FeedCard[];
  filter: BrowseFilter;
  /**
   * The tonight-mood chip in force, or null. RE-SORTS, never filters: every card the row would
   * have shown is still shown, in a different order. See lib/tonightMood.
   */
  mood?: TonightMood | null;
}

/** Tonight's picks as an endless reel: seeds from the hero snapshot, then pages forever. */
export default function InfiniteHero({ seed, filter, mood = null }: Props) {
  // The medium goes to the server so a Movies view pages movies (deepening from TMDB when the
  // eligible slice thins) — the parent re-keys this component on filter.media, so a switch remounts
  // with a fresh, correct-medium seed. Availability (released/coming) stays a client-side filter.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, loading, exhausted } = useInfiniteRow({ row: "hero", mediaType }, seed);
  const hero = sortForMood(items.filter((c) => matchesBrowseFilter(c, filter)), mood);

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
