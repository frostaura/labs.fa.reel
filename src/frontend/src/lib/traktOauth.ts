/**
 * Trakt OAuth client-side glue. The server builds the authorize URL and a signed `state`
 * (POST /api/auth/trakt/start); we keep that state in sessionStorage across the redirect
 * round-trip and require an exact match on return before exchanging the code. The server
 * independently verifies its own HMAC + expiry — this check just rejects mismatched tabs early.
 */
const STATE_KEY = "reel.trakt.oauthState";

export function rememberOauthState(state: string): void {
  sessionStorage.setItem(STATE_KEY, state);
}

export function consumeOauthState(): string | null {
  const state = sessionStorage.getItem(STATE_KEY);
  sessionStorage.removeItem(STATE_KEY);
  return state;
}

/**
 * Non-destructive read of the stored nonce. The callback screen validates during render, which
 * must stay repeatable — `consumeOauthState` deletes the key, so a second invocation (React
 * StrictMode double-renders in dev) would see null and silently skip the mismatch check.
 * Clearing stays with the effect, which runs exactly once.
 */
export function peekOauthState(): string | null {
  return sessionStorage.getItem(STATE_KEY);
}

export interface CallbackParams {
  code: string | null;
  state: string | null;
  error: string | null;
}

export function parseCallbackParams(search: string): CallbackParams {
  const params = new URLSearchParams(search);
  return {
    code: params.get("code"),
    state: params.get("state"),
    error: params.get("error"),
  };
}

/**
 * Pure verdict on a callback: the user-facing rejection reason, or null when the code is safe to
 * exchange. Kept out of the component so the rules — including the state-nonce comparison — are
 * directly testable rather than only reachable through a rendered OAuth round-trip.
 *
 * A null `expectedState` deliberately passes: sessionStorage is per-tab, so a callback landing in
 * a fresh tab has no nonce to compare against. The server independently verifies its own HMAC and
 * expiry, which is the actual security boundary; this check rejects mismatched tabs early.
 */
export function describeCallbackRejection(
  params: CallbackParams,
  expectedState: string | null
): string | null {
  if (params.error) {
    return params.error === "access_denied"
      ? "Trakt access was declined."
      : `Trakt returned: ${params.error}`;
  }
  if (!params.code || !params.state) {
    return "Missing authorization code — start again from the sign-in page.";
  }
  if (expectedState && params.state !== expectedState) {
    return "Sign-in state mismatch. Start again from the sign-in page.";
  }
  return null;
}
