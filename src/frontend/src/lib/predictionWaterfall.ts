import { featureLabel } from "./featureLabel";

/**
 * The prediction as a walk from a baseline (F-25): "your average 6.7 → +0.8 directors you rate →
 * −0.3 … → 8.2 for you". Bars alone show what pushed, never from where to where, so a reader cannot
 * tell an 8.2 built on a 6.7 baseline from one built on an 8.0.
 *
 * The walk needs ADDITIVE steps in rating points that sum to the prediction, and the contribution
 * bars are not that: they are FastTree's normalised feature contributions, unitless and scaled to
 * the largest one. So the client never invents a baseline from them; it draws the walk only from a
 * server `waterfall` (change request to WS-5), and only when that walk actually adds up to the
 * number on the page — a waterfall that does not land on the prediction is worse than none.
 */
export interface WaterfallPayload {
  /** Where the walk starts, in rating points (e.g. the account's mean rating, 6.7). */
  baseline: number;
  /** What the baseline is, in the reader's words ("your average"). */
  baselineLabel: string;
  /** Additive steps in rating points, strongest first. */
  steps: { feature: string; value: number }[];
}

export interface WaterfallStep {
  /** The server feature the step is, or null for the "everything else" line. */
  feature: string | null;
  label: string;
  /**
   * The step AS PRINTED: a whole number of tenths, never zero. Each step is within a tenth of its
   * own value, and together they carry the printed start exactly to the printed end (HREV1-D06).
   */
  value: number;
}

export interface Waterfall {
  start: { label: string; value: number };
  steps: WaterfallStep[];
  end: number;
}

/** WS-5's name for the model's minor features, summed into one step (OnDemandScorer's PredictionWaterfall). */
const OTHER_FEATURES = "otherFeatures";

/** Steps smaller than this are noise at one decimal: they are folded into "everything else". */
export const WATERFALL_MIN_STEP = 0.05;

/** Largest gap allowed between the walk's end and the shown prediction (both rounded to 1dp). */
const LANDING_TOLERANCE = 0.051;

/** A number as the page prints it (`toFixed(1)`, the badge's own formatting), in whole tenths. */
const printedTenths = (value: number) => Math.round(Number(value.toFixed(1)) * 10);

export function predictionWaterfall(
  predictedRating: number,
  waterfall: WaterfallPayload | null | undefined
): Waterfall | null {
  if (!waterfall || !Number.isFinite(waterfall.baseline) || !Number.isFinite(predictedRating)) return null;
  const steps = waterfall.steps.filter((s) => Number.isFinite(s.value));
  const end = waterfall.baseline + steps.reduce((sum, s) => sum + s.value, 0);
  if (Math.abs(end - predictedRating) > LANDING_TOLERANCE) return null;

  // WS-5's walk already folds the model's minor features into one "otherFeatures" step; it joins the
  // steps too small to draw, so the walk never shows two "everything else" lines.
  const isRest = (s: { feature: string; value: number }) =>
    s.feature === OTHER_FEATURES || Math.abs(s.value) < WATERFALL_MIN_STEP;
  const lines: { feature: string | null; label: string; raw: number }[] = steps
    .filter((s) => !isRest(s))
    .map((s) => ({ feature: s.feature, label: featureLabel(s.feature), raw: s.value }));
  // The small steps are hidden, not dropped: their sum is one honest line.
  const rest = steps.filter(isRest).reduce((sum, s) => sum + s.value, 0);
  lines.push({ feature: null, label: "everything else", raw: rest });

  // HREV1-D06: rounding every step on its own printed "6.3 → +0.6 +0.2 +0.2 −0.1 −0.2 −0.6 −0.1 →
  // 6.2", a chain that adds to 6.3. The steps are rounded TOGETHER instead (largest remainder), so
  // the printed steps carry the printed start exactly to the printed end — the badge's number.
  const printed = apportion(
    lines.map((l) => l.raw * 10),
    printedTenths(predictedRating) - printedTenths(waterfall.baseline)
  );
  const out: WaterfallStep[] = lines
    .map((line, i) => ({ feature: line.feature, label: line.label, value: printed[i] / 10 }))
    // A step that prints as ±0.0 says nothing; dropping it leaves the sum exact.
    .filter((step) => step.value !== 0);

  return {
    start: { label: waterfall.baselineLabel, value: waterfall.baseline },
    steps: out,
    end: predictedRating,
  };
}

