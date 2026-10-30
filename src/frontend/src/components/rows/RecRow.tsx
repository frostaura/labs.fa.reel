import { useState, type ReactNode } from "react";
import type { AnchoredSupply, FeedCard, RailSupply } from "../../store/feedTypes";
import RecCard from "../rec/RecCard";
import FadeScrollRow from "../FadeScrollRow";
import RailRetryTile from "./RailRetryTile";
import PickConditionsButton from "../rec/PickConditionsButton";
import { Button } from "../ui/button";
import { railSupplyLine } from "../../lib/railSupplyCopy";
import { useGetFiltersQuery, useGetSessionQuery } from "../../store/api";

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
  /** What the server counted behind a short last page (RET-07); see `railSupplyLine`. */
  supply?: RailSupply | null;
  /** What an anchored rail's short last page lost to the gate and the bar (HREV5-RET-01). */
  anchoredSupply?: AnchoredSupply | null;
  /** The anchored rail's anchor, named in its empty line. */
  anchorName?: string | null;
  /** Cards this rail loaded that the per-device browse toggles hide. */
  hiddenByView?: number;
  /**
   * Opens the conditions editor from the rail's "See why". Home passes its own dialog, so a save
   * re-seeds the page; without one the rail mounts the same dialog itself.
   */
  onSeeWhy?: () => void;
}

/** A skeleton poster card trailing the rail while the next page resolves. */
function ShimmerCard() {
  return (
    <div className="snap-start w-card shrink-0" aria-hidden data-testid="rail-skeleton">
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
  exhausted = false,
  supply,
  anchoredSupply = null,
  anchorName = null,
  hiddenByView = 0,
  onSeeWhy,
}: Props) {
  const [editorOpen, setEditorOpen] = useState(false);
  const { data: session } = useGetSessionQuery();
  const empty = items.length === 0;
  // The bar is named in an anchored rail's line only when the bar emptied part of its window; no
  // other rail asks for the filters.
  const { data: filters } = useGetFiltersQuery(undefined, { skip: !(anchoredSupply && anchoredSupply.belowBar > 0) });
  // Free pages no rails: its /more is always empty and exhausted, and the tier is the only reason.
  const supplyLine =
    exhausted && !loading && !failed
      ? railSupplyLine(supply, {
          empty,
          hiddenByView,
          tierCapped: session?.tier === "Free",
          anchored: anchoredSupply,
          anchorName,
          bar: filters?.minPredictedRating ?? null,
        })
      : null;

  // Nothing to show and nothing asked for yet — render nothing. While a first page is in flight
  // (genre rails mount empty), fall through so the shimmer rail appears instead of a gap; a rail that
  // failed says so rather than vanishing, because an empty rail and a broken one are different news.
  // And a rail the server has said is dry says THAT, with what its conditions hide, instead of
  // vanishing (RET-07): "nothing here" and "your conditions hide 5,386" are different news too. A
  // dry rail with nothing true to say about why (a Free account's) still vanishes.
  if (empty && !loading && !failed && (!exhausted || !supplyLine)) {
    return null;
  }

  const isGenre = variant === "genre";
  const seeWhy = () => (onSeeWhy ? onSeeWhy() : setEditorOpen(true));

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
          <span className="fa-caption text-3">more in this genre →</span>
        </div>
      ) : caption ? (
        <div className="space-y-1">
          <h2 className="fa-section-title text-base">{title}</h2>
          <p className="fa-caption text-3" data-testid="rail-caption">
            {caption}
          </p>
        </div>
      ) : (
        <h2 className="fa-section-title text-base">{title}</h2>
      )}

      {/* Bleeds through the gutter to the edge of whatever it sits in (WS-9, O-9): the first card
          lines up with the heading, the row scrolls out to the edge, the fades sit in the gutter. */}
      {!(empty && supplyLine) && (
        <FadeScrollRow
          bleed
          onNearEnd={loadMore}
          className="flex gap-4 overflow-x-auto snap-x pb-2 [scrollbar-width:thin]"
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
      )}

      {supplyLine && (
        <p className="fa-caption flex flex-wrap items-center gap-x-1.5 text-3" data-testid="rail-supply" data-empty={empty}>
          <span>{supplyLine.text}</span>
          {supplyLine.seeWhy && (
            <Button
              variant="link"
              onClick={seeWhy}
              className="h-auto p-0 text-xs font-normal text-fa-frost underline underline-offset-2 coarse:min-h-11"
              data-testid="rail-supply-see-why"
            >
              {supplyLine.seeWhy}
            </Button>
          )}
        </p>
      )}
      {/* The rail's own conditions dialog, only when nothing above it owns one (the title page). */}
      {editorOpen && !onSeeWhy && <PickConditionsButton trigger={false} open onOpenChange={setEditorOpen} />}
    </section>
  );
}
