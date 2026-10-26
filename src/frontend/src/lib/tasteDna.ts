/**
 * Geometry for the Taste DNA genre fingerprint bars.
 *
 * Two geometries live here. The anchored 0–10 fill directly below now serves only the SHARE CARD;
 * the page itself draws diverging bars from {@link divergingBars} at the end of this file, and the
 * note there says why the earlier rejection of diverging bars no longer holds.
 *
 * `affinity` is a mean over the user's own 0–10 ratings, so the honest axis is the full 0–10 scale.
 * The bar used to be drawn as `((affinity - 5) / 5) * 100` clamped at a 6% floor, which is negative
 * for anything at or below 5 — so 5.0, 3.0 and 0.5 all rendered the same minimal stub while the
 * correct number sat printed beside them. Top genres are picked by `affinity × log(count)`, so a
 * high-volume genre with a below-average affinity genuinely reaches this list; the broken half of
 * the scale was not hypothetical.
 *
 * **Choice: a linear 0–10 fill, with the user's own mean marked on the track.** A bar that diverges
 * from the mean instead of filling from zero was the obvious candidate — it spends the whole track
 * on the part that varies — and it was tried first, then rejected against real data. Genre
 * affinities cluster hard: the founder's top eight span 6.5–7.2 around a 6.8 mean, which a diverging
 * span renders as 1.5%–4.2% of the track, i.e. 4–10px. The anchored fill renders the same eight
 * genres at 65%–72%, so the *same* differences read at full track resolution, and it stays
 * consistent with the creator and actor bars a few sections down, which already fill from zero.
 *
 * The above/below-your-own-bar signal that made diverging attractive is kept without paying for it:
 * a tick at the user's mean, plus the fill colour switching side of it. Direction is therefore
 * categorical (colour + which side of the tick the bar ends) rather than something the reader has
 * to infer from two similar lengths.
 */

/** Percentage of the track a bar occupies at affinity 0 — an empty track reads as a broken render,
 *  not as a rating. Small enough that it cannot be confused with a real value. */
const MIN_FILL_PCT = 1.5;

const clampRating = (value: number) => (Number.isFinite(value) ? Math.min(10, Math.max(0, value)) : 0);

/** Percentages land in a `style` attribute; two decimals is past the sub-pixel limit and keeps
 *  float noise (7.4 − 6.8 = 0.5999…) out of the rendered markup. */
const round2 = (value: number) => Math.round(value * 100) / 100;

/** The page prints affinities to one decimal, so direction is decided on that same rounding —
 *  otherwise a 6.79 genre is labelled "6.8, below your 6.8 average", which reads as a bug. */
const round1 = (value: number) => Math.round(value * 10) / 10;

export interface GenreBarGeometry {
  /** Length of the bar, as a percentage of the 0–10 track. */
  fillPct: number;
  /** True when the genre sits at or above the user's own rating mean, at displayed precision. */
  aboveMean: boolean;
}

/** Where the mean tick sits on the 0–10 track, as a percentage. */
export function meanMarkerPct(userMean: number): number {
  return round2(clampRating(userMean) * 10);
}

/**
 * The bar for one genre on a 0–10 track, plus which side of the user's own mean it falls.
 * Out-of-range or non-finite inputs are clamped rather than allowed to escape the track.
 */
export function genreBarGeometry(affinity: number, userMean: number): GenreBarGeometry {
  const value = clampRating(affinity);
  const mean = clampRating(userMean);

  return {
    fillPct: round2(Math.max(MIN_FILL_PCT, value * 10)),
    aboveMean: round1(value) >= round1(mean),
  };
}

/**
 * ── The page's charts: diverging from your own mean (P1, G-12) ─────────────────────────────────
 *
 * The anchored 0–10 fill above is kept for the share card, a fixed-size image that has to read at a
 * glance out of context. On the page it carried no information: every genre the founder has sits
 * within ±0.4 of his 6.8 mean, so eight bars at 65%–72% of the track look identical. The page
 * therefore draws each affinity as a bar that leaves a centre line (your mean) to the right when the
 * genre beats your average and to the left when it falls short, **scaled to the spread actually
 * present**: the largest gap fills {@link LARGEST_BAR_PCT} of the chart width, and the scale is
 * printed beside the chart, so a reader never mistakes a 0.4 gap for a 4-point one.
 *
 * The earlier objection to diverging bars (4–10 px bars) came from a fixed ±5 scale; scaling to the
 * data is what removes it. {@link MIN_SCALE} keeps rounding noise from being blown up to full width
 * on an account whose genres genuinely all sit on its mean.
 */

/** The largest gap's bar, as a percentage of the whole chart width: it reaches the edge of its half. */
export const LARGEST_BAR_PCT = 50;

/** The smallest gap, in rating points, that is allowed to fill the chart. */
export const MIN_SCALE = 0.2;

export interface DivergingBar {
  /** Where the bar starts, as a percentage of the chart width (the centre line is 50). */
  leftPct: number;
  /** Bar length, as a percentage of the chart width. Zero for a value on the mean. */
  widthPct: number;
  /** "above", "below", or "level" — decided at the displayed one-decimal precision. */
  side: "above" | "below" | "level";
  /** The gap as printed: "+0.4", "−0.3", "±0.0". */
  deltaLabel: string;
}

export interface DivergingChart {
  /** The gap that reaches the edge of the bar area, in rating points (printed as the scale). */
  scale: number;
  bars: DivergingBar[];
}

/** "+0.4" / "−0.3" (a real minus sign) / "±0.0" at one decimal. */
export function signedGap(gap: number): string {
  const rounded = round1(gap);
  if (rounded === 0) return "±0.0";
  return `${rounded > 0 ? "+" : "−"}${Math.abs(rounded).toFixed(1)}`;
}

/**
 * Diverging bars for a set of affinities against the user's mean, on one shared scale. Non-finite
 * inputs are clamped like {@link genreBarGeometry}'s.
 */
export function divergingBars(affinities: number[], userMean: number): DivergingChart {
  // Gaps are taken between the numbers AS PRINTED (one decimal each): a raw 6.75 against a raw 6.74
  // printed as "6.8 ±0.0" beside "your 6.7 average", which reads as arithmetic the page got wrong.
  const mean = round1(clampRating(userMean));
  const gaps = affinities.map((a) => round1(round1(clampRating(a)) - mean));
  const largest = gaps.reduce((max, gap) => Math.max(max, Math.abs(gap)), 0);
  // Rounded BEFORE it scales anything, so the printed scale is exactly the one the bars are drawn on.
  const scale = Math.max(MIN_SCALE, round2(largest));

  return {
    scale,
    bars: gaps.map((gap) => {
      const shown = round1(gap);
      const side = shown > 0 ? "above" : shown < 0 ? "below" : "level";
      const widthPct = side === "level" ? 0 : round2(Math.min(LARGEST_BAR_PCT, (Math.abs(gap) / scale) * LARGEST_BAR_PCT));
      return {
        leftPct: side === "below" ? round2(50 - widthPct) : 50,
        widthPct,
        side,
        deltaLabel: signedGap(gap),
      };
    }),
  };
}
