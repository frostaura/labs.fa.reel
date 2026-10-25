import { FROM_CACHE_HEADER } from "./swContract";

/**
 * The service worker's decisions about the offline API cache and its own activation, kept free of
 * workbox and of `self` so they are unit-tested here rather than only in a browser that runs the
 * built worker (every Playwright spec blocks service workers).
 */

/** Marks a response the worker answers from the offline cache, so the page can say so (I-11). */
export async function markFromCache(cached: Response | undefined | null): Promise<Response | undefined | null> {
  if (!cached) return cached;
  const headers = new Headers(cached.headers);
  headers.set(FROM_CACHE_HEADER, "1");
  return new Response(await cached.blob(), {
    status: cached.status,
    statusText: cached.statusText,
    headers,
  });
}

/** Workbox's per-request plugin state: one object shared by a plugin's callbacks for one request. */
type PluginState = Record<string, unknown> | undefined;

/**
 * Refuses to cache an answer to a request that started before the last sign-out. The page aborts
 * its own requests on sign-out, but the worker's fetch outlives that abort: without this, account
 * A's feed landing a moment after the cache was deleted was written straight back into it, and
 * account B could be served it offline for the next hour.
 */
export function createSignOutGuard() {
  let generation = 0;
  return {
    /** The page signed out: everything already in flight belongs to the old account. */
    signedOut(): void {
      generation += 1;
    },
    requestWillFetch({ request, state }: { request: Request; state?: PluginState }): Request {
      if (state) state.signOutGeneration = generation;
      return request;
    },
    cacheWillUpdate({ response, state }: { response: Response; state?: PluginState }): Response | null {
      const startedIn = state?.signOutGeneration;
      return typeof startedIn === "number" && startedIn !== generation ? null : response;
    },
  };
}

/**
 * A cache the worker creates when it activates, so the NEXT worker can tell whether the one it is
 * replacing understands the update prompt. Bump only if the prompt protocol itself changes.
 */
export const PROMPT_AWARE_MARKER = "reel-sw-prompt-v1";

/**
 * Whether a newly installed worker should take over at once instead of waiting for the prompt.
 *
 * Normally it waits (I-3). The one exception is the first deploy of the prompt itself: tabs still
 * running the previous build registered it in auto-update mode, which never posts SKIP_WAITING, so
 * a waiting worker would sit there until every Reel tab and the installed app were fully closed —
 * and on reload the old worker would keep serving the old build. Those tabs DO reload when a new
 * worker takes control, so taking over once is exactly what they were built for. The marker says
 * whether the active worker is new enough to have shown a prompt; with no active worker at all
 * there is nothing to replace.
 */
export function shouldTakeOverOnInstall({
  hasActiveWorker,
  promptAwareMarker,
}: {
  hasActiveWorker: boolean;
  promptAwareMarker: boolean;
}): boolean {
  return hasActiveWorker && !promptAwareMarker;
}
