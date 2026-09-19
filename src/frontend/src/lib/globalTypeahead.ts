/**
 * Whether the typeahead should reach beyond the ingested catalogue for a term.
 *
 * The global TMDB leg is a fallback, not a firehose: it fires only once the LOCAL query has
 * resolved sparse (fewer than 3 title rows) for a term substantial enough to mean something
 * (3+ characters after trimming). A well-matched local term never spends the extra request.
 */
export function shouldQueryGlobal(term: string, localTitleCount: number): boolean {
  return term.trim().length >= 3 && localTitleCount < 3;
}
