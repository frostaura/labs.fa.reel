/**
 * Geometry for the Taste DNA genre fingerprint bars.
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
