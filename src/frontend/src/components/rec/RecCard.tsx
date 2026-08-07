import { Link, useLocation } from "react-router";
import type { FeedCard } from "../../store/feedTypes";
import PosterImage from "./PosterImage";
import PredictedRatingBadge from "./PredictedRatingBadge";
import CrowdRatingBadge from "./CrowdRatingBadge";
import { logoUrl } from "../../lib/tmdbImages";
import { formatRuntime } from "../../lib/format";
import { hasPredictedRating } from "../../lib/predictedRating";

/** Collapsed recommendation card: poster · meta line · why-this · predicted rating. */
export default function RecCard({ card }: { card: FeedCard }) {
  const location = useLocation();

  return (
    <Link
      to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
      state={{ backgroundLocation: location }}
      className="group block w-44 sm:w-48 shrink-0 focus:outline-none"
      data-testid="rec-card"
    >
      <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-focus-visible:ring-2 group-focus-visible:ring-fa-frost/60">
        <PosterImage path={card.posterPath} alt={card.name} />
        {/* Guarded like every other consumer: the badges format with toFixed, and the feed DTO's
            non-nullability is a backend promise rather than a type this file can rely on. The same
            guard covers `crowdRating`, which is genuinely nullable — plenty of titles have no
            community score, and the rule there is show nothing rather than a placeholder. Stacked
            rather than side-by-side because the card is 176px wide on mobile and two pills in a row
            would wrap over the poster art. */}
        {(hasPredictedRating(card.predictedRating) || hasPredictedRating(card.crowdRating)) && (
          <div className="absolute top-2 right-2 flex flex-col items-end gap-1">
            {hasPredictedRating(card.predictedRating) && <PredictedRatingBadge rating={card.predictedRating} />}
            {hasPredictedRating(card.crowdRating) && (
              <CrowdRatingBadge rating={card.crowdRating} votes={card.crowdVotes} />
            )}
          </div>
        )}
        {(card.providers?.length ?? 0) > 0 && (
          <div className="absolute bottom-2 left-2 flex gap-1" data-testid="card-providers">
            {card.providers!.slice(0, 3).map((p) =>
              logoUrl(p.logoPath) ? (
                <img key={p.name} src={logoUrl(p.logoPath)!} alt={p.name} title={p.name} className="h-5 w-5 rounded-[4px] ring-1 ring-black/30" loading="lazy" />
              ) : null,
            )}
          </div>
        )}
      </div>
      <div className="mt-2 space-y-1">
        <p className="fa-body font-medium text-fa-frost-bright truncate" title={card.name}>
          {card.name}
        </p>
        <p className="fa-caption text-fa-frost-dim">
          {[card.year, formatRuntime(card.runtimeMinutes)].filter(Boolean).join(" · ")}
        </p>
        <p className="fa-caption text-fa-frost/80 line-clamp-2 leading-snug">{card.whyThis}</p>
      </div>
    </Link>
  );
}
