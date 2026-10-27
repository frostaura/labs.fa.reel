/**
 * Human names for the model's features, as the title page's breakdown bars show them.
 *
 * Every label is a claim about the viewer, so a label may say only what the feature measures. Kept
 * out of the page so the vocabulary is unit-tested: one stale word here ("your favourite era")
 * survived the explanation it came from being retired.
 */

/**
 * The written label for every FeatureSchema scalar and every synthetic step of the title page's
 * walk. The unit test reads FeatureSchema.Scalars and OnDemandScorer's step names from the C#
 * source, so a scalar added there without a label here fails the build instead of reaching the page
 * as camelCase-in-words (R1-41: knownPeopleFraction read "known people fraction").
 */
export const FEATURE_LABELS: Readonly<Record<string, string>> = {
  // cast & crew
  castAffinity: "cast you rate highly",
  lovedCastCount: "favourite cast members",
  directorAffinity: "director affinity",
  writerAffinity: "writer affinity",
  hasCredits: "cast & crew data",
  // genre / era
  userGenreAffinity: "your genre taste",
  genreDrift: "recent genre streak",
  // The release decade's contribution, named for what it is. It used to read "your favourite
  // era", the claim the program retired from the why-lines (KILL-7): under a "2000 or later"
  // condition it is a tautology, and it presents selection bias as taste (R1-24).
  decadeAffinity: "release era",
  // crowd signals
  traktRating: "crowd rating",
  traktVotesLog: "vote volume",
  tmdbPopularityLog: "popularity",
  contrarianAdjustedRating: "your contrarian lens",
  // title scalars
  releaseAgeLog: "release-year fit",
  runtimeMinutes: "runtime",
  certificationOrdinal: "maturity rating",
  isShow: "series format",
  // LLM tone attributes
  attrDarkness: "tone (darkness)",
  attrPacing: "pacing",
  attrComplexity: "complexity",
  attrEmotionalIntensity: "emotional intensity",
  attrHumor: "humor",
  attrOptimism: "optimism",
  attrEnsembleVsSolo: "ensemble vs. lead",
  hasAttributes: "tone analysis",
  // plot-embedding similarities
  simLovedCentroid: "close in feel to titles you love",
  simRecentCentroid: "matches your recent taste",
  simTopLovedMax: "very close to a favourite",
  simTopLovedMean: "feels like your favourites",
  hasEmbedding: "story similarity",
  // presence flags (FeatureSchema v4) — "we know this about the title", not the value itself
  hasRuntime: "runtime data",
  hasTraktRating: "crowd-rating data",
  hasPopularity: "popularity data",
  hasCertification: "maturity-rating data",
  hasSubgenres: "subgenre data",
  hasAttributesTone: "tone data",
  hasAttributesEra: "setting data",
  // synthetic serving-time entries (not model features)
  dislikedSimilarity: "similar to titles you passed on",
  // the title page's walk (WS-5's PredictionWaterfall): the steps after the model's own features
  otherFeatures: "everything else",
  recentRatingsCorrection: "your recent ratings",
  calibrationToYourScale: "fitted to how you rate",
  // Ranking-time entries: these explain where the title SAT, not what the model predicted.
  tastePriorNoHistoryGenre: "a genre you don't watch",
  tastePriorGenreAffinity: "how you rate this genre",
  tastePriorMedia: "films vs. series",
  // FeatureSchema v5: what the viewer knows about the people (counts, never an affinity), and
  // the dislike signals the trainer reads.
  knownPeopleFraction: "cast & crew you know from your ratings",
  knownDirectorOrWriter: "a director or writer you know from your ratings",
  isUnscripted: "unscripted format",
  simNotInterestedCentroid: "close to titles you turned down",
  dislikedGenreOverlap: "genres you've turned down",
};

/** Humanize a model feature name for the breakdown bars. Covers every FeatureSchema scalar; any
 *  future/unmapped name falls back to a camelCase→words humanizer so no raw token ever shows. */
export function featureLabel(feature: string): string {
  if (feature.startsWith("genre:")) return feature.slice(6).replace(/-/g, " ");
  if (feature.startsWith("subgenreBucket:")) return "theme match";
  // FeatureSchema v4 one-hot blocks — the vocabulary value is already the human word.
  if (feature.startsWith("tone:")) return `${feature.slice(5).replace(/-/g, " ")} tone`;
  if (feature.startsWith("era:")) return `${feature.slice(4).replace(/-/g, " ")} setting`;
  const label = FEATURE_LABELS[feature];
  if (label) return label;
  // camelCase → spaced words, lower-cased (e.g. "simTopLovedMax" → "sim top loved max").
  return feature.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}
