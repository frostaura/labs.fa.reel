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
