// Replay-mode tests for the golden eval: pure, no API, no database, no paid call.
//   node --test 'scripts/eval/*.test.mjs'
// Each test varies one field of a minimal recording, so a green result means the rule under test fired,
// not that the fixture happened to satisfy everything.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { spawn } from "node:child_process";
import { score, evaluateTurn, isEcho, compare, STUB_WHY, HEDGE_WHY, NON_LATIN } from "./askreel-score.mjs";
import { compactTurn, runDay, unusedOpenTruth } from "./askreel.mjs";
import { envKeyHash, classifyKey, modelKeyFor, holdAllBuildSlots } from "./guards.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const gates = JSON.parse(readFileSync(join(repo, "eval", "askreel", "gates.json"), "utf8"));
const prices = JSON.parse(readFileSync(join(repo, "eval", "askreel", "prices.json"), "utf8"));

const A = "00000000-0000-0000-0000-00000000000a";
const B = "00000000-0000-0000-0000-00000000000b";
const C = "00000000-0000-0000-0000-00000000000c";
const D = "00000000-0000-0000-0000-00000000000d";
const card = (titleId, name, extra = {}) => ({ titleId, name, why: `${name} fits.`, ...extra });
const turn = (query, cards, extra = {}) => ({ query, status: 200, failed: false, turnId: `t-${query}`, cards, belowFloor: [], nearMiss: [], reply: "Here you go.", totalMs: 1000, firstCardMs: 500, mediaTypes: null, mode: cards.length ? "Picks" : "NoneFound", ...extra });
const facts = (extra = {}) => ({ eligible: [A, B, C], media: { [A]: "Movie", [B]: "Movie", [C]: "Show", [D]: "Movie" }, truth: {}, server: {}, named: {}, ...extra });

test("echo: a quoted message is an echo, restating a short topic is not", () => {
  assert.equal(isEcho("ninja movies", "Here are some action picks for “ninja movies” — leaning in."), true);
  assert.equal(isEcho("romantic comedies", "Romantic comedies are blocked by your standing filters."), false);
  assert.equal(isEcho("a slow film where the silences say more", "You want a slow film where the silences say more, so here goes."), true);
  assert.equal(isEcho("ninja movies", null), false);
});

test("stub and hedge families match the v1 shapes and nothing ordinary", () => {
  assert.ok(STUB_WHY.test("A horror pick aligned with your taste."));
  assert.ok(STUB_WHY.test("An action pick aligned with your taste."));
  assert.ok(!STUB_WHY.test("A tense horror pick with a clever twist."));
  assert.ok(HEDGE_WHY.test("Not strictly a ninja film, but close."));
  assert.ok(HEDGE_WHY.test("It isn't a ninja film, but the swordplay lands."));
  assert.ok(!HEDGE_WHY.test("A ninja clan war with real swordplay."));
  // The feed's hedges from the 99c0761 surface recording.
  assert.ok(HEDGE_WHY.test("A sharp procedural, though this procedural series is less aligned with your film-first taste."));
  assert.ok(HEDGE_WHY.test("Clever and tense, though its mystery focus may be less to your taste."));
  assert.ok(HEDGE_WHY.test("Gripping, though its ongoing drama is a series rather than your usual film pick."));
  assert.ok(HEDGE_WHY.test("You may connect with the coming-of-age story, but this heavy drama is a weaker match for your action lean."));
  assert.ok(HEDGE_WHY.test("You get a tense murder case, though the film leans more into drama than action."));
  assert.ok(HEDGE_WHY.test("You get a tense mystery, but this psychological mystery is a less certain match."));
  // A "though" that does not argue against the pick is not a hedge.
  assert.ok(!HEDGE_WHY.test("A quieter film, though just as tense as the ones you rated highest."));
  assert.ok(!HEDGE_WHY.test("It favours character over spectacle, which suits your highest ratings."));
});

