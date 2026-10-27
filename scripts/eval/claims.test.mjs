// The user-facing claims audit (WS-7 task 9): extraction, the register check, and the live tree.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CLAIM_TRIGGER, isProse, normalise } from "./claims-extract.mjs";
import { auditClaims, loadRegister, testExists, REPO } from "./claims.mjs";

test("the trigger catches the program's example claims and leaves control labels alone", () => {
  for (const claim of [
    "Only a model that beats it ships",
    "Watch something and it leaves by itself.",
    "Everything here syncs to the managed list",
    "Paid unlocks Ask Reel",
    "Streaming on your services",
    "A seconds-long rebuild",
    "New & high for you this week",
    "Just landed on Netflix",
    "1 rating in — Reel trains your first model tonight.",
  ]) assert.ok(CLAIM_TRIGGER.test(claim), claim);
  for (const label of ["Sign in with Trakt", "Clear rating", "Show more", "Open settings"]) {
    assert.ok(!CLAIM_TRIGGER.test(label), label);
  }
});

test("prose is told apart from identifiers, class lists, routes and short labels", () => {
  assert.ok(isProse("Watch something and it leaves by itself."));
  assert.ok(!isProse("flex items-center gap-2 text-fa-frost-dim"));
  assert.ok(!isProse("/api/feed/more"));
  assert.ok(!isProse("Save"));
  assert.equal(normalise("One notification on Fridays when your &ldquo;new &amp; high&rdquo;  picks land."), 'One notification on Fridays when your "new & high" picks land.');
});

test("a claim-shaped string the register does not cover fails the check, and so does an entry whose text left the tree", () => {
  const register = {
    families: [{ id: "f", owner: "WS-4", verdict: "true", assertion: "a", evidence: "e", tests: ["scripts/eval/claims.test.mjs"], claims: [{ file: "x.tsx", text: "leaves by itself" }] }],
    notClaims: [{ file: "x.tsx", text: "Gone label", reason: "label" }],
  };
  const extracted = [
    { file: "x.tsx", line: 1, text: "Watch something and it leaves by itself." },
    { file: "y.tsx", line: 2, text: "Everything here syncs automatically." },
  ];
  const a = auditClaims(REPO, register, extracted);
  assert.equal(a.unregistered.length, 1);
  assert.match(a.problems.join("\n"), /unregistered claim y\.tsx:2/);
  assert.match(a.problems.join("\n"), /no longer in x\.tsx — “Gone label”/);
});

test("a false family needs a defect, every family an owner and tests that exist", () => {
  const register = {
    families: [{ id: "f", owner: "nobody", verdict: "false", assertion: "a", evidence: "e", tests: [`scripts/eval/claims.test.mjs::${["no", "such", "test"].join("-")}`], claims: [{ file: "x.tsx", text: "t" }] }],
    notClaims: [],
  };
  const problems = auditClaims(REPO, register, [{ file: "x.tsx", line: 1, text: "t" }]).problems.join("\n");
  assert.match(problems, /no workstream owner/);
  assert.match(problems, /false and names no defect/);
  assert.match(problems, /does not exist/);
  assert.ok(testExists(REPO, "scripts/eval/claims.test.mjs::a false family needs a defect"));
});

test("the tree and the register agree: every claim-shaped string is registered, every test exists", () => {
  const a = auditClaims(REPO, loadRegister());
  assert.deepEqual(a.problems, [], a.problems.join("\n"));
  assert.ok(a.extracted >= 200, `only ${a.extracted} strings extracted — the sweep lost a source`);
  // The false claims are owned defects, counted by the surface harness; the register states each one's fix.
  assert.ok(a.falseFamilies.every((f) => f.defect && f.owner));
});
