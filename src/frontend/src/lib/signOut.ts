import { api } from "../store/api";
import { sessionReset } from "../store/sessionReset";
import type { AppDispatch } from "../store";
import { API_CACHE, SIGNED_OUT } from "./swContract";

/** Every key this app writes to browser storage starts with this prefix. */
export const STORAGE_PREFIX = "reel.";

/** Removes every `reel.*` key from one storage area. Returns the keys removed. */
export function clearReelKeys(storage: Pick<Storage, "length" | "key" | "removeItem">): string[] {
  const keys: string[] = [];
  for (let i = 0; i < storage.length; i += 1) {
    const key = storage.key(i);
    if (key?.startsWith(STORAGE_PREFIX)) keys.push(key);
  }
  keys.forEach((key) => storage.removeItem(key));
  return keys;
}

/**
 * Set by the shell just before it leaves for the landing page, and consumed there
 * (components/SignOutCleanup.tsx) once the signed-in pages have unmounted. Clearing the RTK cache
 * while those pages are still mounted makes every one of their queries refetch with no session —
 * and React Router runs navigations in a transition, so "navigate, then clear" in the same handler
 * did exactly that.
 */
let signOutPending = false;

export function markSignOutPending(): void {
  signOutPending = true;
}

/** True once per sign-out: the first caller after `markSignOutPending` owns the cleanup. */
export function consumeSignOutPending(): boolean {
  const pending = signOutPending;
  signOutPending = false;
  return pending;
}

/** The slice of the RTK API this cleanup drives; injectable so the order is unit-testable. */
export interface SignOutDeps {
  dispatch: AppDispatch;
  /** Requests still in flight from the signed-out session (queries and mutations). */
  inFlight: () => { abort(): void; then: PromiseLike<unknown>["then"] }[];
  /** The page's service worker, when one controls it. */
  worker: () => Pick<ServiceWorker, "postMessage"> | null;
  /** The Cache API, when there is one. */
  cacheStorage: () => Pick<CacheStorage, "delete"> | null;
  storageAreas: () => Pick<Storage, "length" | "key" | "removeItem">[];
}

const browserDeps = (dispatch: AppDispatch): SignOutDeps => ({
  dispatch,
  inFlight: () => [
    ...dispatch(api.util.getRunningQueriesThunk()),
    ...dispatch(api.util.getRunningMutationsThunk()),
  ],
  worker: () => (typeof navigator !== "undefined" && navigator.serviceWorker?.controller) || null,
  cacheStorage: () => (typeof caches !== "undefined" ? caches : null),
  storageAreas: () => {
    const areas: Pick<Storage, "length" | "key" | "removeItem">[] = [];
    for (const area of ["localStorage", "sessionStorage"] as const) {
      try {
        areas.push(window[area]);
      } catch {
        // Storage unavailable (private mode, blocked site data) — nothing of ours is in it.
      }
    }
    return areas;
  },
});

/**
 * Forgets everything this browser holds for the signed-out account (I-11): the RTK Query cache,
 * every client slice, every `reel.*` key (the Ask Reel conversation id among them), and the service
 * worker's offline API cache — the one a second account on the same device could otherwise be
 * served from when the network is slow or gone. Each step is independent; one failing (storage
 * blocked, no Cache API) never stops the others.
 *
 * The order is the point:
 * 1. Abort what the old session still has in flight and wait for it to settle — a GET that resolves
 *    after the reset would write account A's answer back into a cache that was just emptied.
 * 2. Reset the store and storage.
 * 3. Tell the worker (it deletes the API cache itself and refuses to store any response to a request
 *    that started before this moment — the worker's own fetch outlives the page's abort), then
 *    delete the cache from here too, for a page no worker controls.
 */
export async function clearClientState(dispatch: AppDispatch, deps: SignOutDeps = browserDeps(dispatch)): Promise<void> {
  const inFlight = deps.inFlight();
  inFlight.forEach((request) => request.abort());
  await Promise.allSettled(inFlight.map((request) => Promise.resolve(request)));

  deps.dispatch(api.util.resetApiState());
  deps.dispatch(sessionReset());

  for (const area of deps.storageAreas()) {
    try {
      clearReelKeys(area);
    } catch {
      // Storage refused mid-way — nothing more of ours can be removed from it.
    }
  }

  try {
    deps.worker()?.postMessage({ type: SIGNED_OUT });
  } catch {
    // No worker to tell — the cache delete below still runs.
  }
  try {
    await deps.cacheStorage()?.delete(API_CACHE);
  } catch {
    // No Cache API, or it refused — the worker's one-hour expiry is the backstop.
  }
}