test("non-Latin script in English copy is caught; accents and punctuation are not", () => {
  assert.ok(NON_LATIN.test("less aligned with your film-first偏好"));
  assert.ok(NON_LATIN.test("a Кино you will like"));
  assert.ok(!NON_LATIN.test("Amélie’s whimsy — 100% your kind of café film, naïve and warm."));
});

test("honest-empty: zero eligible supply passes only with zero picks", () => {
  const f = facts({ truth: { ninja: { members: [D], supply: { Any: { matching: 1, eligible: 0 } } } } });
  const def = { expect: { concept: "ninja" } };
  assert.equal(evaluateTurn(def, turn("ninja movies", []), 0, [turn("ninja movies", [])], f).checks.honestEmpty, true);
  const filled = turn("ninja movies", [card(A, "Off topic")]);
  const r = evaluateTurn(def, filled, 0, [filled], f);
  assert.equal(r.checks.honestEmpty, false);
  assert.equal(r.checks.mode, false, "the expected mode derives to NoneFound when supply is zero");
  // A 500 or a timeout shows zero picks too — and is not honest.
  const crashed = turn("ninja movies", [], { status: 500, failed: true, mode: null });
  assert.equal(evaluateTurn(def, crashed, 0, [crashed], f).checks.honestEmpty, false);
  const timedOut = turn("ninja movies", [], { status: 200, failed: true, error: "client_timeout (idle)", mode: null });
  assert.equal(evaluateTurn(def, timedOut, 0, [timedOut], f).checks.honestEmpty, false);
  // Zero picks that the engine did not report as none-found (e.g. a clarify) is not honest-empty either.
  const clarified = turn("ninja movies", [], { mode: "Clarify" });
  assert.equal(evaluateTurn(def, clarified, 0, [clarified], f).checks.honestEmpty, false);
});

test("a failed turn scores precision 0 instead of leaving the precision denominator", () => {
  const f = facts({ truth: { heist: { members: [A], supply: { Any: { matching: 3, eligible: 2 } } } } });
  const ok = turn("heist movies", []);
  assert.equal(evaluateTurn({ expect: { concept: "heist" } }, ok, 0, [ok], f).precision, undefined, "an empty answer that ran is a missed supply, not precision");
  const failed = turn("heist movies", [], { status: 500, failed: true });
  assert.equal(evaluateTurn({ expect: { concept: "heist" } }, failed, 0, [failed], f).precision, 0);
  assert.equal(evaluateTurn({ expect: { mustNot: ["heist"] } }, failed, 0, [failed], f).precision, 0);
});

test("precision@8 counts only truth members, and a must-not member is never precise", () => {
  const f = facts({ truth: { heist: { members: [A, B], supply: { Any: { matching: 3, eligible: 2 } } }, ghost: { members: [B], supply: {} } } });
  const t = turn("heist movies", [card(A, "Heist 1"), card(B, "Haunted heist"), card(C, "Romance")]);
  const r = evaluateTurn({ expect: { concept: "heist", mustNot: ["ghost"] } }, t, 0, [t], f);
  assert.equal(r.precision, 1 / 3);
  assert.deepEqual(r.mustNotViolations, ["Haunted heist"]);
});

test("leaks: a shown title outside the eligibility truth is a leak on every channel, trays included", () => {
  const t = turn("anything", [card(A, "Fine")], { belowFloor: [card(D, "Seen it")] });
  const r = evaluateTurn({ expect: {} }, t, 0, [t], facts());
  assert.deepEqual(r.leaks, ["Seen it"]);
});

test("media: words-scoped media and the request filter both bind", () => {
  const t = turn("only movies", [card(A, "Movie"), card(C, "Show")]);
  assert.deepEqual(evaluateTurn({ expect: { media: "Movie" } }, t, 1, [t, t], facts()).mediaViolations, ["Show"]);
  const filtered = { ...t, mediaTypes: ["tv"] };
  assert.deepEqual(evaluateTurn({ expect: {} }, filtered, 0, [filtered], facts()).mediaViolations, ["Movie"]);
});

