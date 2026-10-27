import { Link, useLocation } from "react-router";
import { X } from "lucide-react";
import type { ContinueEntry } from "../../store/feedTypes";
import PosterImage from "../rec/PosterImage";
import { formatEpisodeRef } from "../../lib/format";
import { useMarkDroppedMutation } from "../../store/api";
import FadeScrollRow from "../FadeScrollRow";
import { Button } from "../ui/button";

/** In-progress shows with the next episode front and centre, in resume-likelihood order. */
export default function ContinueWatchingRow({ entries }: { entries: ContinueEntry[] }) {
  const location = useLocation();
  const [markDropped] = useMarkDroppedMutation();

  if (entries.length === 0) {
    return null;
  }

  return (
    <section className="space-y-3">
      <h2 className="fa-section-title text-base">Continue watching</h2>
      <FadeScrollRow bleed className="flex gap-4 overflow-x-auto snap-x pb-2 [scrollbar-width:thin]">
        {entries.map((entry) => {
          const next = formatEpisodeRef(entry.nextEpisodeSeason, entry.nextEpisodeNumber);
          const pct = Math.round(entry.completionPct * 100);
          // A stretched link with the drop control as its SIBLING, not nested inside it: a button
          // inside a link is unreachable as itself by keyboard and announces as part of the link
          // (the rail cards' pattern, CLAUDE.md "card controls are siblings of the card link").
          return (
            <div key={entry.titleId} className="group relative w-card shrink-0 snap-start" data-testid="continue-card">
              <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-has-[a:focus-visible]:ring-2 group-has-[a:focus-visible]:ring-fa-frost/60">
                <PosterImage path={entry.posterPath} alt={entry.name} size="w185" />
                <div className="absolute inset-x-0 bottom-0 h-1 bg-fa-ink/70">
                  <div className="h-full bg-fa-frost" style={{ width: `${pct}%` }} />
                </div>
              </div>
              <div className="mt-2 space-y-0.5">
                <p className="fa-body font-medium text-fa-frost-bright truncate" title={entry.name}>
                  <Link
                    to={`/title/${entry.mediaType.toLowerCase()}/${entry.tmdbId}`}
                    state={{ backgroundLocation: location }}
                    className="after:absolute after:inset-0 after:content-[''] focus:outline-none"
                    data-testid="continue-card-link"
                  >
                    {entry.name}
                  </Link>
                </p>
                {/* One slot, one style, two honest states: "Next: S2 E2" when the next episode is
                    known, otherwise the watched/total count. The count only renders when totalAired
                    is real — "180/0 episodes" (aired count not yet synced) fell back to a third
                    format and made rows look inconsistent card-to-card. */}
                <p className="fa-caption text-3">
                  {next
                    ? `Next: ${next}`
                    : entry.totalAired > 0
                      ? `${entry.watchedEpisodes}/${entry.totalAired} episodes`
                      : `${pct}% watched`}
                </p>
              </div>
              <Button
                variant="ghost"
                size="icon"
                onClick={() => {
                  if (entry.tmdbId != null) {
                    // `titleId` keys the local removal set, so the drop also prunes the card
                    // from Tonight's picks rather than only from this rail.
                    markDropped({
                      titleId: entry.titleId,
                      mediaType: entry.mediaType.toLowerCase(),
                      tmdbId: entry.tmdbId,
                    });
                  }
                }}
                aria-label={`Drop ${entry.name}`}
                title="Drop — remove from Continue Watching"
                // Above the stretched link; hidden until hover or focus on a mouse, always there on a
                // touch screen (no hover to reveal it), at 44 px.
                className="absolute right-1.5 top-1.5 z-10 h-11 w-11 rounded-full bg-fa-ink/70 text-3 hover:bg-fa-ink/70 hover:text-fa-danger [@media(pointer:fine)]:h-7 [@media(pointer:fine)]:w-7 [@media(pointer:fine)]:opacity-0 [@media(pointer:fine)]:group-hover:opacity-100 [@media(pointer:fine)]:focus-visible:opacity-100"
                data-testid="drop-button"
              >
                <X className="h-3.5 w-3.5" aria-hidden />
              </Button>
            </div>
          );
        })}
      </FadeScrollRow>
    </section>
  );
}
