import { useState, type FocusEvent } from "react";

/**
 * Whether someone is on a timed toast right now, and the handlers that tell it so.
 *
 * Every timed toast in Reel holds its clock while keyboard focus is inside it or a pointer rests on
 * it (HREV4-PH-02, WCAG 2.2.1 Timing Adjustable): a keyboard or screen-reader user who has just
 * reached one of its controls must not lose it to a timer they cannot see, and a person reading it
 * with the cursor on it is plainly still deciding. One hook, so the rule cannot be honoured by one
 * toast and forgotten by its sibling — which is exactly how the verdict toast kept a never-pausing
 * nine-second clock after the undo toast learned to hold.
 *
 * Spread `holdProps` onto the toast's root element and leave the timer off while `held`. Moving
 * focus between the toast's own controls is still "on it"; only focus going elsewhere lets go.
 *
 * The state belongs to whatever mounts the toast's root, so mount that component only while the
 * toast is shown: the control holding focus disappears with the toast and no blur need ever arrive,
 * and a hold that outlived its toast would freeze the next one forever.
 */
export function useToastHold() {
  const [pointerOn, setPointerOn] = useState(false);
  const [focusIn, setFocusIn] = useState(false);
  return {
    held: pointerOn || focusIn,
    holdProps: {
      onFocus: () => setFocusIn(true),
      onBlur: (event: FocusEvent<HTMLElement>) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocusIn(false);
      },
      onPointerEnter: () => setPointerOn(true),
      onPointerLeave: () => setPointerOn(false),
    },
  };
}
