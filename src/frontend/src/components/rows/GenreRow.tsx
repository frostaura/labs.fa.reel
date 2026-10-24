import { useEffect, useRef } from "react";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow } from "../../lib/useInfiniteRow";
import RecRow from "./RecRow";
import { useNearViewport } from "./useNearViewport";

interface Props {
  slug: string;
  label: string;
  filter: BrowseFilter;
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
export default function GenreRow({ slug, label, filter }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const near = useNearViewport(ref);
  // Thread the browse-filter medium so the rail pages the selected medium; the parent re-keys the
  // rail on filter.media, so a switch remounts it cleanly with the correct-medium first page.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, retry, loading, failed, idle, exhausted } = useInfiniteRow(
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
        // Not yet near and nothing loaded: hold the rail's place rather than collapse to nothing.
        loading={loading || (!near && items.length === 0 && !exhausted)}
        failed={failed}
        onRetry={retry}
        exhausted={exhausted}
      />
    </div>
  );
}
