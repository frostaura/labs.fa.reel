import { useEffect, useRef } from "react";
import { useDispatch } from "react-redux";
import { api, useGetSyncStatusQuery } from "../store/api";
import { picksChangedReported, syncCompleted, syncIdle, syncProgress } from "../store/syncSlice";
import { scoresRecalibrated, type ScoreChange } from "../store/scoreOverridesSlice";
import { jobKindToSseKind, isBackgroundKind } from "../lib/syncCopy";
import type { AppDispatch } from "../store";

/**
 * Headless steady-state SSE consumer mounted once in the app shell (fa.foresight's
 * realtimeSync pattern). Two inputs keep the header pill honest:
 *   1. Seed — /api/sync/status (polled every 5s, and on tag invalidation), so a reload
 *      mid-job shows the pill immediately and it self-corrects to the authoritative
 *      active job — the endpoint prefers a primary pipeline job over a background poll.
 *   2. Live — `job-progress` events update it in place between polls; `job-completed`
 *      invalidates the Sync tag so the refetch confirms idle (or the next active job).
 *
 * A background Trakt poll (delta/reconcile) fires every few minutes and must NOT hijack the
 * pill from a primary job (onboarding, enrichment, training) the user is waiting on — so its
 * live events are ignored while a primary job is active. `onerror` is a no-op: EventSource
 * auto-reconnects and the poll is the fallback.
 */
