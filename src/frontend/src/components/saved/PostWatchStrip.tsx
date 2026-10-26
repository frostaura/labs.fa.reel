import { useMemo, useState } from "react";
import { X } from "lucide-react";
import { useGetRecentlyWatchedQuery, useRateTitleMutation } from "../../store/api";
import PosterImage from "../rec/PosterImage";
import { dismiss, readDismissed } from "../../lib/postWatchDismissals";
import { ratingDelta } from "../../lib/savedQueue";
import { Button } from "../ui/button";

const SCALE = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] as const;

/**
 * The loop, closed out loud.
 *
 * Reel says "8.4 for you", the user saves it, watches it weeks later, and the app has never once
 * mentioned the 8.4 again — the promise is made loudly and settled silently. This strip settles it:
 * one watched-via-Reel title at a time, the number Reel committed to BEFORE the watch, an inline
 * 1–10, and then the delta stated plainly, including when it was badly wrong.
 *
 * <b>What this is not.</b> It is not a label-acquisition mechanism, and it must not be sold as one.
 * The server only offers titles with no rating yet, and on the founder's own history all 73
 * conversions were already rated on Trakt before Reel could ask — the same delta sync that detects
 * the watch ingests the rating. For a user who rates on Trakt the incremental label yield is close
 * to zero. Its value is that the product answers for itself.
 *
 * Self-contained on purpose: it owns its own query, its own dismissals and its own copy, so Home
 * mounts it with one line and no state of its own.
 */
export default function PostWatchStrip() {
  const { data } = useGetRecentlyWatchedQuery();
  const [rateTitle, { isLoading: isRating }] = useRateTitleMutation();
  const [dismissed, setDismissed] = useState<Set<string>>(() => readDismissed());
  const [settled, setSettled] = useState<{ titleId: string; predicted: number; actual: number } | null>(null);

  const pending = useMemo(
    () => (data?.items ?? []).filter((item) => !dismissed.has(item.titleId)),
    [data, dismissed]
  );
  const item = pending[0];

  // The settled card outlives the query row it came from: rating the title invalidates the strip's
  // tag, so the item vanishes from `data` on the very next tick. The delta is the whole point of the
  // interaction and must not flash out from under the user who just typed it.
  if (settled) {
    return (
      <div className="fa-card px-4 py-3 flex items-center justify-between gap-3" data-testid="post-watch-settled">
        <p className="fa-body text-fa-frost-bright">{ratingDelta(settled.predicted, settled.actual)}</p>
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setSettled(null)}
          className="h-11 w-11 shrink-0 [@media(pointer:fine)]:h-9 [@media(pointer:fine)]:w-9"
          aria-label="Dismiss"
          data-testid="post-watch-settled-close"
        >
          <X className="h-4 w-4" aria-hidden />
        </Button>
      </div>
    );
  }

  if (!item) return null;

  return (
    <div className="fa-card p-4" data-testid="post-watch-strip">
      <div className="flex items-start gap-3">
        <div className="w-12 shrink-0">
          <PosterImage path={item.posterPath} alt={item.name} size="w185" />
        </div>

        <div className="min-w-0 flex-1 space-y-2">
          <p className="fa-body text-fa-frost-bright">
            You watched <span className="font-medium">{item.name}</span>. Reel guessed{" "}
            <span className="tabular-nums">{item.predictedRating.toFixed(1)}</span> — what did you actually think?
          </p>

          {/* Two rows of five at 44 px on a touch screen (the cards' picker shape); one row of
              compact squares under a mouse. */}
          <div
            className="grid max-w-[16rem] grid-cols-5 gap-1 [@media(pointer:fine)]:flex [@media(pointer:fine)]:max-w-none [@media(pointer:fine)]:flex-wrap [@media(pointer:fine)]:items-center"
            role="group"
            aria-label={`Rate ${item.name} out of 10`}
          >
            {SCALE.map((value) => (
              <Button
                key={value}
                disabled={isRating}
                onClick={async () => {
                  try {
                    await rateTitle({
                      titleId: item.titleId,
                      mediaType: item.mediaType.toLowerCase(),
                      tmdbId: item.tmdbId!,
                      rating: value,
                      // Already watched — that is precisely why it is being offered here. Asking
                      // the server to log a play would write a second one back to Trakt.
                      markWatched: false,
                    }).unwrap();
                    setSettled({ titleId: item.titleId, predicted: item.predictedRating, actual: value });
                  } catch {
                    // The rating did not land; leave the prompt up rather than claiming a delta
                    // from a number the server never accepted.
                  }
                }}
                aria-label={`${value} out of 10`}
                data-testid={`post-watch-rate-${value}`}
                className="h-11 w-11 rounded-md bg-fa-glass p-0 font-normal text-3 tabular-nums hover:border-fa-frost/40 hover:text-fa-frost-bright [@media(pointer:fine)]:h-7 [@media(pointer:fine)]:w-7"
              >
                <span className="fa-overline">{value}</span>
              </Button>
            ))}
          </div>

          <p className="fa-caption text-3">
            Closing the loop — Reel only learns how wrong it was when you tell it.
          </p>
        </div>

        <Button
          variant="ghost"
          size="icon"
          onClick={() => setDismissed(dismiss(item.titleId))}
          className="h-11 w-11 shrink-0 [@media(pointer:fine)]:h-9 [@media(pointer:fine)]:w-9"
          aria-label={`Dismiss the prompt for ${item.name}`}
          title="Not now. Hidden on this device only."
          data-testid="post-watch-dismiss"
        >
          <X className="h-4 w-4" aria-hidden />
        </Button>
      </div>
    </div>
  );
}
