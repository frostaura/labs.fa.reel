// Replay-mode tests for the surface harness: pure, no API, no database, no paid call.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { scoreSurfaces, stubKind, compareSurfaces, surfaceGates, rendered, inputArtifact, digestSeedKey } from "./surfaces-score.mjs";
import { STUB_WHY, HEDGE_WHY } from "./askreel-score.mjs";

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

// R1-15: the copy is scored as the user reads it, per rendering component.
const lens = (titleId, f) => ({ titleId, name: titleId, whyThis: null, lensClause: null, hook: null, reason: null, ...f });
const SLOWER = "For you, the emotional intensity may be the draw, though this is a much slower drama than the action and adventure you often prefer.";
const DETOUR = "For your usual appetite for brisk action and adventure, this is a quieter, heavier detour: a man returns to Ireland with nowhere to live but his car, then a…";
const CLEAN = "If you want a darker city-thriller that still moves cleanly, this is Jodie Foster in a New York revenge story: a woman, shaken by a brutal attack, starts hunting…";
const HEDGED_WHY = "Tense, though this procedural series is less aligned with your film-first taste.";

test("rendered copy: a rail card is read as lensClause ?? whyThis, a hero as hook || whyThis", () => {
  const m = scoreSurfaces(rec([
    { surface: "feed-row", key: "r", component: "card", cards: [
      lens(A, { lensClause: SLOWER, whyThis: "Fits." }), // the lens clause is what shows, and it hedges
      lens(B, { lensClause: CLEAN, whyThis: HEDGED_WHY }), // the hedging whyThis is never on screen
      lens(C, { whyThis: HEDGED_WHY }), // no lens copy: whyThis shows
      lens(X, { lensClause: "", whyThis: HEDGED_WHY }), // `??` keeps an empty clause: a blank line shows
    ] },
    { surface: "feed-hero", key: "h", component: "hero", cards: [
      lens(A, { hook: "You get a hopeful musical detour from the thrillers you usually reach for.", whyThis: "Fits." }),
      lens(B, { hook: "Ralph Fiennes turns memory into a slow-burn trap.", whyThis: HEDGED_WHY }),
      lens(C, { hook: "", whyThis: DETOUR }), // RecCardHero tests `card.hook ?` — an empty hook falls back
    ] },
  ], { eligible: [A, B, C, X] }));
  assert.equal(m.perSurface["feed-row"].hedgingWhy, 2, "SLOWER (lensClause) and C's whyThis; never B's or X's whyThis");
  assert.equal(m.perSurface["feed-hero"].hedgingWhy, 2, "A's hook and C's fallback whyThis; never B's whyThis");
  assert.deepEqual(m.perSurface["feed-row"].renderedFrom, { lensClause: 2, whyThis: 1 });
  assert.deepEqual(m.perSurface["feed-hero"].renderedFrom, { hook: 2, whyThis: 1 });
  assert.ok(m.hedges.some((h) => h.includes("[lensClause]") && h.includes("much slower drama")));
});

test("rendered copy: the Watch-tonight pin is read by its reason; Saved, person and typeahead cards carry none", () => {
  const m = scoreSurfaces(rec([
    { surface: "watch-tonight", key: "w", component: "watch-tonight", cards: [lens(C, { reason: "A quieter detour for a weeknight, though it is slower than your usual.", whyThis: "Fits." })] },
    { surface: "saved", key: "s", component: "none", cards: [lens(X, { whyThis: HEDGED_WHY, lensClause: SLOWER })] },
    { surface: "typeahead", key: "t", component: "none", cards: [lens(A, { whyThis: "Strong fit with your taste profile." })] },
  ]));
  assert.equal(m.perSurface["watch-tonight"].hedgingWhy, 1);
  assert.equal(m.perSurface.saved.hedgingWhy, 0, "nothing on a Saved grid card is read as a why-line");
  assert.equal(m.perSurface.typeahead.stubWhy, 0);
  // 99c0761's hero showed the lens clause beneath the hook, so a hedge there was on screen.
  assert.deepEqual(rendered({ key: "h", component: "hero+lens" }, lens(A, { hook: "Ralph Fiennes turns memory into a slow-burn trap.", lensClause: DETOUR, whyThis: "Fits." })).from, "hook+lensClause");
  assert.equal(HEDGE_WHY.test(rendered({ key: "h", component: "hero+lens" }, lens(A, { hook: "A trap.", lensClause: DETOUR })).text), true);
  assert.equal(HEDGE_WHY.test(rendered({ key: "h", component: "hero" }, lens(A, { hook: "A trap.", lensClause: DETOUR })).text), false, "today's hero does not show the clause");
  assert.deepEqual(rendered({ key: "h", component: "hero+lens" }, lens(A, { whyThis: "Fits.", lensClause: DETOUR })), { text: "Fits.", from: "whyThis" }, "no hook: the templated line alone");
  assert.throws(() => rendered({ key: "k", component: "carousel" }, lens(A, {})), /unknown component 'carousel'/);
  assert.deepEqual(rendered({ key: "legacy" }, { why: "Fits." }), { text: "Fits.", from: "whyThis" }, "a version-2 recording is scored on its single why");
});

