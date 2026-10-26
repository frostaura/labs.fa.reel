// Replay-mode tests for the judge and its calibration: pure, no database, no paid call (fetch is faked).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { sampleItems, itemsCsv, parseCsv, labelsFrom, cohensKappa, agreement, promptSections, judgeRecording, judgeMetrics, requestContext, satisfiesMessage, keyFromEnvFile, compareJudge } from "./judge.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const baseline = JSON.parse(readFileSync(join(repo, "eval", "askreel", "baselines", "99c0761", "open", "recording.json"), "utf8"));
const open = JSON.parse(readFileSync(join(repo, "eval", "askreel", "scenarios", "open.json"), "utf8")).scenarios;
const PROMPT = "## satisfies\nRate 0-3.\n\n## grounded\nSay whether the reply is grounded.\n";

test("the labelling set is 100 items, one per turn before any turn repeats, and redraws exactly from its seed", () => {
  const a = sampleItems(baseline, open, { n: 100, seed: 42 });
  const b = sampleItems(baseline, open, { n: 100, seed: 42 });
  assert.equal(a.length, 100);
  assert.deepEqual(a, b);
  const turns = new Set(a.map((i) => `${i.scenario}#${i.turn}`));
  const withCards = baseline.scenarios.reduce((n, s) => n + s.turns.filter((t) => (t.cards ?? []).some((c) => c.rank <= 8)).length, 0);
  assert.equal(turns.size, Math.min(100, withCards), "every item is from a different turn while turns last");
  assert.ok(a.every((i) => i.rank >= 1 && i.rank <= 8));
  assert.notDeepEqual(sampleItems(baseline, open, { n: 100, seed: 7 }).map((i) => i.itemId), a.map((i) => i.itemId));
});

