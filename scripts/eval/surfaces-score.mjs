// Pure scorer for the surface harness (scripts/eval/surfaces.mjs). No API, no database, no paid call:
// everything it reads is frozen into the recording, so a recorded run re-scores identically in CI.
import { STUB_WHY, HEDGE_WHY, NON_LATIN } from "./askreel-score.mjs";

// Suggestion surfaces: everything shown must pass eligibility. Lookup surfaces (Saved, Watch tonight,
// typeahead, person pages) legitimately show titles you know; there the question is the footprint.
export const SUGGESTION = new Set(["feed-hero", "feed-row", "more", "genre-rail", "similar", "digest"]);
// Lookup surfaces where a watched or rated title is a defect (G-1: watching something should take it off
// your list). Typeahead and person pages show what you have seen by design, so they are reported only.
export const FOOTPRINT_GATED = new Set(["saved", "watch-tonight"]);

const blockFailed = (b) => b.status !== undefined && b.status !== 200;

export function scoreSurfaces(rec) {
  const f = rec.facts;
  const eligible = new Set(f.eligible);
  const seenSet = new Set(f.seen ?? []);
  const perSurface = {};
  const bump = (s) => (perSurface[s] ??= { blocks: 0, errors: 0, errorKeys: [], emptyBlocks: 0, cards: 0, stubWhy: 0, hedgingWhy: 0, nonLatinWhy: 0, leaks: 0, leakNames: [], anchored: 0, evidencePass: 0, footprint: 0, footprintNames: [] });
  const hedges = [];
  for (const block of rec.blocks) {
    const s = bump(block.surface);
    s.blocks++;
    // A surface that answered with an HTTP error is NOT a surface with zero leaks: it is no data.
    if (blockFailed(block)) { s.errors++; s.errorKeys.push(`${block.key} (HTTP ${block.status})`); continue; }
    if (block.cards.length === 0) s.emptyBlocks++;
    for (const c of block.cards) {
      s.cards++;
      if (c.why && STUB_WHY.test(c.why.trim())) s.stubWhy++;
      if (c.why && HEDGE_WHY.test(c.why)) { s.hedgingWhy++; if (hedges.length < 40) hedges.push(`${block.key} · ${c.name}: ${c.why}`); }
      if (c.why && NON_LATIN.test(c.why)) s.nonLatinWhy++;
      const ok = eligible.has(c.titleId);
      if (SUGGESTION.has(block.surface) && !ok) { s.leaks++; if (s.leakNames.length < 20) s.leakNames.push(`${c.name} (${block.key})`); }
      // On a lookup surface the question is not eligibility (a Saved title is ineligible by being saved)
      // but whether a title already watched or rated is still sitting there (Saved, Watch tonight, G-1).
      if (!SUGGESTION.has(block.surface) && seenSet.has(c.titleId)) { s.footprint++; if (s.footprintNames.length < 20) s.footprintNames.push(`${c.name} (${block.key})`); }
      if (block.anchorTitleId && c.titleId !== block.anchorTitleId) {
        s.anchored++;
        const e = f.evidence[`${block.anchorTitleId}|${c.titleId}`];
        if (e && (e.sharedKeywords >= 2 || (e.cosine !== null && f.randomCosineP90 !== null && e.cosine >= f.randomCosineP90))) s.evidencePass++;
      }
    }
  }
  // Cross-rail repeats: one title on more than one row of the feed page as served.
  const seen = new Map();
  for (const block of rec.blocks.filter((b) => (b.surface === "feed-hero" || b.surface === "feed-row") && !blockFailed(b))) {
    for (const c of block.cards) seen.set(c.titleId, [...(seen.get(c.titleId) ?? []), block.key]);
  }
  const repeats = [...seen.entries()].filter(([, keys]) => new Set(keys).size > 1);
  const sugg = Object.entries(perSurface).filter(([s]) => SUGGESTION.has(s));
  const total = (k) => sugg.reduce((n, [, v]) => n + v[k], 0);
  const cards = total("cards");
  const anchored = total("anchored");
  // Coverage: a surface that errored, or a suggestion surface that came back empty, has NO measurement.
  const surfacesWithoutData = Object.entries(perSurface)
    .filter(([s, v]) => v.errors > 0 || (SUGGESTION.has(s) && v.cards === 0))
    .map(([s, v]) => (v.errors > 0 ? `${s}: ${v.errors} HTTP error(s) — ${v.errorKeys.slice(0, 3).join(", ")}` : `${s}: no cards`));
  const ratio = (num, den) => (den ? num / den : null);
  return {
    summary: {
      label: rec.label, build: rec.build ?? null, buildInfo: rec.buildInfo ?? null, snapshotId: rec.snapshotId, fresh: rec.fresh, buildStatus: rec.buildStatus ?? null,
      artifact: rec.artifact ?? null, modelKey: rec.modelKey ?? "unrecorded",
      suggestionCards: cards, stubRate: ratio(total("stubWhy"), cards), exclusionLeaks: total("leaks"), leakRate: ratio(total("leaks"), cards),
      crossRailRepeats: repeats.length, evidenceShare: ratio(total("evidencePass"), anchored), anchoredCards: anchored,
      hedgingWhyLines: total("hedgingWhy"), hedgeRate: ratio(total("hedgingWhy"), cards), nonLatinWhyLines: total("nonLatinWhy"),
      lookupFootprint: Object.entries(perSurface).filter(([s]) => FOOTPRINT_GATED.has(s)).reduce((n, [, v]) => n + v.footprint, 0),
      httpErrors: Object.values(perSurface).reduce((n, v) => n + v.errors, 0), surfacesWithoutData, complete: surfacesWithoutData.length === 0,
      copyClaimsChecked: null, randomCosineP90: f.randomCosineP90,
      exposuresWritten: rec.exposuresWritten, costUsd: rec.costUsd ?? null, traktCalls: (rec.ledger ?? []).filter((r) => r.provider === "Trakt").reduce((n, r) => n + r.calls, 0),
      jobsDrainedBeforeFetch: rec.jobsDrainedBeforeFetch ?? null, wallSeconds: rec.wallSeconds,
    },
    perSurface,
    hedges,
    repeats: repeats.map(([id, keys]) => ({ titleId: id, name: rec.names[id] ?? id, rows: [...new Set(keys)] })),
    ledger: rec.ledger,
  };
}

