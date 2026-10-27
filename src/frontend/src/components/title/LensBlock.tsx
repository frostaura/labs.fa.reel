import { useState } from "react";
import { AlertTriangle, ChevronDown, Flag } from "lucide-react";
import { useGetLensQuery, useReportLensSpoilerMutation } from "../../store/api";
import { lensPendingNote, lensPollInterval } from "../../lib/lensPending";
import { Button } from "../ui/button";

/** A caption-sized text control: no fill, 44 px on a coarse pointer, compact on a mouse. */
const TEXT_CONTROL =
  "h-auto min-h-11 gap-1.5 rounded-sm px-0 py-0 text-xs font-normal leading-snug text-3 hover:bg-transparent hover:text-1 [@media(pointer:fine)]:min-h-0";

/** Human labels for the closed vocabularies. Closed so they can be filtered on; labelled so they can be read. */
const MOOD_LABELS: Record<string, string> = {
  light: "light",
  heavy: "heavy",
  tense: "tense",
  cosy: "cosy",
  cerebral: "cerebral",
  background: "easy background",
};

const WHEN_LABELS: Record<string, string> = {
  "tonight-alone": "good on your own",
  "with-someone": "better with someone",
  "needs-full-attention": "wants your full attention",
  "fine-as-background": "fine as background",
  weekend: "one for the weekend",
};

/**
 * "Through your lens" — the title's premise retold in the vocabulary of the person reading it.
 *
 * ONE PITCH LINE, THE LENS BEHIND "MORE" (F-14). The page used to open on the why-line, then the
 * lens paragraph, its hook, two mood chips and an anchor list — five voices pitching before the
 * synopsis, and on a phone a screen of them. Now the page leads with one line: the why-line when
 * there is one (the caller renders it, so it is on screen before this block's own request
 * returns), else the lens's hook (rendered here). Everything else the lens says is one tap away.
 *
 * Three things here are load-bearing and easy to mistake for decoration:
 *
 *  - **The caution is real.** It is the one field in the product allowed to argue against its own
 *    recommendation, and it is behind a disclosure rather than hidden — a recommender that only
 *    ever flatters is one you stop believing about the good picks too. It is deliberately NOT run
 *    through the anti-recommendation guard the rest of the copy is.
 *  - **A stale note says it is stale.** Old prose beats no prose, and pretending it is current is
 *    the cheap version of both.
 *  - **A queued pass says so, briefly.** The GET only reads (WS-1 task 7); when the note is not
 *    current the server queues the pass and answers `pending`. The block re-asks a bounded number
 *    of times (lib/lensPending) so the note lands in a modal that is already open, and stops —
 *    the page never polls forever for a pass that was refused or failed.
 *  - **The overview never disappears.** This block sits above it, it does not replace it. If the
 *    lens is wrong or absent the page still tells you what the film is about.
 */
