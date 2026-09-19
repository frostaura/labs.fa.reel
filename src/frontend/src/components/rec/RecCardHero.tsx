import { useEffect, useRef, useState } from "react";
import { Link, useLocation } from "react-router";
import type { FeedCard } from "../../store/feedTypes";
import { backdropUrl, posterUrl } from "../../lib/tmdbImages";
import PredictedRatingBadge from "./PredictedRatingBadge";
import CrowdRatingBadge from "./CrowdRatingBadge";
import { formatRuntime } from "../../lib/format";
import { hasPredictedRating } from "../../lib/predictedRating";
import CardQuickActions from "./CardQuickActions";
import { heroReachingNote, reachingLabel } from "../../lib/heroConfidence";
import { MD_BREAKPOINT_PX, useMinWidth } from "../../lib/useMinWidth";
import {
  clampColumn,
  heroActiveColumn,
  heroCardClass,
  heroColumns,
  heroContainerClass,
  heroEagerCount,
  heroProseScale,
  heroRowsInEffect,
  heroShimmerClass,
  heroTextBlockClass,
  heroTitleClass,
  showHeroDots,
  type HeroRows,
} from "../../lib/heroLayout";

interface HeroProps {
  cards: FeedCard[];
  /** Infinite scroll — tonight's picks never run out; fired as the carousel nears its end. */
  loadMore?: () => void;
  loading?: boolean;
  /**
   * How many rows of picks to lay out. Two rows is one scroll container flowed by column, so the
   * rows move together and rank 1 sits above rank 2 — see lib/heroLayout.ts. Defaults to the single
   * row the hero has always been; the caller decides, because "can this tier page for more cards"
   * is a tier question, not a layout one.
   */
  rows?: HeroRows;
}

const NEAR_END_PX = 1200;

/**
 * Tonight's-picks hero: big confident picks in a snap carousel that scrolls forever — backdrop art
 * with the frost scrim carrying an oversized why-this, the answer to "what should I watch tonight".
 *
 * "Confident" is a claim, and the build is allowed to fall short of it: when nothing clears the 7.5
 * bar it backfills the row from the best of what is left, silently. On live data that is the normal
 * case. So a pick that came in under the bar says so on the card, and a row where every measured
 * pick is a reach says so once above the row — the derivation is in lib/heroConfidence.ts.
 */
