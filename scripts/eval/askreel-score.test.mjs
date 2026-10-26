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
import { score, evaluateTurn, isEcho, compare, replySupply, knockoutOfLabel, STUB_WHY, HEDGE_WHY, NON_LATIN } from "./askreel-score.mjs";
import { compactTurn, runDay, unusedOpenTruth, degradedOf, supplyFromRows, forYouBar, costMeterDrift, COST_METER_MAX_COSTED_RULES } from "./askreel.mjs";
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

// A v1 transcript as the API streams it today (LiveSearchExpansionService): `conversation`, an
// `assistant-message`, then one `done` carrying the trays, the mode, the supply report and `degraded`.
const v1Frames = (done, assistant = { text: "Nothing I could check against your request.", conflict: false, degraded: false }) => ({
  status: 200, totalMs: 900, firstByteMs: 5, firstCardMs: null,
  frames: [["conversation", { id: "conv-1", turnId: "turn-1", sequence: 2 }], ["assistant-message", assistant],
    ["done", { results: [], belowFloor: [], nearMiss: [], unchecked: [], mode: "NoneFound", supply: null, degraded: [], reason: "x", ...done }]]
    .map(([ev, data], i) => ({ at: i, ev, data })),
});

test("runner: degraded comes from done.degraded and a degraded reply, never from a frame the API does not send", () => {
  // The keyless shape the round-2 reviewer recorded: every turn's done said no-model.
  const keyless = compactTurn(v1Frames({ degraded: ["no-model"] }, { text: "There is no model to read that.", conflict: false, degraded: true }));
  assert.deepEqual(keyless.degraded, ["reply", "no-model"]);
  // Vary one field at a time: only done, only the reply, neither, and a zero judge count.
  assert.deepEqual(compactTurn(v1Frames({ degraded: ["plan"] })).degraded, ["plan"]);
  assert.deepEqual(compactTurn(v1Frames({}, { text: "x", conflict: false, degraded: true })).degraded, ["reply"]);
  assert.deepEqual(compactTurn(v1Frames({})).degraded, []);
  assert.deepEqual(degradedOf([], { degraded: ["judge:0/3", "judge-missing:0/12", "judge:1/3", "judge-missing:2/12"] }), ["judge:1/3", "judge-missing:2/12"]);
  // A v2 `degraded` frame still counts.
  assert.deepEqual(degradedOf([{ ev: "degraded", data: { what: "judge" } }], null), ["judge"]);
  // The mode v1 names on done wins over inferring one from zero cards.
  assert.equal(compactTurn(v1Frames({ mode: "Clarify" })).mode, "Clarify");
});

test("replay: a recording whose done frames say degraded fails the fallback gate", () => {
  const sc = [{ id: "keyless", categories: ["facet"], turns: [{ query: "heist movies", expect: {} }, { query: "more", expect: {} }] }];
  const rec = (degraded) => ({ label: "k", concurrency: 4, ledger: [], facts: facts(), scenarios: [{ id: "keyless", categories: ["facet"], turns: [
    { ...compactTurn(v1Frames({ degraded })), query: "heist movies", mediaTypes: null },
    { ...compactTurn(v1Frames({ degraded: [] })), query: "more", mediaTypes: null },
  ] }] });
  const bad = score(rec(["no-model"]), sc, prices, gates);
  assert.equal(bad.metrics.fallbackRate.value, 0.5);
  assert.equal(bad.gates.find((g) => g.metric === "fallbackRate").status, "FAIL");
  assert.match(bad.scenarios[0].turns[0].fallbackDetail.join(" "), /degraded: no-model/);
  const clean = score(rec([]), sc, prices, gates);
  assert.equal(clean.metrics.fallbackRate.value, 0);
  assert.equal(clean.gates.find((g) => g.metric === "fallbackRate").status, "pass");
  // A zero judge count is not a fallback.
  assert.equal(score(rec(["judge:0/2"]), sc, prices, gates).metrics.fallbackRate.value, 0);
});

