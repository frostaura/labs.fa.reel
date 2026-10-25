import { ExternalLink, Play, Search } from "lucide-react";
import { useGetProvidersQuery } from "../../store/api";
import { logoUrl } from "../../lib/tmdbImages";
import { LINK_DESCRIPTION, LINK_LABEL, providerLadder, type ProviderLink } from "../../lib/whereToWatch";

/**
 * Spec §8 requires the link type to be *visible*, not merely hoverable: a link to the title, a
 * search on the provider and a bounce through TMDB are three different promises, and a `title`
 * tooltip communicates none of them on a phone. Every chip carries its pill, so the absence of a
 * badge can never be mistaken for the better outcome. Only a real deep link reads "direct" — every
 * maintained pattern today is a provider-side search, and it says "search" (R1-19).
 */
function LinkKindPill({ link }: { link: ProviderLink }) {
  const Icon = link === "deep" ? Play : link === "search" ? Search : ExternalLink;
  return (
    <span
      className={`inline-flex items-center gap-0.5 rounded-full border px-1.5 py-px leading-none ${
        link === "deep"
          ? "border-fa-frost/40 bg-fa-frost/10 text-fa-frost-bright"
          : "border-fa-edge bg-fa-glass text-fa-frost-dim"
      }`}
      data-testid="provider-link-kind"
    >
      <Icon className={`h-2.5 w-2.5 ${link === "deep" ? "fill-current" : ""}`} />
      {LINK_LABEL[link]}
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
        (() => {
          // Streaming (subscription, free, ads) first and always as chips, then rent and buy with a
          // link of their own; link-less purchase stores collapse into one trailing TMDB link, since
          // every one of them would bounce to the same page. See lib/whereToWatch.ts.
          const ladder = providerLadder(data.providers, data.tmdbWatchPage);
          return (
            <div className="flex flex-wrap items-center gap-2" data-testid="provider-list">
              {ladder.chips.map((p) => {
                const kinds = p.kinds.join(" · ") || "watch";
                return (
                  <a
                    key={p.provider}
                    href={p.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    title={`${p.provider} · ${kinds} · ${LINK_DESCRIPTION[p.link]}`}
                    aria-label={`${p.provider} — ${kinds} — ${LINK_DESCRIPTION[p.link]}`}
                    className="group flex min-h-11 items-center gap-2 rounded-lg border border-fa-edge bg-fa-glass px-2 py-1.5 transition hover:border-fa-frost/40"
                    data-testid="provider-chip"
                    data-tier={p.tier}
                    data-link-kind={p.link}
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
                        <LinkKindPill link={p.link} />
                      </p>
                    </div>
                  </a>
                );
              })}
              {ladder.tmdbOnlyCount > 0 && ladder.tmdbUrl && (
                <a
                  href={ladder.tmdbUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  aria-label={`Rent or buy from ${ladder.tmdbOnlyCount} more store${ladder.tmdbOnlyCount === 1 ? "" : "s"} on the TMDB watch page`}
                  className="fa-caption inline-flex min-h-11 items-center gap-1 rounded-lg border border-fa-edge bg-fa-glass px-3 py-2 text-fa-frost transition hover:border-fa-frost/40 hover:text-fa-frost-bright"
                  data-testid="tmdb-more-link"
                >
                  {ladder.chips.length > 0 ? "More stores" : `Rent or buy from ${ladder.tmdbOnlyCount} store${ladder.tmdbOnlyCount === 1 ? "" : "s"}`} via TMDB →
                </a>
              )}
            </div>
          );
        })()
      )}

      {data && <p className="fa-caption text-fa-frost-dim/60">{data.attribution}</p>}
    </section>
  );
}
