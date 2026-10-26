import { useState } from "react";
import { Sparkles } from "lucide-react";
import {
  useGetRateSuggestionsQuery,
  useGetSyncStatusQuery,
  useRateTitleMutation,
  type RateSuggestion,
} from "../store/api";
import PosterImage from "../components/rec/PosterImage";
import LoadFailed from "../components/ui/load-failed";
import { refusedOutright } from "../components/ui/save-outcome";
import { Button } from "../components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "../components/ui/popover";

/**
 * The label engine.
 *
 * It used to be a grid of popular titles you had not rated, under "Sharpen your taste", with a
 * single payoff line — "Your model sharpens on the next refresh" — that promised nothing checkable.
 * Two things were wrong with that. The catalogue's most popular titles are mostly ones you have
 * never seen, so every card is a recall test; and the ~1,300 titles you HAVE watched and never
 * scored — free labels, zero recall burden — were invisible to the one screen built to collect
 * labels.
 *
 * Now the watched-but-unrated half leads, and the payoff is a number: how many more ratings until
 * the model actually retrains, read from the same gate the nightly reconcile uses.
 */
export default function RatePicker() {
  const { currentData: data, isError, isFetching, refetch } = useGetRateSuggestionsQuery();
  const { data: sync } = useGetSyncStatusQuery();
  const [rateTitle] = useRateTitleMutation();
  // Two sets, because a tap and a saved rating are different facts. `pending` hides the card the
  // moment it is tapped (the grid must feel instant); `confirmed` is what the server accepted, and
  // only it moves the retrain counter. A failure takes the card out of `pending`, which puts it
  // back in the grid — the old page dropped the card and swallowed the error, so a failed rating
  // vanished AND advanced "N more ratings until Reel retrains".
  const [pending, setPending] = useState<Record<string, number>>({});
  const [confirmed, setConfirmed] = useState<Record<string, number>>({});
  const [failure, setFailure] = useState<string | null>(null);

  const suggestions = data ?? [];
  const remaining = suggestions.filter((t) => !(t.titleId in pending) && !(t.titleId in confirmed));
  const watched = remaining.filter((t) => t.source === "watched");
  const popular = remaining.filter((t) => t.source !== "watched");
  const count = Object.keys(confirmed).length;

  const rate = async (t: RateSuggestion, value: number) => {
    setFailure(null);
    setPending((p) => ({ ...p, [t.titleId]: value }));
    try {
      await rateTitle({ titleId: t.titleId, mediaType: t.mediaType, tmdbId: t.tmdbId, rating: value, markWatched: true }).unwrap();
      setConfirmed((c) => ({ ...c, [t.titleId]: value }));
    } catch (error: unknown) {
      // Only a refusal (4xx) is known not to have landed. A 5xx or a dropped connection may have
      // come after the rating was written, so that copy says what is known: it was not confirmed.
      setFailure(
        refusedOutright(error)
          ? `Couldn't save your ${value} for ${t.name} — it's back in the grid. Nothing was rated.`
          : `Couldn't confirm your ${value} for ${t.name} — it may not have saved, so it's back in the grid. Rate it again.`
      );
    } finally {
      setPending((p) => {
        const next = { ...p };
        delete next[t.titleId];
        return next;
      });
    }
  };

  // The ratings saved in this session are counted locally on top of the server's number: they are
  // in the database the moment the mutation resolves, and the status query is not invalidated by a
  // rating, so without this the counter would sit still while the user works.
  const retrain = sync?.retrain;
  const sinceTrain = (retrain?.newRatingsSinceTrain ?? 0) + count;
  const toGo = retrain ? Math.max(0, retrain.minNewRatings - sinceTrain) : null;

  const progress = (() => {
    if (!retrain) return null;
    if (!retrain.hasActiveModel) {
      return `${sinceTrain} ${sinceTrain === 1 ? "rating" : "ratings"} in — Reel trains your first model tonight.`;
    }
    if (toGo === 0) {
      return `That's enough — Reel retrains tonight on ${sinceTrain} new ${sinceTrain === 1 ? "rating" : "ratings"}.`;
    }
    return `${toGo} more ${toGo === 1 ? "rating" : "ratings"} and Reel retrains tonight.`;
  })();

  // Three states before the grid. `data = []` as a default used to make a failed read and a truly
  // empty pool the same blank page.
  if (!data) {
    return (
      <div className="space-y-6 pb-12">
        <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2" data-testid="rate-heading">
          <Sparkles className="h-5 w-5 text-fa-frost" />
          Rate what you&apos;ve seen
        </h1>
        {isError ? (
          <LoadFailed what="titles to rate" onRetry={() => void refetch()} retrying={isFetching} testId="rate-error" />
        ) : (
          <div className="grid grid-cols-3 sm:grid-cols-5 md:grid-cols-6 gap-4" data-testid="rate-loading">
            {Array.from({ length: 12 }, (_, i) => (
              <div key={i} className="aspect-[2/3] rounded-lg reel-shimmer" />
            ))}
          </div>
        )}
      </div>
    );
  }

  const grid = (items: RateSuggestion[], testId: string) => (
    <div className="grid grid-cols-3 sm:grid-cols-5 md:grid-cols-6 gap-4" data-testid={testId}>
      {items.map((t) => (
        <Popover key={t.titleId}>
          <PopoverTrigger asChild>
            <button className="group block text-left focus:outline-none reel-rise" data-testid="rate-card">
              <div className="relative overflow-hidden rounded-lg group-hover:ring-2 group-hover:ring-fa-frost/50 transition">
                <PosterImage path={t.posterPath} alt={t.name} size="w185" />
              </div>
              <p className="mt-1.5 fa-caption text-fa-frost-bright truncate" title={t.name}>{t.name}</p>
            </button>
          </PopoverTrigger>
          <PopoverContent align="center" className="w-auto">
            <p className="fa-overline text-fa-frost-dim mb-2">rate {t.name}</p>
            <div className="flex gap-1">
              {Array.from({ length: 10 }, (_, i) => i + 1).map((v) => (
                <button
                  key={v}
                  onClick={() => rate(t, v)}
                  className="h-9 w-7 rounded-md border border-fa-edge bg-fa-glass text-sm tabular-nums text-fa-frost hover:border-fa-frost/40 transition"
                  data-testid="rate-value"
                >
                  {v}
                </button>
              ))}
            </div>
          </PopoverContent>
        </Popover>
      ))}
    </div>
  );

  return (
    <div className="space-y-6 pb-12">
      <div>
        <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2" data-testid="rate-heading">
          <Sparkles className="h-5 w-5 text-fa-frost" />
          {watched.length > 0 ? "You watched these and never told me what you thought" : "Sharpen your taste"}
        </h1>
        {progress && (
          <p className="fa-body text-fa-frost mt-2" data-testid="retrain-progress">
            {progress}
          </p>
        )}
        {count > 0 && (
          <p className="fa-caption text-fa-frost-dim mt-1" data-testid="rate-session-count">
            {count} rated in this session
          </p>
        )}
        {/* Stays up until the next rating or a dismissal — a toast that faded would leave the user
            believing the rating had landed. */}
        {failure && (
          <div role="alert" className="mt-3 flex flex-wrap items-center gap-3 fa-card border-fa-danger/40 px-4 py-2" data-testid="rate-failure">
            <p className="fa-caption text-fa-danger flex-1 min-w-0" data-testid="rate-failure-text">{failure}</p>
            <Button variant="ghost" size="sm" onClick={() => setFailure(null)}>
              Dismiss
            </Button>
          </div>
        )}
      </div>

      {suggestions.length === 0 && (
        <p className="fa-body text-fa-frost-dim text-center py-8" data-testid="rate-empty">
          Nothing to rate right now — Reel has no unrated titles left to ask you about.
        </p>
      )}

      {watched.length > 0 && grid(watched, "rate-picker")}

      {popular.length > 0 && (
        <section className="space-y-3">
          {watched.length > 0 && (
            <h2 className="fa-section-title text-fa-frost-dim">
              Or rate anything else you remember
            </h2>
          )}
          {grid(popular, watched.length > 0 ? "rate-picker-popular" : "rate-picker")}
        </section>
      )}

      {suggestions.length > 0 && remaining.length === 0 && Object.keys(pending).length === 0 && (
        <p className="fa-body text-fa-frost-dim text-center py-8" data-testid="rate-batch-done">
          Nice — that&rsquo;s the batch.{progress ? ` ${progress}` : ""}
        </p>
      )}
    </div>
  );
}
