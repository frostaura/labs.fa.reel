import { useEffect, useRef, useState } from "react";
import { ExternalLink, ListChecks, RotateCw } from "lucide-react";
import { skipToken } from "@reduxjs/toolkit/query/react";
import {
  useGetFiltersQuery,
  useGetProvidersQuery,
  useGetRecentlyWatchedQuery,
  useGetSavedQuery,
  useUnsaveForLaterMutation,
} from "../store/api";
import SavedSortControl from "../components/saved/SavedSortControl";
import SavedQueueCard from "../components/saved/SavedQueueCard";
import WatchTonightCard from "../components/saved/WatchTonightCard";
import PostWatchStrip from "../components/saved/PostWatchStrip";
import PickConditionsButton from "../components/rec/PickConditionsButton";
import { Button } from "../components/ui/button";
import { formatRuntime } from "../lib/format";
import { useSettledGate } from "../lib/useSettledGate";
import { advancedPinLine, heldOutLine, MANAGED_LIST_TOOLTIP, pinnedPick, queueBelowPin, SAVED_INTRO, type SavedSort } from "../lib/savedQueue";

/**
 * How long the shelf waits for the reads that change its height (the pinned pick's where-to-watch,
 * the post-watch prompt) before drawing anyway; see `useSettledGate`.
 */
const REVEAL_CAP_MS = 1500;

/**
 * The shelf's placeholder: the Watch tonight card's slot, the caption line under it and the queue's
 * columns, so what lands, lands where its placeholders stood (HREV5-DESK-01).
 */
function SavedSkeleton() {
  return (
    <div className="space-y-6" aria-busy="true" data-testid="saved-loading">
      <div className="fa-card p-4 sm:p-5" aria-hidden>
        <div className="mb-3 h-4 w-28 rounded reel-shimmer bg-fa-ink-3" />
        <div className="flex flex-col sm:flex-row gap-4">
          <div className="w-32 shrink-0">
            <div className="reel-poster reel-shimmer" />
          </div>
          <div className="min-w-0 flex-1 space-y-3">
            <div className="h-6 w-1/2 rounded reel-shimmer bg-fa-ink-3" />
            <div className="h-4 w-1/3 rounded reel-shimmer bg-fa-ink-3" />
            <div className="h-8 w-full rounded reel-shimmer bg-fa-ink-3" />
            <div className="h-40 w-full rounded-lg reel-shimmer bg-fa-ink-3" />
            <div className="h-11 w-40 rounded-lg reel-shimmer bg-fa-ink-3" />
          </div>
        </div>
      </div>
      <div className="h-4 w-56 rounded reel-shimmer bg-fa-ink-3" aria-hidden />
      {/* Same columns as the queue (G-21: 4 at md, 6 at lg). */}
      <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4" aria-hidden>
        {Array.from({ length: 6 }).map((_, i) => (
          <div key={i} className="reel-poster reel-shimmer" />
        ))}
      </div>
    </div>
  );
}

/**
 * The queue. Not a bookmark folder: an ordered answer to "what do I put on tonight", with the
 * pinned pick at the top and every card carrying the three facts the decision needs — how good it
 * is for you (with its error band), how long it is, and whether you can watch it right now.
 */
