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

/**
 * The empty page's source line while the account has no model: nothing has been scored, so the
 * usual "rate titles it has already put a number on" points at numbers that do not exist yet.
 */
export const NO_MODEL_SOURCE_NOTE =
  "Reel puts a number on titles once it has a model; every one it scores that you then rate lands here.";

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
 * The retrain summary. It makes no "closer on your scores" claim: WS-5 retired the MAE-improvement
 * number (R1-56) because it compared a retrain against a different holdout, and the server no
 * longer sends it — so the old clause read "It did not get closer" after EVERY retrain, a claim
 * nothing measured. The retrain's own calibration figures sit under the sentence instead.
 */
export function learnedSentence(learned: NonNullable<AccuracyPayload["learnedThisWeek"]>): string {
  const parts = [`Learned from ${learned.newRatings.toLocaleString("en-US")} new ${learned.newRatings === 1 ? "rating" : "ratings"}.`];
  parts.push(
    learned.picksChanged > 0
      ? `${learned.picksChanged.toLocaleString("en-US")} ${learned.picksChanged === 1 ? "pick" : "picks"} changed.`
      : "No picks changed."
  );
  return parts.join(" ");
}

/** The window switch's choices, narrowest first. */
export const WINDOW_LABELS: Record<string, string> = {
  "90d": "90 days",
  "365d": "a year",
  all: "all time",
};

const KNOWN_WINDOWS = Object.keys(WINDOW_LABELS);

/**
 * The windows worth offering (G-9): only those holding a different set of scored calls from every
 * narrower one. On live all three tabs drew the same chart, because everything scored fell inside
 * 90 days. A server from before WS-5 task 11 sends no summary, so every window is offered as before.
 */
export function offeredWindows(windows: AccuracyPayload["windows"]): string[] {
  if (!windows || windows.length === 0) return KNOWN_WINDOWS;
  const offered = windows.filter((w) => w.sameAs == null && w.key in WINDOW_LABELS).map((w) => w.key);
  return offered.length > 0 ? offered : KNOWN_WINDOWS.slice(0, 1);
}

/**
 * Said in place of a switch when there is only one window worth showing — so the missing buttons
 * read as a fact about the data, not a feature that went away.
 */
export function singleWindowNote(windows: AccuracyPayload["windows"]): string | null {
  if (!windows || windows.length === 0 || offeredWindows(windows).length > 1) return null;
  const only = windows.find((w) => w.sameAs == null) ?? windows[0];
  if (only.pairs === 0) return null;
  const calls = `${only.pairs.toLocaleString("en-US")} scored ${only.pairs === 1 ? "call" : "calls"}`;
  return only.key === "all"
    ? `All ${calls}.`
    : `All ${calls} fall in the last ${WINDOW_LABELS[only.key]}, so a longer window would show the same.`;
}
