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
 * - "…Top-bar search looks titles up by name and only partly applies them, so it can show titles
 *   your conditions exclude" (IR1S-06's fix): true until WS-3's tiered lookup (trust-v1 task 10,
 *   FD-5's default) — both legs now hide what the conditions rule out and count it on a
 *   "N matches hidden by your conditions" line, so "can show titles your conditions exclude" became
 *   the false half. One rule lagged: the global leg let an over-ceiling title through under
 *   "beyond your catalogue" until WS-3's R1-07 fix (the maturity ceiling in both legs), so the
 *   claims register holds this sentence false until that fix is merged.
 *
 * What the lookup still does differently is by design and stable, so it is named: it finds titles
 * you have already seen (it badges them) and it does not apply the "for you" minimum (a name lookup
 * has no prediction to hold to a floor). Everything else is hidden and counted. Ask Reel is "shaped",
 * not "airtight": it keeps titles under the minimum behind a labelled "under your bar" fence rather
 * than dropping them.
 */
export const CONDITIONS_SCOPE_COPY =
  "These shape the hero, every row, Ask Reel and top-bar search. Search still finds titles you've seen and ignores the “for you” minimum; anything else your conditions rule out is counted, not shown.";
