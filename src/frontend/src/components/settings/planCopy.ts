/**
 * The plan card's words, kept equal to what the server actually enforces.
 *
 * The old card said Paid "unlocks … natural-language Ask Reel and the managed Trakt queue". Neither
 * is gated anywhere on the server: Ask Reel and Saved work on Free. What the tier really changes is
 * read off `FeedEndpoints` — Free gets the three-pick shortlist and the rows arrive locked
 * (payloads stripped server-side); Paid gets every row, the endless row pages, the genre rails and
 * an on-demand rebuild. Change this copy only together with those checks.
 */

/** What Free includes, then what Paid adds — each a list of things the server enforces. */
export const FREE_INCLUDES = ["taste DNA", "the track record", "a daily three-pick shortlist", "Ask Reel", "your Saved queue"] as const;
export const PAID_ADDS = ["every recommendation row", "rows that keep loading as you scroll", "genre rails", "rebuilding your picks on demand"] as const;

/** Joins a list as prose: "a, b and c". */
export function proseList(items: readonly string[]): string {
  if (items.length <= 1) return items.join("");
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

/** The one line the card shows for a tier. Unknown tiers read as Free — the server's default. */
export function planLine(tier: string): string {
  switch (tier) {
    case "Founder":
      return "Founder access — everything unlocked.";
    case "Paid":
      return `Paid — ${proseList(PAID_ADDS)}, on top of everything in Free.`;
    default:
      return `Free includes ${proseList(FREE_INCLUDES)}. Paid adds ${proseList(PAID_ADDS)}.`;
  }
}

/** True for the tiers that already have everything the upgrade would sell. */
export function isPaidTier(tier: string): boolean {
  return tier === "Paid" || tier === "Founder";
}
