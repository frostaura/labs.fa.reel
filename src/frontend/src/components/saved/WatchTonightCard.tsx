import { useState } from "react";
import { Link, useLocation } from "react-router";
import { Check, Play, SkipForward } from "lucide-react";
import type { SavedQueueItem } from "../../store/savedTypes";
import PosterImage from "../rec/PosterImage";
import PredictedRatingBadge from "../rec/PredictedRatingBadge";
import WhereToWatch from "../title/WhereToWatch";
import { formatRuntime } from "../../lib/format";
import { predictionWithBand } from "../../lib/savedQueue";
import { Button } from "../ui/button";

interface Props {
  item: SavedQueueItem;
  expectedAbsoluteError: number | null;
  /** Whether another candidate exists behind this one, so "Not tonight" is worth offering. */
  hasNext: boolean;
  onNotTonight: () => void;
}

/**
 * The one card pinned to the top of Saved: what to put on tonight.
 *
 * It is the answer to the founder's actual state after several rounds of queueing — a curated list
 * and no decision. Everything on it is there to make the decision, not to describe the title: what
 * it is, why it won, how long it runs, and where to press play.
 */
export default function WatchTonightCard({ item, expectedAbsoluteError, hasNext, onNotTonight }: Props) {
  const location = useLocation();
  // "Started watching" is CLIENT-SIDE ONLY and deliberately so. Recording watch intent server-side
  // would need either a new UserTitleReaction kind or a column on ManagedListItem, both of which are
  // migrations, and this item ships without one. It is a session-scoped acknowledgement — it does
  // not reach the model, the managed list or Trakt, and the button's own tooltip says as much
  // rather than implying a durable record the product does not keep.
  const [started, setStarted] = useState(false);
  const band = predictionWithBand(item.predictedRating, expectedAbsoluteError);
  const runtime = formatRuntime(item.runtimeMinutes);

  return (
    <section className="fa-card p-4 sm:p-5" data-testid="watch-tonight">
      <div className="flex items-center justify-between gap-3 mb-3">
        <h2 className="fa-overline text-fa-frost">watch tonight</h2>
        {hasNext && (
          <Button
            variant="ghost"
            onClick={onNotTonight}
            className="h-auto min-h-11 gap-1.5 px-3 py-2 font-normal [@media(pointer:fine)]:min-h-0"
            title="Show the next candidate instead. This lasts for the session only — nothing is recorded against the title."
            data-testid="not-tonight"
          >
            <SkipForward className="h-3.5 w-3.5" aria-hidden />
            <span className="fa-overline">Not tonight</span>
          </Button>
        )}
      </div>

      <div className="flex flex-col sm:flex-row gap-4">
        <Link
          to={`/title/${item.mediaType.toLowerCase()}/${item.tmdbId}`}
          state={{ backgroundLocation: location }}
          className="block w-32 shrink-0 rounded-card focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60"
        >
          {/* Named by the poster (its alt, or the fallback's role="img" label): an aria-label here
              would hide the visible initials from the name (axe label-content-name-mismatch). */}
          <PosterImage path={item.posterPath} alt={item.name} size="w342" />
        </Link>

        <div className="min-w-0 flex-1 space-y-3">
          <div className="space-y-1">
            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
              <h3 className="text-lg font-light text-fa-frost-bright truncate" title={item.name}>
                {item.name}
              </h3>
              {item.predictedRating != null && <PredictedRatingBadge rating={item.predictedRating} />}
            </div>
            <p className="fa-caption text-3">
              {[item.year, runtime].filter(Boolean).join(" · ")}
              {band != null && (
                <>
                  {" · "}
                  {/* The band travels with the number wherever the number goes — a prediction
                      without its error is a claim the product cannot support. */}
                  <span title="Predicted rating, and what predictions for you are typically off by">
                    {band}
                  </span>
                </>
              )}
            </p>
          </div>

          {item.watchTonightReason && (
            <p className="fa-caption text-fa-frost/80 leading-snug" data-testid="watch-tonight-reason">
              {item.watchTonightReason}
            </p>
          )}

          {/* The provider ladder, from the same component the title page uses — one implementation
              of "no scraping, no JustWatch deep links", not a second one that drifts. */}
          {item.tmdbId != null && (
            <WhereToWatch mediaType={item.mediaType.toLowerCase()} tmdbId={item.tmdbId} />
          )}

          <div className="flex flex-wrap items-center gap-2 pt-1">
            <Button
              variant={started ? "default" : "primary"}
              onClick={() => setStarted(true)}
              disabled={started}
              className="min-h-11 [@media(pointer:fine)]:min-h-0"
              title={
                started
                  ? "Noted on this device for this session. Reel logs the watch when Trakt reports it — that is the record of truth."
                  : "Marks it as tonight's pick on this device. Reel does not log a play from here; that comes back from Trakt."
              }
              data-testid="started-watching"
            >
              {started ? <Check className="h-4 w-4" aria-hidden /> : <Play className="h-4 w-4" aria-hidden />}
              {started ? "Started" : "Started watching"}
            </Button>
            {started && (
              <span className="fa-caption text-3">
                Noted here only — it leaves the queue by itself once Trakt reports the watch.
              </span>
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
