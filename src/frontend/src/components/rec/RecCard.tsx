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
 * Collapsed recommendation card: poster · meta line · why-this · the score chip.
 *
 * A "stretched link" card (O+2): the title is the link, and its `::after` covers the whole card so
 * a tap anywhere still opens the title. The quick actions are the link's SIBLINGS, stacked above
 * that overlay, not its children — a button inside a link is two nested interactive elements, which
 * a screen reader announces as one and a keyboard cannot reliably act on. So Tab reaches the title,
 * then each action, and Enter or Space does what it says on every one of them.
 */
export default function RecCard({ card }: { card: FeedCard }) {
  const location = useLocation();
  const evidence = evidenceChips(card.evidence);
  const providers = cardProviders(card.providers);

  return (
    <div className="group relative w-44 sm:w-48 shrink-0" data-testid="rec-card">
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
      <div className="mt-2 space-y-1">
        <p className="fa-body font-medium text-fa-frost-bright truncate" title={card.name}>
          <Link
            to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
            state={{ backgroundLocation: location }}
            className="after:absolute after:inset-0 after:content-[''] focus:outline-none"
            data-testid="rec-card-link"
          >
            {card.name}
          </Link>
        </p>
        <p className="fa-caption text-3">
          {[card.year, formatRuntime(card.runtimeMinutes)].filter(Boolean).join(" · ")}
        </p>
        {/* The lens's own first clause when copy has been written for this account and title;
            the templated why-line otherwise. Same two-line clamp either way, so a card that has
            copy and one that does not are the same shape — the budget, the provider and the output
            guards can each withhold it, and the row must not visibly change when they do. */}
        <p className="fa-caption text-fa-frost/80 line-clamp-2 leading-snug" data-testid="card-why">
          {card.lensClause ?? card.whyThis}
        </p>
        {/* Anchored rows only: what this card verifiably shares with the row's anchor — the
            evidence the server admitted it on — so "Because you loved X" is checkable on its face. */}
        {evidence.length > 0 && (
          <ul className="flex flex-wrap gap-1 pt-0.5" data-testid="card-evidence" aria-label="What it shares with the title this row is based on">
            {evidence.map((chip) => (
              <li
                key={chip.text}
                title={chip.description}
                aria-label={chip.description}
                className="max-w-full truncate rounded-full border border-fa-edge bg-fa-glass px-2 py-0.5 fa-caption leading-tight text-3"
                data-testid="card-evidence-chip"
              >
                {chip.text}
              </li>
            ))}
          </ul>
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
