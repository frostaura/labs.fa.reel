import type { AskKnockout } from "../askReelStream";

/**
 * Where in the Conditions editor a none-found relaxation chip lands (WS-2 task 3: "relaxation
 * chips open the Conditions editor on that rule", FD-2c's default).
 *
 * The chip names one standing rule — "Without English only: +4" — and the useful thing it can do
 * is put the reader on the control that rule lives in, not on the top of a long form. The kind is
 * the server's `FilterKind`, written as its name; an unknown one lands on the editor itself, which
 * is still the right dialog, just not the right row.
 *
 * The selectors are the editor's own test ids. That coupling is deliberate and temporary: the
 * editor belongs to another lane and has no "focus this rule" prop yet (requested), and a selector
 * that misses degrades to the editor's top rather than to nothing.
 */
export function ruleTarget(knockout: Pick<AskKnockout, "kind" | "value">): string {
  const value = knockout.value.trim().toLowerCase();
  switch (knockout.kind) {
    case "ExcludeGenre":
    case "IncludeGenre":
      return value ? `[data-testid="genre-${cssEscape(value)}"]` : EDITOR;
    case "MinPredictedRating":
      return '[data-testid="min-rating-slider"]';
    case "MinCrowdRating":
      return '[data-testid="crowd-rating-slider"]';
    case "MinYear":
      return '[data-testid="min-year"]';
    case "MaxYear":
      return '[data-testid="max-year"]';
    case "Language":
      return '[data-testid="lang-all"]';
    case "MaxMaturity":
      return 'select[aria-label="Maturity ceiling"]';
    default:
      return EDITOR;
  }
}

/** The editor's root — the landing place for a rule with no row of its own. */
export const EDITOR = '[data-testid="conditions-editor"]';

/** A genre slug inside an attribute selector: quotes and backslashes are the only hazards there. */
function cssEscape(v: string): string {
  return v.replace(/["\\]/g, "\\$&");
}

/** The chip's words: what turning the rule off would unlock, in the reader's terms. */
export function relaxationLabel(knockout: Pick<AskKnockout, "label" | "wouldUnlock">): string {
  return `Without “${knockout.label}”: +${knockout.wouldUnlock}`;
}
