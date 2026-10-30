import { useEffect, useRef, useState } from "react";
import { INITIAL_RAIL_PAGING, nextRailPaging, type RailPaging } from "../components/rows/railPaging";

/**
 * A page-level read that failed, retried on the rails' schedule (HREV8-DESK-02).
 *
 * Home's genre list, Continue watching, the weekly digest and the saved conditions are each one
 * RTK Query read with no retry of its own. One 503 left the section absent — no error, no Retry and
 * no second request — so a failure read exactly like "nothing here": eight genre rails gone, a
 * Continue watching row gone, a conditions line that said none were saved while the server applied
 * twelve. An empty collection is not an answer.
 *
 * So a failed read follows the rail rule (`railPaging.ts`, O-7): automatic retries after 1, 2 and
 * 4 s, then it stops and waits for the user's Retry, which is one more request. The state machine
 * is the rails' own, so the two can never drift apart.
 */

/** What the page says about the read: nothing, "trying again", or "couldn't load · Retry". */
export type ReadRetryPhase = "ok" | "retrying" | "failed";

/** The slice of an RTK Query result this needs. */
export interface ReadState {
  isError: boolean;
  isFetching: boolean;
  isSuccess: boolean;
  refetch: () => unknown;
}

export interface ReadRetry {
  phase: ReadRetryPhase;
  /** The user's Retry: one request, straight back to `failed` if it fails too. */
  retry: () => void;
}

/** The page's phase for a read in the rails' paging state. */
export function readRetryPhase(paging: RailPaging): ReadRetryPhase {
  if (paging.phase === "failed") return "failed";
  return paging.failures > 0 ? "retrying" : "ok";
}

/**
 * Use once per read — the component that owns the section — since every call schedules its own
 * retries. A read that never failed costs nothing here.
 */
export function useReadRetry({ isError, isFetching, isSuccess, refetch }: ReadState): ReadRetry {
  const [paging, setPaging] = useState<RailPaging>(INITIAL_RAIL_PAGING);
  // Each settled outcome is counted once, on the render that first sees it. Adjusted during render
  // (React's alternative to a syncing effect), as Home does with its seed key.
  const settled = isFetching ? null : isError ? "error" : isSuccess ? "success" : null;
  const [seen, setSeen] = useState<typeof settled>(null);
  if (settled !== seen) {
    setSeen(settled);
    if (settled === "error") setPaging((p) => nextRailPaging(p, { type: "failure" }));
    if (settled === "success") setPaging(INITIAL_RAIL_PAGING);
  }

  // The latest refetch, so a new function identity never restarts a backoff already counting down.
  const refetchRef = useRef(refetch);
  useEffect(() => {
    refetchRef.current = refetch;
  }, [refetch]);

  useEffect(() => {
    if (paging.phase !== "backoff" || paging.retryInMs === null) return;
    const timer = setTimeout(() => {
      setPaging((p) => nextRailPaging(p, { type: "start" }));
      refetchRef.current();
    }, paging.retryInMs);
    return () => clearTimeout(timer);
  }, [paging]);

  return {
    phase: readRetryPhase(paging),
    retry: () => {
      if (paging.phase !== "failed") return;
      setPaging((p) => nextRailPaging(p, { type: "retry" }));
      refetch();
    },
  };
}
