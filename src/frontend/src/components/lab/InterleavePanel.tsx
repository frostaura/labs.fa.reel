import { Swords } from "lucide-react";
import type { InterleaveSummary } from "../../store/api";
import ScrollRegion from "./ScrollRegion";

/**
 * The interleaving readout on /lab.
 *
 * Three things it is written to make unavoidable. First, the cadence: at roughly one decided slate
 * a day this comparison takes two to three months, and comparisons serialise — so the panel says
 * that in words rather than leaving an eager reader to infer weeks from a moving p-value. Second,
 * that peeking is free: the whole reason for an always-valid test is that looking at this number
 * hourly costs nothing, and a founder who does not know that will either avoid looking or over-read
 * an early dip. Third, that a tie is the ordinary outcome — most slates earn no outcome credit at
 * all, and a "decided slates" count far below "slates" is the instrument working, not failing.
 */
export default function InterleavePanel({ data }: { data: InterleaveSummary | undefined }) {
  if (!data) {
    return null;
  }

  const rate = data.preferenceRateA;
  const leader = rate == null ? null : rate > 0.5 ? data.policyA : rate < 0.5 ? data.policyB : null;

  return (
    <section className="fa-card p-5 space-y-5" data-testid="interleave-panel">
      <div className="flex items-start justify-between gap-4 flex-wrap">
        <div>
          <h2 className="fa-section-title flex items-center gap-2">
            <Swords className="h-4 w-4 text-fa-frost" /> Interleaving — {data.policyA} vs {data.policyB}
          </h2>
          <p className="fa-caption text-3 mt-1">
            While it runs, every Fresh build drafts each of tonight's rows alternately from both rankers.
            Whichever ranker's cards you save, watch or rate 8+ wins that row. The only online instrument
            that works with one user.
          </p>
        </div>
        <span
          className={`fa-caption px-2 py-1 rounded ${
            data.enabled ? "bg-fa-frost/10 text-fa-frost" : "bg-fa-glass text-3"
          }`}
          data-testid="interleave-state"
        >
          {data.enabled ? "running" : "not running"}
        </span>
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">rows drafted</p>
          <p className="fa-stat-value" data-testid="interleave-slates">{data.slates}</p>
          <p className="fa-caption text-3 mt-1">{data.ties} tied — no preference either way</p>
        </div>
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">decided rows</p>
          <p className="fa-stat-value" data-testid="interleave-decided">{data.decidedSlates}</p>
          <p className="fa-caption text-3 mt-1">
            {data.winsA} · {data.winsB}
          </p>
        </div>
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">always-valid p</p>
          <p
            className={`fa-stat-value ${data.significant ? "text-fa-success" : "text-fa-frost-bright"}`}
            data-testid="interleave-p"
          >
            {data.alwaysValidP.toFixed(3)}
          </p>
          <p className="fa-caption text-3 mt-1">
            {data.significant ? `decided at α ${data.alpha}` : `needs ≤ ${data.alpha}`}
          </p>
        </div>
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">{data.policyA} win rate</p>
          <p className="fa-stat-value" data-testid="interleave-rate">
            {rate == null ? "—" : `${(rate * 100).toFixed(0)}%`}
          </p>
          <p className="fa-caption text-3 mt-1">
            range {(data.ciLow * 100).toFixed(0)}–{(data.ciHigh * 100).toFixed(0)}%
          </p>
        </div>
      </div>

      {/* The honest cadence. This is the sentence the adversarial review insisted on: the
          instrument is right and it is slow, and being told "weeks" would have been a lie that
          caps how many ideas can ever be validated. */}
      <p className="fa-body text-3" data-testid="interleave-cadence">
        A 65/35 preference takes about {data.slatesToDecisionAt65} decided rows to call. At around one
        decided row a day that is two to three months per comparison, and comparisons run one at a
        time.
        {data.decidedSlatesPerDay != null && (
          <>
            {" "}
            You are averaging {data.decidedSlatesPerDay.toFixed(2)} decided rows a day
            {data.estimatedDaysToDecision != null && (
              <> — about {Math.round(data.estimatedDaysToDecision)} days left at that rate</>
            )}
            .
          </>
        )}{" "}
        Look at this number as often as you like: the test stays honest under repeated peeking, which
        a fixed-sample test would not.
      </p>

      {data.standings.length > 0 && (
        <ScrollRegion label="Interleaving standings">
          <table className="w-full text-left" data-testid="interleave-standings">
            <thead>
              <tr className="fa-overline text-3 border-b border-fa-edge/50">
                <th className="py-2 pr-4">ranker</th>
                <th className="py-2 pr-4">rows</th>
                <th className="py-2 pr-4">cards</th>
                <th className="py-2 pr-4">saved / watched / 8+</th>
                <th className="py-2">rows won</th>
              </tr>
            </thead>
            <tbody>
              {data.standings.map((s) => (
                <tr key={s.policyId} className="border-b border-fa-edge/30 fa-body">
                  <td className="py-2 pr-4">
                    {s.policyId}
                    {s.team != null && <span className="text-3"> · team {s.team}</span>}
                    {s.description != null && (
                      <span className="block fa-caption text-3">{s.description}</span>
                    )}
                  </td>
                  <td className="py-2 pr-4 tabular-nums">{s.slates}</td>
                  <td className="py-2 pr-4 tabular-nums">{s.cards}</td>
                  <td className="py-2 pr-4 tabular-nums">{s.credits}</td>
                  <td className="py-2 tabular-nums">{s.slateWins}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </ScrollRegion>
      )}

      <p className="fa-caption text-3">
        mixing τ {data.tau} · α {data.alpha} · sign-test p {data.signTestP.toFixed(3)} (a sanity
        check only — it assumes you looked exactly once, so it always reads lower)
        {leader != null && !data.significant && <> · {leader} is ahead, but not yet by enough to call</>}
      </p>

      {data.slates === 0 && (
        <p className="fa-body text-3" data-testid="interleave-empty">
          No rows drafted yet. Set INTERLEAVE_ENABLED=true and rebuild the feed to start the
          comparison — see docs/interleaving.md.
        </p>
      )}
    </section>
  );
}