/**
 * Rounds each of `values` to a whole number so the results sum to `target` (largest-remainder
 * apportionment, signed). Each starts at its nearest whole number; the shortfall is then made up one
 * unit at a time on whichever value the rounding treated most unfairly, so every result stays within
 * one unit of its value — and a step is never pushed through zero to the other sign, because the
 * last line ("everything else") takes whatever no named step can.
 */
function apportion(values: number[], target: number): number[] {
  const round = (v: number) => Math.sign(v) * Math.round(Math.abs(v));
  const out = values.map(round);
  let gap = target - out.reduce((a, b) => a + b, 0);
  const restIndex = values.length - 1;
  while (gap !== 0) {
    const dir = Math.sign(gap);
    let best = -1;
    let bestError = -Infinity;
    for (let i = 0; i < values.length; i++) {
      const next = out[i] + dir;
      // A named step may move by at most one unit from its value and may not cross zero.
      if (i !== restIndex && (Math.abs(next - values[i]) > 1 || (next !== 0 && Math.sign(next) !== Math.sign(values[i])))) continue;
      const error = (values[i] - out[i]) * dir;
      if (error > bestError) {
        bestError = error;
        best = i;
      }
    }
    out[best === -1 ? restIndex : best] += dir;
    gap -= dir;
  }
  return out;
}

/** "+0.8" / "−0.3" — a signed step to one decimal, with a real minus sign. */
export function signedStep(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return rounded >= 0 ? `+${rounded.toFixed(1)}` : `−${Math.abs(rounded).toFixed(1)}`;
}

/** Contribution bars worth drawing: anything under {@link WATERFALL_MIN_STEP} is noise (F-25). */
export function significantContributions<T extends { value: number }>(contributions: readonly T[]): T[] {
  return contributions.filter((c) => Number.isFinite(c.value) && Math.abs(c.value) >= WATERFALL_MIN_STEP);
}

/**
 * The serving-time rows the server appends after the model's own contributions — the dislike
 * demotion and the taste-prior factors. They explain a card sitting lower than its number suggests,
 * so the top-5 cut must never crowd them out.
 */
const SYNTHETIC_FEATURES = [
  "dislikedSimilarity",
  "tastePriorNoHistoryGenre",
  "tastePriorGenreAffinity",
  "tastePriorMedia",
];

export interface BreakdownBar {
  /** The server feature behind the bar (for its tap-through), or null for "everything else". */
  feature: string | null;
  label: string;
  /** Signed: the direction the bar pushed. */
  value: number;
  /** Bar length, as a share of the longest bar (minimum 5 so a short one stays visible). */
  widthPct: number;
}

/**
 * The bars under "why X for you" — from ONE attribution source (HREV1-D02).
 *
 * With a walk, the bars ARE the walk's printed steps: same features, same signs, lengths on the
 * same scale. They used to come from FastTree's normalised contributions instead, a second source
 * with its own signs and features, and the panel contradicted itself: the walk said "−0.1 crowd
 * rating" over a green crowd-rating bar, drew a "contrarian lens" push the walk never took, and drew
 * a +0.6 step at full length beside a −0.6 at 60%.
 *
 * With no walk there is nothing to contradict, and the model's own top contributions are drawn
 * (the top five plus every serving-time row; anything under 0.05 is noise and not drawn).
 */
export function breakdownBars(
  walk: Waterfall | null,
  contributions: readonly { feature: string; value: number }[]
): BreakdownBar[] {
  const rows: { feature: string | null; label: string; value: number }[] = walk
    ? walk.steps
    : (() => {
        const significant = significantContributions(contributions);
        const top = significant.slice(0, 5);
        const synthetic = significant.filter((c) => SYNTHETIC_FEATURES.includes(c.feature) && !top.includes(c));
        return [...top, ...synthetic].map((c) => ({ feature: c.feature, label: featureLabel(c.feature), value: c.value }));
      })();
  const max = Math.max(0, ...rows.map((r) => Math.abs(r.value))) || 1;
  return rows.map((r) => ({ ...r, widthPct: Math.max(5, Math.round((Math.abs(r.value) / max) * 1000) / 10) }));
}
