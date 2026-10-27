import { useEffect, useRef } from "react";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow } from "../../lib/useInfiniteRow";
import RecRow from "./RecRow";
import { useNearViewport } from "./useNearViewport";

interface Props {
  slug: string;
  label: string;
  filter: BrowseFilter;
  /** Opens the page's conditions editor from the rail's "See why" (RET-07). */
  onSeeWhy?: () => void;
}

/**
 * A genre rail — visually distinct from the personalised rows (tinted, pill header). It carries no
 * snapshot seed, so it fills its first page when it nears the viewport (not on mount: eight eager
 * rails were eight billed curation calls and eight batches of exposure rows per Home load for rails
 * nobody scrolled to — I-2, O-22) and pages on as the user scrolls. Until then it holds its place
 * with a placeholder, which is also what keeps the rails apart: eight zero-height rails would all
 * sit at the same point and all come into range at once. The parent re-keys it (slug + feed
 * generatedAt), so a genre swap or rebuild remounts it cleanly.
 */
export default function GenreRow({ slug, label, filter, onSeeWhy }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const near = useNearViewport(ref);
  // Thread the browse-filter medium so the rail pages the selected medium; the parent re-keys the
  // rail on filter.media, so a switch remounts it cleanly with the correct-medium first page.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, retry, loading, failed, idle, exhausted, supply } = useInfiniteRow(
    { row: "genre", genre: slug, mediaType },
    [],
    { active: near }
  );

  const visible = items.filter((c) => matchesBrowseFilter(c, filter));

  // Auto-fill an empty filtered view — including the very first page — once the rail is near:
  // page the next media-specific slice rather than show an empty rail; exhausted latches the stop
  // when the medium is dry, and `idle` keeps a failing rail from re-firing (O-7).
  useEffect(() => {
    if (near && idle && visible.length === 0 && !exhausted) {
      loadMore();
    }
  }, [near, idle, visible.length, exhausted, loadMore]);

  return (
    <div ref={ref} data-testid="genre-rail" data-genre={slug}>
      <RecRow
        title={label}
        items={visible}
        variant="genre"
        loadMore={near ? loadMore : undefined}
        // Nothing loaded yet: hold the rail's place rather than collapse to nothing — whether or not
        // it is near. Holding it only while NOT near left one frame, between `near` latching and the
        // first request starting, in which the rail had no height: every rail below jumped up by a
        // rail's height, came into range, and fetched too (review D4 — a cold phone Home with no
        // personalised rows asked for drama and horror from 1,385px down).
        loading={loading || (items.length === 0 && !exhausted && !failed)}
        failed={failed}
        onRetry={retry}
        exhausted={exhausted}
        supply={supply}
        hiddenByView={items.length - visible.length}
        onSeeWhy={onSeeWhy}
      />
    </div>
  );
}
