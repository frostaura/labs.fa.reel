import { Link, useLocation } from "react-router-dom";
import { BookmarkX, ExternalLink, ListChecks } from "lucide-react";
import { useGetSavedQuery, useUnsaveForLaterMutation } from "../store/api";
import PosterImage from "../components/rec/PosterImage";
import ScoreBadges from "../components/rec/ScoreBadges";

/** Save-for-later shelf + the managed "Reel — Up Next" Trakt list panel. */
export default function Saved() {
  const { data, isLoading } = useGetSavedQuery();
  const location = useLocation();
  const [unsave] = useUnsaveForLaterMutation();

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <h1 className="text-2xl font-light text-fa-frost-bright">Saved</h1>
        {data?.managedListUrl && (
          <a
            href={data.managedListUrl}
            target="_blank"
            rel="noreferrer"
            className="fa-button"
            title="Reel keeps a Trakt list called “Reel — Up Next” in sync with this shelf. Anything saved here lands on it — and shows up in Plex, Kodi, Infuse and every Trakt-connected app."
          >
            <ListChecks className="h-4 w-4" />
            Reel — Up Next on Trakt
            <ExternalLink className="h-3.5 w-3.5" />
          </a>
        )}
      </div>

      <p className="fa-caption text-fa-frost-dim max-w-2xl">
        Everything you saved, synced to your Trakt watchlist and the managed{" "}
        <span className="text-fa-frost">Reel — Up Next</span> list — visible in Plex, Kodi, Infuse and
        every Trakt-connected app. Watch something and it leaves the queue by itself.
      </p>

      {isLoading ? (
        <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4">
          {Array.from({ length: 6 }).map((_, i) => (
            <div key={i} className="reel-poster reel-shimmer" />
          ))}
        </div>
      ) : (data?.items.length ?? 0) === 0 ? (
        <div className="fa-card p-8 text-center">
          <p className="fa-body text-fa-frost-dim">
            Nothing saved yet — hit “Save for later” on any pick and it lands here and on your Trakt queue.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4">
          {data!.items.map((item) => (
            <Link
              key={item.titleId}
              to={`/title/${item.mediaType.toLowerCase()}/${item.tmdbId}`}
              state={{ backgroundLocation: location }}
              className="group block focus:outline-none"
            >
              <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03]">
                <PosterImage path={item.posterPath} alt={item.name} size="w185" />
                <ScoreBadges predictedRating={item.predictedRating} />
                {/* One-tap unsave, same overlay pattern as the feed cards' quick actions. */}
                {item.tmdbId != null && (
                  <button
                    type="button"
                    onClick={(e) => {
                      e.preventDefault();
                      unsave({ titleId: item.titleId, mediaType: item.mediaType.toLowerCase(), tmdbId: item.tmdbId! });
                    }}
                    aria-label={`Remove ${item.name} from Saved`}
                    title="Remove from Saved"
                    className="absolute left-1.5 top-1.5 rounded-full bg-fa-ink/70 p-1.5 text-fa-frost-dim backdrop-blur-sm transition hover:text-fa-danger opacity-0 group-hover:opacity-100 focus:opacity-100 focus-visible:opacity-100 [@media(pointer:coarse)]:opacity-60"
                    data-testid="saved-unsave"
                  >
                    <BookmarkX className="h-3.5 w-3.5" />
                  </button>
                )}
              </div>
              <p className="mt-2 fa-body text-fa-frost-bright truncate" title={item.name}>
                {item.name}
              </p>
              {/* Every card states its list status — "on Up Next" once the outbox has written the
                  ManagedListItem to Trakt, "syncing…" while that write is still queued. The blank
                  third state QA saw was onManagedList=false rendered as nothing. */}
              <p className="fa-caption text-fa-frost-dim">
                {item.year ?? ""}
                {item.year != null ? " · " : ""}
                {item.onManagedList ? (
                  "on Up Next"
                ) : (
                  <span title="Queued for the managed Trakt list — Reel writes it back in the background.">syncing…</span>
                )}
              </p>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
