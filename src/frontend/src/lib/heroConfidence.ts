import type { FeedCard } from "../store/feedTypes";

/**
 * The hero honesty label.
 *
 * The feed build requires a 7.5 predicted rating to lead the hero, and then — rather than open
 * blank — backfills the remaining slots from the best of what is left with that floor dropped. On
 * live data that backfill is not an edge case: 31 of 470 scored titles reach 7.5, and the founder's
 * hero is five network procedurals averaging 6.86 under the heading "Tonight's picks", with the UI
 * saying nothing at all. "The picks are getting worse" is that state, in his own words.
 *
 * So: say it. A card that came in under the bar says so quietly, and when the whole row is reaching
 * the row says so once, in the same register as the feed's existing degraded-reason copy. Naming it
 * costs a sentence; leaving it unnamed costs the trust in every number on the screen.
 */

/** The bar as it is spoken — one decimal, so 7.5 does not render as "7.5000". */
export function formatBar(bar: number): string {
  return bar.toFixed(1);
}

/**
 * True when this card is on the hero only because nothing cleared the bar. Cards with no
 * `confidence` at all — every non-hero surface, and hero cards paged in after the snapshot — are
 * NOT reaching: they were never measured against the hero bar, and guessing would be worse than
 * silence.
 */
export function isReaching(card: Pick<FeedCard, "confidence">): boolean {
  return card.confidence === "reaching";
}

/**
 * The per-card line. First person because it is Reel owning its own shortfall — the same voice as
 * "Reel said 8.4 · you said 5" — rather than something the user is being told about their taste.
 */
export function reachingLabel(card: Pick<FeedCard, "confidence" | "floorUsed">): string | null {
  if (!isReaching(card)) return null;
  return card.floorUsed != null
    ? `Reaching a bit — nothing cleared my usual ${formatBar(card.floorUsed)} bar tonight`
    : "Reaching a bit — nothing cleared my usual bar tonight";
}

/**
 * The row-level band, in the second person the feed's `degradedReason` copy already uses: shown only
 * when EVERY measured pick is a reach, because a row where four cleared the bar and one did not is
 * already answered by that one card's own label.
 *
 * Cards with no confidence are ignored rather than counted as clearing it — the hero pages on past
 * the snapshot, and an unmeasured card arriving must not quietly retract a true statement about the
 * five picks the build actually chose.
 */
export function heroReachingNote(cards: Pick<FeedCard, "confidence" | "floorUsed">[]): string | null {
  const measured = cards.filter((c) => c.confidence != null);
  if (measured.length === 0 || !measured.every(isReaching)) return null;
  const bar = measured.find((c) => c.floorUsed != null)?.floorUsed;
  const barText = bar != null ? ` ${formatBar(bar)}` : "";
  return `Nothing cleared your usual${barText} bar tonight — these are the best of what's left, ranked honestly.`;
}