test("references: v1 names and v2 handles both resolve against the recorded display order", () => {
  const first = turn("spy movies", [card(A, "Alpha"), card(B, "Bravo")]);
  const v1 = turn("more like the second one", [card(C, "Charlie")], { intent: { referenceTitles: ["Bravo"] } });
  assert.equal(evaluateTurn({ expect: { references: [{ turn: 1, rank: 2 }] } }, v1, 1, [first, v1], facts()).checks.reference, true);
  const v1wrong = { ...v1, intent: { referenceTitles: ["Alpha"] } };
  assert.equal(evaluateTurn({ expect: { references: [{ turn: 1, rank: 2 }] } }, v1wrong, 1, [first, v1wrong], facts()).checks.reference, false);
  const v2 = { ...v1, intent: null, references: ["T1.2"] };
  assert.equal(evaluateTurn({ expect: { references: [{ turn: 1, rank: 2 }] } }, v2, 1, [first, v2], facts()).checks.reference, true);
  const empty = turn("spy movies", []);
  const r = evaluateTurn({ expect: { references: [{ turn: 1, rank: 2 }] } }, v1, 1, [empty, v1], facts());
  assert.equal(r.referenceUnresolvable, true, "an honest empty first turn voids the reference premise rather than failing it");
  assert.equal(r.checks.reference, undefined);
});

test("critique: a re-shown rejected title fails, an honest empty answer passes", () => {
  const f = facts({ truth: { ninja: { members: [], supply: { Any: { matching: 5, eligible: 0 } } } } });
  const first = turn("ninja movies", [card(A, "Alpha")]);
  const def = { expect: { act: "CritiqueSet", concept: "ninja", rejectTurns: [1] } };
  const again = turn("none of these are ninja films", [card(A, "Alpha")]);
  assert.equal(evaluateTurn(def, again, 1, [first, again], f).checks.critique, false);
  const honest = turn("none of these are ninja films", []);
  assert.equal(evaluateTurn(def, honest, 1, [first, honest], f).checks.critique, true);
});

test("act: no data for an engine that emits none (v1); v2 is scored by its plan, and a failed turn fails", () => {
  const t = turn("is the third one any good?", []);
  assert.equal(evaluateTurn({ expect: { act: "ItemQuestion" } }, t, 1, [t, t], facts()).checks.act, undefined, "v1: turn position is not behaviour");
  assert.equal(evaluateTurn({ expect: { act: "NewRequest" } }, t, 0, [t], facts()).checks.act, undefined);
  assert.equal(evaluateTurn({ expect: { act: "ItemQuestion" } }, { ...t, act: "ItemQuestion" }, 1, [t, t], facts()).checks.act, true);
  assert.equal(evaluateTurn({ expect: { act: "ItemQuestion" } }, { ...t, act: "Refine" }, 1, [t, t], facts()).checks.act, false);
  const crashed = { ...t, act: null, failed: true, status: 500 };
  assert.equal(evaluateTurn({ expect: { act: "ItemQuestion" } }, crashed, 1, [t, crashed], facts(), { actObservable: true }).checks.act, false);
  const scenarios = [{ id: "s", categories: ["ordinal"], turns: [{ query: "a", expect: { act: "NewRequest" } }, { query: "b", expect: { act: "Refine" } }] }];
  const v1 = { label: "x", concurrency: 4, ledger: [], facts: facts(), scenarios: [{ id: "s", categories: ["ordinal"], turns: [turn("a", [card(A, "Alpha")]), turn("b", [card(B, "Bravo")])] }] };
  assert.equal(score(v1, scenarios, prices, gates).metrics.actClassification.n, 0);
  assert.equal(score(v1, scenarios, prices, gates).gates.find((g) => g.metric === "actClassification").status, "no data");
});

