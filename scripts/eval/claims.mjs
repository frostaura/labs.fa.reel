#!/usr/bin/env node
// User-facing claims audit (program WS-7 task 9). Pure: reads the source tree and eval/claims/claims.json.
//
//   node scripts/eval/claims.mjs check            exit 1 when the tree and the register disagree (below)
//   node scripts/eval/claims.mjs report           the register as a table: every family, owner, verdict
//   node scripts/eval/claims.mjs unregistered     the claim-shaped strings no register entry covers
//
// `check` fails on: a claim-shaped string the register does not cover (new copy is audited before it
// ships); a registered text no longer in its file (an edited claim is re-audited, never silently
// dropped); a family with no owner, no verdict or no test; a test reference whose file or test name does
// not exist. It does NOT fail on a false or unverified claim — those are defects owned by the family's
// workstream, counted by the surface harness ("false copy claims") and listed by `report` — the same way
// the exclusion matrix ledgers a known leak rather than going red on it.
import { readFileSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { extractClaims } from "./claims-extract.mjs";

const here = dirname(fileURLToPath(import.meta.url));
export const REPO = resolve(here, "..", "..");
const VERDICTS = new Set(["true", "false", "unverified"]);
const OWNERS = /^WS-(?:[0-9]|10[AB]|2B)$/u;

export function loadRegister(repo = REPO) {
  return JSON.parse(readFileSync(join(repo, "eval", "claims", "claims.json"), "utf8"));
}

/** Does a test reference ("path" or "path::name") resolve to a file that holds that test? */
export function testExists(repo, ref) {
  const [path, name] = ref.split("::");
  const file = join(repo, path);
  if (!existsSync(file)) return false;
  return name === undefined || readFileSync(file, "utf8").includes(name);
}

/**
 * Holds the extracted claims to the register. Returns everything a caller needs to report or gate:
 * the problems that fail `check`, and the false and unverified claims that are owned defects.
 */
export function auditClaims(repo = REPO, register = loadRegister(repo), extracted = extractClaims(repo)) {
  const entries = [
    ...register.families.flatMap((f) => f.claims.map((c) => ({ ...c, family: f.id }))),
    ...register.notClaims.map((c) => ({ ...c, family: null })),
  ];
  const covers = (entry, s) => entry.file === s.file && s.text.includes(entry.text);
  const unregistered = extracted.filter((s) => !entries.some((e) => covers(e, s)));
  const stale = entries.filter((e) => !extracted.some((s) => covers(e, s)));
  const problems = [];
  const ids = new Set();
  for (const f of register.families) {
    if (ids.has(f.id)) problems.push(`family '${f.id}' is registered twice`);
    ids.add(f.id);
    if (!OWNERS.test(f.owner ?? "")) problems.push(`family '${f.id}' has no workstream owner`);
    if (!VERDICTS.has(f.verdict)) problems.push(`family '${f.id}' has verdict '${f.verdict}' (true | false | unverified)`);
    if (f.verdict === "false" && !f.defect) problems.push(`family '${f.id}' is false and names no defect`);
    if (!f.tests?.length) problems.push(`family '${f.id}' names no test`);
    for (const t of f.tests ?? []) if (!testExists(repo, t)) problems.push(`family '${f.id}': test '${t}' does not exist`);
    if (!f.claims?.length) problems.push(`family '${f.id}' registers no claim`);
  }
  for (const u of unregistered) problems.push(`unregistered claim ${u.file}:${u.line} — “${u.text}”`);
  for (const s of stale) problems.push(`registered text no longer in ${s.file} — “${s.text}”${s.family ? ` (family '${s.family}': re-audit the edited copy)` : ""}`);

  const shown = (verdict) => register.families.filter((f) => f.verdict === verdict)
    .map((f) => ({ id: f.id, owner: f.owner, claims: f.claims.length, defect: f.defect ?? null, assertion: f.assertion }));
  const falseFamilies = shown("false");
  const unverifiedFamilies = shown("unverified");
  return {
    extracted: extracted.length,
    registered: entries.length,
    families: register.families.length,
    claims: register.families.reduce((n, f) => n + f.claims.length, 0),
    trueClaims: register.families.filter((f) => f.verdict === "true").reduce((n, f) => n + f.claims.length, 0),
    falseClaims: falseFamilies.reduce((n, f) => n + f.claims, 0),
    unverifiedClaims: unverifiedFamilies.reduce((n, f) => n + f.claims, 0),
    falseFamilies,
    unverifiedFamilies,
    unregistered,
    stale,
    problems,
  };
}

function report(a, register) {
  const L = ["| Family | Owner | Verdict | Claims | Tests |", "|---|---|---|---|---|"];
  for (const f of register.families) L.push(`| ${f.id} | ${f.owner} | ${f.verdict} | ${f.claims.length} | ${f.tests.length} |`);
  L.push("", `Extracted ${a.extracted} claim-shaped strings; ${a.claims} claims in ${a.families} families (${a.trueClaims} true, ${a.falseClaims} false, ${a.unverifiedClaims} unverified); ${register.notClaims.length} registered as not claims.`);
  if (a.falseFamilies.length) L.push("", "## False claims (owned defects)", "", ...a.falseFamilies.map((f) => `- **${f.id}** (${f.owner}, ${f.claims} line(s)): ${f.defect}`));
  if (a.unverifiedFamilies.length) L.push("", "## Unverified (the owner owes the test)", "", ...a.unverifiedFamilies.map((f) => `- **${f.id}** (${f.owner}, ${f.claims} line(s)): ${f.assertion}`));
  if (a.problems.length) L.push("", "## Check failures", "", ...a.problems.map((p) => `- ${p}`));
  return L.join("\n");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const command = process.argv[2] ?? "check";
  const register = loadRegister();
  const a = auditClaims(REPO, register);
  if (command === "report") {
    console.log(report(a, register));
  } else if (command === "unregistered") {
    for (const u of a.unregistered) console.log(`${u.file}:${u.line}\t${u.text}`);
  } else if (command === "check") {
    if (a.problems.length) {
      console.error(`claims: ${a.problems.length} problem(s)\n${a.problems.map((p) => `  - ${p}`).join("\n")}`);
      process.exit(1);
    }
    console.log(`claims: ${a.extracted} claim-shaped strings all registered; ${a.claims} claims (${a.trueClaims} true, ${a.falseClaims} false, ${a.unverifiedClaims} unverified) in ${a.families} families, every test present`);
  } else {
    console.error(`claims: unknown command '${command}' (check | report | unregistered)`);
    process.exit(2);
  }
}
