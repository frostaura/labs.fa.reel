/**
 * "Has this browser ever signed in?" flag. The session itself lives in HttpOnly cookies the JS
 * cannot see, so without this hint the landing page's `/auth/me` probe fires for every first-time
 * visitor and logs a guaranteed 401 — console noise that reads like a bug. Set on any successful
 * session establishment (OAuth callback exchange, or a `/auth/me` that answers), cleared on
 * logout. Only the LANDING page trusts its absence; protected routes always probe, so a cleared
 * localStorage can never lock a signed-in user out.
 */
const KEY = "reel.hasSession";

export function markHasSession(): void {
  try {
    localStorage.setItem(KEY, "1");
  } catch {
    // Storage unavailable (private mode) — the landing probe just stays on, as before.
  }
}

export function clearHasSession(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    // Ignore — worst case is one extra 401 probe on the next landing visit.
  }
}

export function hasSessionFlag(): boolean {
  try {
    return localStorage.getItem(KEY) === "1";
  } catch {
    return false;
  }
}
