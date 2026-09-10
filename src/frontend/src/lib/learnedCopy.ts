import type { ModelLearned } from "../store/api";

/** localStorage key for the dismissal, scoped to the retrain it belongs to. */
export const LEARNED_DISMISS_PREFIX = "reel.learned.dismissed:";

/**
 * The band's sentence: what the retrain learned from, whether it made the model closer, and how
 * much of tonight's page moved as a result.
 *
 * Every clause is dropped when its number is absent or does not support the claim. A band that
 * says "runs 0.0 closer" or "0 picks changed" is worse than a shorter one — it reads as the product
 * padding, which is exactly the impression the honest-from-day-one posture exists to avoid.
 */
export function learnedCopy(learned: ModelLearned): string {
  const parts: string[] = [
    `Learned from ${learned.newRatingsSinceLast.toLocaleString("en-US")} new ${
      learned.newRatingsSinceLast === 1 ? "rating" : "ratings"
    }`,
  ];

  // Only claimed when it IMPROVED. A retrain that made calibration worse is real and must not be
  // dressed up; the track-record page is where the full picture belongs.
  const improvement = learned.maeImprovement;
  if (improvement != null && improvement >= 0.05) {
    parts.push(`runs ${improvement.toFixed(1)} closer on your scores now`);
  }

  const sentence = parts.join(" — ") + ".";
  return learned.picksChanged > 0
    ? `${sentence} ${learned.picksChanged.toLocaleString("en-US")} ${
        learned.picksChanged === 1 ? "pick" : "picks"
      } changed.`
    : sentence;
}

/**
 * Was this exact retrain's band already dismissed?
 *
 * Keyed on `trainedAt`, not on a single flag: the band is news about ONE retrain, so dismissing it
 * must not suppress the next one. Every access is guarded — a private window, cleared site data or
 * a browser blocking storage throws on the accessor itself, and the honest fallback is to show the
 * band rather than to hide it behind an exception.
 */
export function isLearnedDismissed(trainedAt: string): boolean {
  try {
    return localStorage.getItem(LEARNED_DISMISS_PREFIX + trainedAt) === "1";
  } catch {
    return false;
  }
}

/** Records the dismissal. Silent on failure: a band that reappears beats a crash. */
export function dismissLearned(trainedAt: string): void {
  try {
    localStorage.setItem(LEARNED_DISMISS_PREFIX + trainedAt, "1");
  } catch {
    // storage unavailable — the band comes back next reload, which is the harmless direction
  }
}
