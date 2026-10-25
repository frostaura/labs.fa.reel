/**
 * What the saved conditions actually govern, in one sentence for the conditions dialog.
 *
 * It used to say "Airtight — these apply to the hero, every row and all search", and the search
 * half was false: typeahead applies the genre, keyword, language, crowd and year rules, but not the
 * "for you" minimum, so it listed titles at 4.9–5.7 for an account whose condition was 6.0 with
 * nothing on screen to say why (ireview_surface defect 13, R1-26 / R1-27). The sentence now states
 * the exception instead of the absolute.
 *
 * Exported so Settings' conditions section can say the same thing in the same words — two copies
 * of one claim are how this one drifted.
 */
export const CONDITIONS_SCOPE_COPY =
  "These apply to the hero and every row. Search applies them too, except the “for you” minimum — it can still list titles below it.";
