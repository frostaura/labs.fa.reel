import { ExternalLink, Play } from "lucide-react";
import { useGetProvidersQuery } from "../../store/api";
import { logoUrl } from "../../lib/tmdbImages";

/** A single TMDB streaming "kind" → a short human badge. */
const KIND_LABEL: Record<string, string> = {
  Flatrate: "Stream",
  Free: "Free",
  Ads: "Free (ads)",
  Rent: "Rent",
  Buy: "Buy",
};

/**
 * Spec §8 requires the link type to be *visible*, not merely hoverable: a maintained deep link and
 * a bounce through TMDB are materially different promises, and a `title` tooltip communicates
 * neither on a phone. Both kinds get a chip so the absence of a badge can never be mistaken for the
 * better outcome — the old markup labelled only the TMDB bounce, leaving "direct" indistinguishable
 * from "not yet rendered". The pill wraps under the kinds line on narrow chips rather than
 * squeezing the row.
 */
function LinkKindPill({ linkKind }: { linkKind: string }) {
  const direct = linkKind === "direct";
  return (
    <span
      className={`inline-flex items-center gap-0.5 rounded-full border px-1.5 py-px leading-none ${
        direct
          ? "border-fa-frost/40 bg-fa-frost/10 text-fa-frost-bright"
          : "border-fa-edge bg-fa-glass text-fa-frost-dim"
      }`}
    >
      {direct ? <Play className="h-2.5 w-2.5 fill-current" /> : <ExternalLink className="h-2.5 w-2.5" />}
      {direct ? "direct" : "via TMDB"}
    </span>
  );
}

/**
 * Where-to-watch: provider logos linking out to the title (provider-side search where we
 * maintain a pattern, else the TMDB watch page). TMDB/JustWatch attribution is mandatory and
 * shown verbatim. No scraping, no per-title JustWatch deep links (the M5 gate).
 */
export default function WhereToWatch({ mediaType, tmdbId }: { mediaType: string; tmdbId: number }) {
  const { data, isFetching, isError } = useGetProvidersQuery({ mediaType, tmdbId });

  if (isError) return null; // availability is best-effort; never block the page on it

  return (
    <section className="space-y-2" data-testid="where-to-watch">
      <div className="flex items-center justify-between">
        <h2 className="fa-overline text-fa-frost-dim">where to watch</h2>
        {data && <span className="fa-caption text-fa-frost-dim/70">{data.region}</span>}
      </div>

      {isFetching ? (
        <div className="flex gap-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="h-10 w-10 rounded-lg reel-shimmer bg-fa-ink-3" />
          ))}
        </div>
      ) : !data || data.providers.length === 0 ? (
        <p className="fa-caption text-fa-frost-dim">
          No streaming options found in your region.{" "}
          {data && (
            <a href={data.tmdbWatchPage} target="_blank" rel="noopener noreferrer" className="fa-link">
              Check TMDB
            </a>
          )}
        </p>
      ) : (
        <div className="flex flex-wrap gap-2" data-testid="provider-list">
          {data.providers.map((p) => {
            const kinds = p.kinds.map((k) => KIND_LABEL[k] ?? k).join(" · ") || "watch";
            const direct = p.linkKind === "direct";
            return (
              <a
                key={p.provider}
                href={p.url}
                target="_blank"
                rel="noopener noreferrer"
                title={`${p.provider} · ${kinds} · ${direct ? "opens provider" : "via TMDB"}`}
                aria-label={`${p.provider} — ${kinds} — ${
                  direct ? "direct link, opens the provider" : "opens the TMDB watch page"
                }`}
                className="group flex items-center gap-2 rounded-lg border border-fa-edge bg-fa-glass px-2 py-1.5 transition hover:border-fa-frost/40"
                data-testid="provider-chip"
                data-link-kind={p.linkKind}
              >
                <div className="h-8 w-8 rounded-md overflow-hidden bg-fa-ink-3 shrink-0">
                  {logoUrl(p.logoPath) ? (
                    <img src={logoUrl(p.logoPath)!} alt="" loading="lazy" className="h-full w-full object-cover" />
                  ) : (
                    <div className="h-full w-full flex items-center justify-center fa-caption text-fa-frost-dim" aria-hidden>
                      {p.provider.slice(0, 2)}
                    </div>
                  )}
                </div>
                <div className="pr-1 min-w-0" aria-hidden>
                  <p className="fa-caption text-fa-frost-bright leading-tight">{p.provider}</p>
                  <p className="fa-caption leading-tight flex flex-wrap items-center gap-x-1 gap-y-0.5">
                    <span className="text-fa-frost-dim/70">{kinds}</span>
                    <LinkKindPill linkKind={p.linkKind} />
                  </p>
                </div>
              </a>
            );
          })}
        </div>
      )}

      {data && <p className="fa-caption text-fa-frost-dim/60">{data.attribution}</p>}
    </section>
  );
}
