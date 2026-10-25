/**
 * Recovery from a deploy that happened under an open tab (I-3).
 *
 * A tab running the previous build asks for a lazy chunk by its old hash; after a redeploy that
 * file is gone, the dynamic import fails, and — with nothing handling it — the route went blank.
 * The fix is one full reload, which fetches the new build's index and chunk names. One, not a
 * loop: if the reload does not cure it (the network is down, or the server is broken), the error
 * reaches the route boundary, which says so and offers a manual reload.
 */

const KEY = "reel.reloadedForNewBuildAt";

/** A second failure within this window means the reload did not help; stop reloading. */
export const RELOAD_GUARD_MS = 60_000;

/** Messages browsers use for a failed dynamic import (Chromium, WebKit, Firefox). */
const CHUNK_LOAD_ERROR =
  /Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module|Unable to preload CSS/i;

export function isChunkLoadError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  return CHUNK_LOAD_ERROR.test(message);
}

/**
 * Reloads the page unless it already reloaded for this reason within {@link RELOAD_GUARD_MS}.
 * Returns true when a reload was started. Storage failures (private mode) fail closed — no reload —
 * because an unguarded reload is a potential loop.
 */
export function reloadOnceForNewBuild(
  now: number = Date.now(),
  reload: () => void = () => window.location.reload()
): boolean {
  try {
    const stored = sessionStorage.getItem(KEY);
    const last = stored == null ? null : Number(stored);
    if (last != null && Number.isFinite(last) && now - last < RELOAD_GUARD_MS) return false;
    sessionStorage.setItem(KEY, String(now));
  } catch {
    return false;
  }
  reload();
  return true;
}