test("stub family: GenericFallback alone or as an anchored tail is a stub; a real reason that mentions taste is not", () => {
  for (const w of ["Strong fit with your taste profile.", "strong fit with your taste profile", "Shares the slow-burn dread of Hereditary, and strong fit with your taste profile.", "A horror pick aligned with your taste."]) {
    assert.ok(STUB_WHY.test(w), w);
  }
  for (const w of ["Strong fit with your taste for heists, and you rate Michael Mann 8.1.", "A strong fit for your taste profile's love of noir.", "Shares the slow-burn dread of Hereditary."]) {
    assert.ok(!STUB_WHY.test(w), w);
  }
  const m = scoreSurfaces(rec([{ surface: "more", key: "m", component: "card", cards: [lens(A, { whyThis: "Strong fit with your taste profile." }), lens(B, { whyThis: "Shares the dread of Hereditary." })] }]));
  assert.equal(m.summary.stubRate, 0.5);
});

test("stub kinds (IR2S-04): the once-per-page generic line, the composed tail (IR2S-01) and the aligned template are each a stub, and the report says which", () => {
  // Verbatim from the round-2 integration run (9f42c9d, keyless).
  const composed = ["Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride, and Strong fit with your taste profile.",
    "Explores murder mystery, like Sherlock: The Abominable Bride, and Strong fit with your taste profile.",
    "Same writer as Sherlock: The Abominable Bride: Arthur Conan Doyle, and Strong fit with your taste profile.",
    "Shares Twitches Too's theme of magic, and Strong fit with your taste profile."];
  for (const w of composed) assert.equal(stubKind(w), "composed", w);
  assert.equal(stubKind("Strong fit with your taste profile."), "generic");
  assert.equal(stubKind("A drama pick aligned with your taste."), "aligned");
  assert.equal(stubKind("Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride."), null);
  // One bare generic line per rail is what the paginator allows; every one still counts. Eight genre rails
  // with one each, a composed tail on an anchored row: 9 stubs, by kind.
  const rails = Array.from({ length: 8 }, (_, i) => ({ surface: "genre-rail", key: `genre:${i}`, component: "card",
    cards: [lens(`g${i}a`, { whyThis: "Strong fit with your taste profile." }), lens(`g${i}b`, { whyThis: "Shares the dread of Hereditary." })] }));
  const row = { surface: "feed-row", key: "because-you-loved:Sherlock", component: "card", anchorTitleId: X, cards: [lens(A, { whyThis: composed[0] }), lens(B, { whyThis: "You rate Arthur Conan Doyle 8.4." })] };
  const s = scoreSurfaces(rec([...rails, row])).summary;
  assert.deepEqual(s.stubKinds, { generic: 8, composed: 1, aligned: 0 });
  assert.equal(s.stubRate, 9 / 18);
  // Vary one card: the composed tail replaced by its clause alone leaves only the generic lines.
  const fixed = { ...row, cards: [lens(A, { whyThis: "Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride." }), row.cards[1]] };
  assert.deepEqual(scoreSurfaces(rec([...rails, fixed])).summary.stubKinds, { generic: 8, composed: 0, aligned: 0 });
});