test("trays: near-miss and unchecked cards on done are shown cards, held to leaks, ruled-out titles and grounding", () => {
  const raw = v1Frames({ nearMiss: [{ titleId: A, name: "Alpha" }], unchecked: [{ titleId: D, name: "Blue Beetle" }, { titleId: B, name: "Bravo" }] });
  const t = { ...compactTurn(raw), query: "no superheroes please", mediaTypes: null };
  assert.deepEqual(t.nearMiss.map((c) => c.titleId), [A]);
  assert.deepEqual(t.unchecked.map((c) => c.titleId), [D, B]);
  const f = facts({ truth: { superhero: { members: [D], supply: {} } } });
  const def = { expect: { mustNot: ["superhero"] } };
  const r = evaluateTurn(def, t, 0, [t], f);
  assert.deepEqual(r.leaks, ["Blue Beetle"], "D is outside the eligibility truth, and the unchecked tray is on screen");
  assert.deepEqual(r.mustNotViolations, ["Blue Beetle"]);
  // The same card among no tray at all: no leak, nothing ruled out.
  const empty = { ...compactTurn(v1Frames({})), query: "no superheroes please", mediaTypes: null };
  const r0 = evaluateTurn(def, empty, 0, [empty], f);
  assert.deepEqual([r0.leaks, r0.mustNotViolations], [[], []]);
  // A reply naming a title in the unchecked tray names something on screen: grounded.
  const named = { ...t, reply: "Blue Beetle is in the unchecked row." };
  const g = evaluateTurn(def, named, 0, [named], { ...f, named: { [t.turnId]: [{ titleId: D, name: "Blue Beetle" }] } });
  assert.equal(g.groundingViolations, 0);
  // Scored over a recording, both gated counts fail.
  const sc = [{ id: "rainy", categories: ["negation"], turns: [{ query: "no superheroes please", expect: { mustNot: ["superhero"] } }] }];
  const res = score({ label: "t", concurrency: 4, ledger: [], facts: f, scenarios: [{ id: "rainy", categories: ["negation"], turns: [t] }] }, sc, prices, gates);
  assert.equal(res.gates.find((x) => x.metric === "exclusionLeaks").status, "FAIL");
  assert.equal(res.gates.find((x) => x.metric === "mustNotViolations").status, "FAIL");
});

// Supply honesty (IREV1-SYS-06). The truth's floored counts are what supply.sql returns.
const supplyTruthOf = (over = {}) => ({ matching: 40, eligible: 5, scored: 4, aboveBar: 2, clearing: 2,
  floored: { 50: { minVotes: 50, matching: 30, alreadySeen: 12, savedOrDismissed: 1, excludedByConditions: 13, eligible: 4, unscored: 1, belowFloor: 1, clearingFloor: 2,
    knockouts: [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 3 }, { kind: "MinYear", value: "2000", wouldUnlock: 2 }], ...over.floored } }, ...over.top });
const saidSupply = (over = {}) => ({ alreadySeen: 12, unscored: 1, minVotes: 50, knockoutsMeasured: true,
  knockouts: [{ kind: "ExcludeGenre", value: "Comedy", wouldUnlock: 3 }, { kind: "MinYear", value: "2000", wouldUnlock: 2 }], ...over });

test("missed supply: no pick while eligible titles clear the bar fails a gate; nothing clearing the bar is honest-empty", () => {
  const def = { expect: { concept: "heist" } };
  const none = turn("heist movies", [], { mode: "NoneFound" });
  const r = evaluateTurn(def, none, 0, [none], facts({ truth: { heist: { members: [A], supply: { Any: supplyTruthOf() } } } }));
  assert.equal(r.missedSupply, true);
  assert.equal(r.checks.honestEmpty, undefined);
  // Eligible titles exist but none clears the bar: none-found is the honest answer, not missed supply.
  const underBar = facts({ truth: { heist: { members: [A], supply: { Any: supplyTruthOf({ top: { clearing: 0, aboveBar: 0 } }) } } } });
  const h = evaluateTurn(def, none, 0, [none], underBar);
  assert.equal(h.missedSupply, undefined);
  assert.equal(h.checks.honestEmpty, true);
  const clarify = turn("heist movies", [], { mode: "Clarify" });
  assert.equal(evaluateTurn(def, clarify, 0, [clarify], underBar).checks.honestEmpty, false);
  // A recording from before `clearing` existed falls back to aboveBar.
  const legacy = facts({ truth: { heist: { members: [A], supply: { Any: { matching: 3, eligible: 2, scored: 2, aboveBar: 1 } } } } });
  assert.equal(evaluateTurn(def, none, 0, [none], legacy).missedSupply, true);
  // Gated: one missed turn fails the run.
  const sc = [{ id: "heist", categories: ["facet"], turns: [{ query: "heist movies", expect: { concept: "heist" } }] }];
  const rec = (t, f) => ({ label: "m", concurrency: 4, ledger: [], facts: f, scenarios: [{ id: "heist", categories: ["facet"], turns: [t] }] });
  const f = facts({ truth: { heist: { members: [A], supply: { Any: supplyTruthOf() } } } });
  assert.equal(score(rec(none, f), sc, prices, gates).gates.find((g) => g.metric === "missedSupply").status, "FAIL");
  assert.equal(score(rec(turn("heist movies", [card(A, "Alpha")]), f), sc, prices, gates).gates.find((g) => g.metric === "missedSupply").status, "pass");
});

