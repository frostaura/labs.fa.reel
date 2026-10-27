/**
 * The line under the title page's rating picker: what a rating also does on Trakt.
 *
 * It follows ReactionEndpoints exactly (R1-40). Trakt history write-back is movie-only, and only for
 * a movie Reel has no play for yet: that one play is anchored on `ReelLoggedWatchedAt`, so removing
 * the rating removes it. A movie already in the history (plays from ingest, or the play Reel logged
 * the first time it was rated) gets the rating alone, so promising a play there was false.
 */
export function ratingPopoverNote(mediaType: string, plays: number): string {
  if (mediaType !== "Movie") return "Ratings sync to Trakt — your episode history is never touched.";
  if (plays > 0) return "Ratings sync to Trakt — this movie is already in your history, so no play is added.";
  return "Also logs a play on your Trakt history — cleared if you remove the rating.";
}
