import type {
  CrossReferenceBacktest,
  CrossReferenceStatus,
  CrossReferenceVerdictOutcome,
} from "../store/api";

/**
 * What the home band is allowed to say about the cross-reference pass.
 *
 * <p>Every number in here comes from `GET /api/feed/cross-reference`, and there is deliberately no
 * path through this module that produces a figure it was not handed. The band's entire licence to
 * sit on the front page is that it quotes what actually happened — "cross-referenced 1,840 titles
 * against your history" is worth reading precisely because it is a count and not a reassurance.</p>
 *
 * <p><b>The rule-out clause is driven by the server's gating decision and never by a default.</b>
 * On the founder's own holdout the `out` verdict caught 16.2% of the titles he went on to love
 * against 22.8% of the ones he went on to dislike, so nothing in the ranking layer acts on it: the
 * band has to say so in words rather than let "cross-referenced 2,242 titles" imply that 640 of
 * them were pushed out of the way.</p>
 */

/** Grouped the same way the server groups it — invariant, never the browser's locale. */
const count = (value: number) => value.toLocaleString("en-US");

const plural = (value: number, singular: string) => (value === 1 ? singular : `${singular}s`);

/** The gating decisions the server can send. `unmeasured` means no backtest has ever run. */
export const GATING_DEMOTE_ONLY = "demote-only";
export const GATING_GATING = "gating";
export const GATING_UNMEASURED = "unmeasured";

/**
 * The clause about what the rule-outs did — which is, in every branch shipped today, nothing.
 *
 * Three branches rather than two because "nobody has measured this" and "it was measured and did
 * not earn it" are different admissions, and collapsing them would let the band imply a measurement
 * that never happened. The third branch exists for the day a prompt clears the bar: the backtest
 * having earned a demotion is still not the same thing as the ranking layer applying one, and the
 * band must describe the code rather than the permission.
 */
export function ruleOutClause(gating: string): string {
  if (gating === GATING_GATING) {
    // The measurement cleared the bar; the ranking layer still does not act on it (round 2 and the
    // demotion are a later release). Saying "hidden" here would be the band promising a behaviour
    // the feed does not have.
    return "rule-outs have earned their keep but still don't change your picks — see the track record";
  }

  if (gating === GATING_UNMEASURED) {
    return "rule-outs change nothing — nothing has measured them against your history yet";
  }

  return "rule-outs change nothing yet — see the track record";
}

/**
 * The band's sentence, or null when there is nothing honest to say.
 *
 * While a pass is in flight it is a counter, because a pass over the whole eligible pool can
 * legitimately run for ten minutes and "still working" is not information. Afterwards it is what
 * the pass found, with the keep clause dropped entirely when nothing was kept — a band that says
 * "0 kept" reads as the product padding, which is the exact impression the honest-from-day-one
 * posture exists to avoid. Zero rule-outs is likewise not an error and not a boast; the clause is
 * about what rule-outs DO, and it is as true at zero as at six hundred.
 */
export function xrefBandCopy(status: CrossReferenceStatus | null | undefined): string | null {
  if (!status) return null;

  if (status.running) {
    return `Deep pass running · ${count(status.judged)} of ${count(status.total)} cross-referenced`;
  }

  if (status.judged <= 0) return null;

  const parts = [
    `Cross-referenced ${count(status.judged)} ${plural(status.judged, "title")} against your history`,
  ];

  if (status.kept > 0) {
    parts.push(`${count(status.kept)} kept, nudged up a little`);
  }

  parts.push(ruleOutClause(status.gating));

  return parts.join(" · ");
}

/**
 * The coverage footnote: the eligible, scored titles the pass could not reason about at all.
 *
 * Null when there are none, because a footnote reading "0 had no plot summary yet" is noise. It is
 * separate from the sentence above rather than appended to it so the headline count can never be
 * read as covering the whole pool when it does not.
 */
export function xrefCoverageNote(status: CrossReferenceStatus | null | undefined): string | null {
  if (!status || status.noSignal <= 0) return null;

  return `${count(status.noSignal)} ${plural(status.noSignal, "title")} had nothing to reason from yet — no plot summary and no read-through.`;
}

