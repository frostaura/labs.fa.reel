import { Star } from "lucide-react";
import PredictedRatingBadge from "./PredictedRatingBadge";
import CrowdRatingBadge from "./CrowdRatingBadge";
import { scoreBadgesPlan, type ScoreBadgesInput } from "../../lib/scoreBadges";

interface Props extends ScoreBadgesInput {
  crowdVotes?: number | null;
}

/**
 * The poster-corner score stack — predicted ("8.4 for you") over crowd ("★ 7.9 crowd 40k") — as
 * ONE cohesive unit, shared by every card surface (feed, Ask Reel, Person filmography, Saved).
 *
 * It exists because four surfaces hand-rolled the same two absolutely-positioned pills and one of
 * them let the pair overlap on narrow cards. The invariants live here instead:
 *   - a single column with a fixed gap, so the pills can never land on top of each other;
 *   - `whitespace-nowrap` on both pills, with the crowd pill (the only one whose width is
 *     unbounded — the vote count rides in it) clamped to the card and ellipsised;
 *   - a top gradient scrim behind them, so both stay legible over light poster art;
 *   - which pills render is `scoreBadgesPlan` (pure, unit-tested), including the Person-page
 *     variant where an explicit user rating replaces the prediction.
 *
 * Renders inside a `relative` poster box; renders nothing when there is nothing to say.
 */
export default function ScoreBadges({ predictedRating, crowdRating, crowdVotes, userRating }: Props) {
  const plan = scoreBadgesPlan({ predictedRating, crowdRating, userRating });
  if (plan.empty) return null;

  return (
    <div className="pointer-events-none absolute inset-x-0 top-0" data-testid="score-badges">
      {/* Legibility scrim: the pills' backdrop-blur alone loses to near-white artwork. */}
      <div aria-hidden className="absolute inset-x-0 top-0 h-16 rounded-t-lg bg-gradient-to-b from-fa-ink/60 via-fa-ink/25 to-transparent" />
      <div className="absolute top-2 right-2 flex max-w-[calc(100%-1rem)] flex-col items-end gap-1">
        {plan.showUserRating && (
          <span
            className="whitespace-nowrap rounded-full bg-fa-ink/80 text-fa-frost-bright fa-caption px-1.5 py-0.5 flex items-center gap-0.5"
            title="Your rating"
          >
            <Star className="h-2.5 w-2.5 fill-current" aria-hidden /> {userRating}
          </span>
        )}
        {plan.showPredicted && (
          <span className="whitespace-nowrap">
            <PredictedRatingBadge rating={predictedRating!} />
          </span>
        )}
        {plan.showCrowd && (
          <span className="block max-w-full overflow-hidden text-ellipsis whitespace-nowrap">
            <CrowdRatingBadge rating={crowdRating!} votes={crowdVotes} />
          </span>
        )}
      </div>
    </div>
  );
}