export default function LensBlock({
  mediaType,
  tmdbId,
  hasPitch,
}: {
  mediaType: string;
  tmdbId: number;
  /** The caller already led with a pitch line (the why-line); otherwise the hook becomes it. */
  hasPitch: boolean;
}) {
  // Answers seen so far. Each answer after the first is one re-ask spent, and the interval is
  // re-derived from the latest answer, so a fresh note, a cleared `pending` or the spent limit all
  // stop the polling. Counted by adjusting state during render (React's pattern for deriving from
  // a changing value), not in an effect.
  const [answers, setAnswers] = useState<{ seen: number | undefined; count: number }>({ seen: undefined, count: 0 });
  const [pollInterval, setPollInterval] = useState(0);
  const { data, isLoading, fulfilledTimeStamp } = useGetLensQuery(
    { mediaType, tmdbId },
    { pollingInterval: pollInterval, skipPollingIfUnfocused: true },
  );
  const [report, { isLoading: reporting }] = useReportLensSpoilerMutation();
  if (fulfilledTimeStamp !== undefined && fulfilledTimeStamp !== answers.seen) {
    setAnswers({ seen: fulfilledTimeStamp, count: answers.count + 1 });
  }
  const pollsDone = Math.max(0, answers.count - 1);
  const nextInterval = lensPollInterval(data, pollsDone);
  if (nextInterval !== pollInterval) setPollInterval(nextInterval);
  const pendingNote = lensPendingNote(data, pollsDone);
  const [open, setOpen] = useState(false);
  const [showCaution, setShowCaution] = useState(false);
  const [reported, setReported] = useState(false);

  if (isLoading) {
    // A one-line placeholder only where this block will supply the pitch; under a why-line the
    // "more" control simply arrives, so nothing on the page jumps for a block that may be empty.
    return hasPitch ? null : <div className="h-6 max-w-md rounded-md reel-shimmer" data-testid="lens-loading" />;
  }

  if (reported) return null;

  // No copy yet, but a pass is writing it: one quiet line where the note will land.
  if (data && (data.source === "none" || !data.lens) && pendingNote) {
    return (
      <p className="fa-caption text-3" role="status" data-testid="lens-pending">
        {pendingNote}
      </p>
    );
  }

  // No copy — the modal keeps exactly what it had before this feature existed: the templated
  // why-line above and the synopsis below, both rendered by the caller.
  if (!data || data.source === "none" || !data.lens) {
    return null;
  }

  const hookIsPitch = !hasPitch && Boolean(data.hook);

  return (
    <section className="space-y-2 max-w-2xl" data-testid="lens-block">
      {hookIsPitch && (
        <p className="fa-body sm:text-base text-fa-frost max-w-2xl leading-snug" data-testid="lens-pitch">
          {data.hook}
        </p>
      )}

      <Button
        variant="ghost"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls="lens-more-body"
        className={`${TEXT_CONTROL} min-w-11`}
        data-testid="lens-more"
      >
        {open ? "Less" : "More — through your lens"}
        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${open ? "rotate-180" : ""}`} aria-hidden />
      </Button>

      {open && (
        <div id="lens-more-body" className="space-y-3">
        <p className="fa-body sm:text-base text-fa-frost-bright leading-relaxed" data-testid="lens-text">
          {data.lens}
        </p>

        {data.hook && !hookIsPitch && (
          <p className="fa-caption text-fa-frost" data-testid="lens-hook">
            {data.hook}
          </p>
        )}

        {/* Mood and occasion — the closed-vocabulary pair the tonight-mood chips also sort on. */}
        {(data.mood || data.whenToWatch) && (
          <div className="flex flex-wrap gap-2" data-testid="lens-mood">
            {data.mood && (
              <span className="fa-overline rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 text-3">
                {MOOD_LABELS[data.mood] ?? data.mood}
              </span>
            )}
            {data.whenToWatch && (
              <span className="fa-overline rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 text-3">
                {WHEN_LABELS[data.whenToWatch] ?? data.whenToWatch}
              </span>
            )}
          </div>
        )}

        {/* Anchors: titles from your OWN library, with what links them. */}
        {data.anchors.length > 0 && (
          <ul className="space-y-1" data-testid="lens-anchors">
            {data.anchors.map((anchor) => (
              <li key={anchor.title} className="fa-caption text-3">
                <span className="text-fa-frost">{anchor.title}</span> — {anchor.relation}
              </li>
            ))}
          </ul>
        )}

        {/* The honest reservation, behind a tap. Present because a recommender that never names a
            downside is one you stop trusting on the upside. */}
        {data.caution && (
          <div data-testid="lens-caution">
            <Button
              variant="ghost"
              onClick={() => setShowCaution((v) => !v)}
              data-testid="lens-caution-toggle"
              aria-expanded={showCaution}
              className={TEXT_CONTROL}
            >
              <AlertTriangle className="h-3.5 w-3.5" aria-hidden />
              What might bug you
              <ChevronDown className={`h-3.5 w-3.5 transition-transform ${showCaution ? "rotate-180" : ""}`} aria-hidden />
            </Button>
            {showCaution && (
              <p className="fa-caption text-fa-warning mt-1.5" data-testid="lens-caution-text">
                {data.caution}
              </p>
            )}
          </div>
        )}

        <div className="flex items-center gap-3">
          {data.source === "stale" && (
            <span className="fa-caption text-3" data-testid="lens-stale">
              An older note
            </span>
          )}
          {data.source === "stale" && pendingNote && (
            <span className="fa-caption text-3" role="status" data-testid="lens-pending">
              {pendingNote}
            </span>
          )}
          <Button
            variant="ghost"
            disabled={reporting}
            data-testid="lens-spoiled"
            title="Withdraw this note — Reel will fall back to the plain synopsis"
            onClick={async () => {
              await report({ mediaType, tmdbId }).unwrap().catch(() => undefined);
              setReported(true);
            }}
            className={`${TEXT_CONTROL} hover:text-fa-danger`}
          >
            <Flag className="h-3.5 w-3.5" aria-hidden /> This spoiled it
          </Button>
        </div>
      </div>
      )}
    </section>
  );
}
