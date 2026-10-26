import { useEffect, useRef, useState } from "react";
import { Link, useLocation } from "react-router";
import type { FeedCard } from "../../store/feedTypes";
import { backdropUrl, posterUrl } from "../../lib/tmdbImages";
import { ScoreChip } from "./ScoreBadges";
import { formatRuntime } from "../../lib/format";
import CardQuickActions from "./CardQuickActions";
import RailRetryTile from "../rows/RailRetryTile";
import { heroReachingNote, reachingLabel } from "../../lib/heroConfidence";
import { useMinWidth } from "../../lib/useMinWidth";
import {
  LG_BREAKPOINT_PX,
  clampColumn,
  heroActiveColumn,
  heroArtClass,
  heroCardClass,
  heroColumns,
  heroContainerClass,
  heroEagerCount,
  heroProseScale,
  heroRowsInEffect,
  heroScrimClass,
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
  /** Paging failed after its automatic retries: the trailing cell becomes a Retry control. */
  failed?: boolean;
  onRetry?: () => void;
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
 * case. So a pick that came in under the bar says so on the card — unless every measured pick is a
 * reach, in which case Home's status line says it once and no card repeats it (FD-15). The
 * derivation is in lib/heroConfidence.ts.
 */
export default function RecCardHero({ cards, loadMore, loading, failed, onRetry, rows = 1 }: HeroProps) {
  const [active, setActive] = useState(0);
  const location = useLocation();
  // Two rows is a request; below `lg` the grid collapses back to one and every derived count has to
  // collapse with it, or the dots promise scroll positions that do not exist.
  const isLgUp = useMinWidth(LG_BREAKPOINT_PX);
  const rowsInEffect = heroRowsInEffect(rows, isLgUp);
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

  // The whole row is a reach: Home's status line says so once, so the cards stay quiet.
  const rowIsReaching = heroReachingNote(cards) !== null;
  const columns = heroColumns(cards.length, rowsInEffect);
  const eagerCount = heroEagerCount(rowsInEffect);

  return (
    <section aria-label="Tonight's picks" className="space-y-3">
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
            // The card is a container, not a link: its link is an overlay covering the whole card
            // (so a tap anywhere still opens the title) and the quick actions are the overlay's
            // siblings, stacked above it, rather than controls nested inside a link (see RecCard).
            // An overlay rather than RecCard's `::after`, because the text block is positioned and
            // would trap a pseudo-element inside itself.
            <div key={card.titleId} className={heroCardClass(rows)} data-testid="hero-card">
              <div className={heroArtClass()}>
                {art && (
                  <img
                    src={art}
                    alt=""
                    loading={index < eagerCount ? "eager" : "lazy"}
                    decoding="async"
                    className="absolute inset-0 h-full w-full object-cover transition-transform duration-500 group-hover:scale-[1.03]"
                  />
                )}
                <div className={heroScrimClass()} />
              </div>
              <div className={heroTextBlockClass(rows)} data-testid="hero-text">
                <div className="flex items-center gap-x-3 gap-y-1.5 flex-wrap">
                  <h2 className={heroTitleClass(rows)}>{card.name}</h2>
                  {/* The same one chip every card carries (FD-4) — the banded prediction over the
                      crowd score — at hero scale, beside the title. Each line is absent when its
                      number is, never a placeholder (the guards are in lib/scoreBadges.ts). */}
                  <ScoreChip
                    predictedRating={card.predictedRating}
                    crowdRating={card.crowdRating}
                    crowdVotes={card.crowdVotes}
                    size="lg"
                  />
                </div>
                <p className="fa-caption text-fa-frost">
                  {[card.year, formatRuntime(card.runtimeMinutes), card.genres.slice(0, 3).join(" / ")]
                    .filter(Boolean)
                    .join(" · ")}
                </p>
                {/* The hook — the one line written to make you press play tonight — clamped to two
                    lines. The lens clause that used to sit under it now lives only in the detail
                    view: on the card it was the line that pushed the title out of the box. Falls
                    back to the single templated why-line when no hook exists. */}
                {card.hook ? (
                  <p className={`${heroProseScale(rows)} text-fa-frost-bright max-w-2xl leading-snug`} data-testid="hero-hook">
                    {card.hook}
                  </p>
                ) : (
                  card.whyThis && (
                    <p className={`${heroProseScale(rows)} text-fa-frost max-w-2xl leading-snug`} data-testid="hero-why">
                      {card.whyThis}
                    </p>
                  )
                )}
                {/* Quiet on purpose: an admission, not an alarm. It sits under the why-line so the
                    pick is still the headline and the caveat is attached to it, not to the row. */}
                {!rowIsReaching && reachingLabel(card) && (
                  <p className="fa-caption text-fa-frost-dim" data-testid="hero-reaching-label">
                    {reachingLabel(card)}
                  </p>
                )}
              </div>
              <Link
                to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
                state={{ backgroundLocation: location }}
                className="absolute inset-0 z-[1] rounded-2xl focus:outline-none"
                data-testid="hero-card-link"
              >
                <span className="sr-only">{card.name}</span>
              </Link>
              {/* The same three actions the rail cards carry, at hero scale. Below `lg` they are a
                  row of their own under the text (I-10: over the art they sat on the meta and hook
                  lines); from `lg` they are a row in the art's top-right corner, stacked above the
                  title link's card-wide overlay so they act instead of navigating. */}
              <CardQuickActions
                titleId={card.titleId}
                mediaType={card.mediaType}
                tmdbId={card.tmdbId}
                name={card.name}
                size="lg"
              />
            </div>
          );
        })}
        {loading && !failed && (
          <div
            className={heroShimmerClass(rows)}
            aria-hidden
          />
        )}
        {failed && onRetry && (
          <div className="snap-center self-center shrink-0 w-48">
            <RailRetryTile shape="inline" onRetry={onRetry} />
          </div>
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
