import LoadFailed from "../ui/load-failed";
import { relTime } from "../../lib/format";
import { num, pct } from "../../lib/trackRecordCopy";
import { xrefBacktestCaveat, xrefOnlineNote, xrefVerdictLabel } from "../../lib/xrefCopy";
import { useGetCrossReferenceStatsQuery } from "../../store/api";

/**
 * The cross-reference pass's statistics, moved here from the track record (G-10, KILL-11): every
 * prompt generation's backtest with its separation (AUC), its interval and its gating, and the
 * online outcomes with their raw rates. The track record keeps the plain-words reading of the
 * generation running now; this is the evidence behind it, for the founder.
 */
export default function CrossReferenceStats() {
  const { data, isError, isFetching, refetch } = useGetCrossReferenceStatsQuery({ window: "all" });

  if (!data) {
    return isError ? (
      <LoadFailed
        what="the cross-reference statistics"
        onRetry={() => void refetch()}
        retrying={isFetching}
        variant="inline"
        testId="lab-xref-error"
      />
    ) : null;
  }

  const { backtests, online, minimumOutcomes } = data.crossReference;
  if (backtests.length === 0 && online.length === 0) return null;

  return (
    <section className="fa-card p-5 space-y-4" data-testid="lab-xref">
      <div>
        <h2 className="fa-section-title">Cross-reference — backtests and outcomes</h2>
        <p className="fa-caption text-fa-frost-dim mt-1">
          Every prompt generation&apos;s offline backtest, newest first, and how titles served under each
          verdict were later rated (all time). An outcome row needs {minimumOutcomes.toLocaleString("en-US")}{" "}
          ratings before it is read as anything.
        </p>
      </div>

      {backtests.map((b) => (
        <div key={b.promptVersion} className="border-t border-fa-edge/50 pt-3 space-y-1" data-testid="lab-xref-backtest">
          <p className="fa-body text-fa-frost-bright">
            Prompt v{b.promptVersion}
            {b.current && <span className="fa-caption text-fa-success"> · running now</span>}
            <span className="fa-caption text-fa-frost-dim"> · evaluated {relTime(b.evaluatedAt)} · gating: {b.gating}</span>
          </p>
          <p className="fa-caption text-fa-frost">{xrefBacktestCaveat(b)}</p>
          <p className="fa-caption text-fa-frost-dim">
            Separation between what you loved and what you disliked (AUC): {num(b.auc, 3)} — 0.500 would be none.
            Kept titles later averaged {num(b.meanLaterRatingKeep)}, {pct(b.keepLovedRate, 1)} of them loved and{" "}
            {pct(b.keepDislikedRate, 1)} disliked; ruled-out titles averaged {num(b.meanLaterRatingOut)}.
          </p>
          {b.caveat && <p className="fa-caption text-fa-warning">{b.caveat}</p>}
        </div>
      ))}

      {online.length > 0 && (
        <div className="border-t border-fa-edge/50 pt-3 space-y-1">
          <p className="fa-overline text-fa-frost-dim">online outcomes</p>
          {online.map((o) => (
            <p key={o.verdict} className="fa-caption text-fa-frost" data-testid="lab-xref-online-row">
              <span className="text-fa-frost-dim">{xrefVerdictLabel(o.verdict)}:</span> {xrefOnlineNote(o)} · loved{" "}
              {pct(o.lovedRate, 1)} · disliked {pct(o.dislikedRate, 1)}
            </p>
          ))}
        </div>
      )}
    </section>
  );
}
