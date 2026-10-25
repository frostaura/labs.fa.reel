#!/usr/bin/env node
// Daily ledger cost check (program §7 rules 3, 4 and 7; WS-7 task 11). Manual dispatch — no schedule.
//
//   node scripts/eval/ledger.mjs --all [--day 2026-10-24]            every eval and lane database on :5434
//                                                                    plus archived rows of dropped ones
//   node scripts/eval/ledger.mjs --db reel_eval [--day 2026-10-24]   one database
//   node scripts/eval/ledger.mjs archive --db reel_ws7               keep a database's rows before a DROP
//   node scripts/eval/ledger.mjs --openrouter-key <env file>         ask OpenRouter what that key has spent
//   node scripts/eval/ledger.mjs --db reel --port 5433 --allow-live-read      (founder / integrator only)
//
// §7's $5 / $8 lines are per day PER KEY. Every lane database and the eval database share whatever key
// their API was started with (today: one shared key), so a per-database total cannot enforce them: --all
// sums every eval/lane database on the eval Postgres, net of the rows each inherited from the snapshot
// template, plus the archived rows of databases that were dropped and re-cloned during the day (snapshot.sh
// archives before every DROP). --openrouter-key reads the provider's own figure for a key (a free
// metadata call; the key is sent only to openrouter.ai and never printed), which is the only number that
// also covers the live stack and anything not recorded in a ledger. The verdict applies to the total.
//
// It only ever READS the databases. Against the live database it requires --allow-live-read and runs in a
// read-only transaction; agents in the build lanes never pass that flag (they may not touch :5433).
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { homedir } from "node:os";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const isMain = process.argv[1] === fileURLToPath(import.meta.url);
const argv = isMain ? process.argv.slice(2) : [];
const args = { _: [] };
for (let i = 0; i < argv.length; i++) {
  if (!argv[i].startsWith("--")) { args._.push(argv[i]); continue; }
  const next = argv[i + 1];
  if (next === undefined || next.startsWith("--")) args[argv[i].slice(2)] = true;
  else { args[argv[i].slice(2)] = next; i++; }
}
const die = (m) => { console.error(`ledger: ${m}`); process.exit(2); };
export const ARCHIVE_DIR = process.env.EVAL_LEDGER_ARCHIVE ?? join(homedir(), "src", "fa.reel-ws", "ledger-archive");
const EVAL_DB = /^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*)$/;
const TEMPLATE = process.env.EVAL_TEMPLATE_DB ?? "reel_eval_template";
const port = String(args.port ?? process.env.EVAL_PGPORT ?? "5434");

function psql(db, query, live = false) {
  const env = { ...process.env, PGTZ: "UTC", PGOPTIONS: "-c default_transaction_read_only=on" };
  if (!live) env.PGPASSWORD = process.env.EVAL_PGPASSWORD ?? "reel_eval";
  return execFileSync("psql", ["-X", "-v", "ON_ERROR_STOP=1", "-At", "-F", "\x1f", "-h", process.env.EVAL_PGHOST ?? "127.0.0.1", "-p", port,
    "-U", process.env.EVAL_PGUSER ?? "reel", "-d", db, "-c", query], { env }).toString()
    .split("\n").filter(Boolean).map((l) => l.split("\x1f"));
}

const rowsOf = (db, where, live = false) => psql(db, `select "Provider", coalesce("Model", ''), coalesce("Purpose", ''), "Day"::text, sum("CallCount"), sum(coalesce("PromptTokens", 0)), sum(coalesce("CompletionTokens", 0)), sum(coalesce("TokensUsed", 0))
     from "ExternalApiUsages" ${where} group by 1, 2, 3, 4`, live)
  .map(([provider, model, purpose, day, calls, pt, ct, tu]) => ({ provider, model, purpose, day, calls: Number(calls), promptTokens: Number(pt), completionTokens: Number(ct), tokensUsed: Number(tu) }));

