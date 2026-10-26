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
  label: string;
  value: number;
}

export interface Waterfall {
  start: { label: string; value: number };
  steps: WaterfallStep[];
  end: number;
}

/** Steps smaller than this are noise at one decimal: they are folded into "everything else". */
export const WATERFALL_MIN_STEP = 0.05;

/** Largest gap allowed between the walk's end and the shown prediction (both rounded to 1dp). */
const LANDING_TOLERANCE = 0.051;

export function predictionWaterfall(
  predictedRating: number,
  waterfall: WaterfallPayload | null | undefined
): Waterfall | null {
  if (!waterfall || !Number.isFinite(waterfall.baseline) || !Number.isFinite(predictedRating)) return null;
  const steps = waterfall.steps.filter((s) => Number.isFinite(s.value));
  const end = waterfall.baseline + steps.reduce((sum, s) => sum + s.value, 0);
  if (Math.abs(end - predictedRating) > LANDING_TOLERANCE) return null;

  const shown = steps.filter((s) => Math.abs(s.value) >= WATERFALL_MIN_STEP);
  const rest = steps.filter((s) => Math.abs(s.value) < WATERFALL_MIN_STEP).reduce((sum, s) => sum + s.value, 0);
  const out: WaterfallStep[] = shown.map((s) => ({ label: featureLabel(s.feature), value: s.value }));
  // The small steps are hidden, not dropped: their sum is one honest line when it is itself visible.
  if (Math.abs(rest) >= WATERFALL_MIN_STEP) out.push({ label: "everything else", value: rest });

  return {
    start: { label: waterfall.baselineLabel, value: waterfall.baseline },
    steps: out,
    end: predictedRating,
  };
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
