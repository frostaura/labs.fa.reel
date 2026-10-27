import { Link, useLocation } from "react-router";
import { AlertTriangle, RotateCcw } from "lucide-react";
import PosterImage from "../rec/PosterImage";
import ScoreBadges from "../rec/ScoreBadges";
import CardQuickActions from "../rec/CardQuickActions";
import PillButton from "./PillButton";
import { listPhrase } from "../../lib/askReel/copy";
import type { AskCard } from "../../lib/askReelStream";

/** The grid every Ask Reel card list renders in — the answer, its trays and earlier answers alike. */
export const CARD_GRID = "grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4";

/**
 * One Ask Reel result card — identical for clean picks, the outside-request channel, every tray and
 * every earlier answer (the only difference is WHERE they render and what their server-written
 * `why` says).
 *
 * No entrance fade. The shared `reel-rise` animation starts at opacity 0, so every card streaming
 * into an answer spent its first frames under the 0.9 floor chat UI v2 holds every visible card to
 * (WS-2 task 3: "no card drops below 0.9 opacity while streaming"). The grid reserves its cells
 * with skeletons, so a card arriving fills a box that is already there and needs no motion to read
 * as new.
 */
export function ResultCard({ card, onSaveTapped }: { card: AskCard; onSaveTapped: (card: AskCard) => void }) {
  const location = useLocation();
  return (
    <Link
      to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
      state={{ backgroundLocation: location }}
      className="group block focus:outline-none"
      data-testid="askreel-card"
    >
      {/* The focus ring lives here, not on the <a>: the link sets `focus:outline-none` so the
          browser default does not cut across the poster, which left keyboard navigation with NO
          visible focus at all. `group-focus-visible` puts it back, on the card, and only for
          keyboard focus. */}
      <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-focus-visible:ring-2 group-focus-visible:ring-fa-frost/60 group-focus-visible:ring-offset-2">
        <PosterImage path={card.posterPath} alt={card.name} size="w185" />
        {/* The shared stack: prediction over crowd, no-overlap, scrimmed — and it owns the
            null/NaN guards, so a title with no community score shows one badge, never a dash. */}
        <ScoreBadges
          predictedRating={card.predictedRating}
          crowdRating={card.crowdRating}
          crowdVotes={card.crowdVotes}
        />
        {/* Quick actions and the Seen pill share the top-left corner; a seen title gets the pill
            (dismissing or queueing something already watched is meaningless), everything else gets
            the one-tap actions. ResultCard deliberately does NOT converge onto RecCard — the Ask
            payload has `why`/`seen` and lacks runtime/providers, so only the overlay is shared. */}
        {card.seen ? (
          <span className="absolute top-2 left-2 rounded-full bg-fa-ink/80 px-2 py-0.5 fa-caption text-fa-frost-dim ring-1 ring-fa-edge">
            Seen
          </span>
        ) : (
          /* The save tap, observed rather than owned. `CardQuickActions` belongs to the rec
             surfaces and fires the same three actions everywhere; only Ask Reel needs to know
             that a save happened HERE, because only here does the card vanish out of an answer
             the page is still explaining in a footnote. `display: contents` so the wrapper adds
             no box — the overlay stays positioned against the card, not against this span. */
          <span style={{ display: "contents" }} onClickCapture={(e) => {
            if ((e.target as Element).closest?.('[data-testid="quick-save"]')) onSaveTapped(card);
          }}>
            <CardQuickActions
              titleId={card.titleId}
              mediaType={card.mediaType}
              tmdbId={card.tmdbId}
              name={card.name}
            />
          </span>
        )}
      </div>
      <p className="mt-2 fa-body text-fa-frost-bright truncate" title={card.name}>{card.name}</p>
      <p className="fa-caption text-fa-frost-dim">{card.year ?? ""}</p>
      {card.why && (
        <p className="fa-caption text-fa-frost/80 line-clamp-2" title={card.why}>{card.why}</p>
      )}
    </Link>
  );
}

/** A list of cards in the shared grid. */
export function CardGrid({ cards, onSaveTapped }: { cards: AskCard[]; onSaveTapped: (card: AskCard) => void }) {
  return (
    <div className={CARD_GRID}>
      {cards.map((card) => (
        <ResultCard key={card.titleId} card={card} onSaveTapped={onSaveTapped} />
      ))}
    </div>
  );
}

/**
 * A card-shaped hole in the grid, at the size a card will fill.
 *
 * Poster, name and year — deliberately not the why-line, which arrives with the rerank and which a
 * streaming card does not have either. `aria-hidden` because it says nothing: the live region and
 * the banner's narration are what tell a screen-reader user the turn is working.
 */
export function CardSkeleton() {
  return (
    <div aria-hidden data-testid="askreel-skeleton">
      <div className="reel-poster reel-shimmer" />
      <p className="mt-2 fa-body select-none">&nbsp;</p>
      <p className="fa-caption select-none">&nbsp;</p>
    </div>
  );
}

/**
 * The retry Ask Reel never had. Before this, a failed turn left the user with nothing but the
 * option to retype their question — so it replays the last one in place, adding no second copy of
 * their bubble to the transcript.
 */
export function RetryButton({ onRetry, disabled, label = "Try again" }: { onRetry: () => void; disabled: boolean; label?: string }) {
  return (
    <PillButton tone="strong" onClick={onRetry} disabled={disabled} data-testid="askreel-retry">
      <RotateCcw className="h-3.5 w-3.5" aria-hidden /> {label}
    </PillButton>
  );
}

/**
 * The collision notice: the server's sentence, and the terms it is about.
 *
 * The prose is server-written and rendered verbatim — it knows both halves (what was asked and
 * what the account has saved) and the page knows neither. The term list beside it is the page's
 * one addition, and it earns its place by being DATA rather than prose: it is the scannable,
 * actionable part, it sits next to the pointer at Conditions, and it is the only thing on screen
 * that can name an avoid-keyword collision ("anime", "musical") — those carry an empty
 * `excludedGenres`, so before `excludedKeywords` reached the client there was nothing to name.
 */
export function ConflictNotice({ message, terms }: { message: string; terms: string[] }) {
  return (
    <div className="fa-card border-fa-warning/30 p-4 flex items-start gap-3" data-testid="askreel-conflict">
      <AlertTriangle className="h-4 w-4 text-fa-warning shrink-0 mt-0.5" />
      <div className="space-y-1">
        <p className="fa-caption text-fa-frost-bright">{message}</p>
        {terms.length > 0 && (
          <p className="fa-caption text-fa-frost-dim/80" data-testid="askreel-conflict-terms">
            Rules out {listPhrase(terms)}.
          </p>
        )}
      </div>
    </div>
  );
}
