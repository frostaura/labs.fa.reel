import type { FetchBaseQueryError } from "@reduxjs/toolkit/query";
import type { SerializedError } from "@reduxjs/toolkit";

/**
 * True only for an error that means "this browser has no session": a 401 from `/auth/me` that the
 * base query's refresh could not heal. A 5xx, a timeout or a dropped connection says nothing about
 * the session, and treating them as sign-outs is how a restarting API bounced the user to the
 * landing page (H-15).
 */
export function isSignedOutError(error: FetchBaseQueryError | SerializedError | undefined): boolean {
  return error != null && "status" in error && error.status === 401;
}

/** What a protected route renders for the current state of the `auth/me` probe. */
export type SessionGate = "splash" | "signed-out" | "children" | "retry";

/**
 * The protected-route decision, in order:
 * 1. A terminal 401 signs out — EVEN with a session from an earlier answer still cached. RTK keeps
 *    `data` after a later re-check fails, so checking `data` first stranded a user whose refresh
 *    token had expired on pages that all errored.
 * 2. A session in hand outlives any other failed re-check (a 5xx or offline blip mid-session keeps
 *    the page).
 * 3. No session and a non-terminal error is the retry shell; still loading is the splash.
 */
export function sessionGate(probe: {
  isLoading: boolean;
  hasSession: boolean;
  error: FetchBaseQueryError | SerializedError | undefined;
}): SessionGate {
  if (isSignedOutError(probe.error)) return "signed-out";
  if (probe.hasSession) return "children";
  if (probe.isLoading) return "splash";
  if (probe.error) return "retry";
  return "children";
}