test("unrun turns are failed turns with failed checks, never a smaller denominator", () => {
  const f = facts({ truth: { heist: { members: [A], supply: { Any: { matching: 3, eligible: 2 } } } } });
  const scenarios = [{ id: "s", categories: ["critique"], turns: [
    { query: "heist movies", expect: { act: "NewRequest", concept: "heist" } },
    { query: "none of these", expect: { act: "CritiqueSet", rejectTurns: [1], conflict: false } },
    { query: "more like the first", expect: { references: [{ turn: 1, rank: 1 }] } },
  ] }];
  const full = { label: "x", concurrency: 4, ledger: [], facts: f, scenarios: [{ id: "s", categories: ["critique"], turns: [
    turn("heist movies", [card(A, "Alpha")], { act: "NewRequest" }), turn("none of these", [card(B, "Bravo")], { act: "CritiqueSet" }), turn("more like the first", [card(B, "Bravo")], { act: "Refine", references: ["T1.1"] })] }] };
  const crashed = { ...full, scenarios: [{ ...full.scenarios[0], turns: [full.scenarios[0].turns[0], { ...full.scenarios[0].turns[1], status: 500, failed: true, cards: [], mode: null, act: null }] }] };
  const m = score(crashed, scenarios, prices, gates);
  assert.equal(m.metrics.notRunTurns.value, 1);
  assert.equal(m.metrics.failedTurns.value, 2);
  assert.equal(m.metrics.critiqueCompliance.n, 1);
  assert.equal(m.metrics.critiqueCompliance.value, 0);
  assert.equal(m.metrics.referenceAccuracy.value, 0, "the reference turn never ran: a failed check, not a void one");
  assert.equal(m.metrics.conflictDetection.value, 0);
  assert.equal(m.metrics.actClassification.value, 1 / 2, "turn 1 passes, the crashed turn 2 fails; turn 3 expects no act");
  assert.equal(m.scenarios[0].missingTurns, 1);
  assert.equal(score(full, scenarios, prices, gates).metrics.referenceAccuracy.value, 1);
});

test("latency: a failed or unrun turn is charged the cap, so a timeout can never improve p95", () => {
  const scenarios = [{ id: "s", categories: ["facet"], turns: [{ query: "a", expect: {} }, { query: "b", expect: {} }] }];
  const rec = (turns) => ({ label: "x", concurrency: 2, buildSlotsHeld: true, turnCapMs: 300000, ledger: [], facts: facts(), scenarios: [{ id: "s", categories: ["facet"], turns }] });
  const fast = score(rec([turn("a", [card(A, "Alpha")], { totalMs: 9000 }), turn("b", [card(B, "Bravo")], { totalMs: 60000 })]), scenarios, prices, gates).metrics.turnLatencyMs;
  const hung = score(rec([turn("a", [card(A, "Alpha")], { totalMs: 9000 }), turn("b", [], { totalMs: 120000, failed: true, error: "client_timeout (idle)" })]), scenarios, prices, gates).metrics.turnLatencyMs;
  assert.equal(fast.p95, 60000);
  assert.equal(hung.p95, 300000);
  assert.equal(hung.chargedAtCap, 1);
  const died = score(rec([turn("a", [card(A, "Alpha")], { totalMs: 9000, status: 500, failed: true })]), scenarios, prices, gates).metrics.turnLatencyMs;
  assert.equal(died.n, 2, "the unrun second turn is in the sample");
  assert.equal(died.p50, 300000);
});

test("fallback and grounding: a stub why-line is both; a stub reply is a fallback only", () => {
  const stub = turn("thrillers", [card(A, "Alpha", { why: "A thriller pick aligned with your taste." })]);
  const r = evaluateTurn({ expect: {} }, stub, 0, [stub], facts());
  assert.equal(r.fallback, true);
  assert.equal(r.groundingViolations, 1);
  const reply = turn("thrillers", [card(A, "Alpha")], { reply: "Here are some thriller picks for “thrillers” — enjoy." });
  const r2 = evaluateTurn({ expect: {} }, reply, 0, [reply], facts());
  assert.equal(r2.fallback, true);
  assert.equal(r2.groundingViolations, 0);
  const typed = turn("thrillers", [card(A, "Alpha", { whySource: "Fallback" })]);
  assert.equal(evaluateTurn({ expect: {} }, typed, 0, [typed], facts()).fallback, true, "a typed Fallback source counts even with ordinary copy");
  const filmography = turn("christopher nolan", [card(A, "Alpha", { why: null, whySource: "Generic" })]);
  assert.equal(evaluateTurn({ expect: {} }, filmography, 0, [filmography], facts()).fallback, false, "the captionless filmography list is by design, not a failed call");
});

