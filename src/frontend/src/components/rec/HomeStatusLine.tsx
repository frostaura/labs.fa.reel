import { Fragment, type ReactNode } from "react";
import { Link } from "react-router";
import { useGetAccuracyQuery, useGetModelLearnedQuery } from "../../store/api";
import { learnedStatusParts, learnedStatusSegment } from "../../lib/learnedCopy";

/**
 * Home's one status line (FD-15, O-6): what is true about tonight's picks, said once, in a single
 * caption line directly above them.
 *
 * Each admission used to be its own banner — a bordered track-record strip, an amber line above the
 * hero, the same "nothing cleared your bar" again on every card, a "your model just learned" band
 * and a cross-reference band — and between them they pushed the first pick to y≈418 on a desktop
 * and below the fold on a phone. The honesty is kept; the repetition is not:
 *
 * - **Reaching** — only when every measured pick is a reach (the cards stay quiet then).
 * - **Retrained** — the latest retrain while it is still news (two days), in place of the band.
 * - **Track record** — only once there is a number; "within one point on 0%" for a new account
 *   would be a claim the data does not support.
 *
 * The cross-reference pass is not here: the pill names it while it runs, and its detailed band
 * belongs to Track record (FD-15). With nothing to say the line is absent, not empty.
 */
export default function HomeStatusLine({
  reachingNote,
  className = "",
}: {
  /** The hero's row-level admission (`heroReachingNote`), or null when the picks cleared the bar. */
  reachingNote: string | null;
  className?: string;
}) {
  const { data } = useGetAccuracyQuery({ window: "365d" });
  const { data: learned } = useGetModelLearnedQuery();
  const withinOne = data && data.headline.n > 0 ? data.headline.withinOnePoint : null;
  const retrained = learnedStatusParts(learned);

  const segments: { key: string; node: ReactNode }[] = [];
  if (reachingNote !== null) {
    segments.push({
      key: "reaching",
      node: (
        <span className="text-fa-warning" data-testid="hero-reaching-note">
          {reachingNote}
        </span>
      ),
    });
  }
  if (retrained !== null) {
    segments.push({
      key: "retrained",
      // From `lg` the line shares a row with the mood chips, and three segments in full wrapped it
      // under them and the first pick with it (222 px against 220 at 1440×900). So between `lg`
      // and `2xl` the segment is "Retrained 3h ago" and the rest of the sentence is its tooltip;
      // on a phone (its own line) and from `2xl` (room to spare) it is said in full.
      node: (
        <span data-testid="status-retrained" title={learnedStatusSegment(learned) ?? undefined}>
          {retrained.when}
          <span className="lg:hidden 2xl:inline">
            {retrained.ratings}
            {retrained.changed}
          </span>
        </span>
      ),
    });
  }
  if (withinOne !== null) {
    segments.push({
      key: "track-record",
      node: (
        <Link
          to="/track-record"
          data-testid="status-track-record"
          className="underline-offset-2 hover:text-fa-frost hover:underline"
        >
          {/* The qualifier drops on a phone so the whole line stays short above the picks. */}
          <span className="tabular-nums text-fa-frost">{(withinOne * 100).toFixed(0)}%</span> within a point
          <span className="hidden sm:inline"> of your ratings</span> →
        </Link>
      ),
    });
  }

  if (segments.length === 0) return null;

  return (
    <p className={`fa-caption text-3 ${className}`} data-testid="home-status-line">
      {segments.map((segment, index) => (
        <Fragment key={segment.key}>
          {index > 0 && <span aria-hidden> · </span>}
          {segment.node}
        </Fragment>
      ))}
    </p>
  );
}