// Regression check of a surface run against a baseline (eval/surfaces/gates.json). Runs on different
// snapshots or artifacts are incomparable; a surface the baseline measured and this run did not (an
// HTTP error, an empty suggestion surface, a surface missing entirely) is a regression, never a pass.
export function compareSurfaces(current, baseline, gates) {
  const c = current.summary, b = baseline.summary;
  if ((c.snapshotId ?? null) !== (b.snapshotId ?? null)) return { refused: `snapshots differ (${c.snapshotId} vs ${b.snapshotId})`, rows: [] };
  if (c.artifact?.id && b.artifact?.id && c.artifact.id !== b.artifact.id) return { refused: `model artifacts differ (${c.artifact.id} vs ${b.artifact.id})`, rows: [] };
  const rows = [];
  const measured = (sum, ps, s) => ps[s] && ps[s].errors === 0 && (!SUGGESTION.has(s) || ps[s].cards > 0);
  for (const s of Object.keys(baseline.perSurface)) {
    if (!measured(b, baseline.perSurface, s)) continue;
    if (!measured(c, current.perSurface, s)) rows.push({ metric: `coverage:${s}`, baseline: baseline.perSurface[s].cards, current: current.perSurface[s]?.cards ?? null, status: "REGRESSION (no data)" });
  }
  for (const t of gates.regression) {
    const cv = c[t.metric], bv = b[t.metric];
    if (bv === null || bv === undefined) { rows.push({ ...t, current: cv ?? null, baseline: null, status: "no baseline data" }); continue; }
    if (cv === null || cv === undefined) { rows.push({ ...t, current: null, baseline: bv, status: "REGRESSION (no data)" }); continue; }
    const allowed = bv + (t.higherIsBetter ? -t.tolerance : t.tolerance);
    const pass = t.higherIsBetter ? cv >= allowed : cv <= allowed;
    rows.push({ ...t, current: cv, baseline: bv, allowed, status: pass ? "pass" : "REGRESSION" });
  }
  return { refused: null, rows };
}

// The absolute (task 8) targets, evaluated on one run. A surface with no data fails httpErrors or shows
// up in summary.surfacesWithoutData; a metric with no value is "no data", never a pass.
export function surfaceGates(metrics, gates) {
  return gates.gates.map((g) => {
    const v = metrics.summary[g.metric];
    const status = v === null || v === undefined ? "no data" : (g.op === ">=" ? v >= g.target : v <= g.target) ? "pass" : "FAIL";
    return { ...g, value: v ?? null, status };
  });
}
