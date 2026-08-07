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
 * So it is one external store. What it holds is a server-issued unlock TOKEN — an opaque,
 * account-bound, time-limited DataProtection payload from `/api/settings/pin/verify` — never the
 * raw PIN. An earlier version cached the PIN itself in sessionStorage, which meant anyone opening
 * devtools on a shared screen could read the secret that the lock exists to protect; the legacy
 * `{pin: ...}` shape is scrubbed on sight in {@link hydrate}. The unlock lives in sessionStorage —
 * it dies with the tab — and expires at the server-stamped `until`, via a timer rather than a
 * check-on-read, so a mounted editor actually re-locks instead of waiting for a remount.
 */
const UNLOCK_KEY = "reel.pinUnlock";

/** Fallback lifetime if the server's expiresAt is unparsable — mirrors the backend's 15-minute TTL. */
const FALLBACK_TTL_MS = 15 * 60 * 1000;

/** A live unlock: the opaque token to send as `X-Settings-Unlock`, and its expiry (epoch ms). */
export interface SettingsUnlock {
  token: string;
  until: number;
}

let cached: SettingsUnlock | null = null;
let expiryTimer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

function scheduleExpiry(until: number): void {
  if (expiryTimer !== null) clearTimeout(expiryTimer);
  expiryTimer = setTimeout(() => {
    expiryTimer = null;
    clearUnlockToken();
  }, Math.max(0, until - Date.now()));
}

/**
 * Rehydrates from sessionStorage once at module load — a reload inside the TTL stays unlocked.
 * Anything that is not a live `{token, until}` — including the legacy raw-PIN `{pin, until}`
 * shape from before the token existed — is DISCARDED and deleted, so a deploy over an old tab
 * scrubs the cached secret on first load.
 */
function hydrate(): void {
  try {
    const raw = sessionStorage.getItem(UNLOCK_KEY);
    if (!raw) return;
    const parsed = JSON.parse(raw) as { token?: unknown; until?: unknown; pin?: unknown };
    if (
      typeof parsed.token !== "string" ||
      typeof parsed.until !== "number" ||
      parsed.pin !== undefined ||
      Date.now() >= parsed.until
    ) {
      sessionStorage.removeItem(UNLOCK_KEY);
      return;
    }
    cached = { token: parsed.token, until: parsed.until };
    scheduleExpiry(parsed.until);
  } catch {
    // A disabled or full sessionStorage must not take the settings screen down with it.
  }
}
hydrate();

/** Adopts a fresh unlock from `/pin/verify`. `expiresAt` is the server's ISO-8601 stamp. */
export function storeUnlockToken(token: string, expiresAt: string): void {
  const parsed = Date.parse(expiresAt);
  const until = Number.isNaN(parsed) ? Date.now() + FALLBACK_TTL_MS : parsed;
  cached = { token, until };
  try {
    sessionStorage.setItem(UNLOCK_KEY, JSON.stringify({ token, until }));
  } catch {
    // Unlock still holds in memory for this page; it just will not survive a reload.
  }
  scheduleExpiry(until);
  emit();
}

export function clearUnlockToken(): void {
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
export function getUnlockToken(): SettingsUnlock | null {
  return cached;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The current unlock, or null when the section is locked. Re-renders every consumer on change. */
export function useUnlockToken(): SettingsUnlock | null {
  return useSyncExternalStore(subscribe, getUnlockToken, getUnlockToken);
}
