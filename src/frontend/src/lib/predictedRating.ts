/**
 * The predicted-rating badge formats with `toFixed(1)`, which throws on null/undefined and happily
 * prints "NaN for you" for a non-finite score. Every surface that reads a nullable DTO already
 * guards with `!= null`; the feed card DTO is non-nullable *today*, so the feed surfaces did not —
 * a guarantee one backend change away from a runtime crash on the app's most-visited screen.
 *
 * This is that guard, shared so the rule is stated once and covers NaN as well as null.
 */
export function hasPredictedRating(rating: number | null | undefined): rating is number {
  return rating != null && Number.isFinite(rating);
}
