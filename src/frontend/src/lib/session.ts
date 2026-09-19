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
 * Rotates the session via `/auth/refresh`, collapsing concurrent callers onto one request.
 * Resolves true when the caller may retry, false when the session is genuinely gone (or the
 * network is): a rejected refresh is a failed refresh, never a thrown error at the call site,
 * because every caller's only sensible response to either is "give up on this attempt".
 */
export async function ensureFreshSession(): Promise<boolean> {
  refreshInFlight ??= fetch("/api/auth/refresh", { method: "POST", credentials: "include" });
  try {
    const refresh = await refreshInFlight.finally(() => {
      refreshInFlight = null;
    });
    return refresh.ok;
  } catch {
    return false;
  }
}
