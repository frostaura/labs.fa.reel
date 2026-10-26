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
 * How long after `beforeunload` a failed import is taken to be the departure's doing, not a
 * deploy's. Long enough for a slow navigation to commit; a leave the user cancels (the unsaved-draft
 * prompt) holds back the automatic reload for this long, and the boundary's manual reload remains.
 */
export const LEAVING_WINDOW_MS = 10_000;

let leavingSince: number | null = null;
if (typeof window !== "undefined") {
  window.addEventListener("beforeunload", () => {
    leavingSince = Date.now();
  });
  // Back in from the back/forward cache: this document is live again.
  window.addEventListener("pageshow", () => {
    leavingSince = null;
  });
}

/**
 * True while the document is on its way out. A navigation away cancels the old document's
 * in-flight lazy-chunk imports, and WebKit rejects each with "Importing a module script failed" —
 * the same words as a chunk a redeploy removed. Reloading then cancels the navigation the user
 * asked for and puts them back where they were (IREV3S-03's last WebKit flake: `page.goto("/search")`
 * "interrupted by another navigation" to the page it was leaving).
 */
export function isLeaving(now: number = Date.now()): boolean {
  return leavingSince != null && now - leavingSince < LEAVING_WINDOW_MS;
}

/**
 * Reloads the page unless it already reloaded for this reason within {@link RELOAD_GUARD_MS}, or
 * the document is leaving anyway ({@link isLeaving}). Returns true when a reload was started.
 * Storage failures (private mode) fail closed — no reload — because an unguarded reload is a
 * potential loop.
 */
export function reloadOnceForNewBuild(
  now: number = Date.now(),
  reload: () => void = () => window.location.reload(),
  leaving: boolean = isLeaving(now)
): boolean {
  if (leaving) return false;
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
