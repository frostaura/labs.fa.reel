import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { Link } from "react-router";
import { X } from "lucide-react";
import type { AppDispatch, RootState } from "../store";
import { verdictDismissed, type ShownVerdict } from "../store/verdictSlice";
import { verdictBand, verdictCopy } from "../lib/verdictCopy";
import { Button } from "./ui/button";
import { ToastPortal } from "./ui/toast-stack";
import { useToastHold } from "./ui/use-toast-hold";

/** Long enough to read a two-clause sentence, short enough not to sit on top of the page. */
export const DISMISS_AFTER_MS = 9000;

/**
 * The after-rating moment: "Reel said 8.4 · you said 5 — noted, that goes into your next retrain".
 *
 * Mounted ONCE, in the layout, and driven by the store — so every rating path raises it and none of
 * them has to know it exists. The copy itself lives in `lib/verdictCopy`, tested there, and this
 * component only decides where it sits and how long it stays.
 *
 * <p>It is deliberately not styled as a success message. On the live data most of these are the
 * product being wrong by two or three points, and dressing a miss in the same green as a hit is how
 * a scorecard stops meaning anything. Over-promises get the warning edge, hits get the success one,
 * and the wording carries the rest.</p>
 *
 * <p>It sits in the shared toast column (ui/toast-stack), which is portalled to <c>body</c> and
 * attached lazily so the live region is still announced over the title detail modal — the commonest
 * place to rate — and stays clickable there. A card undo raised by the same rating stacks above it
 * instead of hiding under it.</p>
 *
 * <p>Its clock holds while someone is on it, by the same rule as every timed toast
 * ({@link useToastHold}): it carries two focusable controls and stacks with the card undo on every
 * rating, so a keyboard or screen-reader user passing through it must not have it vanish under
 * them.</p>
 */
export default function VerdictToast() {
  const verdict = useSelector((state: RootState) => state.verdict.current);
  // Mounted only while a verdict is up, so the hold dies with the toast: the control that had focus
  // goes with it, no blur need ever arrive, and the next rating's toast must start unheld.
  return verdict ? <ShownVerdictToast verdict={verdict} /> : null;
}

function ShownVerdictToast({ verdict }: { verdict: ShownVerdict }) {
  const dispatch = useDispatch<AppDispatch>();
  const { held, holdProps } = useToastHold();
  const shownKey = verdict.shownKey;

  useEffect(() => {
    if (held) return;
    const timer = window.setTimeout(() => dispatch(verdictDismissed()), DISMISS_AFTER_MS);
    return () => window.clearTimeout(timer);
    // Keyed on shownKey so a second rating restarts the clock instead of inheriting the first's;
    // on `held` so letting go gives a fresh full window rather than the scraps of the old one.
  }, [shownKey, held, dispatch]);

  const band = verdictBand(verdict.delta);
  const missed = band === "over" || band === "under";

  return (
    <ToastPortal>
      <div
        role="status"
        aria-live="polite"
        data-testid="verdict-toast"
        {...holdProps}
        className={`rounded-xl border px-4 py-3 shadow-lg backdrop-blur-md ${
          missed ? "border-fa-warning/50 bg-fa-ink/95" : "border-fa-success/50 bg-fa-ink/95"
        }`}
      >
        <div className="flex items-start gap-3">
          <p className="fa-body text-fa-frost-bright flex-1">{verdictCopy(verdict, verdict.actual)}</p>
          <Button
            variant="ghost"
            size="icon-sm"
            onClick={() => dispatch(verdictDismissed())}
            aria-label="Dismiss"
            className="-mr-1.5 -mt-1 shrink-0 text-fa-frost-dim"
          >
            <X className="h-4 w-4" />
          </Button>
        </div>
        <Link
          to="/track-record"
          onClick={() => dispatch(verdictDismissed())}
          className="fa-caption text-fa-frost hover:text-fa-frost-bright underline underline-offset-2 mt-1 inline-block"
        >
          See the track record
        </Link>
      </div>
    </ToastPortal>
  );
}
