import { Link } from "react-router-dom";
import { Target } from "lucide-react";
import { useGetAccuracyQuery } from "../../store/api";

/**
 * One line on Home: "Within one point on 26% of what you rate — see the track record."
 *
 * The strip exists so the scorecard is not something you have to go looking for. Putting the
 * product's own error rate on its front page is the whole "honest from day one" decision expressed
 * in one sentence, and it is deliberately not framed as a boast or an apology — it is a fact with a
 * link.
 *
 * <p>Renders nothing at all when there is no number yet. A strip reading "within one point on 0%"
 * on a brand-new account would be a claim about the model that the data does not support — the
 * absence of evidence is not a bad score.</p>
 */
export default function TrackRecordStrip() {
  const { data } = useGetAccuracyQuery({ window: "365d" });

  if (!data || data.headline.n === 0) return null;

  return (
    <Link
      to="/track-record"
      data-testid="track-record-strip"
      className="flex items-center gap-2 rounded-lg border border-fa-edge/60 bg-fa-glass px-3 py-2 hover:border-fa-frost/40 transition"
    >
      <Target className="h-4 w-4 text-fa-frost shrink-0" />
      <p className="fa-caption text-fa-frost">
        Within one point on{" "}
        <span className="text-fa-frost-bright tabular-nums">
          {(data.headline.withinOnePoint * 100).toFixed(0)}%
        </span>{" "}
        of what you rate —{" "}
        <span className="underline underline-offset-2">see the track record</span>
      </p>
    </Link>
  );
}
