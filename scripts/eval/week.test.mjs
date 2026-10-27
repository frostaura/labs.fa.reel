// The synthetic week's acceptance checks (R1-36): pure, no database.
//   node --test scripts/eval/week.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { CHECKS, judge, parseNumber, render } from "./week.mjs";

test("every check is owned by the workstream whose acceptance row it is, once, with a gate", () => {
  const ids = CHECKS.map((c) => c.id);
  assert.equal(new Set(ids).size, ids.length, "duplicate check id");
  for (const c of CHECKS) {
    assert.match(c.owner, /^WS-[456]$/u, `${c.id}: the week carries WS-4, WS-5 and WS-6 rows only`);
    assert.ok(["==", "<", "<=", ">=", "info"].includes(c.op), `${c.id}: unknown op ${c.op}`);
    if (c.op !== "info") assert.equal(typeof c.target, "number", `${c.id}: a gate needs a numeric target`);
  }
  // The rows §5.2 names explicitly are all present.
  for (const id of ["ws6-attempts-over-3", "ws6-failed-without-completion", "ws6-parameter-names-in-errors", "ws6-delta-wait-p95", "ws6-buildfeed-run-p95",
    "ws4-consumed-active-saves", "ws4-failed-attributes-with-raw", "ws4-overlapping-enrich-runs", "ws4-saves-with-fresh-availability",
    "ws5-shortlist-p50-spread", "ws5-anchors-from-9-10", "ws5-retrains-on-days-without-ratings"]) {
    assert.ok(ids.includes(id), `missing ${id}`);
  }
});

test("every check is bounded to the week or to the account, never the whole snapshot's job history", () => {
  for (const c of CHECKS) {
    if (c.id.startsWith("ws4-failed-attribute")) continue; // the attribute table's state after the week is the row itself
    assert.match(c.sql, /:'since'|:'account'/u, `${c.id} reads neither the week nor the account`);
    if (/"SyncJobs"/u.test(c.sql)) assert.match(c.sql, /"EnqueuedAt" >= :'since'/u, `${c.id} reads jobs outside the week`);
  }
});

test("a gate passes or fails on a number; no data and info rows gate nothing", () => {
  const eq0 = { op: "==", target: 0 };
  assert.equal(judge(eq0, 0), true);
  assert.equal(judge(eq0, 1), false);
  assert.equal(judge(eq0, null), null);
  // Vary one field at a time: the boundary of each operator.
  assert.equal(judge({ op: "<", target: 30 }, 29.9), true);
  assert.equal(judge({ op: "<", target: 30 }, 30), false);
  assert.equal(judge({ op: "<=", target: 1 }, 1), true);
  assert.equal(judge({ op: ">=", target: 0.9 }, 0.9), true);
  assert.equal(judge({ op: ">=", target: 0.9 }, 0.899), false);
  assert.equal(judge({ op: "info" }, 7), null);
  assert.throws(() => judge({ op: "~" }, 1));
});

test("psql's empty field is NULL, not zero", () => {
  assert.equal(parseNumber(""), null);
  assert.equal(parseNumber("\n"), null);
  assert.equal(parseNumber("0\n"), 0);
  assert.equal(parseNumber("1.2"), 1.2);
});

test("the report counts failures and no-data rows separately and names an early stop", () => {
  const run = { database: "reel_ws7_week", ticks: 28, startedAt: "s", agedHours: 162, since: "x", traktCalls: { "sync/ratings": 3 }, stopped: null };
  const rows = [
    { check: { owner: "WS-6", label: "a", op: "==", target: 0 }, value: 1, pass: false },
    { check: { owner: "WS-5", label: "b", op: "<=", target: 1 }, value: null, pass: null },
    { check: { owner: "WS-4", label: "c", op: "info" }, value: 4, pass: null },
  ];
  const text = render(run, rows);
  assert.match(text, /1 failed, 1 without data, 1 passed or informational/u);
  assert.match(text, /\| WS-6 \| a \| 1 \| == 0 \| \*\*FAIL\*\* \|/u);
  assert.match(text, /\| WS-5 \| b \| no data \| <= 1 \| no data \|/u);
  assert.match(text, /\| WS-4 \| c \| 4 \| — \| info \|/u);
  assert.doesNotMatch(text, /stopped early/u);
  assert.match(render({ ...run, stopped: "tick 3: busy" }, rows), /\*\*stopped early:\*\* tick 3: busy/u);
});
