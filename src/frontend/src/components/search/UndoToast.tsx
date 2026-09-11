import { useEffect } from "react";
import { Undo2, X } from "lucide-react";

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
  useEffect(() => {
    const timer = window.setTimeout(onExpire, ms);
    return () => window.clearTimeout(timer);
    // `onExpire` is deliberately not a dependency: callers pass a fresh closure on every render,
    // and depending on it would restart the window on every keystroke elsewhere on the page —
    // which is how a six-second undo becomes an indefinite one. The `id` is the real key.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, ms]);

  return (
    <div
      role="status"
      aria-live="polite"
      data-testid={testId}
      className="fixed inset-x-4 bottom-24 z-[60] mx-auto flex max-w-md items-center gap-3 rounded-xl border border-fa-edge bg-fa-ink/95 px-4 py-3 shadow-lg backdrop-blur-md md:bottom-6"
    >
      <p className="fa-body flex-1 text-fa-frost-bright">{message}</p>
      <button
        type="button"
        onClick={onUndo}
        data-testid={`${testId}-undo`}
        className="fa-caption inline-flex min-h-11 items-center gap-1.5 rounded-full border border-fa-frost/30 px-4 text-fa-frost-bright transition-colors hover:border-fa-frost/60 hover:bg-fa-frost/10"
      >
        <Undo2 className="h-3.5 w-3.5" /> {undoLabel}
      </button>
      {/* Dismiss commits, exactly as letting the clock run out does — the button means "yes, I
          meant it", never "cancel the thing I just asked for". */}
      <button
        type="button"
        onClick={onExpire}
        aria-label="Dismiss"
        data-testid={`${testId}-dismiss`}
        className="inline-flex min-h-11 min-w-11 shrink-0 items-center justify-center text-fa-frost-dim transition hover:text-fa-frost-bright"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