test("a later turn's request carries the conversation it refers to", () => {
  const def = open.find((s) => s.id === "ninja-critique");
  const rec = baseline.scenarios.find((s) => s.id === "ninja-critique");
  const { request, earlier } = requestContext(def, rec.turns, 2);
  assert.equal(request, "more like the second one you showed me first time round");
  assert.match(earlier[0], /^Turn 1: "ninja movies" — shown: 1\. Triple Threat; 2\. Man of Tai Chi/);
  const msg = satisfiesMessage({ request, earlier: earlier.join(" | "), facts: { name: "X", year: 2001, media: "Movie", genres: ["action"], keywords: ["ninja"], language: "ja", overview: "o" } });
  assert.match(msg, /Request: "more like the second one/);
  assert.doesNotMatch(msg, /why/i, "the judge never sees the system's own why-line");
});

test("the founder's sheet round-trips through CSV, and only 0–3 counts as a label", () => {
  const items = [{ itemId: "a#1#1", scenario: "a", turn: 1, earlier: "", request: 'say "hi", ok', titleId: "t", facts: { name: "N, the film", year: 2000, media: "Movie", genres: ["drama"], overview: "line one\nline two" } }];
  const rows = parseCsv(itemsCsv(items));
  assert.equal(rows.length, 1);
  assert.equal(rows[0].request, 'say "hi", ok');
  assert.equal(rows[0].title, "N, the film");
  assert.equal(rows[0].overview, "line one\nline two");
  assert.equal(rows[0].label, "");
  const labels = labelsFrom([{ item_id: "x", kind: "satisfies", label: "2" }, { item_id: "y", kind: "satisfies", label: "4" }, { item_id: "z", kind: "satisfies", label: " " }, { item_id: "c", kind: "confirm (yes/no)", label: "1" }]);
  assert.deepEqual([...labels], [["x", 2]]);
});

test("Cohen's κ matches the textbook values, unweighted and quadratic", () => {
  // 2×2: agreement 0.7, chance 0.5 → κ 0.4.
  const pairs = [...Array(20).fill([0, 0]), ...Array(5).fill([0, 1]), ...Array(10).fill([1, 0]), ...Array(15).fill([1, 1])];
  assert.ok(Math.abs(cohensKappa(pairs, { categories: 2 }).kappa - 0.4) < 1e-12);
  // Perfect agreement is 1; a judge that always says 2 against a varied founder agrees no better than chance.
  assert.equal(cohensKappa([[0, 0], [1, 1], [2, 2], [3, 3]]).kappa, 1);
  assert.equal(cohensKappa([[0, 2], [1, 2], [2, 2], [3, 2]]).kappa, 0);
  // Quadratic weights forgive a near miss more than a far one.
  const near = cohensKappa([[0, 1], [1, 2], [2, 3], [3, 3], [0, 0]], { weighted: true }).kappa;
  const far = cohensKappa([[0, 3], [1, 3], [2, 0], [3, 0], [0, 0]], { weighted: true }).kappa;
  assert.ok(near > far);
  assert.equal(cohensKappa([]), null);
});

test("the judge gates only at κ ≥ 0.6 with every labelled item judged", () => {
  const labels = new Map([["a", 3], ["b", 0], ["c", 2], ["d", 1]]);
  const agree = { cards: [["a", 3], ["b", 0], ["c", 2], ["d", 1]].map(([itemId, satisfies]) => ({ itemId, satisfies })) };
  assert.equal(agreement(labels, agree).judgeGates, true);
  const chance = { cards: [["a", 2], ["b", 2], ["c", 2], ["d", 2]].map(([itemId, satisfies]) => ({ itemId, satisfies })) };
  assert.equal(agreement(labels, chance).judgeGates, false);
  const partial = { cards: agree.cards.slice(0, 3) };
  const r = agreement(labels, partial);
  assert.equal(r.judgeGates, false, "an unjudged labelled item keeps the judge from gating");
  assert.deepEqual(r.missing, ["d"]);
});

test("the sealed prompt must carry both sections", () => {
  assert.deepEqual(promptSections(PROMPT), { satisfies: "Rate 0-3.", grounded: "Say whether the reply is grounded." });
  assert.throws(() => promptSections("## satisfies\nx\n"), /"## grounded"/);
});

test("a judge run makes one call per top-8 card and per reply, at effort none, and records only the prompt's hash", async () => {
  const calls = [];
  const fakeFetch = async (url, init) => {
    const body = JSON.parse(init.body);
    calls.push(body);
    const content = body.response_format.json_schema.name === "satisfies"
      ? { satisfies: body.messages[1].content.includes("Triple Threat") ? 3 : 1, reason: "r" }
      : { grounded: false, unsupported: ["Shinobi"] };
    return { ok: true, status: 200, json: async () => ({ choices: [{ message: { content: JSON.stringify(content) }, finish_reason: "stop" }], usage: { prompt_tokens: 10, completion_tokens: 2, cost: 0.00001 } }) };
  };
  const rec = { label: "t", scenarios: [baseline.scenarios.find((s) => s.id === "ninja-critique")] };
  const facts = new Map(rec.scenarios[0].turns.flatMap((t) => (t.cards ?? []).map((c) => [c.titleId, { name: c.name, year: c.year, media: c.mediaType }])));
  const out = await judgeRecording({ recording: rec, definitions: open, facts, prompt: PROMPT, apiKey: "k", fetchImpl: fakeFetch, concurrency: 2 });
  const cardCalls = rec.scenarios[0].turns.reduce((n, t) => n + (t.cards ?? []).filter((c) => c.rank <= 8).length, 0);
  const replyCalls = rec.scenarios[0].turns.filter((t) => (t.cards ?? []).some((c) => c.rank <= 8) && t.reply).length;
  assert.equal(calls.length, cardCalls + replyCalls);
  assert.ok(calls.every((c) => c.reasoning.effort === "none" && c.model === "openai/gpt-6-luna" && c.temperature === 0));
  assert.match(out.promptSha256, /^[0-9a-f]{64}$/);
  assert.doesNotMatch(JSON.stringify(out), /Rate 0-3/, "the prompt text never lands in the output");
  const m = judgeMetrics(out);
  assert.equal(m.judgedCards, cardCalls);
  assert.equal(m.ungroundedReplies, replyCalls);
  assert.ok(out.cards.find((c) => c.rank === 1 && c.turn === 1).satisfies === 3);
});

test("a 402 stops the run: nothing after it is called (§7 rule 4)", async () => {
  let n = 0;
  const fakeFetch = async () => { n += 1; return { ok: false, status: 402, json: async () => ({}) }; };
  const rec = { label: "t", scenarios: [baseline.scenarios.find((s) => s.id === "ninja-critique")] };
  const out = await judgeRecording({ recording: rec, definitions: open, facts: new Map(), prompt: PROMPT, apiKey: "k", fetchImpl: fakeFetch, concurrency: 1 });
  assert.equal(n, 1);
  assert.match(out.stopped, /402/);
  assert.ok(out.cards.slice(1).every((c) => /^not run/.test(c.error)));
});

test("the key is read from the env file by name and an absent one is null", () => {
  const dir = mkdtempSync(join(tmpdir(), "judge-env-"));
  const f = join(dir, "env");
  writeFileSync(f, "OTHER=1\nexport OPENROUTER_API_KEY=\"abc\"\n");
  assert.equal(keyFromEnvFile(f), "abc");
  writeFileSync(f, "OTHER=1\n");
  assert.equal(keyFromEnvFile(f), null);
  rmSync(dir, { recursive: true });
});

test("the judge's regression check gates nothing until κ clears, and then holds the pre-registered tolerances", () => {
  const gates = JSON.parse(readFileSync(join(repo, "eval", "askreel", "gates.json"), "utf8")).judge;
  const run = (relevant, ungrounded) => ({ model: "m", promptSha256: "p",
    cards: Array.from({ length: 10 }, (_, i) => ({ itemId: `x#${i}`, satisfies: i < relevant ? 3 : 0 })),
    replies: Array.from({ length: 4 }, (_, i) => ({ grounded: i >= ungrounded })) });
  const base = run(8, 0);
  assert.equal(compareJudge(run(2, 3), base, null, gates).gating, false, "no κ report: never gates");
  assert.equal(compareJudge(run(2, 3), base, { kappa: 0.41, judgeGates: false }, gates).gating, false);
  const ok = { kappa: 0.7, judgeGates: true, promptSha256: "p" };
  assert.ok(compareJudge(run(8, 0), base, ok, gates).rows.every((r) => r.status === "pass"));
  const worse = compareJudge(run(7, 1), base, ok, gates).rows;
  assert.equal(worse.find((r) => r.metric === "judge.relevantShare").status, "REGRESSION");
  assert.equal(worse.find((r) => r.metric === "judge.ungroundedReplies").status, "REGRESSION");
  assert.match(compareJudge({ ...base, promptSha256: "q" }, base, ok, gates).refused, /different judge prompts/);
});
