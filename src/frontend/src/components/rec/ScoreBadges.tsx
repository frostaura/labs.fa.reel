import { Star } from "lucide-react";
import { formatVotes } from "../../lib/format";
import { bandText, useForYouBand } from "../../lib/forYouBand";
import { scoreBadgesPlan, scoreChipDescription, type ScoreBadgesInput } from "../../lib/scoreBadges";

interface ChipProps extends ScoreBadgesInput {
  crowdVotes?: number | null;
  /**
   * The measured band behind the prediction. `undefined` reads the account's band from the cache
   * (`useForYouBand`); `null` says the caller knows there is none. Never fetched by this component.
   */
  band?: number | null;
  /** Poster corner ("sm") or hero scale ("lg"). */
  size?: "sm" | "lg";
}

/**
 * ONE chip for the scores (FD-4): the banded "for you" number over the crowd score, in a single
 * bordered unit. Two separate pills on the art read as two badges competing for the corner, and a
 * bare "8.4 for you" claimed a precision the model has never had — the band ("±1.1", the account's
 * own measured error) is printed beside the number it qualifies, not on another screen.
 *
 * The two numbers still differ on three axes, not one, so they are never confusable at a glance:
 * colour (frost for everything personal, amber for the crowd), mark (a filled star on the crowd
 * line only) and wording ("for you"). The vote count rides in the description, and visibly at hero
 * scale, because a 9.1 from 12 votes is not a 9.1 from 40,000.
 *
 * An explicit user rating (the Person filmography variant) replaces the prediction — "you rated
 * this 8" and "8.2 for you" answer the same question, and showing both reads as the model grading
 * the user. Which lines render is `scoreBadgesPlan` (pure, unit-tested).
 */
export function ScoreChip({ predictedRating, crowdRating, crowdVotes, userRating, band, size = "sm" }: ChipProps) {
  const cached = useForYouBand();
  const resolvedBand = band === undefined ? cached : band;
  const plan = scoreBadgesPlan({ predictedRating, crowdRating, userRating });
  if (plan.empty) return null;

  const large = size === "lg";
  const bandLabel = plan.showPredicted ? bandText(resolvedBand) : null;
  const votes = large ? formatVotes(crowdVotes) : null;
  const description = scoreChipDescription({ predictedRating, crowdRating, userRating, crowdVotes, band: resolvedBand });

  return (
    <span
      className={`inline-flex flex-col items-end rounded-lg border border-fa-frost/30 bg-fa-ink/80 backdrop-blur-sm font-medium tabular-nums leading-tight ${
        large ? "gap-0.5 px-3 py-1.5 text-base" : "gap-px px-2 py-1 text-xs"
      }`}
      title={description}
      aria-label={description}
      role="img"
      data-testid="score-chip"
    >
      {plan.showUserRating && (
        <span className="flex items-center gap-1 whitespace-nowrap text-fa-frost-bright" data-testid="user-rating">
          {userRating}
          <span className="fa-overline font-normal text-fa-frost-dim">yours</span>
        </span>
      )}
      {plan.showPredicted && (
        <span className="flex items-baseline gap-1 whitespace-nowrap text-fa-frost-bright" data-testid="predicted-rating">
          {predictedRating!.toFixed(1)}
          {bandLabel && (
            <span className="font-normal text-fa-frost" data-testid="predicted-band">
              {bandLabel}
            </span>
          )}
          <span className="fa-overline font-normal text-fa-frost-dim">for you</span>
        </span>
      )}
      {plan.showCrowd && (
        <span
          className="flex max-w-full items-center gap-1 overflow-hidden whitespace-nowrap text-fa-warning"
          data-testid="crowd-rating"
        >
          <Star className={large ? "h-3.5 w-3.5 shrink-0 fill-current" : "h-2.5 w-2.5 shrink-0 fill-current"} aria-hidden />
          {crowdRating!.toFixed(1)}
          <span className="fa-overline truncate font-normal text-fa-warning/60">crowd{votes ? ` ${votes}` : ""}</span>
        </span>
      )}
    </span>
  );
}

/**
 * The poster-corner placement of {@link ScoreChip}, shared by every card surface (feed, Ask Reel,
 * Person filmography, Saved): top-right, clamped to the card, over a top gradient scrim so the chip
 * stays legible on near-white artwork. Renders inside a `relative` poster box; renders nothing when
 * there is nothing to say.
 */
export default function ScoreBadges(props: Omit<ChipProps, "size">) {
  const plan = scoreBadgesPlan(props);
  if (plan.empty) return null;

  return (
    <div className="pointer-events-none absolute inset-x-0 top-0" data-testid="score-badges">
      {/* Legibility scrim: the chip's backdrop-blur alone loses to near-white artwork. */}
      <div aria-hidden className="absolute inset-x-0 top-0 h-16 rounded-t-lg bg-gradient-to-b from-fa-ink/60 via-fa-ink/25 to-transparent" />
      <div className="absolute top-2 right-2 flex max-w-[calc(100%-1rem)] justify-end">
        <ScoreChip {...props} />
      </div>
    </div>
  );
}
