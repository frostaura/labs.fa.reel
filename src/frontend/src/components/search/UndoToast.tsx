import { useEffect, useRef, useState, type FocusEvent } from "react";
import { Undo2, X } from "lucide-react";
import { Button } from "../ui/button";
import { ToastPortal } from "../ui/toast-stack";

/** Long enough to notice a thing has happened and change your mind; short enough not to camp. */
export const UNDO_WINDOW_MS = 6000;

/**
 * "That happened — [Undo]", for the two Ask Reel actions a person can take by accident.
 *
 * Round 2 filed both of them. F6-07: deleting a thread was one 26 px tap, with no dialog, no
 * inline confirm and no undo, and it takes the transcript AND the stored cards with it. F4-09:
 * saving a card removes it from the answer instantly, with nothing on screen saying that it went
 * anywhere — a grid that goes 10 → 9 under a footnote still reading 10 is indistinguishable from
 * a bug.
 *
 * Deliberately NOT the {@link VerdictToast} component, and deliberately the same shape. That one
 * is a singleton wired to a store slice for one specific event (a rating came back) and portals
 * itself out of a Radix dialog's `aria-hidden` sweep; this is a plain, local, twice-used control
 * that its caller owns the state for. Sharing the LOOK is the point — a person should not have to
 * learn two toasts — and sharing the implementation would mean a second global slice for two
 * ephemeral facts that never leave `/search`.
 *
 * The timer is the caller's contract, not a decoration: {@link onExpire} is where a deferred
 * destructive action actually commits, so it must fire exactly once per toast. Keyed on the
 * caller's own `id`, which is what makes a second action restart the clock rather than inherit
 * the first one's remaining time.
 *
 * The clock holds while someone is on the toast (HREV4-PH-02, WCAG 2.2.1 Timing Adjustable): while
 * keyboard focus is inside it or a pointer rests on it, nothing commits. A keyboard or screen-reader
 * user who has just reached "Undo" must not lose it to a timer they cannot see, and a person reading
 * the message with the cursor on it is plainly still deciding. Leaving restarts a fresh full window
 * rather than resuming the remainder, so letting go never leaves a few milliseconds to act in.
 */
export default function UndoToast({
  id,
  message,
  onUndo,
  onExpire,
  testId,
  undoLabel = "Undo",
  ms = UNDO_WINDOW_MS,
}: {
  /** Changes when the toast is about a different action; restarts the window. */
  id: string;
  message: string;
  onUndo: () => void;
  /** The window closed without an undo. For a deferred delete, this is where it commits. */
  onExpire: () => void;
  testId: string;
  undoLabel?: string;
  ms?: number;
}) {
  const [pointerOn, setPointerOn] = useState(false);
  const [focusIn, setFocusIn] = useState(false);
  const held = pointerOn || focusIn;

  // The newest closure, so a window that outlives a re-render commits what the caller means now,
  // without the closure itself being a key: callers pass a fresh one on every render, and keying on
  // it would restart the window on every keystroke elsewhere on the page — which is how a
  // six-second undo becomes an indefinite one. The `id` is the real key.
  const expire = useRef(onExpire);
  useEffect(() => {
    expire.current = onExpire;
  });

  useEffect(() => {
    if (held) return;
    const timer = window.setTimeout(() => expire.current(), ms);
    return () => window.clearTimeout(timer);
  }, [id, ms, held]);

  // Moving between Undo and Dismiss is still "on the toast"; only focus going elsewhere lets go.
  const onBlur = (event: FocusEvent<HTMLDivElement>) => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocusIn(false);
  };

  // Into the shared toast column (ui/toast-stack), so a second toast stacks above this one instead
  // of covering it.
  return (
    <ToastPortal>
      <div
        role="status"
        aria-live="polite"
        data-testid={testId}
        onFocus={() => setFocusIn(true)}
        onBlur={onBlur}
        onPointerEnter={() => setPointerOn(true)}
        onPointerLeave={() => setPointerOn(false)}
        className="flex items-center gap-3 rounded-xl border border-fa-edge bg-fa-ink/95 px-4 py-3 shadow-lg backdrop-blur-md"
      >
        <p className="fa-body flex-1 text-fa-frost-bright">{message}</p>
        <Button
          variant="chip"
          onClick={onUndo}
          data-testid={`${testId}-undo`}
          className="min-h-11 gap-1.5 border-fa-frost/30 px-4 text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10"
        >
          <Undo2 className="h-3.5 w-3.5" /> {undoLabel}
        </Button>
        {/* Dismiss commits, exactly as letting the clock run out does — the button means "yes, I
            meant it", never "cancel the thing I just asked for". */}
        <Button
          variant="ghost"
          size="icon"
          onClick={onExpire}
          aria-label="Dismiss"
          data-testid={`${testId}-dismiss`}
          className="min-h-11 min-w-11 shrink-0 text-fa-frost-dim hover:bg-transparent"
        >
          <X className="h-4 w-4" />
        </Button>
      </div>
    </ToastPortal>
  );
}
