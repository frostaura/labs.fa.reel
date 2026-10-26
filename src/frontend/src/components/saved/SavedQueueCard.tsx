import { Link, useLocation } from "react-router";
import { BookmarkX } from "lucide-react";
import type { SavedQueueItem } from "../../store/savedTypes";
import PosterImage from "../rec/PosterImage";
import ScoreBadges from "../rec/ScoreBadges";
import { logoUrl } from "../../lib/tmdbImages";
import { formatRuntime } from "../../lib/format";
import { listSyncLabel, predictionWithBand, savedAgo } from "../../lib/savedQueue";
import { cardProviders } from "../../lib/whereToWatch";

interface Props {
  item: SavedQueueItem;
  expectedAbsoluteError: number | null;
  onUnsave: (item: SavedQueueItem) => void;
}

/**
 * One card in the queue below the pin. It answers the three questions the old poster grid did not:
 * is it any good for me (with its error band), how long is it, and can I actually watch it now.
 *
 * A stretched-link card, like the feed's RecCard: the title is the link and its `::after` covers
 * the card, and the unsave button is a sibling above it rather than a control nested in a link.
 */
export default function SavedQueueCard({ item, expectedAbsoluteError, onUnsave }: Props) {
  const location = useLocation();
  const band = predictionWithBand(item.predictedRating, expectedAbsoluteError);
  const runtime = formatRuntime(item.runtimeMinutes);
  const syncLabel = listSyncLabel(item);
  const providers = cardProviders(item.providers);

  return (
    <div className="group relative" data-testid="saved-card">
      <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-has-[a:focus-visible]:ring-2 group-has-[a:focus-visible]:ring-fa-frost/60">
        <PosterImage path={item.posterPath} alt={item.name} size="w185" />
        <ScoreBadges
          predictedRating={item.predictedRating}
          crowdRating={item.crowdRating}
          crowdVotes={item.crowdVotes}
          band={expectedAbsoluteError}
        />
        {providers.length > 0 && (
          <div className="absolute bottom-2 left-2 flex gap-1" data-testid="saved-card-providers">
            {providers.map((p) =>
              logoUrl(p.logoPath) ? (
                <img
                  key={p.name}
                  src={logoUrl(p.logoPath)!}
                  alt={p.name}
                  title={`${p.name} — streaming now`}
                  className="h-5 w-5 rounded-[4px] ring-1 ring-black/30"
                  loading="lazy"
                />
              ) : (
                <span
                  key={p.name}
                  title={`${p.name} — streaming now`}
                  className="fa-overline rounded-[4px] bg-fa-ink/80 px-1 py-0.5 text-fa-frost-dim ring-1 ring-black/30"
                >
                  {p.name.slice(0, 2)}
                </span>
              )
            )}
          </div>
        )}
      </div>

      <div className="mt-2 space-y-0.5">
        <p className="fa-body text-fa-frost-bright truncate" title={item.name}>
          <Link
            to={`/title/${item.mediaType.toLowerCase()}/${item.tmdbId}`}
            state={{ backgroundLocation: location }}
            className="after:absolute after:inset-0 after:content-[''] focus:outline-none"
          >
            {item.name}
          </Link>
        </p>
        <p className="fa-caption text-fa-frost-dim">
          {[item.year, runtime].filter(Boolean).join(" · ") || " "}
        </p>
        <p className="fa-caption text-fa-frost-dim">
          {band != null ? (
            <span title="Predicted rating, and what predictions for you are typically off by">{band}</span>
          ) : (
            // Never a blank corner: an absent number is a state of the backfill, not a verdict.
            <span title="Reel has not scored this one yet — it is being backfilled.">scoring…</span>
          )}
          {" · "}
          {item.availableNow ? (
            <span className="text-fa-frost/80">streaming now</span>
          ) : (
            <span title="Not on a subscription or free service in your region right now.">not streaming</span>
          )}
        </p>
        {/* The list line says "syncing…" only while a write-back is genuinely waiting — see
            listSyncLabel. With no evidence either way it says nothing rather than guess. */}
        <p className="fa-caption text-fa-frost-dim/70" data-testid="saved-card-meta">
          {savedAgo(item.daysSaved)}
          {syncLabel && (
            <>
              {" · "}
              <span title={syncLabel.title} data-testid="saved-list-sync">
                {syncLabel.text}
              </span>
            </>
          )}
        </p>
      </div>

      {/* One-tap unsave, the feed cards' quick-action pattern: a 44 px target on any coarse pointer
          around a poster-sized pill, a sibling above the card's link overlay. */}
      {item.tmdbId != null && (
        <button
          type="button"
          onClick={() => onUnsave(item)}
          aria-label={`Remove ${item.name} from Saved`}
          title="Remove from Saved"
          className="absolute left-0 top-0 z-10 inline-flex min-h-11 min-w-11 items-center justify-center text-fa-frost-dim transition hover:text-fa-danger opacity-0 group-hover:opacity-100 focus:opacity-100 focus-visible:opacity-100 [@media(pointer:coarse)]:opacity-60 md:[@media(pointer:fine)]:left-1.5 md:[@media(pointer:fine)]:top-1.5 md:[@media(pointer:fine)]:min-h-0 md:[@media(pointer:fine)]:min-w-0"
          data-testid="saved-unsave"
        >
          <span className="flex items-center justify-center rounded-full bg-fa-ink/70 p-1.5 backdrop-blur-sm">
            <BookmarkX className="h-3.5 w-3.5" />
          </span>
        </button>
      )}
    </div>
  );
}
