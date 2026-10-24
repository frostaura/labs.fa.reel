import { useSyncExternalStore } from "react";
import { registerSW } from "virtual:pwa-register";

/**
 * Service-worker updates, as a prompt instead of a silent takeover (I-3).
 *
 * The worker used to `skipWaiting()` the moment a deploy installed it, deleting the previous
 * build's precached chunks out from under every open tab. It now waits; this module notices the
 * waiting worker, and the "New version — reload" toast lets the user choose the moment, so an
 * in-progress rating or Ask Reel turn is never cut off. Accepting posts SKIP_WAITING to the worker
 * (sw.ts listens for it) and reloads once the new worker controls the page.
 *
 * Production only: in dev the plugin serves a kill-switch worker instead (vite.config.ts).
 */

type Listener = () => void;

let updateReady = false;
let applyUpdate: ((reloadPage?: boolean) => Promise<void>) | null = null;
const listeners = new Set<Listener>();

const emit = () => listeners.forEach((listener) => listener());

export function startPwaUpdates(): void {
  if (import.meta.env.DEV || !("serviceWorker" in navigator)) return;

  applyUpdate = registerSW({
    immediate: true,
    onNeedRefresh() {
      updateReady = true;
      emit();
    },
    onRegisteredSW(_swUrl, registration) {
      if (!registration) return;
      // An installed PWA can sit open for days without a navigation, which is when the browser
      // would otherwise look for a new worker. Coming back to the app is a natural moment to look.
      document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") registration.update().catch(() => undefined);
      });
    },
  });
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** True once a new build's worker is installed and waiting for the user's go-ahead. */
export function usePwaUpdateReady(): boolean {
  return useSyncExternalStore(subscribe, () => updateReady, () => false);
}

/** Activates the waiting worker; the page reloads when it takes control. */
export function acceptPwaUpdate(): void {
  void applyUpdate?.(true);
}

/** Hides the prompt for this page load; the waiting worker still activates once every tab closes. */
export function dismissPwaUpdate(): void {
  updateReady = false;
  emit();
}