const k = (r) => [r.provider, r.model, r.purpose, r.day].join("|");
// Rows a database spent itself: its rows minus the ones it inherited from the snapshot template.
export function netOf(rows, inherited) {
  const base = new Map(inherited.map((r) => [k(r), r]));
  return rows.map((r) => {
    const b = base.get(k(r)) ?? { calls: 0, promptTokens: 0, completionTokens: 0, tokensUsed: 0 };
    return { ...r, calls: r.calls - b.calls, promptTokens: r.promptTokens - b.promptTokens, completionTokens: r.completionTokens - b.completionTokens, tokensUsed: r.tokensUsed - b.tokensUsed };
  }).filter((r) => r.calls || r.promptTokens || r.completionTokens || r.tokensUsed);
}

export function priced(rows, prices) {
  let total = 0;
  const unpriced = new Set();
  const out = rows.map((r) => {
    const p = prices[r.model];
    let usd = null;
    if (p) { usd = ((r.promptTokens || (r.completionTokens ? 0 : r.tokensUsed)) * p.inputPerMillion + r.completionTokens * (p.outputPerMillion ?? 0)) / 1e6; total += usd; }
    else if (r.provider === "OpenRouter") unpriced.add(r.model || "(no model)");
    return { ...r, usd };
  });
  return { rows: out, total, unpriced: [...unpriced] };
}

// §7 rule 3 and the Trakt stop rule, on one day's total across every database sharing the key.
export function verdicts(total, traktCalls, live) {
  const v = [];
  if (total > 8) v.push("ABOVE $8: everything pauses (§7 rule 3)");
  else if (total > 5) v.push("ABOVE $5: all non-eval live calls pause (§7 rule 3)");
  if (!live && traktCalls > 0) v.push(`ISOLATION FAILURE: ${traktCalls} Trakt call(s) recorded on an eval database — stop and fix (§7 rule 4)`);
  return v;
}

const prices = () => JSON.parse(readFileSync(join(repo, "eval", "askreel", "prices.json"), "utf8")).models;
const INTERACTIVE = /^(search-|rerank-|ask-|lens)/;
const day = args.day ?? new Date().toISOString().slice(0, 10);
if (isMain && !/^\d{4}-\d{2}-\d{2}$/.test(day)) die("--day must be YYYY-MM-DD");

function printTable(rows) {
  console.log("db | provider | model | purpose | calls | prompt | completion | usd");
  for (const r of rows.sort((a, b) => b.calls - a.calls)) {
    const flag = INTERACTIVE.test(r.purpose) && r.calls >= 50 ? "  ← ≥50 calls: check the finish_reason=length rate (§7 rule 4)" : "";
    console.log(`${r.db ?? "—"} | ${r.provider} | ${r.model || "—"} | ${r.purpose || "—"} | ${r.calls} | ${r.promptTokens} | ${r.completionTokens} | ${r.usd === null ? "—" : r.usd.toFixed(4)}${flag}`);
  }
}

if (isMain && args._[0] === "archive") {
  const db = args.db ?? die("archive needs --db");
  if (db === "reel" || port === "5433" || !EVAL_DB.test(db)) die(`refusing database '${db}'`);
  const rows = rowsOf(db, "");
  mkdirSync(ARCHIVE_DIR, { recursive: true });
  const file = join(ARCHIVE_DIR, `${db}-${new Date().toISOString().replace(/[:.]/g, "-")}.json`);
  writeFileSync(file, JSON.stringify({ db, archivedAt: new Date().toISOString(), template: TEMPLATE, rows }));
  console.log(`ledger: archived ${rows.length} ledger rows of ${db} to ${file}`);
  process.exit(0);
}

