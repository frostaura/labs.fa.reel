import type { FiltersPayload, FilterCostReport, FilterCostSuggestion } from "../store/api";
import { ALL_LANGUAGES } from "./pickConditions";

/**
 * The conditions editor's cost meter — turning the server's counts into the sentences and badges
 * the editor renders, and turning a suggestion back into a draft.
 *
 * Kept pure and separate from the component for the usual reason: this is the half worth unit
 * testing, and the editor mounts twice (Settings and the dialog on the feed), so a formatting rule
 * living inside one of them would be a rule the other did not have.
 *
 * VOICE. Second person, neutral, and never nudging. The plan's founder decision is explicit — the
 * app shows the cost of a rule, the founder decides what to do about it, and nothing here changes
 * a rule on its own. So the copy is "these rules leave 421 of 5,319 titles", never "your filters
 * are too tight"; a suggestion is "Allow comedy", never "You should allow comedy".
 */

/** Grouped digits, fixed to en-US so a test asserts one string and not the runner's locale. */
export function formatCount(value: number): string {
  return Math.max(0, Math.round(value)).toLocaleString("en-US");
}

/**
 * The headline. Deliberately says "leave", not "exclude": the drop from the catalogue total to the
 * eligible count is partly the rules and partly titles already watched, rated or dismissed, and
 * claiming the rules did all of it would be a bigger lie than the vagueness. The per-rule marginals
 * below it are where the honest attribution lives.
 */
export function costLine(report: FilterCostReport | undefined): string | null {
  if (!report) return null;
  return `These rules leave ${formatCount(report.eligible)} of ${formatCount(report.total)} titles`;
}

/** A marginal as a badge: "+180". Null when relaxing the rule returns nothing worth a badge. */
export function formatMarginal(marginal: number | null | undefined): string | null {
  return marginal != null && marginal > 0 ? `+${formatCount(marginal)}` : null;
}

/** The one-line note under a non-chip control ("+640 titles if you allow every language"). */
export function marginalNote(marginal: number | null | undefined, relaxation: string): string | null {
  if (marginal == null || marginal <= 0) return null;
  return `${formatCount(marginal)} more ${marginal === 1 ? "title" : "titles"} if you ${relaxation}`;
}

/** Looks up one rule's marginal. Absent (still loading, or the rule is off) reads as null. */
export function marginalFor(
  report: FilterCostReport | undefined,
  kind: string,
  value: string,
): number | null {
  const hit = report?.byRule.find((r) => r.kind === kind && r.value === value);
  return hit ? hit.marginal : null;
}

/**
 * The value the server reports the language rule under: its EFFECTIVE allow-list, joined. An empty
 * selection is the English-only default on both sides, so it reports as "en" rather than "".
 */
export function languageRuleValue(languages: string[]): string {
  return (languages.length > 0 ? languages : ["en"]).join(",");
}

/** The value the server reports a rating floor under — one decimal, matching ContentFilterRules. */
export function ratingRuleValue(value: number): string {
  return value.toFixed(1);
}

/**
 * What tapping a suggestion would do, said plainly and in the second person. These mirror
 * `FilterRuleSet.Without` on the server one-for-one — if a label promises something the counterfactual
 * did not measure, the number beside it is describing a different action to the one the button takes.
 */
export function suggestionLabel(suggestion: FilterCostSuggestion): string {
  switch (suggestion.kind) {
    case "ExcludeGenre":
      return `Allow ${suggestion.value.replace("-", " ")}`;
    case "IncludeGenre":
      return `Stop requiring ${suggestion.value.replace("-", " ")}`;
    case "ExcludeKeyword":
      return `Allow “${suggestion.value}”`;
    case "Language":
      return "Allow every language";
    case "MinCrowdRating":
      return `Drop the ${suggestion.value} crowd floor`;
    case "MinYear":
      return `Allow titles before ${suggestion.value}`;
    case "MaxYear":
      return `Allow titles after ${suggestion.value}`;
    default:
      return "Relax this rule";
  }
}

/**
 * Applies a suggestion to the DRAFT — never to the saved conditions. Nothing is persisted until the
 * user saves, which is the founder decision this whole feature exists to serve: the meter shows the
 * trade, the founder makes it.
 *
 * The language case relaxes to the all-languages sentinel rather than to an empty list, because an
 * empty list means English-only again — the same call `FilterRuleSet.Without` makes, so the count on
 * the chip is the count the tap delivers.
 */
export function applySuggestion(draft: FiltersPayload, suggestion: FilterCostSuggestion): FiltersPayload {
  switch (suggestion.kind) {
    case "ExcludeGenre":
      return { ...draft, excludeGenres: draft.excludeGenres.filter((g) => g !== suggestion.value) };
    case "IncludeGenre":
      return { ...draft, includeGenres: draft.includeGenres.filter((g) => g !== suggestion.value) };
    case "ExcludeKeyword":
      return { ...draft, excludeKeywords: draft.excludeKeywords.filter((k) => k !== suggestion.value) };
    case "Language":
      return { ...draft, languages: [ALL_LANGUAGES] };
    case "MinCrowdRating":
      return { ...draft, minCrowdRating: null };
    case "MinYear":
      return { ...draft, minYear: null };
    case "MaxYear":
      return { ...draft, maxYear: null };
    default:
      return draft;
  }
}
