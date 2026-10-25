/**
 * Session refresh, shared by every transport in the app.
 *
 * Auth rides in HttpOnly cookies (`reel_at` / `reel_rt`) and the access cookie lives 15 minutes,
 * so any long-lived surface will meet a 401 eventually. This used to live inside
 * `baseQueryWithReauth`, which meant RTK Query healed itself and the two raw `fetch` callers —
 * the Ask Reel POST-SSE stream and the `/conversations/latest` bootstrap — did not: a 401 there
 * ended the conversation rather than rotating the session.
 *
 * The single-flight latch moved WITH the call deliberately. A second copy of `refreshInFlight`
 * anywhere else silently defeats it: a page that fires an RTK query and an Ask Reel turn at the
 * same moment would then POST `/auth/refresh` twice, and the loser of that race rotates the
 * refresh cookie out from under the winner. One latch, one module, imported everywhere.
 */

let refreshInFlight: Promise<Response> | null = null;

/**
 * What a refresh attempt established about the session:
 * - `refreshed` — new cookies are set; the caller may retry.
 * - `rejected` — the server refused the refresh token (401/403): the session is genuinely gone.
 * - `unavailable` — the refresh never got an answer on its merits (5xx, 429, a dropped connection).
 *   This says nothing about the session. An expired access cookie is rejected statelessly, but the
 *   refresh needs the database, so a Postgres restart turns every reload into a 401 followed by an
 *   `unavailable` refresh — treating that as signed out bounced the user to the landing page (H-15).
 */
export type RefreshOutcome =
  | { outcome: "refreshed" }
  | { outcome: "rejected"; status: number }
  | { outcome: "unavailable"; status: number | null };

/** The statuses that mean "this refresh token is no good", as opposed to "the server is not well". */
const REJECTED = new Set([401, 403]);

/** Rotates the session via `/auth/refresh`, collapsing concurrent callers onto one request. */
export async function refreshSession(): Promise<RefreshOutcome> {
  refreshInFlight ??= fetch("/api/auth/refresh", { method: "POST", credentials: "include" });
  try {
    const refresh = await refreshInFlight.finally(() => {
      refreshInFlight = null;
    });
    if (refresh.ok) return { outcome: "refreshed" };
    return REJECTED.has(refresh.status)
      ? { outcome: "rejected", status: refresh.status }
      : { outcome: "unavailable", status: refresh.status };
  } catch {
    return { outcome: "unavailable", status: null };
  }
}

/**
 * True when the caller may retry. For callers whose only response to a failed refresh is "give up
 * on this attempt" (the Ask Reel stream); anything that decides whether the user is signed out
 * reads {@link refreshSession} instead, because "the session is gone" and "the server did not
 * answer" are different outcomes there.
 */
export async function ensureFreshSession(): Promise<boolean> {
  return (await refreshSession()).outcome === "refreshed";
}
