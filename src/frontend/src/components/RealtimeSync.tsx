import { useEffect, useRef } from "react";
import { useDispatch, useSelector } from "react-redux";
import { api, useGetSyncStatusQuery } from "../store/api";
import { activeJobObserved, picksChangedReported, syncCompleted, syncProgress } from "../store/syncSlice";
import { scoresRecalibrated, type ScoreChange } from "../store/scoreOverridesSlice";
import { isUserVisibleWorkInFlight, shownActiveJob, statusPollInterval } from "../lib/syncPill";
import { useDocumentVisible } from "../lib/useDocumentVisible";
import type { AppDispatch } from "../store";

const selectSyncStatus = api.endpoints.getSyncStatus.select();

/**
 * Headless steady-state SSE consumer mounted once in the app shell (fa.foresight's
 * realtimeSync pattern). Two inputs keep the header pill honest:
 *   1. Authority — /api/sync/status. Whether anything is running, and what, is read from every
 *      poll answer; a live frame can never keep the pill "active" after the poll says idle (H-17).
 *      Polled every 5 s only while work the user started is in flight, otherwise every 60 s, and
 *      not at all while the tab is hidden (H-16, I-12).
 *   2. Live — `job-progress` frames carry numbers between polls; the pill applies one only while
 *      its kind matches the active job's `sseKind`. `job-completed` and `failed` invalidate the
 *      Sync tag so the next answer confirms idle, the next job, or the failure.
 * `onerror` is a no-op: EventSource auto-reconnects and the poll is the fallback.
 */
export default function RealtimeSync() {
  const dispatch = useDispatch<AppDispatch>();
  const visible = useDocumentVisible();
  // The cadence follows the latest answer, read from the cache so the hook can depend on it. RTK
  // runs the lowest interval across subscribers; this is the only one in the shell that sets one.
  const cached = useSelector(selectSyncStatus).data;
  const interval = statusPollInterval(cached);
  const { data: status, fulfilledTimeStamp, refetch } = useGetSyncStatusQuery(undefined, {
    // 0 stops polling outright while hidden; skipPollingIfUnfocused alone still lets the poll that
    // was already scheduled fire after the tab goes away.
    pollingInterval: visible ? interval : 0,
    skipPollingIfUnfocused: true,
  });

  // Coming back to a tab whose answer is older than the current cadence: ask now rather than wait
  // a whole interval with a stale pill. Re-running on an interval change is harmless — the answer
  // that changed the interval is never stale.
  useEffect(() => {
    if (visible && fulfilledTimeStamp !== undefined && Date.now() - fulfilledTimeStamp >= interval) refetch();
  }, [visible, interval, fulfilledTimeStamp, refetch]);

  // The last train/evaluate run seen in flight, so its completion can refresh the Lab ledger.
  // Those two handlers publish no `job-completed` frame, so the only completion signal that exists
  // today is the polled status moving on.
  const labJobActiveRef = useRef(false);
  const lastJobIdRef = useRef<string | null>(null);
  const job = shownActiveJob(status);
  const jobId = job?.id ?? null;
  const userVisible = isUserVisibleWorkInFlight(status);
  // Lab work is background after onboarding, so it is looked for among the recent jobs too.
  const labJobInFlight = [status?.activeJob, ...(status?.recentJobs ?? [])].some(
    (j) => (j?.kind === "Train" || j?.kind === "Evaluate") && (j.status === "Pending" || j.status === "Running")
  );

  // Keyed on primitives, not on the status object: structural sharing hands back the same object
  // for an identical answer, and an effect keyed on it never re-ran to clear a stale frame (H-17).
  useEffect(() => {
    if (status === undefined) return;
    const jobChanged = lastJobIdRef.current !== jobId;
    lastJobIdRef.current = jobId;
    dispatch(activeJobObserved({ userVisible, jobChanged }));

    if (labJobActiveRef.current && !labJobInFlight) {
      // A train/evaluate run just finished: pull the fresh iteration into the Lab ledger without
      // waiting for its 10s metric poll (or a reload).
      dispatch(api.util.invalidateTags(["Lab"]));
    }
    labJobActiveRef.current = labJobInFlight;
  }, [status, jobId, labJobInFlight, userVisible, dispatch]);

  useEffect(() => {
    const source = new EventSource("/sse/pipeline");

    source.addEventListener("job-progress", (ev) => {
      try {
        const data = JSON.parse((ev as MessageEvent).data);
        // Numbers only. The frame's free-text `message` is never shown — the pill applies this
        // frame only while its kind matches the active job's, so a background poll's frame can no
        // longer overwrite the job the user is watching.
        const kind = typeof data.kind === "string" ? data.kind : null;
        dispatch(
          syncProgress({
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

        // A finished cross-reference pass changes the band's numbers and the track record's online
        // half, neither of which is covered by the Sync/Feed refetch above. Invalidated off the
        // frame rather than polled: the pass runs for minutes and then lands all at once, so a poll
        // fast enough to catch it would spend the whole pass asking a question with no new answer.
        if (data?.kind === "xref") {
          dispatch(api.util.invalidateTags(["CrossReference", "Accuracy"]));
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

    // A job ran out of attempts. The frame's text is free text and stays off the screen; the
    // refetch brings the failure in as `lastFailures`, with a stable reason code.
    source.addEventListener("failed", () => {
      dispatch(api.util.invalidateTags(["Sync"]));
    });

    // Floor against a feed-ready frame missed while the stream was down: the hub keeps no replay
    // buffer, so on every RE-connect refetch the feed to reconcile whatever changed in the gap. The
    // first open is skipped — the page has only just fetched both, and refetching them again was
    // a duplicate request on every cold load (I-12).
    let opened = false;
    source.addEventListener("open", () => {
      if (opened) dispatch(api.util.invalidateTags(["Feed", "Sync"]));
      opened = true;
    });

    return () => source.close();
  }, [dispatch]);

  return null;
}
