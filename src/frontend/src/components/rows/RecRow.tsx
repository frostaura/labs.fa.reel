import type { ReactNode } from "react";
import type { FeedCard } from "../../store/feedTypes";
import RecCard from "../rec/RecCard";
import FadeScrollRow from "../FadeScrollRow";
import RailRetryTile from "./RailRetryTile";

interface Props {
  title: ReactNode;
  /** One line under the heading, for a rail that has to qualify its own title (the relaxed digest). */
  caption?: ReactNode;
  items: FeedCard[];
  /** Visual lane: "default" = personalised rows; "genre" = the distinct genre rails. */
  variant?: "default" | "genre";
  /** Infinite scroll — fired as the rail nears its end; the hook guards against re-entry. */
  loadMore?: () => void;
  loading?: boolean;
  exhausted?: boolean;
  /** Automatic retries are spent: show the Retry tile instead of a placeholder. */
  failed?: boolean;
  onRetry?: () => void;
  /**
   * How many skeleton cards hold the rail while its FIRST page loads (default 1). A seedless rail
   * that shows one shimmer card reads as "one result coming"; a row's worth reads as the row it is.
   */
  skeletonCount?: number;
}

/** A skeleton poster card trailing the rail while the next page resolves. */
function ShimmerCard() {
  return (
    <div className="snap-start w-44 sm:w-48 shrink-0" aria-hidden data-testid="rail-skeleton">
      <div className="reel-poster reel-shimmer" />
    </div>
  );
}

/** Generic horizontal scroll-snap row of collapsed cards, with optional infinite paging. */
export default function RecRow({
  title,
  caption,
  items,
  variant = "default",
  loadMore,
  loading,
  failed,
  onRetry,
  skeletonCount = 1,
}: Props) {
  // Nothing to show and nothing coming — render nothing. While a first page is in flight (genre
  // rails mount empty), fall through so the shimmer rail appears instead of a gap; a rail that
  // failed says so rather than vanishing, because an empty rail and a broken one are different news.
  if (items.length === 0 && !loading && !failed) {
    return null;
  }

  const isGenre = variant === "genre";

  return (
    <section
      className={
        isGenre
          ? "space-y-3 rounded-2xl border border-fa-edge/40 bg-gradient-to-b from-fa-frost/[0.05] to-transparent px-3 sm:px-4 py-4 -mx-1"
          : "space-y-3"
      }
    >
      {isGenre ? (
        <div className="flex items-center gap-2">
          <span className="inline-flex items-center gap-1.5 rounded-full border border-fa-frost/30 bg-fa-frost/10 px-3 py-1 text-fa-frost-bright">
            <span className="fa-status-orb bg-fa-frost" aria-hidden />
            <span className="fa-section-title text-sm">{title}</span>
          </span>
          <span className="fa-caption text-fa-frost-dim">more in this genre →</span>
        </div>
      ) : caption ? (
        <div className="space-y-1">
          <h2 className="fa-section-title text-base">{title}</h2>
          <p className="fa-caption text-fa-frost-dim" data-testid="rail-caption">
            {caption}
          </p>
        </div>
      ) : (
        <h2 className="fa-section-title text-base">{title}</h2>
      )}

      <FadeScrollRow
        onNearEnd={loadMore}
        className="flex gap-4 overflow-x-auto snap-x pb-2 -mx-1 px-1 [scrollbar-width:thin]"
      >
        {items.map((card) => (
          <div key={card.titleId} className="snap-start">
            <RecCard card={card} />
          </div>
        ))}
        {loading &&
          !failed &&
          Array.from({ length: items.length === 0 ? skeletonCount : 1 }, (_, i) => <ShimmerCard key={`skeleton-${i}`} />)}
        {failed && onRetry && <RailRetryTile onRetry={onRetry} />}
      </FadeScrollRow>
    </section>
  );
}
