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
 * "Through your lens" — the title's premise retold in the vocabulary of the person reading it,
 * replacing the studio blurb as the first thing on the page.
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
  overview,
}: {
  mediaType: string;
  tmdbId: number;
  overview: string | null;
}) {
  const { data, isLoading } = useGetLensQuery({ mediaType, tmdbId });
  const [report, { isLoading: reporting }] = useReportLensSpoilerMutation();
  const [showCaution, setShowCaution] = useState(false);
  const [reported, setReported] = useState(false);

  if (isLoading) {
    return <div className="h-20 max-w-2xl rounded-lg reel-shimmer" data-testid="lens-loading" />;
  }

  // No copy — the modal keeps exactly what it had before this feature existed: the templated
  // why-line above and the synopsis below, both rendered by the caller.
  if (!data || data.source === "none" || !data.lens || reported) {
    return null;
  }

  return (
    <section className="space-y-3 max-w-2xl" data-testid="lens-block">
      <p className="fa-body sm:text-base text-fa-frost-bright leading-relaxed" data-testid="lens-text">
        {data.lens}
      </p>

      {data.hook && (
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
            className="fa-caption inline-flex items-center gap-1.5 text-fa-frost-dim hover:text-fa-frost"
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
          className="fa-caption inline-flex items-center gap-1.5 text-fa-frost-dim hover:text-fa-danger disabled:opacity-50"
        >
          <Flag className="h-3.5 w-3.5" /> This spoiled it
        </button>
      </div>

      {/* The synopsis is never replaced — it is what the page falls back to, and it is still what
          tells you what the thing is about when the lens is wrong. Rendered by the caller. */}
      {overview === null && null}
    </section>
  );
}