test("hedge family: the lens copy's comparisons and detours count; a pitch's own contrast does not", () => {
  for (const w of [SLOWER, DETOUR, "You get a Nashville detour and a reflective change of pace.", "You usually want brisk action or adventure, but this is a quieter kind of journey: Mark joins Kurt.",
    "Brandon Beckett leads soldiers against a militia in this military action film, not a ninja adventure for you.", "Its brooding series is slower than what you usually go for."]) {
    assert.ok(HEDGE_WHY.test(w), w);
  }
  for (const w of [CLEAN, "Give this a try if you want grief handled through a friend, not a speech.", "Ralph Fiennes turns memory into a slow-burn trap.",
    "Shares the slow-burn dread of Hereditary, and you rate Ari Aster 8.2.", "A brisk heist you will like more than the sequel."]) {
    assert.ok(!HEDGE_WHY.test(w), w);
  }
});

test("compare gates card volume: a surface that keeps answering with a fraction of its cards regresses", () => {
  const cards = (n, prefix = "t") => Array.from({ length: n }, (_, i) => card(`${prefix}${i}`));
  const run = (similar, anchoredRow) => scoreSurfaces({ ...rec([
    { surface: "similar", key: "s", component: "card", anchorTitleId: X, status: 200, cards: cards(similar, "s") },
    { surface: "feed-row", key: "because", component: "card", anchorTitleId: X, status: 200, cards: cards(anchoredRow, "f") },
  ], { eligible: [...cards(60, "s"), ...cards(66, "f")].map((c) => c.titleId) }), snapshotId: "s1" });
  const base = run(60, 66);
  assert.equal(base.summary.anchoredCards, 126);
  const shrunk = compareSurfaces(run(5, 6), base, gates);
  assert.equal(shrunk.rows.find((r) => r.metric === "volume:similar").status, "REGRESSION (volume)");
  assert.equal(shrunk.rows.find((r) => r.metric === "volume:anchored").status, "REGRESSION (volume)", "126 → 11");
  const half = compareSurfaces(run(30, 33), base, gates);
  assert.equal(half.rows.filter((r) => r.metric.startsWith("volume:")).every((r) => r.status === "pass"), true, "half the baseline is the floor, inclusive");
  assert.equal(compareSurfaces(run(29, 66), base, gates).rows.find((r) => r.metric === "volume:similar").status, "REGRESSION (volume)");
});

test("compare: runs are comparable by the artifact their build started from, not the one a retrain produced", () => {
  const s = (artifact, fresh = true) => ({ ...scoreSurfaces({ ...rec([{ surface: "genre-rail", key: "g", status: 200, cards: [card(A)] }]), snapshotId: "s1", fresh, artifact }) });
  const a = s({ input: "art-0", id: "art-1" }), b = s({ input: "art-0", id: "art-2" });
  assert.equal(inputArtifact(a.summary), "art-0");
  assert.equal(compareSurfaces(a, b, gates).refused, null, "two Fresh builds from one snapshot each retrain to a new id");
  assert.match(compareSurfaces(s({ input: "art-9", id: "art-1" }), b, gates).refused, /model artifacts differ \(art-9 vs art-0\)/);
  assert.equal(compareSurfaces(s({ id: "art-1" }), b, gates).refused, null, "a Fresh recording without an input artifact predates the field");
  assert.match(compareSurfaces(s({ id: "art-1" }, false), s({ id: "art-2" }, false), gates).refused, /art-1 vs art-2/, "with no build, the serving artifact is the input");
});

test("compare (IREV3S-02): runs whose digest arrivals were made differently are refused, not scored", () => {
  const s = (digestSeed) => scoreSurfaces({ ...rec([{ surface: "digest", key: "digest", status: 200, cards: [card(A)] }]), snapshotId: "s1", fresh: true, digestSeed });
  const seeded = s({ titles: [A, B], requested: 60, skipped: null, buildStatus: "Succeeded" });
  assert.equal(seeded.summary.digestSeed, "seeded:60");
  assert.equal(digestSeedKey({ digestSeed: null }), "none");
  assert.equal(digestSeedKey({ digestSeed: { titles: [], requested: 60, skipped: "the schema predates FirstSeenAt" } }), "none");
  assert.equal(digestSeedKey({}), "unrecorded");
  assert.equal(compareSurfaces(seeded, seeded, gates).refused, null);
  assert.match(compareSurfaces(seeded, s(undefined), gates).refused, /digest arrivals were made differently \(seeded:60 vs unrecorded\)/);
  assert.match(compareSurfaces(s(null), seeded, gates).refused, /none vs seeded:60/);
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
