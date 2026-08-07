import { useEffect } from "react";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { useInfiniteRow } from "../../lib/useInfiniteRow";
import RecRow from "./RecRow";

interface Props {
  slug: string;
  label: string;
  filter: BrowseFilter;
}

/**
 * A genre rail — visually distinct from the personalised rows (tinted, pill header). It carries no
 * snapshot seed, so it fills its first page on mount and pages on as the user scrolls. The parent
 * re-keys it (slug + feed generatedAt), so a genre swap or rebuild remounts it cleanly.
 */
export default function GenreRow({ slug, label, filter }: Props) {
  // Thread the browse-filter medium so the rail pages the selected medium; the parent re-keys the
  // rail on filter.media, so a switch remounts it cleanly with the correct-medium first page.
  const mediaType = filter.media === "all" ? undefined : filter.media;
  const { items, loadMore, loading, exhausted } = useInfiniteRow({ row: "genre", genre: slug, mediaType }, []);

  // Kick the first page so the rail appears without waiting for a scroll near-end.
  useEffect(() => {
    void loadMore();
  }, [loadMore]);

  const visible = items.filter((c) => matchesBrowseFilter(c, filter));

  // Auto-fill an empty filtered view: page the next media-specific slice rather than showing an
  // empty rail; exhausted latches the stop when the medium is dry.
  useEffect(() => {
    if (visible.length === 0 && !loading && !exhausted) {
      void loadMore();
    }
  }, [visible.length, loading, exhausted, loadMore]);

  return (
    <RecRow
      title={label}
      items={visible}
      variant="genre"
      loadMore={loadMore}
      loading={loading}
      exhausted={exhausted}
    />
  );
}
