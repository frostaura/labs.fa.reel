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
 * The row-level admission: shown only when EVERY measured pick is a reach, because a row where four
 * cleared the bar and one did not is already answered by that one card's own label. It is a segment
 * of Home's single status line (FD-15), so it is one short clause, and when it shows the cards drop
 * their own labels — the same message five times over was the O-6 finding.
 *
 * Whose bar it is decides the possessive (HREV1-D01). The bar is `HeroConfidence.BarFor` — the
 * product's own 7.5, raised only by an account's higher "for you" minimum — so it is Reel's bar,
 * spoken in Reel's voice like {@link reachingLabel}, UNLESS the bar the picks missed is exactly the
 * account's own minimum (`accountMinimum`, from its saved conditions). Telling a founder whose only
 * bar is 6.0 that nothing cleared "your usual 7.5" told him about a setting he never made.
 *
 * Cards with no confidence are ignored rather than counted as clearing it — the hero pages on past
 * the snapshot, and an unmeasured card arriving must not quietly retract a true statement about the
 * five picks the build actually chose.
 */
export function heroReachingNote(
  cards: Pick<FeedCard, "confidence" | "floorUsed">[],
  accountMinimum?: number | null
): string | null {
  const measured = cards.filter((c) => c.confidence != null);
  if (measured.length === 0 || !measured.every(isReaching)) return null;
  const bar = measured.find((c) => c.floorUsed != null)?.floorUsed;
  if (bar == null) return "Nothing cleared my usual bar tonight";
  if (isAccountsOwnBar(bar, accountMinimum)) return `Nothing cleared your ${formatBar(bar)} bar tonight`;
  return `Nothing cleared my usual ${formatBar(bar)} bar tonight`;
}

/**
 * True when the bar is the account's own setting: its saved "for you" minimum, at the precision the
 * bar is spoken. The server's bar is `max(product bar, account minimum)`, so equality means the
 * account raised it there (or set exactly the product's own) — either way the reader made that
 * setting, and "your" is true. Anything else, unknown included, is Reel's bar.
 */
function isAccountsOwnBar(bar: number, accountMinimum: number | null | undefined): boolean {
  return accountMinimum != null && formatBar(accountMinimum) === formatBar(bar);
}
