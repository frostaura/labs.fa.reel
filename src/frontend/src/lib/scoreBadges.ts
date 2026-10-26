import { hasPredictedRating } from "./predictedRating";

/**
 * The one decision every poster-corner badge stack makes, stated once: which pills render, and in
 * what precedence. Extracted pure so the rule is unit-testable without a DOM — the component
 * (`components/rec/ScoreBadges.tsx`) is a straight projection of this result.
 *
 * Precedence: an explicit user rating (the Person filmography variant) replaces the prediction —
 * "you rated this 8" and "8.2 for you" are answers to the same question, and showing both reads
 * as the model grading the user. The crowd pill is independent: it is the external reference the
 * other pill is measured against, whichever one it is.
 */
export interface ScoreBadgesInput {
  predictedRating?: number | null;
  crowdRating?: number | null;
  /** Explicit user rating; when present it replaces the predicted pill. */
  userRating?: number | null;
}

export interface ScoreBadgesPlan {
  /** True when nothing would render — the caller skips the wrapper (and its scrim) entirely. */
  empty: boolean;
  showUserRating: boolean;
  showPredicted: boolean;
  showCrowd: boolean;
}

export function scoreBadgesPlan({ predictedRating, crowdRating, userRating }: ScoreBadgesInput): ScoreBadgesPlan {
  const showUserRating = userRating != null && Number.isFinite(userRating);
  const showPredicted = !showUserRating && hasPredictedRating(predictedRating);
  const showCrowd = hasPredictedRating(crowdRating);
  return {
    empty: !showUserRating && !showPredicted && !showCrowd,
    showUserRating,
    showPredicted,
    showCrowd,
  };
}

/**
 * What the one score chip says to a screen reader and in its tooltip — the evidence label the
 * banded number carries (FD-4). The visible chip is terse ("8.4 ±1.1 for you" over "★ 7.9"); this
 * spells out what each number is and where it comes from, so a reader never has to guess which one
 * is the model's.
 */
export function scoreChipDescription({
  predictedRating,
  crowdRating,
  userRating,
  crowdVotes,
  band,
}: ScoreBadgesInput & { crowdVotes?: number | null; band?: number | null }): string {
  const plan = scoreBadgesPlan({ predictedRating, crowdRating, userRating });
  const parts: string[] = [];
  if (plan.showUserRating) parts.push(`You rated it ${userRating}`);
  if (plan.showPredicted) {
    const usable = band != null && Number.isFinite(band) && band > 0 ? band : null;
    parts.push(
      usable === null
        ? `Predicted ${predictedRating!.toFixed(1)} for you from your ratings`
        : `Predicted ${predictedRating!.toFixed(1)} for you, give or take ${usable.toFixed(1)} — what predictions for you have typically been off by`
    );
  }
  if (plan.showCrowd) {
    const votes = crowdVotes != null && Number.isFinite(crowdVotes) && crowdVotes > 0 ? crowdVotes : null;
    parts.push(
      votes === null
        ? `Crowd score ${crowdRating!.toFixed(1)}`
        : `Crowd score ${crowdRating!.toFixed(1)} from ${votes.toLocaleString("en-US")} votes`
    );
  }
  return parts.join(". ");
}