test("grounding: a reply naming a catalogue title that is not on screen is a violation", () => {
  const t = turn("space movies", [card(A, "Alpha")], { reply: "Try Interstellar." });
  const f = facts({ named: { [t.turnId]: [{ titleId: D, name: "Interstellar" }, { titleId: A, name: "Alpha" }] } });
  assert.equal(evaluateTurn({ expect: {} }, t, 0, [t], f).groundingViolations, 1);
  // Prose that happens to spell a title is not a citation of it.
  const prose = turn("space movies", [card(A, "Alpha")], { reply: "If the other one felt slow, try something in and out." });
  const g = facts({ named: { [prose.turnId]: [{ titleId: D, name: "The Other" }, { titleId: C, name: "In and Out" }] } });
  assert.equal(evaluateTurn({ expect: {} }, prose, 0, [prose], g).groundingViolations, 0);
});

test("gates: latency is gated only at concurrency ≤ 2 with every build slot held", () => {
  const rec = (concurrency, buildSlotsHeld) => ({ label: "x", concurrency, buildSlotsHeld, ledger: [], scenarios: [{ id: "s", categories: ["facet"], turns: [turn("q", [card(A, "Alpha")], { totalMs: 60000, firstCardMs: 20000 })] }], facts: facts() });
  const scenarios = [{ id: "s", categories: ["facet"], turns: [{ query: "q", expect: {} }] }];
  const p50 = (r) => score(r, scenarios, prices, gates).gates.find((g) => g.metric === "turnLatencyMs" && g.stat === "p50").status;
  assert.match(p50(rec(4, true)), /not-gated \(concurrency > 2\)/);
  assert.match(p50(rec(2, undefined)), /not-gated \(build slots not held/, "a contended run is never a latency baseline");
  assert.equal(p50(rec(2, true)), "FAIL");
});

test("media: picks against the request's own filter are gated; against the words alone they are not", () => {
  const t = turn("only movies", [card(A, "Movie"), card(C, "Show")]);
  const words = evaluateTurn({ expect: { media: "Movie" } }, t, 1, [t, t], facts());
  assert.deepEqual(words.mediaViolations, ["Show"]);
  assert.deepEqual(words.mediaFilterViolations, []);
  const filtered = { ...t, mediaTypes: ["movie"] };
  assert.deepEqual(evaluateTurn({ expect: {} }, filtered, 0, [filtered], facts()).mediaFilterViolations, ["Show"]);
  assert.ok(gates.gates.some((g) => g.metric === "mediaFilterViolations" && g.target === 0));
});

test("fallback: Templater is a fallback on an in-request pick and by design on an outside-request card", () => {
  const f = facts({ server: { "t-thrillers": { whySources: { [A]: "Templater" } } } });
  const inside = turn("thrillers", [card(A, "Alpha")]);
  assert.equal(evaluateTurn({ expect: {} }, inside, 0, [inside], f).fallback, true);
  const outside = turn("thrillers", [card(A, "Alpha", { outsideRequest: true })]);
  assert.equal(evaluateTurn({ expect: {} }, outside, 0, [outside], f).fallback, false);
});

test("cost: the ledger delta is priced per model, and an unpriced model is reported, never free", () => {
  const rec = { label: "x", concurrency: 4, scenarios: [{ id: "s", categories: ["facet"], turns: [turn("q", [])] }], facts: facts(),
    ledger: [{ provider: "OpenRouter", model: "openai/gpt-6-luna", purpose: "rerank-ask", calls: 2, promptTokens: 1_000_000, completionTokens: 1_000_000, tokensUsed: 0 },
      { provider: "OpenRouter", model: "mystery/model", purpose: "x", calls: 1, promptTokens: 10, completionTokens: 10, tokensUsed: 0 }] };
  const m = score(rec, [{ id: "s", categories: ["facet"], turns: [{ query: "q", expect: {} }] }], prices, gates).metrics;
  assert.equal(m.costPerTurnUsd.value, 0.6);
  assert.deepEqual(m.costPerTurnUsd.unpriced, ["mystery/model"]);
});

// A two-scenario recording whose turns can be crashed, removed or reworded one field at a time.
const cmpScenarios = [
  { id: "people-x", categories: ["people"], turns: [{ query: "anything with X", expect: { concept: "x" } }] },
  { id: "facet-y", categories: ["facet"], turns: [{ query: "y movies", expect: { concept: "y" } }, { query: "more like #1", expect: { references: [{ turn: 1, rank: 1 }] } }] },
];
const cmpFacts = facts({ eligible: [A, B, C], truth: { x: { members: [A, D], supply: { Any: { matching: 2, eligible: 1 } } }, y: { members: [B], supply: { Any: { matching: 1, eligible: 1 } } } }, artifact: { id: "art-1", version: 1 } });
const cmpRec = (over = {}) => ({ label: "x", concurrency: 4, snapshotId: "snap-1", ledger: [], facts: cmpFacts, scenarios: [
  { id: "people-x", categories: ["people"], turns: [turn("anything with X", [card(A, "Alpha"), card(D, "Seen it")])] },
  { id: "facet-y", categories: ["facet"], turns: [turn("y movies", [card(B, "Bravo")]), turn("more like #1", [card(C, "Charlie")], { intent: { referenceTitles: ["Bravo"] } })] },
], ...over });
const scored = (rec, sc = cmpScenarios) => score(rec, sc, prices, gates);
const rowOf = (res, metric) => res.rows.find((r) => r.metric === metric && !r.stat);

test("compare: a drop beyond tolerance is a regression, within it is not", () => {
  const base = scored(cmpRec());
  assert.equal(compare(scored(cmpRec()), base, gates).rows.filter((r) => r.status.startsWith("REGRESSION")).length, 0, "a run is no regression of itself");
  const leakier = cmpRec();
  leakier.scenarios[1].turns[0] = turn("y movies", [card(B, "Bravo"), card(D, "Seen it")]);
  assert.equal(rowOf(compare(scored(leakier), base, gates), "exclusionLeaks").status, "REGRESSION");
});

test("compare refuses runs whose scenarios, turn counts, snapshot or artifact differ", () => {
  const base = scored(cmpRec());
  // Removing the people scenario took the open baseline's leaks 66 -> 0 "pass" before this rule.
  const without = cmpRec();
  without.scenarios = without.scenarios.slice(1);
  const res = compare(scored(without, cmpScenarios.slice(1)), base, gates);
  assert.match(res.refused, /missing 1: people-x/);
  assert.deepEqual(res.rows, []);
  const reworded = cmpScenarios.map((s) => (s.id === "facet-y" ? { ...s, turns: [{ ...s.turns[0], query: "y films" }, s.turns[1]] } : s));
  const rerun = cmpRec();
  rerun.scenarios[1].turns[0] = { ...rerun.scenarios[1].turns[0], query: "y films" };
  assert.match(compare(scored(rerun, reworded), base, gates).refused, /reworded 1: facet-y/);
  // An old recording re-scored against edited definitions measured other queries: refused too.
  const staleRun = scored(cmpRec(), reworded);
  assert.deepEqual(staleRun.summary.staleScenarios, ["facet-y"]);
  assert.match(compare(staleRun, base, gates).refused, /no longer matches its scenario definitions/);
  assert.match(compare(scored(cmpRec({ snapshotId: "snap-2" })), base, gates).refused, /snapshots differ/);
  assert.match(compare(scored(cmpRec({ facts: { ...cmpFacts, artifact: { id: "art-2" } } })), base, gates).refused, /artifacts differ/);
  const legacy = { ...base, summary: { ...base.summary, fingerprint: undefined } };
  assert.match(compare(base, legacy, gates).refused, /predates scenario fingerprints/);
});

test("compare: a crash cannot hide a leak or improve a rate — it is charged as a newly failed turn", () => {
  const base = scored(cmpRec());
  const crashed = cmpRec();
  crashed.scenarios[0].turns[0] = turn("anything with X", [], { status: 500, failed: true, mode: null });
  const res = compare(scored(crashed), base, gates);
  const leaks = rowOf(res, "exclusionLeaks");
  assert.equal(leaks.baseline, leaks.current, "paired: the crashed turn is out of both sides");
  assert.equal(res.pairedTurns, 2);
  assert.equal(rowOf(res, "newlyFailedTurns").status, "REGRESSION");
  assert.equal(rowOf(res, "constraintPrecisionAt8").status, "REGRESSION", "a failed turn scores precision 0");
  // A crash before a reference turn: the reference turn never runs and fails rather than leaving the sample.
  const early = cmpRec();
  early.scenarios[1].turns = [turn("y movies", [], { status: 500, failed: true, mode: null })];
  assert.equal(rowOf(compare(scored(early), base, gates), "referenceAccuracy").status, "REGRESSION");
});

test("compare: a metric the baseline measured and the run did not is a regression, not 'no data'", () => {
  const base = scored(cmpRec());
  const cur = scored(cmpRec());
  cur.metrics = { ...cur.metrics, referenceAccuracy: { value: null, n: 0 } };
  assert.equal(rowOf(compare(cur, base, gates), "referenceAccuracy").status, "REGRESSION (no data)");
  // ...while a metric the baseline never measured (v1 act) is reported, not gated.
  assert.equal(rowOf(compare(cur, base, gates), "actClassification").status, "no baseline data");
});

test("runner: a v2 turn without `done` ends on its terminal frame; a stream that just stops is failed", () => {
  const frames = (list) => ({ status: 200, totalMs: 10, firstByteMs: 1, firstCardMs: 5, frames: list.map(([ev, data], i) => ({ at: i, ev, data })) });
  const picks = compactTurn(frames([["plan", { act: "NewRequest", turnId: "t1", conversationId: "c1" }], ["picks", { cards: [{ titleId: A, name: "Alpha" }] }]]));
  assert.equal(picks.failed, false);
  assert.equal(picks.mode, "Picks");
  assert.equal(picks.turnId, "t1");
  const none = compactTurn(frames([["plan", { act: "NewRequest" }], ["none-found", { supply: 0 }]]));
  assert.equal(none.failed, false);
  assert.equal(none.mode, "NoneFound");
  assert.equal(compactTurn(frames([["plan", { act: "NewRequest" }], ["phase", { stage: "judge" }]])).failed, true);
  const v1 = compactTurn(frames([["conversation", { id: "c", turnId: "t" }], ["done", { results: [] }]]));
  assert.equal(v1.failed, false);
  assert.equal(v1.mode, "NoneFound");
  assert.equal(compactTurn(frames([["conversation", { id: "c", turnId: "t" }], ["candidate", {}]])).failed, true, "v1 with no done");
});

test("runner: a resumed run reads the ledger from the day its first attempt started", () => {
  assert.equal(runDay({ day: "2026-10-24", started: "2026-10-24T23:50:00Z" }, "2026-10-25"), "2026-10-24");
  assert.equal(runDay({ started: "2026-10-24T23:50:00Z" }, "2026-10-25"), "2026-10-24", "a run-start.json written before the day was stored");
  assert.equal(runDay(null, "2026-10-25"), "2026-10-25");
});

test("check: an open truth file no open scenario uses is reported (it would name a holdout topic)", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-truth-"));
  try {
    for (const k of ["used", "holdout-topic", "_eligible"]) writeFileSync(join(dir, `${k}.sql`), "select 1");
    const sc = [{ id: "a", set: "open", turns: [{ query: "q", expect: { concept: "used" } }] }, { id: "h", set: "holdout", turns: [{ query: "q", expect: { concept: "holdout-topic" } }] }];
    assert.deepEqual(unusedOpenTruth(sc, dir), ["holdout-topic"]);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("guards: a live run is refused on the founder's key unless signed off, and says which key it used", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-keys-"));
  try {
    writeFileSync(join(dir, "founder.env"), "SMTP_HOST=x\nOPENROUTER_API_KEY=sk-founder-test\n");
    writeFileSync(join(dir, "lane.env"), "export OPENROUTER_API_KEY=\"sk-founder-test\"\nTMDB_API_KEY=t\n");
    writeFileSync(join(dir, "eval.env"), "OPENROUTER_API_KEY=sk-eval-test\n");
    writeFileSync(join(dir, "none.env"), "TMDB_API_KEY=t\n");
    const env = { EVAL_FOUNDER_ENV: join(dir, "founder.env") };
    assert.equal(envKeyHash(join(dir, "lane.env")), envKeyHash(join(dir, "founder.env")), "quotes and export are stripped before hashing");
    assert.equal(classifyKey(join(dir, "lane.env"), env), "founder");
    assert.equal(classifyKey(join(dir, "eval.env"), env), "separate");
    assert.equal(classifyKey(join(dir, "none.env"), env), "none");
    assert.equal(classifyKey(join(dir, "eval.env"), { EVAL_FOUNDER_ENV: join(dir, "missing.env") }), "unknown");
    assert.match(modelKeyFor(join(dir, "lane.env"), null, env).refusal, /founder's own key/);
    assert.match(modelKeyFor(join(dir, "lane.env"), "Dean, 2026-10-25", env).recorded, /FOUNDER'S KEY, signed off: Dean/);
    assert.match(modelKeyFor(join(dir, "eval.env"), null, env).recorded, /separate key/);
    assert.match(modelKeyFor(undefined, null, env).refusal, /--api-env/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("guards: a latency run takes every build slot, waits for a live holder and reclaims a dead one", async () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-locks-"));
  const sleeper = spawn(process.execPath, ["-e", "setTimeout(() => {}, 600)"]);
  try {
    mkdirSync(join(dir, "slot1")); writeFileSync(join(dir, "slot1", "pid"), String(sleeper.pid));
    mkdirSync(join(dir, "slot2")); writeFileSync(join(dir, "slot2", "pid"), "999999");
    const t0 = Date.now();
    const held = await holdAllBuildSlots({ dir, pollMs: 50 });
    assert.ok(Date.now() - t0 >= 500, "it waited for the build in flight to finish");
    for (const i of [1, 2, 3]) assert.equal(readFileSync(join(dir, `slot${i}`, "pid"), "utf8"), String(process.pid));
    held.release();
    assert.equal(existsSync(join(dir, "slot1")), false);
  } finally { sleeper.kill(); rmSync(dir, { recursive: true, force: true }); }
});

test("replay: every committed baseline recording re-scores to its committed metrics", () => {
  const root = join(repo, "eval", "askreel", "baselines");
  const recordings = existsSync(root) ? readdirSync(root, { recursive: true }).filter((p) => String(p).endsWith("recording.json")) : [];
  for (const rel of recordings) {
    const dir = dirname(join(root, String(rel)));
    const recording = JSON.parse(readFileSync(join(root, String(rel)), "utf8"));
    const scenarios = loadOpen(recording);
    const fresh = score(recording, scenarios, prices, gates);
    const stored = JSON.parse(readFileSync(join(dir, "metrics.json"), "utf8"));
    assert.deepEqual(fresh.metrics, stored.metrics, `${rel} no longer replays to its stored metrics`);
  }
});

function loadOpen(recording) {
  const out = [];
  for (const d of recording.scenarioDirs) {
    const dir = resolve(repo, d);
    if (!existsSync(dir)) continue;
    for (const f of readdirSync(dir).filter((n) => n.endsWith(".json"))) out.push(...JSON.parse(readFileSync(join(dir, f), "utf8")).scenarios);
  }
  return out;
}
