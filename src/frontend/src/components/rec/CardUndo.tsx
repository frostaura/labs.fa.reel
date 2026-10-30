import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useDispatch } from "react-redux";
import UndoToast from "../search/UndoToast";
import type { AppDispatch } from "../../store";
import { useUndoNotInterestedMutation, useUnrateTitleMutation, useUnsaveForLaterMutation } from "../../store/api";
import { titleRestored } from "../../store/removedTitlesSlice";
import { verdictDismissed } from "../../store/verdictSlice";
import {
  CardUndoContext,
  cardUndoCopy,
  focusMayReturn,
  type CardAction,
  type CardUndoAnnounce as Announce,
  type CardUndoFocus,
} from "../../lib/cardUndo";

const TOAST_TEST_ID = "card-undo-toast";

/** The toast's own node. It portals into the shared column, so it is found by its id, not a ref. */
function toastNode(): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-testid="${TOAST_TEST_ID}"]`);
}

interface Shown {
  id: string;
  action: CardAction;
  /** The undo itself failed; the toast now offers to try it again. */
  undoFailed: boolean;
  /** Set when the action came from the keyboard: Undo takes focus, and gives it back on close. */
  focus?: CardUndoFocus;
}

/**
 * One undo toast per page for the three one-tap card actions — save, not for me and rate (F-19).
 * Each of them takes the card off the rail the moment the server confirms it, and before this the
 * only way back from a mis-tap was to find the title again and reverse it from its detail page.
 *
 * The toast is raised only AFTER the action succeeded (an undo for something that never happened
 * would be a lie), and the undo calls the reversal each action already had: unsave, the
 * not-interested undo, and un-rate. Un-rating also clears the play the rating logged, retracts the
 * after-rating verdict (the number it compared against is gone), and puts the card back on the
 * rails. A failed undo says so and offers to try again rather than vanishing.
 *
 * Reuses the app's one undo toast so the look is learned once. It portals into the shared toast
 * column (ui/toast-stack), which stacks it with the after-rating verdict, so this provider no longer
 * lifts it over the verdict itself (R1-42 removed that `-translate-y-28` wrapper, dead since the
 * stack landed).
 *
 * FOCUS (HREV4-PH-02, WCAG 2.4.3). An action taken from the keyboard removes the very card that held
 * focus, which used to drop it on <body> with Undo a long Tab walk away (161 Tabs on real data). For
 * those actions the toast's Undo takes focus as it appears — and while focus is on it the toast's
 * clock holds, so nothing commits under someone still deciding — and when the toast closes (Undo,
 * Dismiss or the clock) focus goes to the card that was next, else the previous one, else the rail.
 * It is handed back only if it is still on the toast or has fallen to <body>: someone who has tabbed
 * on is not pulled back. A pointer action passes no focus and moves nothing.
 */
export function CardUndoProvider({ children }: { children: ReactNode }) {
  const dispatch = useDispatch<AppDispatch>();
  const [shown, setShown] = useState<Shown | null>(null);
  const sequence = useRef(0);
  const [unsave] = useUnsaveForLaterMutation();
  const [undoNotInterested] = useUndoNotInterestedMutation();
  const [unrate] = useUnrateTitleMutation();

  const announce = useCallback<Announce>((action, focus) => {
    sequence.current += 1;
    setShown({ id: `${action.kind}:${action.titleId}:${sequence.current}`, action, undoFailed: false, focus });
  }, []);

  // A keyboard action's toast (and a failed undo's "Try again") takes focus as it mounts — one frame
  // later, not in this effect. The toast's column is attached by its own layout effect, and under
  // React's StrictMode a newly mounted toast's effects are torn down and run again AFTER this
  // parent effect: the teardown detaches the column, which drops focus to <body> in every browser,
  // and Chromium's Tab then happened to land back on Undo while WebKit's (which skips buttons) did
  // not. By the next frame the column is attached for good.
  const focusId = shown?.focus ? shown.id : null;
  useEffect(() => {
    if (!focusId) return;
    const frame = requestAnimationFrame(() => {
      toastNode()?.querySelector<HTMLElement>(`[data-testid="${TOAST_TEST_ID}-undo"]`)?.focus();
    });
    return () => cancelAnimationFrame(frame);
  }, [focusId]);

  /** Closes the toast, first handing focus back when it is the toast's to give. */
  const close = (focus: CardUndoFocus | undefined) => {
    if (focus && focusMayReturn(document.activeElement, toastNode())) focus.returnTo()?.focus();
    setShown(null);
  };

  const undo = async () => {
    if (!shown) return;
    const { action, focus } = shown;
    close(focus);
    const ref = { titleId: action.titleId, mediaType: action.mediaType, tmdbId: action.tmdbId };
    try {
      if (action.kind === "save") {
        await unsave(ref).unwrap();
      } else if (action.kind === "dismiss") {
        await undoNotInterested(ref).unwrap();
      } else {
        await unrate({ mediaType: action.mediaType, tmdbId: action.tmdbId }).unwrap();
        dispatch(titleRestored({ titleId: action.titleId }));
        dispatch(verdictDismissed());
      }
    } catch {
      sequence.current += 1;
      setShown({ id: `failed:${action.titleId}:${sequence.current}`, action, undoFailed: true, focus });
    }
  };

  const copy = shown ? cardUndoCopy(shown.action, shown.undoFailed) : null;

  return (
    <CardUndoContext.Provider value={announce}>
      {children}
      {shown && copy && (
        <UndoToast
          id={shown.id}
          message={copy.message}
          undoLabel={copy.undoLabel}
          onUndo={() => void undo()}
          onExpire={() => close(shown.focus)}
          testId={TOAST_TEST_ID}
        />
      )}
    </CardUndoContext.Provider>
  );
}
