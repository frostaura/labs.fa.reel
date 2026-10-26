// Replay-mode tests for the zero-footprint audit's pure parts: no database, no paid call.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { rulesWithoutFootprint, probeArray, rowsToLeaks, renderLeaks, auditSql } from "./footprint-audit.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const rules = readFileSync(join(repo, "eval", "askreel", "supply", "rules.sql"), "utf8");

test("the audit's rule view is rules.sql cut at its footprint filter, comments dropped", () => {
  const view = rulesWithoutFootprint(rules);
  assert.match(view, /^SELECT DISTINCT t\."Id", x\."Kind", x\."Value"/);
  assert.doesNotMatch(view, /"WatchedTitles"/, "the footprint filter is gone, so a title watched later still shows the rule it broke");
  assert.doesNotMatch(view, /^\s*--/m);
  assert.match(auditSql(), /^create temp view eval_rules_all as SELECT DISTINCT/);
});

test("a rules.sql whose footprint filter moved is refused, not silently audited without it", () => {
  assert.throws(() => rulesWithoutFootprint("SELECT 1"), /footprint filter/);
});

test("FD-18 probe ids become a text[] literal; anything that is not a uuid is dropped", () => {
  assert.equal(probeArray({ probeConversations: { A: ["4c3a4eb9-da8a-453f-b3c5-cbf86ce7f73b", "'); drop table x; --"], B: [] } }), "{4c3a4eb9-da8a-453f-b3c5-cbf86ce7f73b}");
  assert.equal(probeArray({}), "{}");
});

test("report rows render with their totals; an empty audit says so", () => {
  const leaks = rowsToLeaks([["GenreRail", "rule ExcludeGenre comedy", "830", "178", "830", "0", "2026-09-10T17:28:35Z", "2026-10-22T21:20:00Z", "A | B"]]);
  assert.deepEqual(leaks[0], { surface: "GenreRail", reason: "rule ExcludeGenre comedy", exposures: 830, titles: 178, afterADay: 830, probes: 0, first: "2026-09-10T17:28:35Z", last: "2026-10-22T21:20:00Z", examples: "A | B" });
  const md = renderLeaks(leaks, { db: "reel_eval", account: "a", since: "s" });
  assert.match(md, /\*\*830 exposure\(s\) served after the lock had started\.\*\*/);
  assert.match(md, /\| GenreRail \| rule ExcludeGenre comedy \| 830 \| 178 \| 830 \| 0 \|.*\| A \/ B \|/);
  assert.match(renderLeaks([], { db: "d", account: "a", since: "s" }), /No exposure was served after/);
});
