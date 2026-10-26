import { useCallback, useRef, useState, type ReactNode } from "react";
import { useDispatch, useSelector } from "react-redux";
import UndoToast from "../search/UndoToast";
import type { AppDispatch, RootState } from "../../store";
import { useUndoNotInterestedMutation, useUnrateTitleMutation, useUnsaveForLaterMutation } from "../../store/api";
import { titleRestored } from "../../store/removedTitlesSlice";
import { verdictDismissed } from "../../store/verdictSlice";
import { CardUndoContext, cardUndoCopy, type CardAction, type CardUndoAnnounce as Announce } from "../../lib/cardUndo";

interface Shown {
  id: string;
  action: CardAction;
  /** The undo itself failed; the toast now offers to try it again. */
  undoFailed: boolean;
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
 * Reuses the app's one undo toast so the look is learned once. The after-rating verdict sits in the
 * same spot, so while one is on screen this toast is lifted above it: the wrapper is `fixed` and
 * transformed, which makes it the containing block for the toast's own `fixed` box.
 */
export function CardUndoProvider({ children }: { children: ReactNode }) {
  const dispatch = useDispatch<AppDispatch>();
  const verdictShowing = useSelector((state: RootState) => state.verdict.current != null);
  const [shown, setShown] = useState<Shown | null>(null);
  const sequence = useRef(0);
  const [unsave] = useUnsaveForLaterMutation();
  const [undoNotInterested] = useUndoNotInterestedMutation();
  const [unrate] = useUnrateTitleMutation();

  const announce = useCallback<Announce>((action) => {
    sequence.current += 1;
    setShown({ id: `${action.kind}:${action.titleId}:${sequence.current}`, action, undoFailed: false });
  }, []);

  const undo = async () => {
    if (!shown) return;
    const { action } = shown;
    setShown(null);
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
      setShown({ id: `failed:${action.titleId}:${sequence.current}`, action, undoFailed: true });
    }
  };

  const copy = shown ? cardUndoCopy(shown.action, shown.undoFailed) : null;

  return (
    <CardUndoContext.Provider value={announce}>
      {children}
      {shown && copy && (
        <div
          className={`pointer-events-none fixed inset-x-0 bottom-0 z-[60] [&>*]:pointer-events-auto ${
            verdictShowing ? "-translate-y-28" : "translate-y-0"
          }`}
        >
          <UndoToast
            id={shown.id}
            message={copy.message}
            undoLabel={copy.undoLabel}
            onUndo={() => void undo()}
            onExpire={() => setShown(null)}
            testId="card-undo-toast"
          />
        </div>
      )}
    </CardUndoContext.Provider>
  );
}