if (isMain && args["openrouter-key"]) {
  // The provider's own account of one key. GET /api/v1/key is metadata, not a model call.
  const file = String(args["openrouter-key"]);
  const line = readFileSync(file, "utf8").split("\n").map((l) => l.trim().replace(/^export\s+/, "")).find((l) => l.startsWith("OPENROUTER_API_KEY="));
  const keyValue = line?.slice("OPENROUTER_API_KEY=".length).trim().replace(/^(["'])(.*)\1$/, "$2");
  if (!keyValue) die(`no OPENROUTER_API_KEY in ${file}`);
  const res = await fetch("https://openrouter.ai/api/v1/key", { headers: { Authorization: `Bearer ${keyValue}` } });
  if (!res.ok) die(`OpenRouter answered HTTP ${res.status}`);
  const data = (await res.json()).data ?? {};
  const numeric = Object.fromEntries(Object.entries(data).filter(([, v]) => typeof v === "number" || v === null || typeof v === "boolean"));
  console.log(`OpenRouter key in ${file} (value not shown): ${JSON.stringify(numeric)}`);
  process.exit(0);
}

if (isMain && args.all) {
  const dbs = psql("postgres", "select datname from pg_database order by 1").map((r) => r[0]).filter((d) => EVAL_DB.test(d) && d !== TEMPLATE && !d.startsWith("reel_test_"));
  const inherited = dbs.length ? rowsOf(TEMPLATE, `where "Day" = '${day}'`) : [];
  const all = [];
  for (const db of dbs) for (const r of netOf(rowsOf(db, `where "Day" = '${day}'`), inherited)) all.push({ ...r, db });
  const archives = existsSync(ARCHIVE_DIR) ? readdirSync(ARCHIVE_DIR).filter((f) => f.endsWith(".json")) : [];
  for (const f of archives) {
    const a = JSON.parse(readFileSync(join(ARCHIVE_DIR, f), "utf8"));
    for (const r of netOf(a.rows.filter((x) => x.day === day), inherited)) all.push({ ...r, db: `${a.db} (archived ${a.archivedAt.slice(11, 16)}Z)` });
  }
  const p = priced(all, prices());
  console.log(`Ledger — every eval/lane database on :${port} (${dbs.length} live, ${archives.length} archive files), net of the snapshot template — UTC day ${day}`);
  printTable(p.rows);
  const byDb = {};
  for (const r of p.rows) byDb[r.db] = (byDb[r.db] ?? 0) + (r.usd ?? 0);
  console.log("\nPer database (§7 rule 7: one line per workstream):");
  for (const [db, usd] of Object.entries(byDb).sort((a, b) => b[1] - a[1])) console.log(`  ${db}: $${usd.toFixed(4)}`);
  const trakt = p.rows.filter((r) => r.provider === "Trakt").reduce((n, r) => n + r.calls, 0);
  console.log(`TOTAL $${p.total.toFixed(4)}${p.unpriced.length ? ` (+ unpriced: ${p.unpriced.join(", ")})` : ""} — this is every recorded eval/lane spend on the shared key; the live stack's own spend is not in it (read it with --openrouter-key or the live ledger)`);
  const v = verdicts(p.total, trakt, false);
  console.log(v.length ? v.join("\n") : "OK: within the §7 ceilings");
  process.exit(v.length ? 1 : 0);
}

if (isMain) {
  const db = args.db ?? die("--db or --all is required");
  const live = db === "reel" || port === "5433";
  if (live && !args["allow-live-read"]) die("refusing the live database without --allow-live-read (founder / integrator only)");
  if (!live && !EVAL_DB.test(db)) die(`refusing database '${db}'`);
  const p = priced(rowsOf(db, `where "Day" = '${day}'`, live), prices());
  console.log(`Ledger ${db}${live ? " (LIVE, read-only)" : ""} — UTC day ${day} (this database only; --all sums every database sharing the key)`);
  printTable(p.rows);
  const trakt = p.rows.filter((r) => r.provider === "Trakt").reduce((n, r) => n + r.calls, 0);
  console.log(`TOTAL $${p.total.toFixed(4)}${p.unpriced.length ? ` (+ unpriced: ${p.unpriced.join(", ")})` : ""}`);
  const v = verdicts(p.total, trakt, live);
  console.log(v.length ? v.join("\n") : "OK: within the §7 ceilings");
  process.exit(v.length ? 1 : 0);
}
