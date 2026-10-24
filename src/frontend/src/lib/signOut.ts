import { api } from "../store/api";
import { sessionReset } from "../store/sessionReset";
import type { AppDispatch } from "../store";
import { API_CACHE } from "./swContract";

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
 * Forgets everything this browser holds for the signed-out account (I-11): the RTK Query cache,
 * every client slice, every `reel.*` key (the Ask Reel conversation id among them), and the service
 * worker's offline API cache — the one a second account on the same device could otherwise be
 * served from when the network is slow or gone. Each step is independent; one failing (storage
 * blocked, no Cache API) never stops the others.
 */
export async function clearClientState(dispatch: AppDispatch): Promise<void> {
  dispatch(api.util.resetApiState());
  dispatch(sessionReset());

  for (const area of ["localStorage", "sessionStorage"] as const) {
    try {
      clearReelKeys(window[area]);
    } catch {
      // Storage unavailable (private mode, blocked site data) — nothing of ours is in it.
    }
  }

  try {
    if ("caches" in window) await caches.delete(API_CACHE);
  } catch {
    // No Cache API, or it refused — the worker's one-hour expiry is the backstop.
  }
}
