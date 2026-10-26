import type { ModelLearned } from "../store/api";

/** localStorage key for the dismissal, scoped to the retrain it belongs to. */
export const LEARNED_DISMISS_PREFIX = "reel.learned.dismissed:";

/**
 * The band's sentence: what the retrain learned from, and how much of tonight's page moved as a
 * result.
 *
 * It no longer says whether the model "runs closer on your scores": the server stopped sending that
 * number (WS-5), because 0 of 101 evaluations beat popularity and a retrain-to-retrain MAE delta on
 * that footing is noise dressed as progress. Every clause is dropped when its number is absent — a
 * band that says "0 picks changed" reads as the product padding.
 */
export function learnedCopy(learned: ModelLearned): string {
  const sentence = `Learned from ${learned.newRatingsSinceLast.toLocaleString("en-US")} new ${
    learned.newRatingsSinceLast === 1 ? "rating" : "ratings"
  }.`;
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
