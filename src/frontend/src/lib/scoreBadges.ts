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
