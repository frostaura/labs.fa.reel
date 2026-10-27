import type { LensPayload } from "../store/feedTypes";

/**
 * The lens is off the read path (WS-1 task 7): `GET /titles/{type}/{id}/lens` answers from storage
 * and, when the note is not current, queues the lens pass and says so with `pending`. A modal that
 * is already open would never see the note the pass writes, so it re-asks — a bounded number of
 * times, because a pass can be refused or fail and the page must not poll forever for it.
 */
export const LENS_POLL_MS = 5_000;
export const LENS_POLL_LIMIT = 8;

type LensState = Pick<LensPayload, "source" | "pending">;

/** The polling interval for the lens query: `LENS_POLL_MS` while a pass is queued for a note that is not current, else 0. */
export function lensPollInterval(data: LensState | undefined, pollsDone: number): number {
  if (!data?.pending || data.source === "fresh") return 0;
  return pollsDone < LENS_POLL_LIMIT ? LENS_POLL_MS : 0;
}

/**
 * The one line the block says while it waits. No time is promised: the pass runs when the job
 * runner reaches it. Once the bounded wait is spent the line goes — the page then shows what it has
 * (the stale note, or the why-line and synopsis), exactly as it would have without the pass.
 */
export function lensPendingNote(data: LensState | undefined, pollsDone: number): string | null {
  if (lensPollInterval(data, pollsDone) === 0) return null;
  return data?.source === "stale"
    ? "A rewrite against your current taste is on its way."
    : "Your note for this title is being written.";
}
