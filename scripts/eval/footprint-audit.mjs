#!/usr/bin/env node
// The timestamp-aware zero-footprint audit (program WS-7 task 7) over an eval database's exposure log.
//
//   node scripts/eval/footprint-audit.mjs --db reel_eval [--since <ISO time>] [--account <id>] [--json <file>]
//
// Lists every exposure served AFTER its title entered the account's footprint or a standing exclusion
// (eval/surfaces/footprint-audit.sql says how each lock's start is known), per surface and reason, with
// the share that came more than a day late and the share from FD-18 probe conversations. Exit 0 when there
// is none since --since, 1 when there is, 2 on a usage or database error. Read-only: it never writes.
// Run it with --since <a harness run's start> to gate that run's own exposures, or with no --since to
// audit the whole history of a snapshot.
import { readFileSync, writeFileSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const isMain = process.argv[1] === fileURLToPath(import.meta.url);

// The marker where supply/rules.sql applies its footprint filter. The audit needs every (title, rule it
// breaks), footprint or not — a title exposed while a rule held it out and watched afterwards is still a
// rule leak at the time it was served — so the view is rules.sql cut at this line.
export const FOOTPRINT_FILTER = ` WHERE NOT EXISTS (SELECT 1 FROM "WatchedTitles" w WHERE w."AccountId" = :'account' AND w."TitleId" = t."Id")`;

/** supply/rules.sql without its footprint filter: every (title, rule it breaks). Throws if the marker moved. */
export function rulesWithoutFootprint(rulesSql) {
  const at = rulesSql.indexOf(FOOTPRINT_FILTER);
  if (at < 0) throw new Error("supply/rules.sql no longer carries its footprint filter where footprint-audit expects it");
  const body = rulesSql.slice(0, at).split("\n").filter((l) => !l.trimStart().startsWith("--")).join("\n").trim();
  return body;
}

/** The audit's whole script for psql: the rules view, then the report query. */
export function auditSql() {
  const rules = readFileSync(join(repo, "eval", "askreel", "supply", "rules.sql"), "utf8");
  const audit = readFileSync(join(repo, "eval", "surfaces", "footprint-audit.sql"), "utf8");
  return `create temp view eval_rules_all as ${rulesWithoutFootprint(rules)};\n${audit}`;
}

/** A Postgres text[] literal of the FD-18 probe conversation ids. */
export function probeArray(fd18) {
  const ids = Object.values(fd18.probeConversations ?? {}).flat().filter((x) => /^[0-9a-f-]{36}$/i.test(x));
  return `{${ids.join(",")}}`;
}

/** The report rows as objects. */
export function rowsToLeaks(rows) {
  return rows.map(([surface, reason, exposures, titles, afterADay, probes, first, last, examples]) => ({
    surface, reason, exposures: Number(exposures), titles: Number(titles), afterADay: Number(afterADay),
    probes: Number(probes), first, last, examples: examples ?? "",
  }));
}

/** Markdown for the report rows. */
export function renderLeaks(leaks, { db, account, since }) {
  const total = leaks.reduce((n, l) => n + l.exposures, 0);
  const L = [`# Zero-footprint audit — ${db}`, "", `- Account \`${account}\`, exposures since ${since}.`,
    "- One exposure can break several locks; it is counted under each.", "",
    total === 0 ? "No exposure was served after its title entered the footprint or a standing exclusion." : `**${total} exposure(s) served after the lock had started.**`, ""];
  if (leaks.length) {
    L.push("| Surface | Reason | Exposures | Titles | > 1 day late | FD-18 probes | First | Last | Examples |", "|---|---|---|---|---|---|---|---|---|");
    for (const l of leaks) L.push(`| ${l.surface} | ${l.reason} | ${l.exposures} | ${l.titles} | ${l.afterADay} | ${l.probes} | ${l.first} | ${l.last} | ${l.examples.replaceAll("|", "/")} |`);
  }
  return L.join("\n") + "\n";
}

if (isMain) {
  const args = {};
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith("--")) continue;
    const next = argv[i + 1];
    if (next === undefined || next.startsWith("--")) args[argv[i].slice(2)] = true;
    else { args[argv[i].slice(2)] = next; i++; }
  }
  const die = (m) => { console.error(`footprint-audit: ${m}`); process.exit(2); };
  const PG = { host: process.env.EVAL_PGHOST ?? "127.0.0.1", port: process.env.EVAL_PGPORT ?? "5434", user: process.env.EVAL_PGUSER ?? "reel", password: process.env.EVAL_PGPASSWORD ?? "reel_eval" };
  const db = args.db ?? die("--db is required");
  if (String(PG.port) === "5433") die("refusing port 5433 — the founder's LIVE Postgres");
  if (db === "reel" || !/^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*|reel_int_.*)$/.test(db)) die(`refusing database '${db}'`);
  const sql = (query, vars = {}) => execFileSync("psql", ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-At", "-F", "\x1f", "-h", PG.host, "-p", PG.port, "-U", PG.user, "-d", db,
    ...Object.entries(vars).flatMap(([k, v]) => ["-v", `${k}=${v}`]), "-f", "-"],
  { input: query, env: { ...process.env, PGPASSWORD: PG.password, PGTZ: "UTC" }, maxBuffer: 256 * 1024 * 1024 })
    .toString().split("\n").filter(Boolean).map((l) => l.split("\x1f"));

  let account = args.account;
  if (!account) {
    const ids = sql(`select "Id" from "Accounts" order by "CreatedAt" limit 2`).map((r) => r[0]);
    if (ids.length !== 1) die(`--account is required: ${db} has ${ids.length === 0 ? "no" : "more than one"} account`);
    account = ids[0];
  }
  const since = typeof args.since === "string" ? args.since : "1970-01-01T00:00:00Z";
  const fd18 = JSON.parse(readFileSync(join(repo, "eval", "askreel", "fd18-exclusions.json"), "utf8"));
  let leaks;
  try {
    leaks = rowsToLeaks(sql(auditSql(), { account, since, probes: probeArray(fd18) }));
  } catch (err) { die(String(err.message).split("\n")[0]); }
  const report = renderLeaks(leaks, { db, account, since });
  console.log(report);
  if (typeof args.json === "string") writeFileSync(resolve(args.json), JSON.stringify({ db, account, since, leaks }, null, 1));
  process.exit(leaks.length ? 1 : 0);
}
