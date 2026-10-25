// Replay-mode tests for the surface harness: pure, no API, no database, no paid call.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { scoreSurfaces, compareSurfaces, surfaceGates } from "./surfaces-score.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const gates = JSON.parse(readFileSync(join(repo, "eval", "surfaces", "gates.json"), "utf8"));
const A = "a", B = "b", C = "c", X = "x";
const rec = (blocks, facts = {}) => ({ label: "t", blocks, names: {}, ledger: [], facts: { eligible: [A, B], seen: [C], evidence: {}, randomCosineP90: 0.4, ...facts } });
const card = (titleId, why = "Fits.") => ({ titleId, name: titleId, why });

test("a suggestion surface leaks an ineligible title; a lookup surface does not, but counts what was watched", () => {
  const m = scoreSurfaces(rec([
    { surface: "genre-rail", key: "genre:action", cards: [card(A), card(C)] },
    { surface: "saved", key: "saved", cards: [card(C), card(X)] },
    { surface: "typeahead", key: "typeahead:c", cards: [card(C)] },
  ]));
  assert.equal(m.summary.exclusionLeaks, 1);
  assert.equal(m.perSurface.saved.leaks, 0);
  assert.equal(m.perSurface.saved.footprint, 1, "only the watched/rated title, not the merely saved one");
  assert.equal(m.perSurface.typeahead.footprint, 1);
  assert.equal(m.summary.lookupFootprint, 1, "typeahead shows what you have seen by design; only Saved / Watch tonight are gated");
});

test("an HTTP error or an empty suggestion surface is no data, never zero leaks", () => {
  const m = scoreSurfaces(rec([
    { surface: "feed-hero", key: "tonight", status: 200, cards: [card(A)] },
    { surface: "digest", key: "digest", status: 200, cards: [] },
    { surface: "similar", key: "similar:x", status: 500, cards: [card(C)] },
    { surface: "saved", key: "saved", status: 200, cards: [] },
  ]));
  assert.equal(m.summary.httpErrors, 1);
  assert.equal(m.summary.exclusionLeaks, 0, "the errored block's cards are not scored");
  assert.equal(m.summary.complete, false);
  assert.deepEqual(m.summary.surfacesWithoutData.map((x) => x.split(":")[0]), ["digest", "similar"], "an empty Saved is a real answer; an empty digest is not a measurement");
  assert.equal(surfaceGates(m, gates).find((g) => g.metric === "httpErrors").status, "FAIL");
});

test("hedging and non-Latin script are counted on suggestion surfaces", () => {
  const m = scoreSurfaces(rec([{ surface: "feed-hero", key: "t", cards: [card(A, "Tense, though this procedural series is less aligned with your film-first偏好."), card(B)] }]));
  assert.equal(m.summary.hedgingWhyLines, 1);
  assert.equal(m.summary.nonLatinWhyLines, 1);
  assert.equal(m.summary.hedgeRate, 0.5);
});

test("compare: a surface that goes dark, a worse rate, or a different snapshot never passes", () => {
  const base = scoreSurfaces({ ...rec([{ surface: "digest", key: "d", status: 200, cards: [card(A), card(B)] }, { surface: "genre-rail", key: "g", status: 200, cards: [card(A)] }]), snapshotId: "s1" });
  const same = compareSurfaces(base, base, gates);
  assert.equal(same.rows.filter((r) => r.status.startsWith("REGRESSION")).length, 0);
  const dark = scoreSurfaces({ ...rec([{ surface: "digest", key: "d", status: 503, cards: [] }, { surface: "genre-rail", key: "g", status: 200, cards: [card(A)] }]), snapshotId: "s1" });
  const res = compareSurfaces(dark, base, gates);
  assert.equal(res.rows.find((r) => r.metric === "coverage:digest").status, "REGRESSION (no data)");
  assert.equal(res.rows.find((r) => r.metric === "httpErrors").status, "REGRESSION");
  const leaky = scoreSurfaces({ ...rec([{ surface: "digest", key: "d", status: 200, cards: [card(A), card(C)] }, { surface: "genre-rail", key: "g", status: 200, cards: [card(A)] }]), snapshotId: "s1" });
  assert.equal(compareSurfaces(leaky, base, gates).rows.find((r) => r.metric === "leakRate").status, "REGRESSION");
  assert.match(compareSurfaces({ ...base, summary: { ...base.summary, snapshotId: "s2" } }, base, gates).refused, /snapshots differ/);
});

test("stub rate counts the stub family over suggestion cards only", () => {
  const m = scoreSurfaces(rec([
    { surface: "feed-row", key: "r", cards: [card(A, "A horror pick aligned with your taste."), card(B)] },
    { surface: "typeahead", key: "q", cards: [card(A, "A horror pick aligned with your taste.")] },
  ]));
  assert.equal(m.summary.stubRate, 0.5);
});

test("evidence: two shared keywords or a cosine above the random-pair bar passes; neither fails", () => {
  const m = scoreSurfaces(rec([{ surface: "similar", key: "s", anchorTitleId: X, cards: [card(A), card(B), card(C)] }], {
    eligible: [A, B, C],
    evidence: { [`${X}|${A}`]: { sharedKeywords: 2, cosine: 0.1 }, [`${X}|${B}`]: { sharedKeywords: 0, cosine: 0.41 }, [`${X}|${C}`]: { sharedKeywords: 1, cosine: 0.39 } },
  }));
  assert.equal(m.summary.anchoredCards, 3);
  assert.equal(m.summary.evidenceShare, 2 / 3);
});

test("cross-rail repeats: one title on two feed rows is one repeat", () => {
  const m = scoreSurfaces(rec([
    { surface: "feed-hero", key: "tonight", cards: [card(A)] },
    { surface: "feed-row", key: "deep-cuts", cards: [card(A), card(B)] },
    { surface: "more", key: "deep-cuts (page 2)", cards: [card(A)] },
  ]));
  assert.equal(m.summary.crossRailRepeats, 1, "a next page is not a second row of the feed page");
});

test("replay: every committed surface baseline re-scores to its committed metrics", () => {
  const root = join(repo, "eval", "surfaces", "baselines");
  const files = existsSync(root) ? readdirSync(root, { recursive: true }).filter((p) => String(p).endsWith("recording.json")) : [];
  for (const rel of files) {
    const recording = JSON.parse(readFileSync(join(root, String(rel)), "utf8"));
    const stored = JSON.parse(readFileSync(join(root, dirname(String(rel)), "metrics.json"), "utf8"));
    assert.deepEqual(scoreSurfaces(recording), stored, `${rel} no longer replays to its stored metrics`);
  }
});