/**
 * The backtest, in words, with the account's own numbers in it.
 *
 * <p>This is the sentence that justifies the whole keep-only design, so it is built entirely out
 * of the run's real figures and drops any clause whose figure is missing. On the founder's own
 * history at prompt v2 it reads:</p>
 *
 * <blockquote>On your own history, titles it kept went on to average 6.83; titles it ruled out
 * averaged 6.16 — so a keep now nudges a title up a little; a rule-out changes nothing yet, because
 * it also caught 16% of the titles you went on to love.</blockquote>
 *
 * <p>The second half is the part that must never be trimmed for length. "A rule-out changes
 * nothing" on its own sounds like caution; naming the 16% is what makes it a reason.</p>
 */
export function xrefBacktestSentence(backtest: CrossReferenceBacktest): string {
  const keep = backtest.meanLaterRatingKeep;
  const out = backtest.meanLaterRatingOut;

  const lead =
    keep != null && out != null
      ? `On your own history, titles it kept went on to average ${keep.toFixed(2)}; titles it ruled out averaged ${out.toFixed(2)}`
      : `On your own history it judged ${count(backtest.judged)} ${plural(backtest.judged, "title")} you had already rated`;

  const consequence =
    keep != null && out != null && keep > out
      ? "so a keep now nudges a title up a little"
      : "so nothing it says moves a pick";

  const why = `a rule-out changes nothing yet, because it also caught ${(backtest.falseRuleOutRate * 100).toFixed(0)}% of the titles you went on to love`;

  return `${lead} — ${consequence}; ${why}.`;
}

/**
 * How to read that run: the interval, the comparison rate, and how much of the pool it could not
 * call. Separate from the sentence above because it is the part a reader skips, and burying the
 * point estimate's uncertainty inside the headline is how a point estimate gets quoted alone.
 */
export function xrefBacktestCaveat(backtest: CrossReferenceBacktest): string {
  const interval = `${(backtest.falseRuleOutLow * 100).toFixed(1)}–${(backtest.falseRuleOutHigh * 100).toFixed(1)}%`;
  return (
    `Measured on ${count(backtest.judged)} of your own ${plural(backtest.judged, "rating")} held out from the model, ` +
    `prompt v${backtest.promptVersion}. Rule-outs caught ${(backtest.falseRuleOutRate * 100).toFixed(1)}% ` +
    `(95% confidence ${interval}) of what you later loved against ${(backtest.trueRuleOutRate * 100).toFixed(1)}% ` +
    `of what you later disliked, and it answered "not sure" on ${(backtest.unsureRate * 100).toFixed(0)}%.`
  );
}

/**
 * The online half's one line per standing — and its refusal, in words, when there is not enough of
 * it to mean anything.
 *
 * <p>The refusal is the point of the function. This measurement can never reach significance: only
 * about 2% of ratings are made in the app, so the join behind it is temporal, and a mean over four
 * outcomes looks exactly like a mean over four hundred on a page. Naming the count and saying "not
 * yet answerable" is the only rendering that cannot be misread.</p>
 */
export function xrefOnlineNote(outcome: CrossReferenceVerdictOutcome): string {
  if (!outcome.answerable) {
    return `not yet answerable — ${count(outcome.n)} ${plural(outcome.n, "outcome")} so far`;
  }

  const mean = outcome.meanLaterRating;
  const loved = outcome.lovedRate;
  const parts = [`${count(outcome.n)} rated since`];
  if (mean != null) parts.push(`averaging ${mean.toFixed(2)}`);
  if (loved != null) parts.push(`${(loved * 100).toFixed(0)}% of them 8 or higher`);

  return parts.join(" · ");
}

/** The standing names, in the second person. Unknown standings render as themselves, never blank. */
export function xrefVerdictLabel(verdict: string): string {
  switch (verdict) {
    case "ConfirmedKeep":
      return "kept twice";
    case "ProvisionalKeep":
      return "kept";
    case "Out":
      return "ruled out";
    case "Unsure":
      return "not sure";
    default:
      return verdict;
  }
}
