import type { TasteCluster } from "../store/api";

/**
 * A pocket under this share of the account's loved-title weight is a weekend, not a taste — the
 * retrieval side still pulls for it (see TasteFactsBuilder's 3% rule), but naming it on the page
 * beside the pockets that actually shape the feed would make the small ones read as equals.
 */
export const MIN_POCKET_SHARE = 0.03;

/** The pockets worth showing, biggest first — the API already orders by weight, this pins it. */
export function visiblePockets(clusters: TasteCluster[] | undefined): TasteCluster[] {
  return (clusters ?? [])
    .filter((c) => c.share >= MIN_POCKET_SHARE && c.exemplars.length > 0)
    .sort((a, b) => b.share - a.share || a.ordinal - b.ordinal);
}

export function pocketSummary(cluster: TasteCluster): string {
  const share = Math.round(cluster.share * 100);
  return `${cluster.titleCount.toLocaleString()} titles · ${share}% of what you love`;
}
