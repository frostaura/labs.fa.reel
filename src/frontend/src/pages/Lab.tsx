import { useEffect, useRef, type ReactNode } from "react";
import { FlaskConical, Play } from "lucide-react";
import { useGetModelMetricsQuery, useTrainModelMutation, useGetSyncStatusQuery, useGetInterleaveQuery } from "../store/api";
import InterleavePanel from "../components/lab/InterleavePanel";
import CrossReferenceStats from "../components/lab/CrossReferenceStats";
import LoadFailed from "../components/ui/load-failed";
import { Button } from "../components/ui/button";
import { COARSE_TAP } from "../lib/tapTarget";
import type { CounterfactualWindow } from "../store/api";
import ScrollRegion from "../components/lab/ScrollRegion";

function pct(value: number | null | undefined, digits = 0): string {
  return value == null ? "—" : `${(value * 100).toFixed(digits)}%`;
}

/** A plain number, or an em-dash when there genuinely isn't one. Never a fabricated zero. */
function num(value: number | null | undefined, digits = 2): string {
  return value == null ? "—" : value.toFixed(digits);
}

/** Signed, so an over-promise reads as "+1.03" and never hides behind an absolute value. */
function signed(value: number | null | undefined, digits = 2): string {
  return value == null ? "—" : `${value > 0 ? "+" : ""}${value.toFixed(digits)}`;
}

function windowLabel(w: CounterfactualWindow): string {
  return w.days == null ? "all time" : `last ${w.days} days`;
}

/** A section with nothing to show yet: its heading and one sentence, so it keeps its place. */
function EmptySection({ title, testId, children }: { title: string; testId: string; children: ReactNode }) {
  return (
    <section className="fa-card p-5" data-testid={testId} data-empty="true">
      <h2 className="fa-section-title mb-1">{title}</h2>
      <p className="fa-caption text-3">{children}</p>
    </section>
  );
}

/** A slot whose data is still on its way, at roughly the height it will fill. */
function SlotSkeleton({ height, testId }: { height: string; testId?: string }) {
  return <div className={`rounded-xl reel-shimmer ${height}`} data-testid={testId} aria-hidden />;
}

/**
 * The model lab — the M2 instrument panel. The gate badge answers the program's one
 * falsifiable question; the ledger tracks the 3-iteration kill criterion run by run.
 *
 * <p><b>Sections in a fixed order</b> (G-14): gate · retro loop · cross-reference · interleaving ·
 * metric tiles · split points · what the model leans on · iteration ledger. Every slot is on the
 * page from the first paint — a skeleton while its data is on the way, a one-line "nothing yet" when
 * there is none — so nothing is inserted above what you are reading when a poll lands. The page
 * used to grow section by section as its three queries arrived (CLS 0.58), and the metrics payload
 * (181 KB) was re-fetched every 10 s whether or not anything could have changed (J-15): it now polls
 * only while a train or evaluate job runs, and reads once more when that job ends.</p>
 */