export default function RecCardHero({ cards, loadMore, loading, rows = 1 }: HeroProps) {
  const [active, setActive] = useState(0);
  const location = useLocation();
  // Two rows is a request; below `md` the grid collapses back to one and every derived count has to
  // collapse with it, or the dots promise scroll positions that do not exist.
  const isMdUp = useMinWidth(MD_BREAKPOINT_PX);
  const rowsInEffect = heroRowsInEffect(rows, isMdUp);
  const scroller = useRef<HTMLDivElement>(null);
  // A re-sort (a tonight-mood chip, the availability toggle) puts a different card first. Chrome's
  // snap-target tracking would otherwise follow the previously snapped card to its new column, and
  // the new number-one pick ends up half off-screen to the left — so when the lead changes, start
  // from the start. Paging appends, so it never trips this.
  const leadId = cards[0]?.titleId;
  // Derived during render rather than in an effect: setState inside an effect costs a second
  // render pass, and the dots would paint one frame against the old index before correcting.
  const [syncedLeadId, setSyncedLeadId] = useState(leadId);
  if (leadId !== syncedLeadId) {
    setSyncedLeadId(leadId);
    setActive(0);
  }
  useEffect(() => {
    scroller.current?.scrollTo({ left: 0, behavior: "instant" as ScrollBehavior });
  }, [leadId]);

  if (cards.length === 0) {
    return null;
  }

  const reachingNote = heroReachingNote(cards);
  const columns = heroColumns(cards.length, rowsInEffect);
  const eagerCount = heroEagerCount(rowsInEffect);

  return (
    <section aria-label="Tonight's picks" className="space-y-3">
      {/* Said once, above the row, in the same register as the feed's degraded-reason copy — the
          whole headline is a reach and pretending otherwise is the thing that costs trust. */}
      {reachingNote && (
        <p className="fa-caption text-fa-warning" data-testid="hero-reaching-note">
          {reachingNote}
        </p>
      )}
      <div ref={scroller} className={heroContainerClass(rows)}
        data-testid="hero-carousel"
        onScroll={(e) => {
          const el = e.currentTarget;
          setActive(heroActiveColumn(el.scrollLeft, el.scrollWidth, el.clientWidth, columns));
          if (loadMore && el.scrollLeft + el.clientWidth >= el.scrollWidth - NEAR_END_PX) {
            loadMore();
          }
        }}
      >
        {cards.map((card, index) => {
          const art = backdropUrl(card.backdropPath, "w1280") ?? posterUrl(card.posterPath, "w500");
          return (
            <Link
              key={card.titleId}
              to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
              state={{ backgroundLocation: location }}
              className={heroCardClass(rows)}
              data-testid="hero-card"
            >
              {art && (
                <img
                  src={art}
                  alt=""
                  loading={index < eagerCount ? "eager" : "lazy"}
                  decoding="async"
                  className="absolute inset-0 h-full w-full object-cover transition-transform duration-500 group-hover:scale-[1.03]"
                />
              )}
              <div className="absolute inset-0 reel-scrim" />
              {/* The same three actions the rail cards carry, at hero scale. The headline pick was
                  the ONE card you could not act on without navigating into the modal and back —
                  which is a strange thing to say about the card the whole screen is built around.
                  Every button calls preventDefault, so the whole-card link still works everywhere
                  the buttons are not. */}
              <CardQuickActions
                titleId={card.titleId}
                mediaType={card.mediaType}
                tmdbId={card.tmdbId}
                name={card.name}
                size="lg"
              />
              <div className={heroTextBlockClass(rows)}>
                <div className="flex items-center gap-3 flex-wrap">
                  <h2 className={heroTitleClass(rows)}>{card.name}</h2>
                  {/* Guarded like every other consumer — see lib/predictedRating.ts. The crowd
                      score sits directly beside the prediction (there is room at hero scale) so the
                      two are read as a pair; it is simply absent when the title has no community
                      score, never a placeholder. */}
                  {hasPredictedRating(card.predictedRating) && (
                    <PredictedRatingBadge rating={card.predictedRating} size="lg" />
                  )}
                  {hasPredictedRating(card.crowdRating) && (
                    <CrowdRatingBadge rating={card.crowdRating} votes={card.crowdVotes} size="lg" />
                  )}
                </div>
                <p className="fa-caption text-fa-frost-dim">
                  {[card.year, formatRuntime(card.runtimeMinutes), card.genres.slice(0, 3).join(" / ")]
                    .filter(Boolean)
                    .join(" · ")}
                </p>
                {/* The hook first — the one line written to make you press play tonight — with the
                    lens's opening clause beneath it. Falls back to the single templated why-line
                    when no copy exists, which is the shape the hero has always had. */}
                {card.hook ? (
                  <>
                    <p className={`${heroProseScale(rows)} text-fa-frost-bright max-w-2xl leading-snug`} data-testid="hero-hook">
                      {card.hook}
                    </p>
                    {card.lensClause && (
                      <p className="fa-caption text-fa-frost max-w-2xl leading-snug" data-testid="hero-lens-clause">
                        {card.lensClause}
                      </p>
                    )}
                  </>
                ) : (
                  <p className={`${heroProseScale(rows)} text-fa-frost max-w-2xl leading-snug`} data-testid="hero-why">
                    {card.whyThis}
                  </p>
                )}
                {/* Quiet on purpose: an admission, not an alarm. It sits under the why-line so the
                    pick is still the headline and the caveat is attached to it, not to the row. */}
                {reachingLabel(card) && (
                  <p className="fa-caption text-fa-frost-dim" data-testid="hero-reaching-label">
                    {reachingLabel(card)}
                  </p>
                )}
              </div>
            </Link>
          );
        })}
        {loading && (
          <div
            className={heroShimmerClass(rows)}
            aria-hidden
          />
        )}
      </div>
      {showHeroDots(columns) && (
        <div className="flex justify-center gap-1.5" aria-hidden data-testid="hero-dots">
          {Array.from({ length: columns }, (_, index) => (
            <span
              key={index}
              className={`h-1.5 rounded-full transition-all ${index === clampColumn(active, columns) ? "w-6 bg-fa-frost" : "w-1.5 bg-fa-frost/30"}`}
            />
          ))}
        </div>
      )}
    </section>
  );
}
