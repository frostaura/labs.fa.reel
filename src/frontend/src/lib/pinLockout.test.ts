import { describe, expect, it } from "vitest";
import { pinAttemptText, pinLockedFor } from "./pinLockout";

/**
 * What a PIN attempt that failed says (WS-8 CR-6). Five wrong PINs in fifteen minutes and the server
 * answers 429 with the stable code `pin_locked` and how long the pause lasts — distinct from a wrong
 * PIN's 423 precisely so the screen can say "try again in N minutes" instead of "wrong PIN" to
 * someone who may well be typing the right one now.
 */
const locked = (seconds: number) => ({
  status: 429,
  data: { error: "Too many wrong PINs. PIN entry is paused for this account.", code: "pin_locked", retryAfterSeconds: seconds },
});

describe("pinLockedFor", () => {
  it("reads the pause from a pin_locked 429", () => {
    expect(pinLockedFor(locked(840))).toBe(840);
  });

  it("is null for anything that is not the lockout", () => {
    expect(pinLockedFor({ status: 423 })).toBeNull();
    expect(pinLockedFor({ status: 429, data: { code: "ask_rate_minute", retryAfterSeconds: 20 } })).toBeNull();
    expect(pinLockedFor({ status: 500 })).toBeNull();
    expect(pinLockedFor(null)).toBeNull();
  });

  it("treats a lockout with no usable duration as a lockout of unknown length", () => {
    expect(pinLockedFor({ status: 429, data: { code: "pin_locked" } })).toBe(0);
    expect(pinLockedFor({ status: 429, data: { code: "pin_locked", retryAfterSeconds: "soon" } })).toBe(0);
  });
});

describe("pinAttemptText", () => {
  const WRONG = "Wrong PIN";

  it("names the pause in whole minutes, rounded up", () => {
    expect(pinAttemptText(locked(840), WRONG)).toBe("Too many wrong PINs — PIN entry is paused. Try again in 14 minutes.");
    expect(pinAttemptText(locked(61), WRONG)).toBe("Too many wrong PINs — PIN entry is paused. Try again in 2 minutes.");
  });

  it("says one minute, not '1 minutes', and never zero", () => {
    expect(pinAttemptText(locked(60), WRONG)).toBe("Too many wrong PINs — PIN entry is paused. Try again in 1 minute.");
    expect(pinAttemptText(locked(5), WRONG)).toBe("Too many wrong PINs — PIN entry is paused. Try again in 1 minute.");
  });

  it("does not invent a duration the server did not give", () => {
    expect(pinAttemptText({ status: 429, data: { code: "pin_locked" } }, WRONG)).toBe(
      "Too many wrong PINs — PIN entry is paused. Try again later.",
    );
  });

  it("keeps the caller's sentence for a wrong PIN", () => {
    expect(pinAttemptText({ status: 423 }, WRONG)).toBe(WRONG);
  });

  it("never calls a failed request a wrong PIN", () => {
    expect(pinAttemptText({ status: 500 }, WRONG)).toBe("Couldn't check the PIN — nothing changed. Try again.");
    expect(pinAttemptText({ status: "FETCH_ERROR", error: "TypeError: Failed to fetch" }, WRONG)).toBe(
      "Couldn't check the PIN — nothing changed. Try again.",
    );
  });

  it("passes a 400's own reason through", () => {
    expect(pinAttemptText({ status: 400, data: { error: "PIN must be 4–8 digits." } }, WRONG)).toBe("PIN must be 4–8 digits.");
  });
});