export default function Saved() {
  const [sort, setSort] = useState<SavedSort>("tonight");
  const { data, isLoading, isError, isFetching, refetch } = useGetSavedQuery({ sort });
  const [unsave] = useUnsaveForLaterMutation();
  // "Not tonight" is session-scoped and client-side: it advances to the next candidate and records
  // nothing against the title. Reset whenever the shelf itself changes underneath it.
  const [skipCount, setSkipCount] = useState(0);
  const [conditionsOpen, setConditionsOpen] = useState(false);

  // The scoring backfill is budgeted per request, so a queue longer than the budget arrives with
  // some cards unscored. Top up while the number is FALLING and stop the moment it stalls — a title
  // with no feature vector never gets a score, and polling it forever would be a spinner that
  // never resolves. Bounded twice over: by progress and by a hard round cap.
  const remaining = data?.unscoredRemaining ?? 0;
  const lastRemaining = useRef<number | null>(null);
  const topUps = useRef(0);
  useEffect(() => {
    if (remaining <= 0) {
      lastRemaining.current = null;
      topUps.current = 0;
      return;
    }

    const progressed = lastRemaining.current == null || remaining < lastRemaining.current;
    if (!progressed || topUps.current >= 6) return;

    lastRemaining.current = remaining;
    topUps.current += 1;
    const timer = setTimeout(() => void refetch(), 400);
    return () => clearTimeout(timer);
  }, [remaining, refetch]);

  const items = data?.items ?? [];
  const budget = data?.eveningBudgetMinutes ?? 150;
  const band = data?.expectedAbsoluteError ?? null;
  const pinned = pinnedPick(items, budget, skipCount);
  const rest = queueBelowPin(items, pinned);
  const heldOut = heldOutLine(data?.heldOutByRules, items.length);
  // The line on the pin (HREV1-D05). The server's own pin carries its reason; a pin "Not tonight"
  // advanced to gets the client's, held to the same rule — under the bar, it says so first. The bar
  // is the server's pin bar when it sends one; until then the account's For-you minimum, read only
  // once the user has advanced (so a Saved visit that never skips adds no conditions read), and the
  // line waits for it rather than flash an unqualified pick first.
  const needsBar = skipCount > 0 && pinned != null && !pinned.watchTonightReason && data?.pinBar == null;
  const { data: filters, isLoading: barLoading } = useGetFiltersQuery(undefined, { skip: !needsBar });
  const pinReason = !pinned
    ? null
    : pinned.watchTonightReason
      ? pinned.watchTonightReason
      : skipCount > 0 && !(needsBar && barLoading)
        ? advancedPinLine(pinned, {
            bar: data?.pinBar ?? filters?.minPredictedRating ?? null,
            expectedAbsoluteError: band,
            eveningBudgetMinutes: budget,
          })
        : null;
  // The shelf is drawn once, whole (HREV5-DESK-01). Drawn the moment /saved answered, the pinned
  // pick's where-to-watch then grew from four shimmer tiles into a wall of chips and pushed the
  // "Started watching" row, the held-out line and the whole queue down 224 px: CLS 0.168 at 834 px,
  // 0.053 at 1440, on every cold load. The post-watch prompt above the shelf can arrive late the
  // same way. Their reads are the same ones the components make (RTK dedupes them), so holding the
  // reveal costs no request; the cap keeps a hung provider read from holding the queue.
  const pinProviders = useGetProvidersQuery(
    pinned?.tmdbId != null ? { mediaType: pinned.mediaType.toLowerCase(), tmdbId: pinned.tmdbId } : skipToken
  );
  const { isLoading: recentLoading } = useGetRecentlyWatchedQuery();
  const pinSettled = pinned?.tmdbId == null || !(pinProviders.isLoading || pinProviders.isUninitialized);
  const revealed = useSettledGate(!isLoading && !recentLoading && pinSettled, REVEAL_CAP_MS);

  const candidateCount = items.filter(
    (i) => i.availableNow && i.predictedRating != null && i.runtimeMinutes != null && i.runtimeMinutes <= budget
  ).length;

  return (
    <div className="space-y-6">
      {/* min-h-11: the Trakt list button (44 px) arrives with the shelf; the row is that tall from
          the first paint, so its arrival moves nothing. */}
      <div className="flex min-h-11 items-center justify-between gap-4 flex-wrap">
        <h1 className="text-2xl font-light text-fa-frost-bright">Saved</h1>
        {data?.managedListUrl && (
          <a
            href={data.managedListUrl}
            target="_blank"
            rel="noreferrer"
            className="fa-button"
            title={MANAGED_LIST_TOOLTIP}
          >
            <ListChecks className="h-4 w-4" />
            Reel — Up Next on Trakt
            <ExternalLink className="h-3.5 w-3.5" />
          </a>
        )}
      </div>

      <div className="flex items-end justify-between gap-4 flex-wrap">
        <p className="fa-caption text-3 max-w-xl">
          {SAVED_INTRO.before} <span className="text-fa-frost">{SAVED_INTRO.listName}</span> {SAVED_INTRO.after}
        </p>
        <SavedSortControl
          value={sort}
          onChange={(next) => {
            setSort(next);
            setSkipCount(0);
          }}
        />
      </div>

      {/* Loop-closing prompts sit above the queue on both surfaces that can carry them. Here it is
          drawn with the shelf, in the slot the skeleton held, so a prompt that lands late pushes
          neither the sort row nor the queue down (HREV5-DESK-01). */}
      {revealed && <PostWatchStrip />}

      {/* Three states, not two (G-15): a failed read used to fall through `data?.items ?? []` into
          "Nothing in the queue yet", telling someone with 34 saves that they had none. */}
      {isError && !data ? (
        <div className="fa-card p-8 text-center space-y-3" role="alert" data-testid="saved-error">
          <p className="fa-body text-fa-frost">Couldn&apos;t load your queue — nothing has changed on it.</p>
          <Button
            onClick={() => void refetch()}
            disabled={isFetching}
            className="min-h-11 mx-auto"
            data-testid="saved-retry"
          >
            <RotateCw className="h-4 w-4" aria-hidden />
            {isFetching ? "Retrying…" : "Retry"}
          </Button>
        </div>
      ) : isLoading || !revealed ? (
        <SavedSkeleton />
      ) : items.length === 0 && heldOut ? (
        // Every save is held out by the conditions: the shelf is full, not empty, and says so.
        <div className="fa-card p-8 text-center space-y-3" data-testid="saved-all-held-out">
          <p className="fa-body text-fa-frost">{heldOut}</p>
          <p className="fa-caption text-3">
            Your conditions apply everywhere, Saved included. Change them to see these saves again.
          </p>
          <Button onClick={() => setConditionsOpen(true)} className="min-h-11 mx-auto" data-testid="saved-held-out-change">
            Change conditions
          </Button>
        </div>
      ) : items.length === 0 ? (
        <div className="fa-card p-8 text-center" data-testid="saved-empty">
          <p className="fa-body text-3">
            Nothing in the queue yet — hit “Save for later” on any pick and it lands here and on your
            Trakt list.
          </p>
          <p className="fa-caption text-3 mt-2">
            Once there are a few, Reel pins the one worth putting on tonight.
          </p>
        </div>
      ) : (
        <>
          {/* A refresh that failed over a shelf already on screen: keep the shelf, say it may be
              stale, offer the retry. */}
          {isError && (
            <div className="fa-card p-3 flex items-center justify-between gap-3" role="alert" data-testid="saved-stale">
              <p className="fa-caption text-fa-frost">Couldn&apos;t refresh — this is the queue as it last loaded.</p>
              <Button onClick={() => void refetch()} disabled={isFetching} className="min-h-11">
                <RotateCw className="h-4 w-4" aria-hidden />
                Retry
              </Button>
            </div>
          )}
          {pinned ? (
            <WatchTonightCard
              item={pinned}
              reason={pinReason}
              advanced={skipCount > 0 && !pinned.watchTonight}
              expectedAbsoluteError={band}
              hasNext={skipCount + 1 < candidateCount}
              onNotTonight={() => setSkipCount((n) => n + 1)}
            />
          ) : (
            // No pin is a real answer, and the shelf says which rule could not be met rather than
            // relaxing one and pinning something the user cannot actually watch tonight.
            <div className="fa-card p-4" data-testid="watch-tonight-empty">
              <p className="fa-overline text-3 mb-1">watch tonight</p>
              <p className="fa-body text-3">
                {skipCount > 0
                  ? "That's everything in the queue that's streaming now and fits tonight."
                  : `Nothing in your queue is streaming right now and under ${formatRuntime(budget)}.`}
              </p>
            </div>
          )}

          {remaining > 0 && (
            <p className="fa-caption text-3" data-testid="saved-unscored">
              Scoring {remaining} more {remaining === 1 ? "save" : "saves"} — they’ll get their number
              shortly.
            </p>
          )}

          {heldOut && (
            <p className="fa-caption text-3 flex flex-wrap items-center gap-x-1" data-testid="saved-held-out">
              {heldOut}
              <Button
                variant="link"
                onClick={() => setConditionsOpen(true)}
                // `coarse:min-h-11`, not `min-h-11`: the link variant carries `coarse:min-h-0` (WCAG's
                // inline-text carve-out), which outranks a plain min-h-11 in the built CSS — this
                // measured 101×16 on touch. Same modifier, so tailwind-merge drops the variant's (PH-07).
                className="h-auto p-0 text-xs font-normal text-fa-frost underline underline-offset-2 coarse:min-h-11"
                data-testid="saved-held-out-change"
              >
                Change conditions
              </Button>
            </p>
          )}

          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
            {rest.map((item) => (
              <SavedQueueCard
                key={item.titleId}
                item={item}
                expectedAbsoluteError={band}
                onUnsave={(target) =>
                  unsave({
                    titleId: target.titleId,
                    mediaType: target.mediaType.toLowerCase(),
                    tmdbId: target.tmdbId!,
                  })
                }
              />
            ))}
          </div>
        </>
      )}

      {/* The same conditions dialog Home opens; a save re-reads the shelf. Mounted only while open,
          so the shelf itself never adds a conditions read to the page. */}
      {conditionsOpen && (
        <PickConditionsButton
          trigger={false}
          open
          onOpenChange={setConditionsOpen}
          onSaved={() => void refetch()}
        />
      )}
    </div>
  );
}
