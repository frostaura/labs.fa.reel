import { posterUrl } from "../../lib/tmdbImages";
import type { TasteCluster } from "../../store/api";

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

/**
 * "What you keep coming back to" — the taste clusters the retrieval side splits its budget across,
 * shown as the titles nearest each pocket's centre. One mean vector over 650 loved titles pointed
 * at nothing; these are the pockets it was hiding. Exemplars are the account's OWN rated titles,
 * never recommendations, which is why there is nothing to click here.
 */
export default function TastePockets({ clusters }: { clusters: TasteCluster[] | undefined }) {
  const pockets = visiblePockets(clusters);
  if (pockets.length === 0) {
    return null;
  }

  return (
    <section className="fa-card p-5" data-testid="taste-pockets">
      <h2 className="fa-section-title mb-1">What you keep coming back to</h2>
      <p className="fa-caption text-fa-frost-dim mb-4">
        {pockets.length} pockets of taste, found from every title you rated 7 or higher or watched twice.
        Tonight&apos;s picks are pulled from each one, not from one average of them all.
      </p>
      <ol className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {pockets.map((pocket) => (
          <li
            key={pocket.ordinal}
            className="rounded-xl border border-fa-edge bg-fa-glass p-3 space-y-2"
            data-testid="taste-pocket"
          >
            <div className="flex gap-1.5">
              {pocket.exemplars.map((title) => {
                const art = posterUrl(title.posterPath, "w185");
                return (
                  <div
                    key={`${title.name}-${title.year ?? ""}`}
                    className="aspect-[2/3] w-1/3 overflow-hidden rounded-md bg-fa-ink-2"
                    title={title.year ? `${title.name} (${title.year})` : title.name}
                  >
                    {art && <img src={art} alt="" loading="lazy" decoding="async" className="h-full w-full object-cover" />}
                  </div>
                );
              })}
            </div>
            <p className="fa-body text-fa-frost-bright leading-snug" data-testid="pocket-exemplars">
              {pocket.exemplars.map((t) => t.name).join(" · ")}
            </p>
            <p className="fa-caption text-fa-frost-dim" data-testid="pocket-summary">
              {pocketSummary(pocket)}
            </p>
          </li>
        ))}
      </ol>
    </section>
  );
}
