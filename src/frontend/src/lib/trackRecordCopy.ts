import type { AccuracyPayload } from "../store/api";

/**
 * The words on the track record. Pure, so they can be tested without a DOM — the repo has no
 * component-test harness (no @testing-library), so the sentences that carry the honesty live here
 * and the page keeps only layout. Page-level behaviour is covered by Playwright.
 */

/** A percentage, or an em-dash. Never a fabricated zero for "we do not know". */
export function pct(value: number | null | undefined, digits = 0): string {
  return value == null ? "—" : `${(value * 100).toFixed(digits)}%`;
}

/** A plain number, or an em-dash. */
export function num(value: number | null | undefined, digits = 2): string {
  return value == null ? "—" : value.toFixed(digits);
}

/** Signed, so an over-promise reads as "+1.03" and never hides behind an absolute value. */
export function signed(value: number | null | undefined, digits = 2): string {
  return value == null ? "—" : `${value > 0 ? "+" : ""}${value.toFixed(digits)}`;
}

/**
 * The headline sentence, in second person and with no softening. On the founder's live data this
 * reads "Within one point on 26% of what you rate" — which is the number, and the number is the
 * point. If it ever reads better, that will be because the model got better.
 */
export function headlineSentence(payload: AccuracyPayload): string {
  if (payload.headline.n === 0) return "Nothing to score yet.";
  return `Within one point on ${pct(payload.headline.withinOnePoint)} of what you rate`;
}

/**
 * Where the numbers came from, said out loud. The exposure log proves a card was on screen; the
 * retro join only proves a score existed before the rating. Presenting the weaker evidence as the
 * stronger is precisely the dishonesty this page exists against, so the distinction is copy, not a
 * footnote.
 */
export function sourceNote(payload: AccuracyPayload): string {
  switch (payload.source) {
    case "exposures":
      return `From ${payload.headline.n} cards Reel actually put in front of you and you later rated.`;
    case "retro":
      return `From ${payload.headline.n} titles Reel had already scored before you rated them. Reel can prove it had committed to a number — not that you saw it. The exposure log takes over as it fills up.`;
    case "none":
      return "Reel has not scored anything you have since rated. Rate a few titles it has already put a number on and this fills in.";
  }
}

/** Which way the model is wrong, in words rather than a sign. */
export function biasNote(bias: number): string {
  return bias > 0 ? "Reel promises more than you give" : "Reel sells titles short";
}

/**
 * What the correlation means, plainly. Near zero is the honest reading of the founder's live data
 * and it must not be dressed up as "still learning" — the order the model puts titles in currently
 * bears no relationship to the order he does.
 */
export function agreementNote(correlation: number | null): string {
  if (correlation == null) return "not measurable yet";
  return Math.abs(correlation) < 0.1 ? "no relationship worth the name" : "does the order match yours?";
}

/**
 * The retrain summary. The improvement clause is dropped whenever the retrain did NOT make the
 * model closer — a band claiming progress after a regression is the fastest way to make the whole
 * page untrustworthy.
 */
export function learnedSentence(learned: NonNullable<AccuracyPayload["learnedThisWeek"]>): string {
  const parts = [`Learned from ${learned.newRatings} new ratings.`];
  parts.push(
    learned.maeImprovement != null && learned.maeImprovement > 0.05
      ? `Runs ${learned.maeImprovement.toFixed(2)} closer on your scores now.`
      : "It did not get closer on your scores."
  );
  parts.push(learned.picksChanged > 0 ? `${learned.picksChanged} picks changed.` : "No picks changed.");
  return parts.join(" ");
}
