import { Link, useLocation } from "react-router";
import type { FeedCard } from "../../store/feedTypes";
import PosterImage from "./PosterImage";
import ScoreBadges from "./ScoreBadges";
import CardQuickActions from "./CardQuickActions";
import { logoUrl } from "../../lib/tmdbImages";
import { formatRuntime } from "../../lib/format";
import { evidenceChips } from "../../lib/evidenceChips";
import { cardProviders } from "../../lib/whereToWatch";

/**
 * A card's text block is the SAME height on every card of a rail, whatever arrives (HREV8-DESK-01).
 * A rail is as tall as its tallest card, so a card that brought one more line than the cards before
 * it grew the rail and shoved everything under it down the page — CLS 0.21 scrolling a cold Home.
 * So each line holds its height whether or not it has text: the title and meta one line each (a
 * card with no year or runtime used to drop its meta line entirely), the why two (it clamps at two,
 * and a one-line why used to be a line shorter), and an evidence slot sized for the most it can
 * hold — three chips, each at most a line, so three lines.
 *
 * `RecCardSkeleton` lays out the same block, so a rail's placeholder is exactly as tall as the
 * cards that replace it.
 */
const TEXT_BLOCK = "mt-2 space-y-1";
const TITLE_LINE = "fa-body font-medium text-fa-frost-bright truncate min-h-[1lh]";
const META_LINE = "fa-caption text-3 min-h-[1lh]";
const WHY_LINES = "fa-caption text-fa-frost/80 line-clamp-2 leading-snug min-h-[2lh]";
/** Three chip lines (h-5 each, the chip's own height), the two gaps between them and the top pad. */
const EVIDENCE_SLOT = "min-h-[4.375rem] pt-0.5";

/** The text block's lines held empty, under a shimmer poster: a rail placeholder card. */
export function RecCardSkeleton({ evidence = false }: { evidence?: boolean }) {
  return (
    <div className="w-card shrink-0" aria-hidden data-testid="rail-skeleton">
      <div className="reel-poster reel-shimmer" />
      <RecCardTextSkeleton evidence={evidence} />
    </div>
  );
}

/** Just the text block's held lines — for anything that stands in a card's slot (the Retry tile). */
export function RecCardTextSkeleton({ evidence = false }: { evidence?: boolean }) {
  return (
    <div className={TEXT_BLOCK} aria-hidden>
      <p className={TITLE_LINE} />
      <p className={META_LINE} />
      <p className={WHY_LINES} />
      {evidence && <div className={EVIDENCE_SLOT} />}
    </div>
  );
}

/**
 * Collapsed recommendation card: poster · meta line · why-this · the score chip.
 *
 * A "stretched link" card (O+2): the title is the link, and its `::after` covers the whole card so
 * a tap anywhere still opens the title. The quick actions are the link's SIBLINGS, stacked above
 * that overlay, not its children — a button inside a link is two nested interactive elements, which
 * a screen reader announces as one and a keyboard cannot reliably act on. So Tab reaches the title,
 * then each action, and Enter or Space does what it says on every one of them.
 */
export default function RecCard({ card, reserveEvidence = false }: {
  card: FeedCard;
  /** An anchored rail: hold the evidence slot on every card, chips or not, so none is shorter. */
  reserveEvidence?: boolean;
}) {
  const location = useLocation();
  const evidence = evidenceChips(card.evidence);
  const providers = cardProviders(card.providers);

  return (
    <div className="group relative w-card shrink-0" data-testid="rec-card">
      <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-has-[a:focus-visible]:ring-2 group-has-[a:focus-visible]:ring-fa-frost/60">
        <PosterImage path={card.posterPath} alt={card.name} />
        {/* The shared stack owns the null/NaN guards, the no-overlap layout and the scrim — the
            feed DTO's non-nullability is a backend promise, not a type this file can rely on. */}
        <ScoreBadges
          predictedRating={card.predictedRating}
          crowdRating={card.crowdRating}
          crowdVotes={card.crowdVotes}
        />
        {providers.length > 0 && (
          <div className="absolute bottom-2 left-2 flex gap-1" data-testid="card-providers">
            {providers.map((p) =>
              logoUrl(p.logoPath) ? (
                <img key={p.name} src={logoUrl(p.logoPath)!} alt={p.name} title={p.name} className="h-5 w-5 rounded-[4px] ring-1 ring-black/30" loading="lazy" />
              ) : null,
            )}
          </div>
        )}
      </div>
      <div className={TEXT_BLOCK}>
        <p className={TITLE_LINE} title={card.name}>
          <Link
            to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
            state={{ backgroundLocation: location }}
            className="after:absolute after:inset-0 after:content-[''] focus:outline-none"
            // Explicit, so plain Tab reaches the card in WebKit too, as it does its actions (R1-42).
            tabIndex={0}
            data-testid="rec-card-link"
          >
            {card.name}
          </Link>
        </p>
        <p className={META_LINE}>
          {[card.year, formatRuntime(card.runtimeMinutes)].filter(Boolean).join(" · ")}
        </p>
        {/* The lens's own first clause when copy has been written for this account and title;
            the templated why-line otherwise. Same two-line clamp either way, so a card that has
            copy and one that does not are the same shape — the budget, the provider and the output
            guards can each withhold it, and the row must not visibly change when they do. */}
        <p className={WHY_LINES} data-testid="card-why">
          {card.lensClause ?? card.whyThis}
        </p>
        {/* Anchored rows only: what this card verifiably shares with the row's anchor — the
            evidence the server admitted it on — so "Because you loved X" is checkable on its face. */}
        {evidence.length > 0 ? (
          <ul className={`flex flex-wrap content-start gap-1 ${EVIDENCE_SLOT}`} data-testid="card-evidence" aria-label="What it shares with the title this row is based on">
            {evidence.map((chip) => (
              <li
                key={chip.text}
                title={chip.description}
                aria-label={chip.description}
                // A fixed 20 px chip (border included), so three lines of them are exactly the slot.
                className="block h-5 max-w-full truncate rounded-full border border-fa-edge bg-fa-glass px-2 fa-caption leading-[1.125rem] text-3"
                data-testid="card-evidence-chip"
              >
                {chip.text}
              </li>
            ))}
          </ul>
        ) : (
          reserveEvidence && <div className={EVIDENCE_SLOT} aria-hidden />
        )}
      </div>
      <CardQuickActions
        titleId={card.titleId}
        mediaType={card.mediaType}
        tmdbId={card.tmdbId}
        name={card.name}
      />
    </div>
  );
}