export default function Lab() {
  const { data: sync } = useGetSyncStatusQuery();
  const trainActive = sync?.activeJob != null && ["Train", "Evaluate"].includes(sync.activeJob.kind);
  const { data, isError, isFetching, refetch } = useGetModelMetricsQuery(undefined, {
    pollingInterval: trainActive ? 10_000 : 0,
  });
  // The run that just finished is the one worth reading: one fetch as the job leaves the status.
  const wasTraining = useRef(trainActive);
  useEffect(() => {
    if (wasTraining.current && !trainActive) void refetch();
    wasTraining.current = trainActive;
  }, [trainActive, refetch]);
  // Slow poll on purpose: the underlying number moves on the scale of days, and a 10s refresh
  // would imply a cadence this instrument does not have.
  const {
    data: interleave,
    isError: interleaveFailed,
    isFetching: interleaveFetching,
    refetch: refetchInterleave,
  } = useGetInterleaveQuery(undefined, { pollingInterval: 120_000 });
  const [train, { isLoading: starting }] = useTrainModelMutation();

  const gate = data?.gate;
  const efficiency = data?.efficiency;
  const latest = data?.runs.find((r) => r.eval != null)?.eval ?? null;

  return (
    <div className="space-y-6" data-testid="lab">
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <div>
          <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2">
            <FlaskConical className="h-6 w-6 text-fa-frost" /> Model lab
          </h1>
          <p className="fa-caption text-3 mt-1">
            Leakage-clean time-split evaluation against the popularity baseline — the edge-proof gate.
          </p>
        </div>
        <div className="flex items-center gap-4">
          {/* Live feedback while the job runs — the button alone flipped to "Training…" and then
              nothing moved for minutes. The active job's own progress feed (polled + SSE-updated
              by RealtimeSync) narrates it instead. */}
          {trainActive && sync?.activeJob && (
            <div className="w-56" data-testid="train-progress">
              <p className="fa-caption text-3 truncate text-right">
                {sync.activeJob.progressMessage ?? "Working…"}
                {sync.activeJob.progressPct != null ? ` · ${Math.round(sync.activeJob.progressPct)}%` : ""}
              </p>
              <div className="relative mt-1 h-1 overflow-hidden rounded-full bg-fa-glass">
                {sync.activeJob.progressPct != null ? (
                  <div
                    className="absolute inset-y-0 left-0 rounded-full bg-fa-frost/60 transition-[width] duration-500"
                    style={{ width: `${Math.min(100, Math.max(0, sync.activeJob.progressPct))}%` }}
                  />
                ) : (
                  <div className="absolute inset-y-0 rounded-full bg-fa-frost/60 fa-progress-slide" />
                )}
              </div>
            </div>
          )}
          <Button variant="primary" onClick={() => train()} disabled={starting || trainActive} className={COARSE_TAP}>
            <Play className="h-4 w-4" />
            {trainActive ? "Training…" : "Train + evaluate"}
          </Button>
        </div>
      </div>

      {/* Until the metrics land: every slot at its height, in its order. A failed read says so. */}
      {!data &&
        (isError ? (
          <LoadFailed what="the model metrics" onRetry={() => void refetch()} retrying={isFetching} testId="lab-error" />
        ) : (
          <div className="space-y-6" data-testid="lab-loading">
            <SlotSkeleton height="h-44" />
            <SlotSkeleton height="h-72" />
            <SlotSkeleton height="h-40" />
            <SlotSkeleton height="h-56" />
            <SlotSkeleton height="h-48" />
            <SlotSkeleton height="h-64" />
          </div>
        ))}

      {data && (
        <>

          {/* Gate badge — two clauses, stated in plain language, and the retired gate beside it. */}
          <section
            className={`fa-card p-5 border ${
              gate?.passed ? "border-fa-success/50" : "border-fa-edge"
            }`}
            data-testid="gate-badge"
          >
            <div className="flex items-center justify-between flex-wrap gap-4">
              <div>
                <p className="fa-overline text-3">M2 edge-proof gate — NDCG@10</p>
                <p className={`text-3xl font-light mt-1 ${gate?.passed ? "text-fa-success" : "text-fa-frost-bright"}`}>
                  {gate?.passed ? "PASSED" : "NOT MET"}
                </p>
                <p className="fa-caption text-3 mt-1">
                  needs both: the 95% range on the model-minus-popularity difference stays above zero,
                  and the average lift across five split points is ≥ {pct(gate?.threshold)}
                </p>
                {gate && (
                  <ul className="fa-caption mt-2 space-y-0.5" data-testid="gate-clauses">
                    <li className={gate.ciExcludesZero ? "text-fa-success" : "text-fa-danger"}>
                      {gate.ciExcludesZero ? "✓" : "✗"} the difference is real — 95% range{" "}
                      {signed(gate.ndcgLiftCiLow, 3)} to {signed(gate.ndcgLiftCiHigh, 3)}
                      {!gate.ciExcludesZero && " (includes zero, so it could be noise)"}
                    </li>
                    <li className={gate.passed ? "text-fa-success" : "text-3"}>
                      {gate.passed ? "✓" : "✗"} the difference is big enough — needs ≥ {pct(gate.threshold)}
                    </li>
                  </ul>
                )}
              </div>
              <div className="text-right">
                <p className="fa-stat-label">ndcg@10 — model vs popularity</p>
                <p className={`fa-metric ${gate?.passed ? "text-fa-success" : "text-fa-frost-bright"}`}>
                  {num(gate?.ndcgModel, 3)} <span className="text-3">vs</span> {num(gate?.ndcgBaseline, 3)}
                </p>
                <p className="fa-caption text-3 mt-1">
                  {/* "iteration 18 of 3" is arithmetic nonsense once the criterion is breached; past
                      the limit the caption names the iteration and the red line below carries the breach. */}
                  {gate?.criterionExceeded
                    ? `iteration ${gate.iterationsUsed}`
                    : `iteration ${gate?.iterationsUsed ?? 0} of ${gate?.killCriterionAt ?? 3} (kill criterion)`}
                </p>
                {/* The criterion was hard-coded at 3 while the ledger ran past 12, and the page said
                    nothing. Stating the breach is the whole point of a kill criterion. */}
                {gate?.criterionExceeded && (
                  <p className="fa-caption text-fa-danger mt-1" data-testid="criterion-exceeded">
                    kill criterion exceeded — {gate.iterationsUsed} iterations against a limit of {gate.killCriterionAt}
                  </p>
                )}
                {/* The old gate, kept visible so twelve rows of history stay readable rather than
                    being quietly re-scored against a metric they were never measured on. */}
                <p className="fa-caption text-3 mt-2" data-testid="legacy-gate">
                  old precision@10 gate: {gate?.legacyPassed ? "passed" : "not met"} ·{" "}
                  {latest ? pct(latest.relativeImprovement) : "—"} lift, needs ≥ {pct(gate?.legacyThreshold)}
                </p>
              </div>
            </div>
          </section>

          {/* Track record — the retro closed loop. Zero new schema: what Reel predicted, joined to what
              actually happened. Deliberately unflattering where the data is unflattering. */}
          {!efficiency && (
            <EmptySection title="Track record — retro loop" testId="retro-loop">
              No closed-loop numbers yet: nothing saved through Reel has been watched and rated.
            </EmptySection>
          )}
          {efficiency && (
            <section className="fa-card p-5 space-y-5" data-testid="retro-loop">
              <div>
                <h2 className="fa-section-title">Track record — retro loop</h2>
                <p className="fa-caption text-3 mt-1">
                  Saved via Reel → watched → rated. This is how wrong the number on the card has been.
                </p>
              </div>

              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">conversions</p>
                  <p className="fa-stat-value">{efficiency.conversions.count}</p>
                  <p className="fa-caption text-3 mt-1">
                    {efficiency.conversions.ratedCount} rated · {efficiency.conversions.scoredCount} scored
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">predicted vs actual</p>
                  <p className="fa-stat-value">
                    {num(efficiency.conversions.accuracy.meanPredicted, 2)} →{" "}
                    {num(efficiency.conversions.accuracy.meanActual, 2)}
                  </p>
                  <p className="fa-caption text-3 mt-1">mean on converted titles</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">mae · bias</p>
                  <p className="fa-stat-value">
                    {num(efficiency.conversions.accuracy.meanAbsoluteError, 2)} ·{" "}
                    {signed(efficiency.conversions.accuracy.bias, 2)}
                  </p>
                  <p className="fa-caption text-3 mt-1">positive bias = over-promised</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">within 1 point</p>
                  <p className="fa-stat-value">{pct(efficiency.conversions.accuracy.withinOnePointRate)}</p>
                  <p className="fa-caption text-3 mt-1">
                    ρ {efficiency.conversions.accuracy.pearsonCorrelation == null
                      ? "undefined"
                      : num(efficiency.conversions.accuracy.pearsonCorrelation, 3)}
                  </p>
                </div>
              </div>

              {/* The wider evidence base: every rating that arrived after Reel had already committed
                  to a number, not just the ones it converted. */}
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3" data-testid="predicted-vs-actual">
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">scored-before-rated pairs</p>
                  <p className="fa-stat-value">{efficiency.predictedVsActual.pairs}</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">mae (all pairs)</p>
                  <p className="fa-stat-value">{num(efficiency.predictedVsActual.accuracy.meanAbsoluteError, 2)}</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">bias (all pairs)</p>
                  <p className="fa-stat-value">{signed(efficiency.predictedVsActual.accuracy.bias, 2)}</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">correlation</p>
                  <p className="fa-stat-value">
                    {efficiency.predictedVsActual.accuracy.pearsonCorrelation == null
                      ? "—"
                      : num(efficiency.predictedVsActual.accuracy.pearsonCorrelation, 3)}
                  </p>
                </div>
              </div>

              {/* The within-user counterfactual — the one number that says whether Reel helped. */}
              {efficiency.counterfactual.length > 0 && (
                <div data-testid="counterfactual">
                  <h3 className="fa-overline text-3 mb-2">Because of Reel vs found yourself</h3>
                  <div className="space-y-1">
                    {efficiency.counterfactual.map((w) => (
                      <p key={w.window} className="fa-body">
                        <span className="text-3">{windowLabel(w)}:</span>{" "}
                        via Reel {num(w.viaReelMeanRating, 2)} ({w.viaReelCount}) · found yourself{" "}
                        {num(w.otherMeanRating, 2)} ({w.otherCount}) ·{" "}
                        <span className={w.delta != null && w.delta < 0 ? "text-fa-danger" : "text-fa-success"}>
                          {signed(w.delta, 2)}
                        </span>
                      </p>
                    ))}
                  </div>
                </div>
              )}

              {efficiency.conversions.worstMisses.length > 0 && (
                <div>
                  <h3 className="fa-overline text-3 mb-2">Worst misses</h3>
                  <ScrollRegion label="Worst misses">
                    <table className="w-full text-left" data-testid="worst-misses">
                      <thead>
                        <tr className="fa-overline text-3 border-b border-fa-edge/50">
                          <th className="py-2 pr-4">title</th>
                          <th className="py-2 pr-4">predicted</th>
                          <th className="py-2 pr-4">you said</th>
                          <th className="py-2">over by</th>
                        </tr>
                      </thead>
                      <tbody>
                        {efficiency.conversions.worstMisses.map((miss) => (
                          <tr key={miss.titleId} className="border-b border-fa-edge/30 fa-body">
                            <td className="py-2 pr-4">
                              {miss.name}
                              {miss.year != null && <span className="text-3"> ({miss.year})</span>}
                            </td>
                            <td className="py-2 pr-4 tabular-nums">{num(miss.predicted, 2)}</td>
                            <td className="py-2 pr-4 tabular-nums">{miss.actual}</td>
                            <td className="py-2 tabular-nums text-fa-danger">{signed(miss.gap, 2)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </ScrollRegion>
                </div>
              )}

              {efficiency.conversions.count === 0 && (
                <p className="fa-body text-3">
                  No conversions yet — nothing Reel put on the list has been watched and rated.
                </p>
              )}
            </section>
          )}

          {/* The cross-reference statistics, moved here from the track record (G-10). */}
          <CrossReferenceStats />

          {/* Interleaving — the online instrument. Below the retro loop because that answers "is the
              number honest" and this answers "is this ranker better than that one", which is only
              worth asking once the first has an answer. */}
          {interleave ? (
            <InterleavePanel data={interleave} />
          ) : interleaveFailed ? (
            <LoadFailed
              what="the interleaving readout"
              onRetry={() => void refetchInterleave()}
              retrying={interleaveFetching}
              variant="inline"
              testId="interleave-error"
            />
          ) : (
            <SlotSkeleton height="h-56" testId="interleave-loading" />
          )}

          {/* Metric tiles */}
          {!latest && (
            <EmptySection title="The latest evaluation" testId="metric-tiles">
              No evaluated run yet — the tiles fill in when “Train + evaluate” finishes its first evaluation.
            </EmptySection>
          )}
          {latest && (
            <section className="space-y-3" data-testid="metric-tiles">
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">ndcg@10 — model</p>
                  <p className="fa-stat-value">{num(latest.ndcgAt10Model, 3)}</p>
                  <p className="fa-caption text-3 mt-1">popularity {num(latest.ndcgAt10Baseline, 3)}</p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">lift, 95% range</p>
                  <p className="fa-stat-value">
                    {signed(latest.ndcgLiftCiLow, 3)} … {signed(latest.ndcgLiftCiHigh, 3)}
                  </p>
                  <p className="fa-caption text-3 mt-1">
                    {latest.ndcgLiftCiLow == null
                      ? "not measured on this run"
                      : latest.ndcgLiftCiLow > 0
                        ? "above zero — a real edge"
                        : "spans zero — could be noise"}
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">rank agreement (ρ ± se)</p>
                  <p className="fa-stat-value">
                    {latest.spearmanRho.toFixed(2)} ± {num(latest.spearmanSe, 3)}
                  </p>
                  <p className="fa-caption text-3 mt-1">
                    does the order match yours? 0 = no better than shuffling
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">how wrong the number is</p>
                  <p className="fa-stat-value">
                    {signed(latest.calibrationBias, 2)} · {latest.mae.toFixed(2)}
                  </p>
                  <p className="fa-caption text-3 mt-1">
                    bias (+ = over-promised) · average miss, in rating points
                  </p>
                </div>
              </div>

              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">calibration error (ece)</p>
                  <p className="fa-stat-value">{num(latest.ece, 3)}</p>
                  <p className="fa-caption text-3 mt-1">
                    across 10 predicted-rating bands; 0 = every band lands where it promised
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">holdout — could actually be shown</p>
                  <p className="fa-stat-value">
                    {latest.eligibleHoldoutCount ?? "—"} / {latest.unfilteredHoldoutCount ?? "—"}
                  </p>
                  <p className="fa-caption text-3 mt-1">
                    {latest.eligibleHoldoutCount != null && latest.unfilteredHoldoutCount != null
                      ? `${latest.unfilteredHoldoutCount - latest.eligibleHoldoutCount} dropped by your own filters`
                      : "not recorded on this run"}
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">model p@10 (display only)</p>
                  <p className="fa-stat-value">
                    {pct(latest.modelPrecisionAt10)} <span className="text-3">vs</span>{" "}
                    {pct(latest.baselinePrecisionAt10)}
                  </p>
                  <p className="fa-caption text-3 mt-1">
                    jumps in steps of 10% — too coarse to gate on
                  </p>
                </div>
                <div className="fa-card px-4 py-3">
                  <p className="fa-stat-label">rmse</p>
                  <p className="fa-stat-value">{latest.rmse.toFixed(2)}</p>
                  <p className="fa-caption text-3 mt-1">
                    {latest.lowSample ? "low sample — threshold fell back to your median" : `loved = ≥ threshold`}
                  </p>
                </div>
              </div>
            </section>
          )}

          {/* Five rolling split points — one origin is one draw, and the ledger used to report one. */}
          {!(latest?.origins && latest.origins.length > 0) && (
            <EmptySection title="Five split points, not one" testId="split-origins">
              The latest evaluation recorded no split points — runs before the five-point evaluation have none.
            </EmptySection>
          )}
          {latest?.origins && latest.origins.length > 0 && (
            <section className="fa-card p-5" data-testid="split-origins">
              <h2 className="fa-section-title mb-1">Five split points, not one</h2>
              <p className="fa-caption text-3 mb-3">
                The same question asked at five different moments in your rating history. One split point
                is a single draw; five say whether the answer holds.
              </p>
              <ScrollRegion label="Split points">
                <table className="w-full text-left">
                  <thead>
                    <tr className="fa-overline text-3 border-b border-fa-edge/50">
                      <th className="py-2 pr-4">split at</th>
                      <th className="py-2 pr-4">train / holdout</th>
                      <th className="py-2 pr-4">ndcg model</th>
                      <th className="py-2 pr-4">ndcg popularity</th>
                      <th className="py-2">lift</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latest.origins.map((o) => (
                      <tr key={o.fraction} className="border-b border-fa-edge/30 fa-body">
                        <td className="py-2 pr-4 tabular-nums">{pct(o.fraction)}</td>
                        <td className="py-2 pr-4 tabular-nums">
                          {o.trainRows} / {o.eligibleHoldout}
                          <span className="text-3"> of {o.unfilteredHoldout}</span>
                        </td>
                        <td className="py-2 pr-4 tabular-nums">{o.ndcgModel.toFixed(3)}</td>
                        <td className="py-2 pr-4 tabular-nums">{o.ndcgBaseline.toFixed(3)}</td>
                        <td
                          className={`py-2 tabular-nums ${o.ndcgRelativeLift > 0 ? "text-fa-success" : "text-fa-danger"}`}
                        >
                          {pct(o.ndcgRelativeLift)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </ScrollRegion>
              <p className="fa-caption text-3 mt-3">
                mean lift across all five:{" "}
                <span className="tabular-nums">
                  {pct(latest.origins.reduce((sum, o) => sum + o.ndcgRelativeLift, 0) / latest.origins.length)}
                </span>
              </p>
            </section>
          )}

          {/* Feature importance */}
          {!(latest?.detail.featureImportance && Object.keys(latest.detail.featureImportance).length > 0) && (
            <EmptySection title="What the model leans on" testId="feature-importance">
              The latest evaluation recorded no feature weights.
            </EmptySection>
          )}
          {latest?.detail.featureImportance && Object.keys(latest.detail.featureImportance).length > 0 && (
            <section className="fa-card p-5" data-testid="feature-importance">
              <h2 className="fa-section-title mb-3">What the model leans on</h2>
              <div className="space-y-2">
                {Object.entries(latest.detail.featureImportance)
                  .sort(([, a], [, b]) => Math.abs(b) - Math.abs(a))
                  .slice(0, 10)
                  .map(([name, value], _, all) => {
                    const max = Math.abs(all[0][1]) || 1;
                    return (
                      <div key={name} className="flex items-center gap-3">
                        <span className="fa-caption text-fa-frost w-44 truncate" title={name}>
                          {name}
                        </span>
                        <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                          <div
                            className="h-full rounded-full bg-fa-frost/70"
                            style={{ width: `${Math.max(4, (Math.abs(value) / max) * 100)}%` }}
                          />
                        </div>
                        <span className="fa-caption text-3 tabular-nums w-14 text-right">
                          {value.toFixed(1)}
                        </span>
                      </div>
                    );
                  })}
              </div>
            </section>
          )}

          {/* Runs ledger */}
          <section className="fa-card p-5" data-testid="iteration-ledger">
            <h2 className="fa-section-title mb-3">Iteration ledger</h2>
            {!data || data.runs.length === 0 ? (
              <p className="fa-body text-3">
                No runs yet — hit “Train + evaluate” to fit the first model on your ratings.
              </p>
            ) : (
              <ScrollRegion label="Iteration ledger runs" testId="iteration-ledger-scroll">
                <table className="w-full text-left">
                  <thead>
                    <tr className="fa-overline text-3 border-b border-fa-edge/50">
                      <th className="py-2 pr-4">iter</th>
                      <th className="py-2 pr-4">when</th>
                      <th className="py-2 pr-4">train / holdout</th>
                      <th className="py-2 pr-4">threshold</th>
                      <th className="py-2 pr-4">ndcg@10</th>
                      <th className="py-2 pr-4">popularity</th>
                      <th className="py-2 pr-4">95% range on the lift</th>
                      <th className="py-2 pr-4">p@10 (old)</th>
                      <th className="py-2 pr-4">gate</th>
                      <th className="py-2">old gate</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.runs.map((run) => (
                      <tr key={run.id} className="border-b border-fa-edge/30 fa-body">
                        <td className="py-2 pr-4 tabular-nums">{run.iteration}</td>
                        <td className="py-2 pr-4 fa-caption text-3">
                          {new Date(run.startedAt).toLocaleString()}
                        </td>
                        <td className="py-2 pr-4 tabular-nums">
                          {run.trainRowCount} / {run.holdoutRowCount}
                        </td>
                        <td className="py-2 pr-4 tabular-nums">
                          ≥{run.positiveThreshold}
                          {run.eval?.lowSample ? " (low sample)" : ""}
                        </td>
                        <td className="py-2 pr-4 tabular-nums">{num(run.eval?.ndcgAt10Model, 3)}</td>
                        <td className="py-2 pr-4 tabular-nums">{num(run.eval?.ndcgAt10Baseline, 3)}</td>
                        <td className="py-2 pr-4 tabular-nums">
                          {run.eval?.ndcgLiftCiLow == null
                            ? "—"
                            : `${signed(run.eval.ndcgLiftCiLow, 3)} … ${signed(run.eval.ndcgLiftCiHigh, 3)}`}
                        </td>
                        <td className="py-2 pr-4 tabular-nums text-3">
                          {pct(run.eval?.modelPrecisionAt10)} / {pct(run.eval?.baselinePrecisionAt10)}
                        </td>
                        <td className="py-2 pr-4">
                          {run.eval == null ? (
                            <span className="fa-caption text-3">—</span>
                          ) : run.eval.ndcgAt10Model == null ? (
                            <span className="fa-caption text-3" title="measured before the instrument repair">
                              not measured
                            </span>
                          ) : run.eval.passedGate ? (
                            <span className="fa-caption text-fa-success">passed</span>
                          ) : (
                            <span className="fa-caption text-fa-danger">not met</span>
                          )}
                        </td>
                        <td className="py-2">
                          {run.eval == null ? (
                            <span className="fa-caption text-3">—</span>
                          ) : run.eval.passedLegacyGate ? (
                            <span className="fa-caption text-fa-success">passed</span>
                          ) : (
                            <span className="fa-caption text-3">not met</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </ScrollRegion>
            )}
          </section>

          {latest?.detail.caveats && <p className="fa-caption text-3">{latest.detail.caveats.join(" · ")}</p>}
        </>
      )}
    </div>
  );
}
