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

/**
 * One pocket's line. The two numbers measure different things and the words say which: the count is
 * how many titles sit in the pocket, the percentage is how much of the account's taste WEIGHT they
 * carry (TasteClusterer.WeightOf — a title rated further above your own average, or rated more
 * recently, weighs more). Read as "% of what you love" the pair contradicted itself on live: 172
 * titles at 15% beside 175 titles at 11% (IREV3S-06).
 */
export function pocketSummary(cluster: TasteCluster): string {
  const share = Math.round(cluster.share * 100);
  const titles = `${cluster.titleCount.toLocaleString("en-US")} ${cluster.titleCount === 1 ? "title" : "titles"}`;
  return `${titles} · ${share}% of your taste weight`;
}

/** The caption under the pockets: why a smaller pocket can carry a bigger share. */
export const POCKET_WEIGHT_CAPTION =
  "A pocket's share is its weight, not its size: a title you rated further above your own average, or more recently, counts for more — so a pocket with fewer titles can carry a bigger share.";