test("supply counts: a none-found reply's seen, unscored and relaxation counts must equal the SQL truth, ±0", () => {
  const def = { expect: { concept: "heist" } };
  const f = facts({ truth: { heist: { members: [A], supply: { Any: supplyTruthOf({ top: { clearing: 0 } }) } } } });
  // The reply states the report's unscored count, as NoneFoundCopy does whenever there is one.
  const run = (supply) => { const t = turn("heist movies", [], { mode: "NoneFound", supply, reply: "1 has no score from your model yet." }); return evaluateTurn(def, t, 0, [t], f); };
  assert.equal(run(saidSupply()).checks.supplyCounts, true, "values compare case-insensitively for the list rules");
  for (const [field, value, pattern] of [
    ["alreadySeen", 11, /alreadySeen 11 vs truth 12/],
    ["unscored", 0, /unscored 0 vs truth 1/],
    ["knockouts", [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 3 }], /knockout MinYear absent vs truth 2/],
    ["knockouts", [...saidSupply().knockouts, { kind: "ExcludeGenre", value: "romance", wouldUnlock: 1 }], /knockout ExcludeGenre:romance 1 vs truth absent/],
    ["knockouts", [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 4 }, { kind: "MinYear", value: "2000", wouldUnlock: 2 }], /ExcludeGenre:comedy 4 vs truth 3/],
  ]) {
    const r = run(saidSupply({ [field]: value }));
    assert.equal(r.checks.supplyCounts, false, `${field} varied`);
    assert.match(r.supplyMismatches.join("; "), pattern);
  }
  // Single-rule kinds match by kind: the API values the language rule as its list.
  assert.equal(run(saidSupply({ knockouts: [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 3 }, { kind: "MinYear", value: "2,000", wouldUnlock: 2 }] })).checks.supplyCounts, true);
  // Skipping relaxations is honest only for a broad request with plenty clearing the bar.
  assert.equal(run(saidSupply({ knockoutsMeasured: false, knockouts: [] })).checks.supplyCounts, false);
  const broad = facts({ truth: { heist: { members: [A], supply: { Any: supplyTruthOf({ floored: { matching: 5000, clearingFloor: 30 } }) } } } });
  const tb = turn("heist movies", [], { mode: "NoneFound", supply: saidSupply({ knockoutsMeasured: false, knockouts: [] }), reply: "1 has no score from your model yet." });
  assert.equal(evaluateTurn(def, tb, 0, [tb], broad).checks.supplyCounts, true);
  // No supply on the turn: nothing to check. A truth at another vote floor: unverified, not passed.
  const quiet = turn("heist movies", [], { mode: "NoneFound", supply: null, reply: "Nothing I found matches what you asked closely enough to show." });
  assert.equal(evaluateTurn(def, quiet, 0, [quiet], f).checks.supplyCounts, undefined);
  const other = run(saidSupply({ minVotes: 10 }));
  assert.equal(other.checks.supplyCounts, undefined);
  assert.match(other.supplyUnverified, /vote floor 10/);
  // Gated at 100%.
  const sc = [{ id: "heist", categories: ["empty-supply"], turns: [{ query: "heist movies", expect: { concept: "heist" } }] }];
  const res = score({ label: "s", concurrency: 4, ledger: [], facts: f, scenarios: [{ id: "heist", categories: ["empty-supply"], turns: [turn("heist movies", [], { mode: "NoneFound", supply: saidSupply({ unscored: 7 }) })] }] }, sc, prices, gates);
  assert.equal(res.gates.find((g) => g.metric === "supplyCountAccuracy").status, "FAIL");
});

