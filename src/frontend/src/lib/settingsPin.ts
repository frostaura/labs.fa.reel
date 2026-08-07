import { useSyncExternalStore } from "react";

/**
 * The settings-PIN unlock, shared.
 *
 * It used to be private state inside PreferencesSection, which was fine while exactly one component
 * could be locked. The conditions editor now mounts on two surfaces (Settings and a dialog on the
 * feed) alongside the PIN-management block that can change the PIN out from under them, and three
 * independent `useState(readUnlock)` copies would disagree the moment any one of them changed:
 * setting a PIN while unlocked would have re-locked the editor sitting next to the button.
 *
 * So it is one external store. The unlock lives in sessionStorage — it dies with the tab — and
 * expires on its own after {@link UNLOCK_TTL_MS}, via a timer rather than a check-on-read, so a
 * mounted editor actually re-locks instead of waiting for someone to remount it.
 */
const UNLOCK_KEY = "reel.pinUnlock";
export const UNLOCK_TTL_MS = 5 * 60 * 1000;

let cached: string | null = null;
let expiryTimer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

function scheduleExpiry(until: number): void {
  if (expiryTimer !== null) clearTimeout(expiryTimer);
  expiryTimer = setTimeout(() => {
    expiryTimer = null;
    clearUnlockedPin();
  }, Math.max(0, until - Date.now()));
}

/** Rehydrates from sessionStorage once at module load — a reload inside the TTL stays unlocked. */
function hydrate(): void {
  try {
    const raw = sessionStorage.getItem(UNLOCK_KEY);
    if (!raw) return;
    const { pin, until } = JSON.parse(raw) as { pin?: string; until?: number };
    if (typeof pin !== "string" || typeof until !== "number" || Date.now() >= until) {
      sessionStorage.removeItem(UNLOCK_KEY);
      return;
    }
    cached = pin;
    scheduleExpiry(until);
  } catch {
    // A disabled or full sessionStorage must not take the settings screen down with it.
  }
}
hydrate();

export function storeUnlockedPin(pin: string): void {
  const until = Date.now() + UNLOCK_TTL_MS;
  cached = pin;
  try {
    sessionStorage.setItem(UNLOCK_KEY, JSON.stringify({ pin, until }));
  } catch {
    // Unlock still holds in memory for this page; it just will not survive a reload.
  }
  scheduleExpiry(until);
  emit();
}

export function clearUnlockedPin(): void {
  cached = null;
  try {
    sessionStorage.removeItem(UNLOCK_KEY);
  } catch {
    // Nothing to do — the in-memory copy is already gone, which is what gates the UI.
  }
  if (expiryTimer !== null) {
    clearTimeout(expiryTimer);
    expiryTimer = null;
  }
  emit();
}

/** The snapshot. Stable by identity between changes, as useSyncExternalStore requires. */
function getUnlockedPin(): string | null {
  return cached;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The current unlock, or null when the section is locked. Re-renders every consumer on change. */
export function useUnlockedPin(): string | null {
  return useSyncExternalStore(subscribe, getUnlockedPin, getUnlockedPin);
}
