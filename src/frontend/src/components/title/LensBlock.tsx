import { useState } from "react";
import { AlertTriangle, ChevronDown, Flag } from "lucide-react";
import { useGetLensQuery, useReportLensSpoilerMutation } from "../../store/api";

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
  const { data, isLoading } = useGetLensQuery({ mediaType, tmdbId });
  const [report, { isLoading: reporting }] = useReportLensSpoilerMutation();
  const [open, setOpen] = useState(false);
  const [showCaution, setShowCaution] = useState(false);
  const [reported, setReported] = useState(false);

  if (isLoading) {
    // A one-line placeholder only where this block will supply the pitch; under a why-line the
    // "more" control simply arrives, so nothing on the page jumps for a block that may be empty.
    return hasPitch ? null : <div className="h-6 max-w-md rounded-md reel-shimmer" data-testid="lens-loading" />;
  }

  // No copy — the modal keeps exactly what it had before this feature existed: the templated
  // why-line above and the synopsis below, both rendered by the caller.
  if (!data || data.source === "none" || !data.lens || reported) {
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

      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls="lens-more-body"
        className="fa-caption inline-flex min-h-11 min-w-11 items-center gap-1.5 text-fa-frost-dim transition hover:text-fa-frost [@media(pointer:fine)]:min-h-0"
        data-testid="lens-more"
      >
        {open ? "Less" : "More — through your lens"}
        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${open ? "rotate-180" : ""}`} aria-hidden />
      </button>

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
              <span className="fa-overline rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 text-fa-frost-dim">
                {MOOD_LABELS[data.mood] ?? data.mood}
              </span>
            )}
            {data.whenToWatch && (
              <span className="fa-overline rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 text-fa-frost-dim">
                {WHEN_LABELS[data.whenToWatch] ?? data.whenToWatch}
              </span>
            )}
          </div>
        )}

        {/* Anchors: titles from your OWN library, with what links them. */}
        {data.anchors.length > 0 && (
          <ul className="space-y-1" data-testid="lens-anchors">
            {data.anchors.map((anchor) => (
              <li key={anchor.title} className="fa-caption text-fa-frost-dim">
                <span className="text-fa-frost">{anchor.title}</span> — {anchor.relation}
              </li>
            ))}
          </ul>
        )}

        {/* The honest reservation, behind a tap. Present because a recommender that never names a
            downside is one you stop trusting on the upside. */}
        {data.caution && (
          <div data-testid="lens-caution">
            <button
              type="button"
              onClick={() => setShowCaution((v) => !v)}
              data-testid="lens-caution-toggle"
              aria-expanded={showCaution}
              className="fa-caption inline-flex min-h-11 items-center gap-1.5 text-fa-frost-dim hover:text-fa-frost [@media(pointer:fine)]:min-h-0"
            >
              <AlertTriangle className="h-3.5 w-3.5" />
              What might bug you
              <ChevronDown className={`h-3.5 w-3.5 transition-transform ${showCaution ? "rotate-180" : ""}`} />
            </button>
            {showCaution && (
              <p className="fa-caption text-fa-warning mt-1.5" data-testid="lens-caution-text">
                {data.caution}
              </p>
            )}
          </div>
        )}

        <div className="flex items-center gap-3">
          {data.source === "stale" && (
            <span className="fa-caption text-fa-frost-dim" data-testid="lens-stale">
              Written before your taste last moved
            </span>
          )}
          <button
            type="button"
            disabled={reporting}
            data-testid="lens-spoiled"
            title="Withdraw this note — Reel will fall back to the plain synopsis"
            onClick={async () => {
              await report({ mediaType, tmdbId }).unwrap().catch(() => undefined);
              setReported(true);
            }}
            className="fa-caption inline-flex min-h-11 items-center gap-1.5 text-fa-frost-dim hover:text-fa-danger disabled:opacity-50 [@media(pointer:fine)]:min-h-0"
          >
            <Flag className="h-3.5 w-3.5" /> This spoiled it
          </button>
        </div>
      </div>
      )}
    </section>
  );
}
