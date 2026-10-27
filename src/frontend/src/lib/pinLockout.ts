import { pinFailureText } from "../components/ui/save-outcome";

/**
 * What a failed settings-PIN attempt says, lockout included (WS-8 CR-6).
 *
 * Five wrong PINs in fifteen minutes and the server refuses the sixth unread: 429, the stable code
 * `pin_locked` and `retryAfterSeconds` in the body (FilterEndpoints.PinLocked). That is deliberately
 * not the wrong-PIN 423, so the screen can tell someone who may now be typing the right PIN that
 * entry is paused and for how long — rather than "Wrong PIN", which would send them guessing again.
 *
 * The body is read rather than the `Retry-After` header because RTK Query's error carries the body
 * only; the server writes the same number to both.
 */

/** A PIN check that failed for a reason other than the PIN itself: a 5xx or a dropped request. */
export const PIN_CHECK_FAILED = "Couldn't check the PIN — nothing changed. Try again.";

/**
 * Seconds until PIN entry reopens when `error` is the lockout; 0 for a lockout of unstated length;
 * null for anything else.
 */
export function pinLockedFor(error: unknown): number | null {
  const e = error as { status?: unknown; data?: unknown } | null;
  if (e?.status !== 429) return null;
  const body = e.data as { code?: unknown; retryAfterSeconds?: unknown } | null | undefined;
  if (body?.code !== "pin_locked") return null;
  const seconds = body.retryAfterSeconds;
  return typeof seconds === "number" && Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
}

/** "Try again in 14 minutes." — whole minutes, rounded up, never zero; "later" when unstated. */
function lockoutText(seconds: number): string {
  if (seconds <= 0) return "Too many wrong PINs — PIN entry is paused. Try again later.";
  const minutes = Math.max(1, Math.ceil(seconds / 60));
  return `Too many wrong PINs — PIN entry is paused. Try again in ${minutes} ${minutes === 1 ? "minute" : "minutes"}.`;
}

/**
 * The sentence for a failed PIN attempt: the lockout with its duration, the caller's own words for a
 * wrong PIN (423), the server's reason for a refused value (400), and `failed` for anything else —
 * which is never "wrong PIN", because a 500 says nothing about the PIN.
 */
export function pinAttemptText(error: unknown, wrongPin: string, failed: string = PIN_CHECK_FAILED): string {
  const locked = pinLockedFor(error);
  if (locked !== null) return lockoutText(locked);
  const status = (error as { status?: unknown } | null)?.status;
  if (status === 423 || status === 400) return pinFailureText(error, wrongPin);
  return failed;
}
