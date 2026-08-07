import type { ReactNode } from "react";
import type { FeedCard } from "../../store/feedTypes";
import RecCard from "../rec/RecCard";
import FadeScrollRow from "../FadeScrollRow";

interface Props {
  title: ReactNode;
  items: FeedCard[];
  /** Visual lane: "default" = personalised rows; "genre" = the distinct genre rails. */
  variant?: "default" | "genre";
  /** Infinite scroll — fired as the rail nears its end; the hook guards against re-entry. */
  loadMore?: () => void;
  loading?: boolean;
  exhausted?: boolean;
}

/** A skeleton poster card trailing the rail while the next page resolves. */
function ShimmerCard() {
  return (
    <div className="snap-start w-44 sm:w-48 shrink-0" aria-hidden>
      <div className="reel-poster reel-shimmer" />
    </div>
  );
}

/** Generic horizontal scroll-snap row of collapsed cards, with optional infinite paging. */
export default function RecRow({ title, items, variant = "default", loadMore, loading }: Props) {
  // Nothing to show and nothing coming — render nothing. While a first page is in flight (genre
  // rails mount empty), fall through so the shimmer rail appears instead of a gap.
  if (items.length === 0 && !loading) {
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
        {loading && <ShimmerCard />}
      </FadeScrollRow>
    </section>
  );
}
