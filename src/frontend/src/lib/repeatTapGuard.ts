/**
 * The second half of a double tap, recognised by where and when it lands (HREV7-DESK-01).
 *
 * A card's quick action takes the card off the rail on the tap, and the next card slides into its
 * place — with ITS same control under the finger. The second tap of a double tap then lands on that
 * control and acts on a title nobody chose. The card that took the first tap is gone, so no flag of
 * its own can catch this; the guard is shared by every card on the page and asks one question: is
 * this press where the last one was, a moment later? A press that has moved, or come after a
 * double tap's interval, is someone choosing the next card on purpose, and acts.
 *
 * Pointer presses only: a keyboard activation has no position (and the control that took it has
 * left the page with its card, so a repeated Enter reaches nothing).
 */
export interface Press {
  /** Milliseconds, from any monotonic clock. */
  at: number;
  x: number;
  y: number;
}

/** A double tap's upper interval on the common platforms (Windows' default is 500 ms). */
export const REPEAT_WINDOW_MS = 500;
/** Within a 44 px target of the first press: a finger's second tap does not land on the same pixel. */
export const REPEAT_RADIUS_PX = 22;

export function createRepeatTapGuard(windowMs = REPEAT_WINDOW_MS, radiusPx = REPEAT_RADIUS_PX) {
  let last: Press | null = null;
  /** True when `press` should act; records it either way, so a triple tap is caught as well. */
  return (press: Press): boolean => {
    const repeat =
      last !== null &&
      press.at - last.at >= 0 &&
      press.at - last.at < windowMs &&
      Math.hypot(press.x - last.x, press.y - last.y) <= radiusPx;
    last = press;
    return !repeat;
  };
}
