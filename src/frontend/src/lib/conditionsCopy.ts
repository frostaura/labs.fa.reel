/**
 * What the saved conditions actually govern — ONE sentence, rendered by both the Home conditions
 * dialog and Settings' conditions section.
 *
 * History, because each version was wrong in a way the next agent will be tempted to repeat:
 * - "Airtight — these apply to the hero, every row and all search" (R1-27): typeahead skips the
 *   "for you" minimum, so "all search" was false.
 * - "Search applies them too, except the “for you” minimum" (IR1S-06): still false. The top-bar
 *   typeahead's local leg drops excluded genres, keywords, languages, crowd and year rules but not
 *   the minimum, and its global leg (`/api/search/typeahead?global=1`, TypeaheadEndpoints'
 *   GlobalLookupAsync) is a TMDB lookup that applies NONE of them by design — "the notebook" returns
 *   The Notebook for an account excluding romance, "heat" returns the 1995 Heat past a 2000 rule,
 *   "amelie" returns Amélie for an English-only account. Settings meanwhile said "only partly
 *   applies them", so the founder read two contradictory scope sentences.
 *
 * So the sentence does not enumerate which rules the lookup keeps — that list is exactly what
 * drifted twice — it says the lookup can show what the conditions exclude, which is true of every
 * leg. Ask Reel is "shaped", not "airtight": it keeps titles under the minimum behind a labelled
 * "under your bar" fence rather than dropping them.
 */
export const CONDITIONS_SCOPE_COPY =
  "These shape the hero, every row and Ask Reel. Top-bar search looks titles up by name and only partly applies them, so it can show titles your conditions exclude.";
