/**
 * Names the service worker (sw.ts) and the page share. Kept in a module with no imports so the
 * worker bundle and the app bundle can both use it without pulling the other's dependencies in.
 */

/** The NetworkFirst cache holding GET /api/* fallbacks. Deleted on sign-out. */
export const API_CACHE = "reel-api";

/** Set on a response the worker answered from {@link API_CACHE} instead of the network. */
export const FROM_CACHE_HEADER = "X-Reel-From-Cache";

/** The message a page posts to activate a waiting worker (what workbox-window's update sends). */
export const SKIP_WAITING = "SKIP_WAITING";