// IREV2-SYS-03: the reply's own words. The two none-found shapes NoneFoundCopy composes on this snapshot,
// with the counts supply.sql gives at the 50-vote floor (ninja movies 70/18/52; samurai movies 66/8/57
// once the samurai truth stopped matching "Ronin" (1998) by name).
const ninjaTruth = { minVotes: 50, matching: 70, lowVoteMatches: 16, alreadySeen: 18, savedOrDismissed: 0, excludedByConditions: 52, eligible: 0, unscored: 0, belowFloor: 0, clearingFloor: 0,
  knockouts: [{ kind: "Language", value: "en", wouldUnlock: 4 }, { kind: "ExcludeGenre", value: "animation", wouldUnlock: 3 }, { kind: "MinYear", value: "2000", wouldUnlock: 3 }, { kind: "MinCrowdRating", value: "6.0", wouldUnlock: 2 }] };
const ninjaReply = "There are no ninja movies left for you. Of the 70 ninja movies in the catalogue with at least 50 votes, you've seen 18 and your conditions hold out 52. "
  + "Turning off “English only” would unlock 4; turning off “excluded genre: animation” would unlock 3.";
const samuraiTruth = { minVotes: 50, matching: 66, lowVoteMatches: 9, alreadySeen: 8, savedOrDismissed: 0, excludedByConditions: 57, eligible: 1, unscored: 0, belowFloor: 1, clearingFloor: 0,
  knockouts: [{ kind: "Language", value: "en", wouldUnlock: 13 }, { kind: "ExcludeGenre", value: "animation", wouldUnlock: 1 }, { kind: "MinCrowdRating", value: "6.0", wouldUnlock: 1 }, { kind: "MinYear", value: "2000", wouldUnlock: 1 }] };
const samuraiReply = "None of your unseen samurai movies clears your 6.0 bar. Of the 66 samurai movies in the catalogue with at least 50 votes, you've seen 8 and your conditions hold out 57. "
  + "Under your 6.0 bar: Blade of the 47 Ronin — shown below. Turning off “English only” would unlock 13; turning off “excluded genre: animation” would unlock 1.";

test("reply supply: the counts a none-found reply states are read back from its words", () => {
  assert.deepEqual(replySupply(ninjaReply), { matching: 70, minVotes: 50, alreadySeen: 18, savedOrDismissed: 0, excludedByConditions: 52,
    knockouts: [{ kind: "Language", value: "", wouldUnlock: 4 }, { kind: "ExcludeGenre", value: "animation", wouldUnlock: 3 }] });
  // Every part of the partition, the under-bar count, the unscored count; the held-titles sentence is not a count.
  const full = replySupply("There are no ninja movies left for you. Of the 11 ninja movies in the catalogue with at least 50 votes, you've seen 6, 1 is saved or dismissed and your conditions hold out 4. "
    + "2 are under your 6.0 bar. 1917 has no score from your model yet, so your 6.0 bar holds it back — shown below. 3 have no score from your model yet.");
  assert.deepEqual([full.matching, full.alreadySeen, full.savedOrDismissed, full.excludedByConditions, full.belowFloor, full.unscored], [11, 6, 1, 4, 2, 3]);
  // The reply SupplyTruthParityTests composes from the real reporter (allow-list fixture), read back.
  assert.deepEqual(replySupply("There are no goldensupply1 titles left for you. Of the 7 goldensupply1 titles in the catalogue with at least 50 votes, your conditions hold out 5. "
    + "2 have no score from your model yet. Turning off “only these genres: thriller” would unlock 2."),
  { matching: 7, minVotes: 50, alreadySeen: 0, savedOrDismissed: 0, excludedByConditions: 5, unscored: 2, knockouts: [{ kind: "IncludeGenre", value: "thriller", wouldUnlock: 2 }] });
  // "none is unseen and inside your conditions": every part is zero.
  const none = replySupply("There are no ninja shows left for you. Of the 2 ninja shows in the catalogue with at least 50 votes, none is unseen and inside your conditions.");
  assert.deepEqual([none.matching, none.alreadySeen, none.savedOrDismissed, none.excludedByConditions], [2, 0, 0, 0]);
  // A reply that states no count; a picks reply.
  assert.equal(replySupply("Nothing I found matches what you asked closely enough to show."), null);
  assert.equal(replySupply("Here are three heist films."), null);
  assert.equal(replySupply(null), null);
  // Every label SupplyReporter.LabelFor prints.
  assert.deepEqual(["excluded genre: Comedy", "only these genres: thriller", "excluded keyword: gore", "English only", "languages: en,fr", "crowd rating at least 6.0", "released 2000 or later", "released 2015 or earlier"]
    .map((l) => knockoutOfLabel(l).kind), ["ExcludeGenre", "IncludeGenre", "ExcludeKeyword", "Language", "Language", "MinCrowdRating", "MinYear", "MaxYear"]);
});