export default function RealtimeSync() {
  const dispatch = useDispatch<AppDispatch>();
  const { data: status } = useGetSyncStatusQuery(undefined, { pollingInterval: 5000 });

  // Tracks whether the authoritative active job is a primary pipeline job, so the SSE handler
  // can drop background-poll events that would otherwise clobber its progress.
  const primaryActiveRef = useRef(false);
  // The last active model-lab job (Train/Evaluate), so its completion can refresh the Lab
  // ledger. Those two handlers publish no `job-completed` SSE frame (the others do), so the
  // only completion signal that exists today is the polled status flipping to idle.
  const labJobActiveRef = useRef(false);

  useEffect(() => {
    if (!status) return;
    const job = status.activeJob;
    if (job && (job.status === "Running" || job.status === "Pending")) {
      const kind = jobKindToSseKind(job.kind);
      primaryActiveRef.current = !isBackgroundKind(kind);
      labJobActiveRef.current = job.kind === "Train" || job.kind === "Evaluate";
      dispatch(syncProgress({ label: job.progressMessage ?? "Syncing", pct: job.progressPct, kind }));
    } else {
      primaryActiveRef.current = false;
      if (labJobActiveRef.current) {
        // A train/evaluate run just finished: pull the fresh iteration into the Lab ledger
        // without waiting for its 10s metric poll (or a reload).
        labJobActiveRef.current = false;
        dispatch(api.util.invalidateTags(["Lab"]));
      }
      dispatch(syncIdle());
    }
  }, [status, dispatch]);

  useEffect(() => {
    const source = new EventSource("/sse/pipeline");

    source.addEventListener("job-progress", (ev) => {
      try {
        const data = JSON.parse((ev as MessageEvent).data);
        const kind = typeof data.kind === "string" ? data.kind : null;
        // A background poll must not overwrite a primary job's live progress.
        if (isBackgroundKind(kind) && primaryActiveRef.current) return;
        if (!isBackgroundKind(kind)) primaryActiveRef.current = true;
        dispatch(
          syncProgress({
            label: String(data.message ?? data.kind ?? "Syncing"),
            pct: typeof data.pct === "number" ? data.pct : null,
            kind,
            current: typeof data.current === "number" ? data.current : null,
            total: typeof data.total === "number" ? data.total : null,
          })
        );
      } catch {
        // ignore malformed frame
      }
    });

    source.addEventListener("job-completed", (ev) => {
      // The status refetch (Sync tag) decides whether the pill clears or hands over to
      // the next active job.
      dispatch(api.util.invalidateTags(["Sync", "Feed", "TasteDna"]));
      // A finished train/evaluate run also stales the model-lab ledger. Today those handlers
      // publish no job-completed frame (the poll-transition above covers them); this listens
      // anyway so a backend that starts emitting them gets the instant refresh for free.
      try {
        const data = JSON.parse((ev as MessageEvent).data);
        if (data?.kind === "train" || data?.kind === "evaluate") {
          dispatch(api.util.invalidateTags(["Lab"]));
        }

        // The post-sync moment. A delta/reconcile now says what it actually did — new ratings,
        // re-rates, new watches, and whether a retrain is coming — so the pill can answer "the sync
        // happened and nothing changed" with the specifics instead of a timestamp.
        if ((data?.kind === "delta" || data?.kind === "reconcile") && typeof data.ratingsAdded === "number") {
          dispatch(
            syncCompleted({
              ratingsAdded: data.ratingsAdded,
              ratingsChanged: typeof data.ratingsChanged === "number" ? data.ratingsChanged : 0,
              watchedAdded: typeof data.watchedAdded === "number" ? data.watchedAdded : 0,
              retrainQueued: data.retrainQueued === true,
              retrainAt: typeof data.retrainAt === "string" ? data.retrainAt : null,
              // Never knowable at sync time — the rebuild is enqueued, not awaited. feed-ready
              // fills it in below.
              picksChanged: typeof data.picksChanged === "number" ? data.picksChanged : null,
            })
          );
        }
      } catch {
        // ignore malformed frame
      }
    });

    // The authoritative "your feed just changed" frame: a reactive rebuild (fired the instant new
    // watches/ratings sync in, or an in-app rating lands) mints a fresh snapshot with a new
    // generatedAt. Invalidating Feed refetches it; the generatedAt-keyed rows in Home then remount
    // and reseed with the new, still-eligible picks — so fresh recommendations lazy-load in live.
    source.addEventListener("feed-ready", (ev) => {
      dispatch(api.util.invalidateTags(["Feed", "TasteDna"]));
      // The rebuild the last sync enqueued has landed and knows how much of the page moved — the
      // one number the sync itself could not report, because it must never block on the build.
      try {
        const picksChanged = JSON.parse((ev as MessageEvent).data)?.picksChanged;
        if (typeof picksChanged === "number") {
          dispatch(picksChangedReported({ picksChanged }));
        }
      } catch {
        // ignore malformed frame
      }
    });

    /**
     * Tonight's picks are unchanged, but the model's opinion of them is not: a Preserve build
     * updated the Active snapshot's rows in place and is telling us the new numbers.
     *
     * The Feed tag is deliberately NOT invalidated. `generatedAt` has not moved (that is the whole
     * contract), so a refetch would return the same identity while the invalidation itself
     * remounted every row and discarded the scroll. Instead the RTK cache entry is patched in place
     * and the same numbers go into the shared override map that the infinite rows and Ask Reel
     * filter through — those render from local state that no cache tag can reach, and without the
     * map half the page would update and half would not.
     */
    source.addEventListener("feed-scores-updated", (ev) => {
      try {
        const data = JSON.parse((ev as MessageEvent).data);
        const changed: ScoreChange[] = Array.isArray(data?.changed) ? data.changed : [];
        if (changed.length === 0) return;

        dispatch(scoresRecalibrated({ changed }));
        dispatch(
          api.util.updateQueryData("getFeed", undefined, (draft) => {
            const byId = new Map(changed.map((c) => [c.titleId, c.predictedRating]));
            const patch = (card: { titleId: string; predictedRating: number }) => {
              const next = byId.get(card.titleId);
              if (next !== undefined) card.predictedRating = next;
            };
            draft.hero.forEach(patch);
            draft.rows.forEach((row) => row.items.forEach(patch));
          })
        );
      } catch {
        // ignore malformed frame
      }
    });

    // Floor against a feed-ready frame missed while the stream was down: the hub keeps no replay
    // buffer, so on every (re)connect refetch the feed to reconcile whatever changed in the gap.
    source.addEventListener("open", () => {
      dispatch(api.util.invalidateTags(["Feed", "Sync"]));
    });

    return () => source.close();
  }, [dispatch]);

  return null;
}
