import { Link } from "react-router";
import { useGetAccuracyQuery } from "../../store/api";

/**
 * Home's one status line (FD-15, O-6): what is true about tonight's picks, said once, in a single
 * caption line directly above them.
 *
 * Each admission used to be its own banner — a bordered track-record strip, an amber line above the
 * hero, and the same "nothing cleared your bar" again on every card — and between them they pushed
 * the first pick to y≈418 on a desktop and below the fold on a phone. The honesty is kept; the
 * repetition is not. A segment appears only when it has something true to say: the reaching
 * admission only when every measured pick is a reach, the track record only once there is a number
 * (a "within one point on 0%" for a new account would be a claim the data does not support).
 * With neither, the line is absent, not empty.
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
  const withinOne = data && data.headline.n > 0 ? data.headline.withinOnePoint : null;

  if (reachingNote === null && withinOne === null) return null;

  return (
    <p className={`fa-caption text-fa-frost-dim ${className}`} data-testid="home-status-line">
      {reachingNote !== null && (
        <span className="text-fa-warning" data-testid="hero-reaching-note">
          {reachingNote}
        </span>
      )}
      {reachingNote !== null && withinOne !== null && <span aria-hidden> · </span>}
      {withinOne !== null && (
        <Link
          to="/track-record"
          data-testid="status-track-record"
          className="underline-offset-2 hover:text-fa-frost hover:underline"
        >
          {/* The qualifier drops on a phone so the whole line stays one line above the picks. */}
          <span className="tabular-nums text-fa-frost">{(withinOne * 100).toFixed(0)}%</span> within a point
          <span className="hidden sm:inline"> of your ratings</span> →
        </Link>
      )}
    </p>
  );
}