test("reply supply: a none-found reply's words must equal the floored SQL truth, ±0 (ninja and samurai)", () => {
  const check = (concept, truth, reply, supply = null) => {
    const def = { expect: { concept } };
    const t = turn(`${concept} movies`, [], { mode: "NoneFound", reply, supply });
    const f = facts({ truth: { [concept]: { members: [A], supply: { Any: { matching: 86, eligible: 0, scored: 0, aboveBar: 0, clearing: 0, floored: { 50: truth } } } } } });
    return evaluateTurn(def, t, 0, [t], f);
  };
  for (const [concept, truth, reply] of [["ninja", ninjaTruth, ninjaReply], ["samurai", samuraiTruth, samuraiReply]]) {
    const ok = check(concept, truth, reply);
    assert.equal(ok.checks.supplyCounts, true, `${concept}: ${ok.supplyMismatches}`);
    assert.equal(ok.checks.honestEmpty, true);
  }
  // Vary one stated number at a time; each fails and names what differs.
  for (const [from, to, pattern] of [
    ["Of the 70", "Of the 86", /reply says matching 86, truth 70/],
    ["you've seen 18", "you've seen 21", /reply says alreadySeen 21, truth 18/],
    ["hold out 52", "hold out 49", /reply says excludedByConditions 49, truth 52/],
    ["would unlock 4;", "would unlock 5;", /dropping Language unlocks 5, truth 4/],
    ["“excluded genre: animation” would unlock 3", "“excluded genre: comedy” would unlock 3", /dropping ExcludeGenre:comedy unlocks 3, truth nothing/],
    ["“English only” would unlock 4; turning off “excluded genre: animation” would unlock 3", "“crowd rating at least 6.0” would unlock 2", /reply leaves out Language, which unlocks 4/],
  ]) {
    const r = check("ninja", ninjaTruth, ninjaReply.replace(from, to));
    assert.equal(r.checks.supplyCounts, false, `${from} -> ${to}`);
    assert.match(r.supplyMismatches.join("; "), pattern);
  }
  // The samurai under-bar count, stated as a number when no title is named.
  const counted = samuraiReply.replace("Under your 6.0 bar: Blade of the 47 Ronin — shown below.", "2 are under your 6.0 bar.");
  assert.match(check("samurai", samuraiTruth, counted).supplyMismatches.join("; "), /reply says belowFloor 2, truth 1/);
  // The words and the report are both held: a true reply over a report that disagrees still fails.
  const report = { matchingInCatalogue: 70, alreadySeen: 18, savedOrDismissed: 0, excludedByConditions: 52, eligible: 0, belowFloor: 0, unscored: 0, clearingFloor: 0, minVotes: 50, knockoutsMeasured: true,
    knockouts: ninjaTruth.knockouts };
  assert.equal(check("ninja", ninjaTruth, ninjaReply, report).checks.supplyCounts, true);
  assert.match(check("ninja", ninjaTruth, ninjaReply, { ...report, eligible: 1 }).supplyMismatches.join("; "), /eligible 1 vs truth 0/);
  // Copy the parser cannot read is a failure, not a pass: the report says a partition and an unscored count were due.
  const reworded = check("ninja", ninjaTruth, "No ninja movies remain. You saw 18 of 70.", report);
  assert.equal(reworded.checks.supplyCounts, false);
  assert.match(reworded.supplyMismatches.join("; "), /reply states no partition/);
  assert.match(check("ninja", { ...ninjaTruth, unscored: 2 }, ninjaReply, { ...report, unscored: 2 }).supplyMismatches.join("; "), /reply states no unscored count/);
  // A reply at a floor the truth was not counted at is unverified, never passed.
  const other = check("ninja", ninjaTruth, ninjaReply.replace("at least 50 votes", "at least 10 votes"));
  assert.equal(other.checks.supplyCounts, undefined);
  assert.match(other.supplyUnverified, /vote floor 10/);
});

test("runner: done.supply is recorded in camelCase as the API streams it, and supply.sql rows fold into one object", () => {
  const t = compactTurn(v1Frames({ supply: { matchingInCatalogue: 30, alreadySeen: 12, savedOrDismissed: 1, excludedByConditions: 13, eligible: 4, belowFloor: 1, unscored: 1, clearingFloor: 2,
    lowVoteMatches: 3, minVotes: 50, predictedFloor: 6, knockoutsMeasured: true,
    knockouts: [{ kind: "ExcludeGenre", value: "comedy", contentFilterId: null, label: "excluded genre: comedy", wouldUnlock: 3, sample: [] }] } }));
  assert.deepEqual(t.supply.knockouts, [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 3 }]);
  assert.equal(t.supply.alreadySeen, 12);
  assert.equal(compactTurn(v1Frames({})).supply, null);
  const folded = supplyFromRows([["matching", "", "", "30"], ["alreadySeen", "", "", "12"], ["knockout", "MinYear", "2000", "2"], ["knockout", "ExcludeGenre", "comedy", "3"]], 50);
  assert.deepEqual(folded, { minVotes: 50, matching: 30, alreadySeen: 12, knockouts: [{ kind: "ExcludeGenre", value: "comedy", wouldUnlock: 3 }, { kind: "MinYear", value: "2000", wouldUnlock: 2 }] });
  // The For-you bar as the API reads it.
  assert.deepEqual(["6.0", "0", "abc", "12", null, " 5.5 "].map(forYouBar), [6, null, null, 10, null, 5.5]);
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

// IREV3-SYS-02: the SQL truth against the API's own eligibility on the real catalogue, via the cost meter.
test("costMeterDrift is silent when the API's cost report and the SQL truth agree", () => {
  const truth = [["eligible", "", "421"], ["marginal", "ExcludeGenre/romance", "12"], ["marginal", "Language/en", "300"]];
  const report = { eligible: 421, byRule: [
    { kind: "ExcludeGenre", value: "romance", marginal: 12 },
    { kind: "Language", value: "en", marginal: 300 },
    // A value the SQL never sees alone (one of a multi-value allow-list) is costed 0 by the meter's clamp.
    { kind: "IncludeGenre", value: "drama", marginal: 0 },
  ] };
  assert.deepEqual(costMeterDrift(truth, report), []);
});

test("costMeterDrift names a moved eligible count, a moved marginal and a rule only the SQL knows", () => {
  const truth = [["eligible", "", "44"], ["marginal", "ExcludeGenre/romance", "3"], ["marginal", "MinYear/2000", "5"]];
  const report = { eligible: 41, byRule: [{ kind: "ExcludeGenre", value: "romance", marginal: 2 }] };
  assert.deepEqual(costMeterDrift(truth, report), [
    "eligible: API 41 vs SQL truth 44",
    "ExcludeGenre/romance: API 2 vs SQL truth 3",
    "MinYear/2000: SQL truth 5, but the API costs no such rule",
  ]);
});

test("costMeterDrift skips rules past the meter's cap, which it reports without a marginal", () => {
  const byRule = Array.from({ length: COST_METER_MAX_COSTED_RULES + 1 }, (_, i) => ({ kind: "ExcludeKeyword", value: `k${i}`, marginal: 0 }));
  const last = `ExcludeKeyword/k${COST_METER_MAX_COSTED_RULES}`;
  const truth = [["eligible", "", "10"], ["marginal", last, "4"]];
  assert.deepEqual(costMeterDrift(truth, { eligible: 10, byRule }), []);
});
