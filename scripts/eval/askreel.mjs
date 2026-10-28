#!/usr/bin/env node
// Ask Reel golden eval runner (program §4.12, WS-7 task 6). Manual dispatch only — never scheduled.
//
//   node scripts/eval/askreel.mjs live   --api http://127.0.0.1:5180 --db reel_eval --label <name> --api-env <env file the API runs with>
//        [--scenarios eval/askreel/scenarios] [--extra-scenarios <dir>] [--truth-dir <dir>] [--concurrency 4]
//        [--only id,id | --only-file eval/askreel/latency-set.txt] [--limit N] [--resume] [--out .tmp/eval/<date>/<label>]
//        --engine v1|v2 [--allow-unnamed-engine] [--build-info <BUILD_INFO.json from build-at.sh>] [--hold-build-slots]
//        [--founder-key-signoff "<who, when>"]
//   node scripts/eval/askreel.mjs replay <recording.json> [--out <dir>]
//   node scripts/eval/askreel.mjs check  [--scenarios <dir>] [--extra-scenarios <dir>] [--truth-dir <dir>] [--db <eval db>]
//   node scripts/eval/askreel.mjs compare <metrics.json> --baseline <metrics.json>
//
// The sealed holdout is run with --extra-scenarios (or --scenarios) <sealed>/askreel/holdout and
// --truth-dir <sealed>/askreel/holdout/truth: its truth queries live with it, never in this tree.
//
// live    drives the API over SSE, one fresh conversation per scenario, at most --concurrency scenarios
//         at once (quality runs ≤ 4; LATENCY GATES COME ONLY FROM A SEPARATE RUN AT ≤ 2). It then
//         materialises every SQL truth set the run needs from the eval database, reads server latency
//         and card sources from the turn record, diffs the token ledger, and writes recording.json,
//         metrics.json and report.md.
// replay  re-scores a recording with no API, no database and no paid call — the CI mode. Everything
//         the scorer needs (truth membership, supply counts, ledger delta) is frozen into the recording.
// compare applies the pre-registered regression tolerances (eval/askreel/gates.json) of a run against a
//         baseline; exit 1 on any regression, exit 2 when the runs are incomparable (different scenario
//         fingerprints, turn counts, snapshot or artifact). This is the merge gate of program §5.0 rule 6.
// check   validates the scenario files (ids, shapes, truth keys, counts, no unused open truth) without
//         running anything; with --db it also executes every truth query on that eval database.
//
// --engine is required: every turn asks the API for that engine (?engine=, honoured for the founder
// account dev/link mints), and the run stops at the first turn whose stream names another engine — or
// none — and exits 1 with the recording marked invalid. The recording's engine is the one the turns
// name, never the flag (HREV2-ASK-05, HREV2-PLAT-3). A pre-§4.13 build (99c0761) names no engine and
// has no v2; only for such a build, --allow-unnamed-engine with --engine v1 records unnamed turns as
// assumed v1 (each flagged engineAssumed).
//
// Latency gates come only from a run at concurrency ≤ 2 with --hold-build-slots, which takes every
// machine build slot for the run's whole length (§5.0 rule 1, §7 rule 5).
//
// The database is only ever the eval Postgres (:5434); the guard below refuses :5433 and `reel`.
import { readFileSync, writeFileSync, appendFileSync, mkdirSync, readdirSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { score, renderReport, compare, replySupply, engineOf, answeredTurn, DEFAULT_TURN_CAP_MS } from "./askreel-score.mjs";
import { modelKeyFor, holdAllBuildSlots } from "./guards.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const evalDir = join(repo, "eval", "askreel");

// ── args ──────────────────────────────────────────────────────────────────────────────────────
// Imported by the replay tests for its pure helpers; only a direct invocation runs a mode.
const isMain = process.argv[1] === fileURLToPath(import.meta.url);
const [, , mode, ...rest] = isMain ? process.argv : [];
const args = { _: [] };
for (let i = 0; i < rest.length; i++) {
  const a = rest[i];
  if (a.startsWith("--")) {
    const key = a.slice(2);
    const next = rest[i + 1];
    if (next === undefined || next.startsWith("--")) args[key] = true;
    else { args[key] = next; i++; }
  } else args._.push(a);
}

function die(msg) { console.error(`askreel: ${msg}`); process.exit(2); }

// ── database (eval Postgres only) ─────────────────────────────────────────────────────────────
const PG = {
  host: process.env.EVAL_PGHOST ?? "127.0.0.1",
  port: process.env.EVAL_PGPORT ?? "5434",
  user: process.env.EVAL_PGUSER ?? "reel",
  password: process.env.EVAL_PGPASSWORD ?? "reel_eval",
};

export function guardTarget(db, port = PG.port) {
  if (!db) die("no database named");
  if (String(port) === "5433") die("refusing port 5433 — that is the founder's LIVE Postgres");
  if (db === "reel") die("refusing database 'reel' — that is the founder's LIVE database");
  if (!/^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*|reel_int_.*)$/.test(db)) die(`refusing database '${db}'`);
}

function sql(db, query, vars = {}) {
  guardTarget(db);
  const v = Object.entries(vars).flatMap(([k, val]) => ["-v", `${k}=${val}`]);
  const out = execFileSync("psql", ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-At", "-F", "\x1f", "-h", PG.host, "-p", PG.port,
    "-U", PG.user, "-d", db, ...v, "-f", "-"], {
    input: query, env: { ...process.env, PGPASSWORD: PG.password, PGTZ: "UTC" }, maxBuffer: 256 * 1024 * 1024,
  }).toString();
  return out.split("\n").filter((l) => l.length > 0).map((l) => l.split("\x1f"));
}

// ── scenarios ─────────────────────────────────────────────────────────────────────────────────
export function loadScenarios(dirs) {
  const all = [];
  for (const dir of dirs) {
    if (!dir || !existsSync(dir)) continue;
    for (const f of readdirSync(dir).filter((n) => n.endsWith(".json")).sort()) {
      const doc = JSON.parse(readFileSync(join(dir, f), "utf8"));
      for (const s of doc.scenarios ?? []) all.push({ ...s, file: f, set: doc.set ?? "open" });
    }
  }
  return all;
}

const ACTS = ["NewRequest", "Refine", "CritiqueSet", "CritiqueItem", "ItemQuestion", "Chitchat"];
const MODES = ["Picks", "Answer", "Clarify", "NoneFound", "Any"];

export function truthKeysIn(dir) {
  return existsSync(dir) ? readdirSync(dir).filter((n) => n.endsWith(".sql") && !n.startsWith("_")).map((n) => n.slice(0, -4)) : [];
}

export function usedTruthKeys(scenarios) {
  return new Set(scenarios.flatMap((s) => (s.turns ?? []).flatMap((t) => [t.expect?.concept, ...(t.expect?.mustNot ?? [])].filter(Boolean))));
}

// The open truth directory holds only what the open set uses: a truth file no open scenario uses names
// a holdout topic (or dead weight) to every lane that reads the tree.
export function unusedOpenTruth(scenarios, openTruthDir) {
  const used = usedTruthKeys(scenarios.filter((s) => s.set === "open"));
  return truthKeysIn(openTruthDir).filter((k) => !used.has(k));
}

export function validateScenarios(scenarios, truthDirs) {
  const errors = [];
  const ids = new Set();
  const truthKeys = new Set([truthDirs].flat().flatMap(truthKeysIn));
  for (const s of scenarios) {
    if (!/^[a-z0-9-]+$/.test(s.id ?? "")) errors.push(`${s.file}: bad id '${s.id}'`);
    if (ids.has(s.id)) errors.push(`${s.file}: duplicate id '${s.id}'`);
    ids.add(s.id);
    if (!Array.isArray(s.categories) || s.categories.length === 0) errors.push(`${s.id}: no categories`);
    if (!Array.isArray(s.turns) || s.turns.length === 0) errors.push(`${s.id}: no turns`);
    (s.turns ?? []).forEach((t, i) => {
      const e = t.expect ?? {};
      if (typeof t.query !== "string" || t.query.trim().length === 0) errors.push(`${s.id}#${i + 1}: empty query`);
      if (e.act && !ACTS.includes(e.act)) errors.push(`${s.id}#${i + 1}: unknown act ${e.act}`);
      if (e.mode && !MODES.includes(e.mode)) errors.push(`${s.id}#${i + 1}: unknown mode ${e.mode}`);
      for (const k of [e.concept, ...(e.mustNot ?? [])].filter(Boolean)) {
        if (!truthKeys.has(k)) errors.push(`${s.id}#${i + 1}: no truth query '${k}.sql'`);
      }
      for (const r of e.references ?? []) {
        if (!(r.turn >= 1 && r.turn <= i && r.rank >= 1)) errors.push(`${s.id}#${i + 1}: reference T${r.turn}.${r.rank} does not point at an earlier turn`);
      }
      if (e.media && !["Movie", "Show"].includes(e.media)) errors.push(`${s.id}#${i + 1}: media must be Movie or Show`);
      for (const r of [...(e.rejectTurns ?? []), ...(e.noRepeatTurns ?? [])]) {
        if (!(r >= 1 && r <= i)) errors.push(`${s.id}#${i + 1}: turn ${r} is not an earlier turn`);
      }
    });
  }
  return errors;
}

// ── SSE client ────────────────────────────────────────────────────────────────────────────────
async function link(api) {
  const res = await fetch(`${api}/api/auth/dev/link`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  if (!res.ok) die(`dev/link failed: HTTP ${res.status}`);
  const cookies = res.headers.getSetCookie().map((c) => c.split(";")[0]);
  const at = cookies.find((c) => c.startsWith("reel_at="));
  if (!at) die("dev/link returned no session cookie");
  return { cookie: at, accountId: (await res.json()).accountId, at: Date.now() };
}

// A user does not wait forever: a turn that sends no byte for TURN_IDLE_MS, or runs past TURN_CAP_MS, is
// aborted and recorded as failed ("client_timeout"). The 99c0761 baseline had four turns hang with no
// byte for 25+ minutes, which is a product defect to measure, not a reason for the run to stall.
const TURN_IDLE_MS = Number(process.env.EVAL_TURN_IDLE_MS ?? 120000);
const TURN_CAP_MS = Number(process.env.EVAL_TURN_CAP_MS ?? DEFAULT_TURN_CAP_MS);

// The ask URL for one turn: founder diagnostics, and the engine the run names (§4.13 per-request override).
export function askUrl(api, engine) {
  return `${api}/api/search/ask?debug=1&engine=${encodeURIComponent(engine)}`;
}

// The engine a stream names: the `conversation` frame's `engine` (every live turn since §4.13) and v2's
// `done.engine`. Null when neither names one; two frames naming different engines is a conflict value
// that equals neither engine. Never inferred from which frames arrived — a `plan` frame is not a label.
export function streamEngine(frames) {
  const named = [...new Set(frames.filter((x) => (x.ev === "conversation" || x.ev === "done") && typeof x.data?.engine === "string")
    .map((x) => x.data.engine))];
  return named.length === 0 ? null : named.length === 1 ? named[0] : `conflict(${named.join("/")})`;
}

// Checks each recorded turn against the engine the run asked for. Returns { ok, why }; a turn that
// answered (a stream arrived) on another engine, or naming none, is not ok. allowUnnamed (a pre-§4.13
// v1 build only) records an unnamed turn as assumed v1 instead.
export function engineGuard(requested, { allowUnnamed = false } = {}) {
  return (t) => {
    if (!answeredTurn(t)) return { ok: true };
    if (t.engine === null && allowUnnamed && requested === "v1") { t.engine = "v1"; t.engineAssumed = true; }
    if (t.engine === requested) return { ok: true };
    return { ok: false, why: `asked for ${requested}, answered by ${t.engine ?? "no named engine"}` };
  };
}

async function askTurn(api, session, body, engine) {
  const t0 = performance.now();
  const controller = new AbortController();
  let idle = setTimeout(() => controller.abort("idle"), TURN_IDLE_MS);
  const cap = setTimeout(() => controller.abort("cap"), TURN_CAP_MS);
  const out = { status: 0, frames: [], firstByteMs: null, firstCardMs: null };
  try {
    const res = await fetch(askUrl(api, engine), {
      method: "POST", headers: { "Content-Type": "application/json", Cookie: session.cookie }, body: JSON.stringify(body), signal: controller.signal,
    });
    out.status = res.status;
    if (!res.ok || !res.body) { out.error = (await res.text()).slice(0, 2000); return out; }
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      clearTimeout(idle);
      idle = setTimeout(() => controller.abort("idle"), TURN_IDLE_MS);
      const now = performance.now();
      if (out.firstByteMs === null) out.firstByteMs = Math.round(now - t0);
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf("\n\n")) >= 0) {
        const frame = buf.slice(0, i); buf = buf.slice(i + 2);
        let ev = "message", data = "";
        for (const line of frame.split("\n")) {
          if (line.startsWith(":")) continue;
          if (line.startsWith("event:")) ev = line.slice(6).trim();
          else if (line.startsWith("data:")) data += line.slice(5).trim();
        }
        if (!data) continue;
        let parsed; try { parsed = JSON.parse(data); } catch { parsed = data; }
        const at = Math.round(now - t0);
        // First card: v1 streams `candidate` frames as they are found; v2 streams judged `picks`.
        if (out.firstCardMs === null && (ev === "candidate" || (ev === "picks" && (parsed?.cards?.length ?? 0) > 0))) out.firstCardMs = at;
        out.frames.push({ at, ev, data: parsed });
      }
    }
  } catch (err) {
    out.error = controller.signal.aborted ? `client_timeout (${controller.signal.reason})` : `transport: ${err?.message ?? err}`;
  } finally {
    clearTimeout(idle); clearTimeout(cap);
    out.totalMs = Math.round(performance.now() - t0);
  }
  return out;
}

const cardOf = (c, rank) => ({
  rank, titleId: c.titleId, tmdbId: c.tmdbId ?? null, name: c.name ?? c.title ?? null, year: c.year ?? null,
  mediaType: c.mediaType ?? null, outsideRequest: c.outsideRequest ?? false, fit: c.fit ?? null, why: c.why ?? null,
  satisfies: c.satisfies ?? null, whySource: c.whySource ?? null, fitSource: c.fitSource ?? null,
});

// What made a turn degraded, as the API says it: `done.degraded` (plan, judge:{failed}/{batches},
// judge-missing:{missing}/{judged}, no-model), a reply sent as the degraded copy (`assistant-message`
// with degraded: true), and v2's `degraded` frames. A judge flag counting zero is no degradation — the
// server's own SearchTurnDegraded.JudgeFailed reads it the same way.
export function degradedOf(frames, done) {
  const flags = [];
  for (const x of frames) {
    // v2 sends `what` as an array of flags (docs/ask-reel-turns.md); an older shape sent one string.
    if (x.ev === "degraded") flags.push(...[x.data?.what ?? x.data].flat());
    if (x.ev === "assistant-message" && x.data?.degraded === true) flags.push("reply");
  }
  if (Array.isArray(done?.degraded)) flags.push(...done.degraded);
  return [...new Set(flags.filter((f) => typeof f === "string" && f.length > 0 && !/^judge(?:-missing)?:0\//.test(f)))];
}

// The supply counts a none-found turn carried (v1 `done.supply`, or a v2 none-found frame's `supply`
// object), reduced to what the scorer compares; null when the turn carried none.
export function supplyOf(done, noneFound) {
  const s = [done?.supply, noneFound?.supply].find((x) => x && typeof x === "object") ?? null;
  if (!s) return null;
  return {
    matchingInCatalogue: s.matchingInCatalogue ?? null, alreadySeen: s.alreadySeen ?? null, savedOrDismissed: s.savedOrDismissed ?? null,
    excludedByConditions: s.excludedByConditions ?? null, eligible: s.eligible ?? null, belowFloor: s.belowFloor ?? null,
    unscored: s.unscored ?? null, clearingFloor: s.clearingFloor ?? null, lowVoteMatches: s.lowVoteMatches ?? null,
    minVotes: s.minVotes ?? null, knockoutsMeasured: s.knockoutsMeasured ?? null,
    knockouts: (s.knockouts ?? []).map((k) => ({ kind: String(k.kind), value: String(k.value ?? ""), wouldUnlock: k.wouldUnlock })),
  };
}

// Reduce a raw SSE transcript to what the scorer reads. Works for v1 and v2 frame vocabularies.
export function compactTurn(raw) {
  const f = (ev) => raw.frames.filter((x) => x.ev === ev);
  const conv = f("conversation")[0]?.data ?? {};
  const done = f("done").at(-1)?.data ?? null;
  const plan = f("plan")[0]?.data ?? null;
  const v2Picks = f("picks").flatMap((x) => x.data?.cards ?? []);
  const trays = f("tray");
  const answer = f("answer").at(-1)?.data?.text ?? null;
  // v2's `clarify` frame is the turn's reply ({ question, options }); no `assistant-message` follows
  // an answer or a clarify, so reading the reply from that frame alone scored every v2 clarify as
  // reply-less — invisible to the echo, grounding and stub-reply checks (R1-34).
  const clarify = f("clarify").at(-1)?.data?.question ?? null;
  const assistant = f("assistant-message").at(-1)?.data ?? null;
  const cards = (v2Picks.length > 0 ? v2Picks : done?.results ?? []).map((c, i) => cardOf(c, i + 1));
  // Every tray the user sees, from v2's `tray` frames when the turn streamed one of that kind, else from
  // v1's `done` (belowFloor, nearMiss, unchecked). v2's `done` repeats the trays it streamed, so reading
  // both counted every v2 tray card twice. The unchecked tray holds cards no model checked against the
  // request — still on screen, so a leak or a ruled-out title there counts exactly as it would among the picks.
  const tray = (kind) => {
    const streamed = trays.filter((t) => t.data?.kind === kind);
    const source = streamed.length > 0 ? streamed.flatMap((t) => t.data.cards ?? []) : Array.isArray(done?.[kind]) ? done[kind] : [];
    return source.map((c, i) => cardOf(c, i + 1));
  };
  const belowFloor = tray("belowFloor");
  const nearMiss = tray("nearMiss");
  const unchecked = tray("unchecked");
  const conflictFrame = f("conflict").length > 0;
  // A turn ends on v1's `done`, or on a v2 terminal frame (none-found, clarify, answer, picks) followed by
  // a clean end of stream — so a v2 engine that stops sending `done` is still scored, not failed.
  const v2Terminal = f("none-found").length > 0 || f("clarify").length > 0 || answer !== null || v2Picks.length > 0;
  const ended = Boolean(done) || (Boolean(plan) && v2Terminal);
  let observedMode = null;
  // v1 states its mode on `done`; a mode it names wins over inferring one from the card count (a clarify
  // has zero cards too, and is not a none-found).
  if (typeof done?.mode === "string" && MODES.includes(done.mode) && done.mode !== "Any") observedMode = done.mode;
  else if (f("none-found").length) observedMode = "NoneFound";
  else if (f("clarify").length) observedMode = "Clarify";
  else if (answer !== null) observedMode = "Answer";
  else if (raw.status === 200 && ended) observedMode = cards.length > 0 ? "Picks" : "NoneFound";
  return {
    status: raw.status, error: raw.error ?? null,
    conversationId: conv.id ?? plan?.conversationId ?? done?.conversationId ?? null,
    turnId: conv.turnId ?? plan?.turnId ?? done?.turnId ?? null, sequence: conv.sequence ?? null,
    firstByteMs: raw.firstByteMs, firstCardMs: raw.firstCardMs, totalMs: raw.totalMs,
    engine: streamEngine(raw.frames),
    act: plan?.act ?? null, references: plan?.references ?? null,
    reply: answer ?? clarify ?? assistant?.text ?? null,
    conflict: Boolean(assistant?.conflict) || conflictFrame,
    failed: Boolean(done?.failed) || raw.status !== 200 || !ended || Boolean(raw.error),
    reason: done?.reason ?? null,
    degraded: degradedOf(raw.frames, done),
    mode: observedMode,
    // The supply report an honest none-found reply is built from (null on a turn with picks, or when
    // no model read the request); the scorer holds its counts to the SQL truth.
    supply: supplyOf(done, f("none-found").at(-1)?.data),
    intent: done?.debug?.intent ?? null,
    relaxation: done?.debug?.relaxation ?? null,
    cards, belowFloor, nearMiss, unchecked,
    frameCounts: Object.fromEntries(Object.entries(raw.frames.reduce((m, x) => ((m[x.ev] = (m[x.ev] ?? 0) + 1), m), {}))),
  };
}

// guard(turn) → { ok, why } after every turn; a turn that is not ok ends the scenario (and, via the
// caller, the run) before another paid turn is sent.
async function runScenario(api, getSession, renewSession, s, engine, guard) {
  let conversationId = null;
  const turns = [];
  for (const t of s.turns) {
    const body = { query: t.query, clientTurnId: randomUUID() };
    if (conversationId) body.conversationId = conversationId;
    const media = t.mediaTypes ?? s.mediaTypes;
    if (media) body.mediaTypes = media;
    const startedAt = new Date().toISOString();
    let raw = await askTurn(api, await getSession(), body, engine);
    if (raw.status === 401) raw = await askTurn(api, await renewSession(), body, engine);
    const c = compactTurn(raw);
    c.query = t.query; c.mediaTypes = media ?? null; c.startedAt = startedAt;
    conversationId = c.conversationId ?? conversationId;
    turns.push(c);
    const verdict = guard(c);
    if (!verdict.ok) return { id: s.id, set: s.set, categories: s.categories, conversationId, turns, engineMismatch: `${s.id}#${turns.length}: ${verdict.why}` };
    if (c.status !== 200 || c.error) break; // a refused or dead turn ends the scenario; the scorer counts the rest as missing
  }
  return { id: s.id, set: s.set, categories: s.categories, conversationId, turns };
}

async function pool(items, n, fn) {
  const out = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.max(1, n) }, async () => {
    for (;;) {
      const i = next++;
      if (i >= items.length) return;
      out[i] = await fn(items[i], i);
    }
  }));
  return out;
}

// ── truth materialisation ─────────────────────────────────────────────────────────────────────
const truthDirs = [join(evalDir, "truth"), ...[args["truth-dir"]].flat().filter((d) => typeof d === "string").map((d) => resolve(d))];
function readTruth(key) {
  for (const dir of truthDirs) if (existsSync(join(dir, `${key}.sql`))) return readFileSync(join(dir, `${key}.sql`), "utf8");
  throw new Error(`no truth query '${key}.sql' in ${truthDirs.join(", ")}`);
}

function mediaClause(media) {
  if (!media || media.length === 0) return null;
  const tv = media.some((m) => ["tv", "show", "series"].includes(m));
  const mv = media.some((m) => ["movie", "movies", "film"].includes(m));
  if (tv && mv) return null;
  return tv ? "Show" : "Movie";
}

function materialise(db, accountId, recording, scenarios) {
  const byId = new Map(scenarios.map((s) => [s.id, s]));
  const shownIds = new Set();
  for (const s of recording.scenarios) for (const t of s.turns) for (const c of [...t.cards, ...t.belowFloor, ...t.nearMiss, ...(t.unchecked ?? [])]) if (c.titleId) shownIds.add(c.titleId);
  const shownList = [...shownIds];
  const inShown = shownList.length ? shownList.map((x) => `'${x}'`).join(",") : "NULL";

  // Eligibility of every shown title (exclusion-leak check), plus media type and footprint detail.
  const eligibleSql = readTruth("_eligible");
  const eligible = new Set(sql(db, `with e as (${eligibleSql}) select "Id" from e where "Id" in (${inShown})`, { account: accountId }).map((r) => r[0]));
  const media = Object.fromEntries(sql(db, `select "Id", "MediaType" from "Titles" where "Id" in (${inShown})`).map((r) => [r[0], r[1]]));
  // Titles below the account's For-you bar under the ACTIVE artifact (artifact-dependent; versioned).
  // The active artifact the API reads: the highest version (SupplyReporter, AskReel scoring).
  const artifact = sql(db, `select "Id", "Version" from "ModelArtifacts" where "AccountId" = :'account' and "Status" = 'Active' order by "Version" desc limit 1`, { account: accountId })[0] ?? [null, null];
  const bar = sql(db, `select "Value" from "ContentFilters" where "AccountId" = :'account' and "Kind" = 'MinPredictedRating' order by "CreatedAt", "Id" limit 1`, { account: accountId })[0]?.[0] ?? null;
  const clearBar = forYouBar(bar);

  const truth = {};
  const need = new Map(); // key -> set of media scopes
  const supplyNeed = new Map(); // concept key -> scope -> set of vote floors, for turns that carried supply counts
  for (const s of recording.scenarios) {
    const def = byId.get(s.id);
    s.turns.forEach((t, i) => {
      const e = def?.turns[i]?.expect ?? {};
      const m = e.media ?? mediaClause(t.mediaTypes) ?? "Any";
      for (const k of [e.concept, ...(e.mustNot ?? [])].filter(Boolean)) {
        if (!need.has(k)) need.set(k, new Set());
        need.get(k).add(m);
      }
      // A turn that states supply counts — in its report or in its reply's words — is held to the truth
      // counted at the vote floor it states.
      const stated = t.supply ?? replySupply(t.reply);
      if (e.concept && stated) {
        const scopes = supplyNeed.get(e.concept) ?? new Map();
        supplyNeed.set(e.concept, scopes.set(m, (scopes.get(m) ?? new Set()).add(Number(stated.minVotes ?? DEFAULT_MIN_VOTES))));
      }
    });
  }
  for (const [key, scopes] of need) {
    const body = readTruth(key);
    const members = sql(db, `with c as (${body}) select "Id" from c where "Id" in (${inShown})`).map((r) => r[0]);
    const supply = {};
    for (const scope of scopes) {
      const mediaFilter = scope === "Any" ? "" : ` and t."MediaType" = '${scope}'`;
      // clearing: eligible, scored by the active model and at or above the For-you bar at badge
      // precision (one decimal, half away from zero) — the only titles Ask Reel may pick. A turn that
      // shows nothing while clearing > 0 missed supply. No vote floor: picks are not vote-floored.
      const [matching, elig, scored, above, clearing] = sql(db, `
        with c as (select distinct "Id" from (${body}) q), e as (${eligibleSql})
        select count(*),
               count(*) filter (where e."Id" is not null),
               count(*) filter (where e."Id" is not null and s."PredictedRating" is not null),
               count(*) filter (where e."Id" is not null and s."PredictedRating" >= coalesce(:bar, 0)),
               count(*) filter (where e."Id" is not null and s."PredictedRating" is not null
                                  and (:clearbar::numeric is null or round(s."PredictedRating", 1) >= :clearbar::numeric))
          from c join "Titles" t on t."Id" = c."Id" left join e on e."Id" = c."Id"
          left join "TitleScores" s on s."TitleId" = c."Id" and s."AccountId" = :'account' and s."ModelArtifactId" = :'artifact'
         where true${mediaFilter}`, { account: accountId, artifact: artifact[0] ?? "00000000-0000-0000-0000-000000000000", bar: bar ?? "NULL", clearbar: clearBar ?? "NULL" })[0].map(Number);
      supply[scope] = { matching, eligible: elig, scored, aboveBar: above, clearing };
      for (const minVotes of supplyNeed.get(key)?.get(scope) ?? []) {
        supply[scope].floored ??= {};
        supply[scope].floored[minVotes] = supplyTruth(db, accountId, body, scope, minVotes);
      }
    }
    truth[key] = { members, supply };
  }

  // Server-side facts from the turn record: latency and the persisted card sources.
  const turnIds = recording.scenarios.flatMap((s) => s.turns.map((t) => t.turnId)).filter(Boolean);
  const inTurns = turnIds.length ? turnIds.map((x) => `'${x}'`).join(",") : "NULL";
  const server = {};
  for (const [id, status, failure, latency, stage] of sql(db, `select "Id", "Status", coalesce("FailureCode", ''), coalesce("LatencyMs"::text, ''), coalesce("StageTimingsJson"::text, '') from "SearchTurns" where "Id" in (${inTurns})`)) {
    server[id] = { status, failureCode: failure || null, latencyMs: latency === "" ? null : Number(latency), stageTimings: stage ? JSON.parse(stage) : null, whySources: {} };
  }
  for (const [turnId, titleId, whySource] of sql(db, `select "TurnId", "TitleId", "WhySource" from "SearchTurnCards" where "TurnId" in (${inTurns})`)) {
    if (server[turnId]) server[turnId].whySources[titleId] = whySource;
  }

  // Grounding: catalogue titles a reply names that are not among the cards shown so far.
  const named = {};
  for (const s of recording.scenarios) for (const t of s.turns) {
    if (!t.reply || !t.turnId) continue;
    named[t.turnId] = sql(db, `select distinct "Id", "Name" from "Titles" where length("Name") >= 6 and "Name" ~ ' ' and position(lower("Name") in lower(:'reply')) > 0`, { reply: t.reply })
      .map((r) => ({ titleId: r[0], name: r[1] }));
  }

  return { eligible: [...eligible], media, artifact: { id: artifact[0], version: artifact[1] ? Number(artifact[1]) : null }, forYouBar: bar === null ? null : Number(bar), truth, server, named };
}

// A query file as a view body: comment lines and a trailing semicolon dropped.
const stripSql = (q) => q.split("\n").filter((l) => !l.trimStart().startsWith("--")).join("\n").trim().replace(/;$/, "");

// The For-you bar as the API reads it (SupplyReporter.PredictedFloor): clamped to 0–10, where zero or a
// malformed value means no bar.
export function forYouBar(raw) {
  if (raw === null || raw === undefined || !/^\s*[+-]?[0-9]+(\.[0-9]+)?\s*$/.test(String(raw))) return null;
  const v = Math.min(10, Math.max(0, Number(raw)));
  return v > 0 ? v : null;
}

// The vote floor the API counts supply with (CrowdVotes.MinRelaxVotes).
export const DEFAULT_MIN_VOTES = 50;

// The SQL truth of one concept's supply (eval/askreel/supply/supply.sql over the concept, the eligibility
// truth and the rule truth), as the object the scorer compares a none-found turn's counts against.
function supplyTruth(db, accountId, conceptSql, scope, minVotes) {
  const views = [
    `create temp view eval_concept as ${stripSql(conceptSql)};`,
    `create temp view eval_eligible as ${stripSql(readTruth("_eligible"))};`,
    `create temp view eval_rules as ${stripSql(readFileSync(join(evalDir, "supply", "rules.sql"), "utf8"))};`,
  ].join("\n");
  const rows = sql(db, `${views}\n${readFileSync(join(evalDir, "supply", "supply.sql"), "utf8")};`, { account: accountId, media: scope, minvotes: minVotes });
  return supplyFromRows(rows, minVotes);
}

// How many titles the rule truth and the eligibility truth classify differently (0 when they agree).
function eligibilityAgreementSql() {
  return `create temp view eval_eligible as ${stripSql(readTruth("_eligible"))};
create temp view eval_rules as ${stripSql(readFileSync(join(evalDir, "supply", "rules.sql"), "utf8"))};
select count(*) from "Titles" t
 where exists (select 1 from eval_eligible e where e."Id" = t."Id")
    <> (not exists (select 1 from "WatchedTitles" w where w."AccountId" = :'account' and w."TitleId" = t."Id")
        and not exists (select 1 from "UserRatings" r where r."AccountId" = :'account' and r."TitleId" = t."Id")
        and not exists (select 1 from "UserTitleReactions" ux where ux."AccountId" = :'account' and ux."TitleId" = t."Id"
                          and ux."RevokedAt" is null and ux."Kind" in ('NotInterested', 'SaveForLater', 'Dropped'))
        and not exists (select 1 from eval_rules r where r."Id" = t."Id"));`;
}

// The rule truth restated as the conditions editor's cost meter counts it (FilterCostMeter, POST
// /api/settings/filters/cost): eligible = footprint-free titles that break no content rule, and each rule's
// marginal = footprint-free titles that rule ALONE holds out. Maturity is not a content rule (the meter does
// not apply it), so it is left out of both. Rows: ("eligible", "", n) and ("marginal", "Kind/Value", n).
function costTruthSql() {
  return `create temp view eval_rules as ${stripSql(readFileSync(join(evalDir, "supply", "rules.sql"), "utf8"))};
with ff as (select t."Id" from "Titles" t
             where not exists (select 1 from "WatchedTitles" w where w."AccountId" = :'account' and w."TitleId" = t."Id")
               and not exists (select 1 from "UserRatings" r where r."AccountId" = :'account' and r."TitleId" = t."Id")
               and not exists (select 1 from "UserTitleReactions" ux where ux."AccountId" = :'account' and ux."TitleId" = t."Id"
                                 and ux."RevokedAt" is null and ux."Kind" in ('NotInterested', 'SaveForLater', 'Dropped'))),
     fails as (select r."Id", count(*) as n, min(r."Kind") as kind, min(r."Value") as value
                 from eval_rules r where r."Kind" <> 'Maturity' group by r."Id")
select 'eligible', '', ((select count(*) from ff) - (select count(*) from fails))::int
union all
select 'marginal', kind || '/' || value, count(*)::int from fails where n = 1 group by kind, value;`;
}

// Where the API's cost meter and the SQL rule truth disagree on the account's SAVED rules: the eligible
// count, and every costed rule's marginal (a rule the SQL never sees alone — a value of a multi-value
// allow-list — is expected at 0, the meter's clamp). Rules past the meter's cap carry no marginal and are
// skipped. Returns human-readable lines; empty when the two statements agree.
export const COST_METER_MAX_COSTED_RULES = 30;
export function costMeterDrift(truthRows, report) {
  const drift = [];
  const truthEligible = Number(truthRows.find(([kind]) => kind === "eligible")?.[2] ?? NaN);
  if (truthEligible !== report.eligible) drift.push(`eligible: API ${report.eligible} vs SQL truth ${truthEligible}`);
  const marginals = new Map(truthRows.filter(([kind]) => kind === "marginal").map(([, key, n]) => [key, Number(n)]));
  const seen = new Set();
  (report.byRule ?? []).forEach((rule, i) => {
    const key = `${rule.kind}/${rule.value}`;
    seen.add(key);
    if (i >= COST_METER_MAX_COSTED_RULES) return;
    const expected = marginals.get(key) ?? 0;
    if (expected !== rule.marginal) drift.push(`${key}: API ${rule.marginal} vs SQL truth ${expected}`);
  });
  for (const [key, n] of marginals) if (!seen.has(key) && !key.endsWith("/*")) drift.push(`${key}: SQL truth ${n}, but the API costs no such rule`);
  return drift;
}

// The API's cost report for the linked account's saved rules (dev/link on the eval stack only).
async function costReport(api) {
  const link = await fetch(`${api}/api/auth/dev/link`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  if (!link.ok) throw new Error(`dev/link failed: ${link.status}`);
  const cookie = link.headers.getSetCookie().map((c) => c.split(";")[0]).find((c) => c.startsWith("reel_at="));
  const { accountId } = await link.json();
  const saved = await fetch(`${api}/api/settings/filters`, { headers: { Cookie: cookie } });
  if (!saved.ok) throw new Error(`GET /api/settings/filters failed: ${saved.status}`);
  const cost = await fetch(`${api}/api/settings/filters/cost`, {
    method: "POST", headers: { Cookie: cookie, "Content-Type": "application/json" }, body: JSON.stringify(await saved.json()),
  });
  if (!cost.ok) throw new Error(`POST /api/settings/filters/cost failed: ${cost.status}`);
  return { accountId, report: await cost.json() };
}

// supply.sql's (Bucket, Kind, Value, Count) rows as one object; knockouts biggest first, as the API orders them.
export function supplyFromRows(rows, minVotes) {
  const out = { minVotes, knockouts: [] };
  for (const [bucket, kind, value, count] of rows) {
    if (bucket === "knockout") out.knockouts.push({ kind, value, wouldUnlock: Number(count) });
    else out[bucket] = Number(count);
  }
  out.knockouts.sort((a, b) => b.wouldUnlock - a.wouldUnlock || (a.kind + a.value < b.kind + b.value ? -1 : 1));
  return out;
}

function ledger(db, accountId, sinceDay) {
  return sql(db, `select "Provider", coalesce("Model", ''), coalesce("Purpose", ''), "Day"::text, "CallCount", coalesce("PromptTokens", 0), coalesce("CompletionTokens", 0), coalesce("TokensUsed", 0)
                    from "ExternalApiUsages" where "Day" >= '${sinceDay}'`)
    .map(([provider, model, purpose, day, calls, pt, ct, tu]) => ({ provider, model, purpose, day, calls: Number(calls), promptTokens: Number(pt), completionTokens: Number(ct), tokensUsed: Number(tu) }));
}

// The UTC day a run's ledger is read from: the day its FIRST attempt started, whatever day it resumes on.
export function runDay(startState, today) {
  return startState?.day ?? startState?.started?.slice(0, 10) ?? today;
}

export function ledgerDelta(before, after) {
  const key = (r) => [r.provider, r.model, r.purpose, r.day].join("|");
  const b = new Map(before.map((r) => [key(r), r]));
  const out = [];
  for (const r of after) {
    const p = b.get(key(r)) ?? { calls: 0, promptTokens: 0, completionTokens: 0, tokensUsed: 0 };
    const d = { provider: r.provider, model: r.model, purpose: r.purpose, calls: r.calls - p.calls, promptTokens: r.promptTokens - p.promptTokens, completionTokens: r.completionTokens - p.completionTokens, tokensUsed: r.tokensUsed - p.tokensUsed };
    if (d.calls || d.promptTokens || d.completionTokens || d.tokensUsed) out.push(d);
  }
  return out;
}

function gitSha() {
  try { return execFileSync("git", ["-C", repo, "rev-parse", "--short", "HEAD"]).toString().trim(); } catch { return null; }
}

function writeOutputs(outDir, recording) {
  mkdirSync(outDir, { recursive: true });
  const prices = JSON.parse(readFileSync(join(evalDir, "prices.json"), "utf8"));
  const gates = JSON.parse(readFileSync(join(evalDir, "gates.json"), "utf8"));
  const scenarios = loadScenarios(recording.scenarioDirs.map((d) => resolve(repo, d)));
  const metrics = score(recording, scenarios, prices, gates);
  writeFileSync(join(outDir, "metrics.json"), JSON.stringify(metrics, null, 1));
  writeFileSync(join(outDir, "report.md"), renderReport(recording, metrics));
  return metrics;
}

// ── modes ─────────────────────────────────────────────────────────────────────────────────────
const scenarioDirs = [args.scenarios ?? join("eval", "askreel", "scenarios"), args["extra-scenarios"]].filter(Boolean);

if (mode === "check") {
  const sc = loadScenarios(scenarioDirs.map((d) => resolve(repo, d)));
  const errors = validateScenarios(sc, truthDirs);
  if (sc.some((x) => x.set === "open")) for (const k of unusedOpenTruth(sc, join(evalDir, "truth"))) errors.push(`truth/${k}.sql is used by no open scenario — delete it, or move a holdout truth to the sealed directory`);
  if (args.db) {
    // Execute every truth query the loaded scenarios use (plus eligibility) against an eval database,
    // so the sealed holdout's truth, which CI never sees, is proven to run on the current schema too.
    guardTarget(args.db);
    const accountId = sql(args.db, `select "Id" from "Accounts" order by "CreatedAt" limit 1`)[0]?.[0];
    for (const k of ["_eligible", ...usedTruthKeys(sc)]) {
      try {
        const n = sql(args.db, `with c as (${readTruth(k)}) select count(*) from c`, { account: accountId })[0][0];
        if (k !== "_eligible" && Number(n) === 0) errors.push(`truth/${k}.sql matches no title on ${args.db}`);
      } catch (err) { errors.push(`truth/${k}.sql failed on ${args.db}: ${String(err.message).split("\n")[0]}`); }
    }
    // The rule truth and the eligibility truth are two statements of one rule: a footprint-free title
    // that breaks no rule in supply/rules.sql must be exactly a title _eligible.sql admits, or every
    // knockout count the eval holds a reply to is wrong.
    try {
      const [[drift]] = sql(args.db, `${eligibilityAgreementSql()}`, { account: accountId });
      if (Number(drift) !== 0) errors.push(`supply/rules.sql and truth/_eligible.sql disagree on ${drift} title(s) on ${args.db}`);
      const concept = [...usedTruthKeys(sc)][0];
      if (concept) supplyTruth(args.db, accountId, readTruth(concept), "Any", DEFAULT_MIN_VOTES);
    } catch (err) { errors.push(`supply truth failed on ${args.db}: ${String(err.message).split("\n")[0]}`); }
    // With --api (an API running on that same database): the SQL truth against the API's own eligibility,
    // through the cost meter — the eligible count and every rule's marginal. Fixture parity tests pin the
    // predicates; this pins them on the real catalogue, where a stand-in keyword or alias the truth does not
    // model shows up as a count that moved (IREV3-SYS-02).
    if (args.api) {
      try {
        const { accountId: linked, report } = await costReport(String(args.api));
        if (linked !== accountId) errors.push(`--api links account ${linked}, but ${args.db}'s first account is ${accountId} — is the API on ${args.db}?`);
        else {
          const drift = costMeterDrift(sql(args.db, costTruthSql(), { account: accountId }), report);
          for (const d of drift) errors.push(`API eligibility and the SQL truth disagree on ${args.db}: ${d}`);
          if (!drift.length) console.log(`api parity: eligible ${report.eligible}, ${report.byRule.length} rule marginals agree with the SQL truth`);
        }
      } catch (err) { errors.push(`API eligibility parity failed: ${String(err.message).split("\n")[0]}`); }
    }
  } else if (args.api) errors.push("--api needs --db (the database that API runs on)");
  const multi = sc.filter((s) => s.turns.length > 1).length;
  const turns = sc.reduce((n, s) => n + s.turns.length, 0);
  console.log(`scenarios ${sc.length} (multi-turn ${multi}), turns ${turns}`);
  const cats = {};
  for (const s of sc) for (const c of s.categories) cats[c] = (cats[c] ?? 0) + 1;
  console.log(Object.entries(cats).sort().map(([k, v]) => `${k}:${v}`).join(" "));
  if (errors.length) { console.error(errors.join("\n")); process.exit(1); }
  process.exit(0);
}

if (mode === "compare") {
  const current = JSON.parse(readFileSync(args._[0] ?? die("compare needs a metrics.json"), "utf8"));
  const baseline = JSON.parse(readFileSync(args.baseline ?? die("--baseline is required"), "utf8"));
  const gates = JSON.parse(readFileSync(join(evalDir, "gates.json"), "utf8"));
  const { refused, rows, pairedTurns } = compare(current, baseline, gates);
  if (refused) { console.error(`askreel: refusing to compare — ${refused}`); process.exit(2); }
  console.log(`Paired over ${pairedTurns} turns that ran in both runs.\n`);
  console.log("| Metric | Baseline | Current | Allowed | Status |\n|---|---|---|---|---|");
  for (const r of rows) console.log(`| ${r.metric}${r.stat ? `.${r.stat}` : ""}${r.paired ? " (paired)" : ""} | ${r.baseline ?? "—"} | ${r.current ?? "—"} | ${r.allowed ?? "—"} | ${r.status} |`);
  process.exit(rows.some((r) => r.status.startsWith("REGRESSION")) ? 1 : 0);
}

if (mode === "replay") {
  const file = args._[0] ?? die("replay needs a recording.json");
  const recording = JSON.parse(readFileSync(file, "utf8"));
  const outDir = args.out ?? dirname(file);
  const metrics = writeOutputs(outDir, recording);
  console.log(JSON.stringify(metrics.summary, null, 1));
  if (!metrics.summary.engineValid) {
    console.error(`askreel: INVALID recording — engine requested ${metrics.summary.engineRequested}, answered ${JSON.stringify(metrics.summary.engineCounts)}`);
    process.exit(1);
  }
  process.exit(metrics.summary.gatesFailed > 0 && args.strict ? 1 : 0);
}

if (mode === "live") {
  const api = args.api ?? die("--api is required");
  const db = args.db ?? die("--db is required (the eval database the API runs on)");
  guardTarget(db);
  const label = args.label ?? die("--label is required");
  const engine = ["v1", "v2"].includes(args.engine) ? args.engine : die("--engine v1|v2 is required: the run asks the API for that engine and refuses any turn another engine answers");
  const allowUnnamed = Boolean(args["allow-unnamed-engine"]);
  if (allowUnnamed && engine !== "v1") die("--allow-unnamed-engine is only for a pre-§4.13 build, which has no v2: use it with --engine v1");
  const guard = engineGuard(engine, { allowUnnamed });
  const concurrency = Number(args.concurrency ?? 4);
  if (concurrency > 4) die("quality runs are capped at concurrency 4 (§4.12)");
  if (args["hold-build-slots"] && concurrency > 2) die("--hold-build-slots is for latency runs, which are capped at concurrency 2");
  const key = modelKeyFor(args["api-env"], typeof args["founder-key-signoff"] === "string" ? args["founder-key-signoff"] : null);
  if (key.refusal) die(key.refusal);
  const buildInfo = args["build-info"] ? JSON.parse(readFileSync(resolve(args["build-info"]), "utf8")) : null;
  let scenarios = loadScenarios(scenarioDirs.map((d) => resolve(repo, d)));
  const errors = validateScenarios(scenarios, truthDirs);
  if (errors.length) die(`scenario set is invalid:\n${errors.join("\n")}`);
  if (args["only-file"]) args.only = readFileSync(resolve(repo, args["only-file"]), "utf8").split("\n").filter((l) => l && !l.startsWith("#")).join(",");
  if (args.only) { const only = new Set(String(args.only).split(",")); scenarios = scenarios.filter((s) => only.has(s.id)); }
  if (args.limit) scenarios = scenarios.slice(0, Number(args.limit));
  const today = new Date().toISOString().slice(0, 10);
  const outDir = resolve(args.out ?? join(repo, ".tmp", "eval", today, label));
  // A latency run takes every machine build slot before its first turn and holds them to the end.
  const slots = args["hold-build-slots"] ? await holdAllBuildSlots({ log: (m) => console.error(`askreel: ${m}`) }) : null;
  if (slots) console.error(`askreel: holding every build slot (waited ${Math.round(slots.waitedMs / 1000)} s)`);

  let session = await link(api);
  // The access cookie expires after JWT_ACCESS_TOKEN_EXPIRY_MINUTES (15 by default). Re-link every
  // 10 minutes so a long run never sends an expired cookie; a 401 that slips through is retried once
  // below with a fresh link rather than recorded as the pipeline's failure.
  const renew = async () => { session = await link(api); return session; };
  const getSession = async () => (Date.now() - session.at > 10 * 60 * 1000 ? renew() : session);
  const snapshotId = execFileSync(join(here, "snapshot.sh"), ["id", db]).toString().trim() || null;
  // The ledger and exposure baselines are taken once per run and kept beside partial.jsonl, so a resumed
  // run still reports the whole run's spend against the same database.
  mkdirSync(outDir, { recursive: true });
  const startFile = join(outDir, "run-start.json");
  // The first attempt's UTC day is kept too: a run resumed after midnight still reads the ledger from the
  // day it started, so the first day's spend stays in the cost and the cost gate.
  const startState = args.resume && existsSync(startFile)
    ? JSON.parse(readFileSync(startFile, "utf8"))
    : { before: ledger(db, session.accountId, today), exposuresBefore: Number(sql(db, `select count(*) from "ExposureEvents"`)[0][0]), started: new Date().toISOString(), day: today };
  const day = runDay(startState, today);
  if (args.resume && slots === null && startState.buildSlotsHeld) die("this run started holding every build slot; resume it with --hold-build-slots");
  startState.buildSlotsHeld = Boolean(slots) && (startState.buildSlotsHeld ?? true);
  writeFileSync(startFile, JSON.stringify(startState));
  const { before, exposuresBefore } = startState;
  const started = new Date(startState.started);
  console.error(`askreel: ${scenarios.length} scenarios, ${scenarios.reduce((n, s) => n + s.turns.length, 0)} turns, concurrency ${concurrency}, db ${db} (${snapshotId})`);
  // Every finished scenario is appended to partial.jsonl, so a killed run is salvaged with --resume.
  mkdirSync(outDir, { recursive: true });
  const partialFile = join(outDir, "partial.jsonl");
  const resumed = new Map();
  if (args.resume && existsSync(partialFile)) {
    // A scenario whose turn died on the transport (the API process was gone) is run again; every
    // other recorded scenario — including the pipeline's own failures — is kept as it was.
    for (const line of readFileSync(partialFile, "utf8").split("\n").filter(Boolean)) {
      const r = JSON.parse(line);
      // A scenario stopped on an engine mismatch is not salvage: resuming re-asks it under this run's guard.
      if (r.engineMismatch || r.turns.some((t) => (t.error ?? "").startsWith("transport:"))) resumed.delete(r.id);
      else resumed.set(r.id, r);
    }
    writeFileSync(partialFile, [...resumed.values()].map((r) => JSON.stringify(r) + "\n").join(""));
    console.error(`askreel: resuming — ${resumed.size} scenarios already recorded`);
  } else writeFileSync(partialFile, "");
  let done = 0;
  // The first turn another engine answers stops the run: no further scenario starts, so no more paid
  // turns go on evidence that cannot count. Recorded scenarios from a resume are held to the same check.
  let mismatch = [...resumed.values()].flatMap((r) => r.turns.map((t) => guard(t)).filter((v) => !v.ok).map((v) => `${r.id}: ${v.why}`))[0] ?? null;
  const results = await pool(scenarios, concurrency, async (s) => {
    if (mismatch && !resumed.has(s.id)) return null;
    const r = resumed.get(s.id) ?? await runScenario(api, getSession, renew, s, engine, guard);
    if (r.engineMismatch && !mismatch) { mismatch = r.engineMismatch; console.error(`askreel: STOPPING — ${mismatch}`); }
    if (!resumed.has(s.id)) appendFileSync(partialFile, JSON.stringify(r) + "\n");
    done++;
    if (done % 5 === 0 || done === scenarios.length) console.error(`askreel: ${done}/${scenarios.length} scenarios (${Math.round((Date.now() - started) / 1000)} s)`);
    return r;
  });
  const finished = new Date();
  // The ledger recorder flushes on a ~15 s window; wait it out so the delta is whole.
  await new Promise((r) => setTimeout(r, 20000));
  const after = ledger(db, session.accountId, day);
  const exposuresAfter = Number(sql(db, `select count(*) from "ExposureEvents"`)[0][0]);
  slots?.release();
  const recording = {
    kind: "askreel-recording", version: 2, label, engine: null, engineRequested: engine, engineStopped: mismatch, build: buildInfo?.commit ?? null, buildInfo, api, db, snapshotId,
    modelKey: key.recorded, turnCapMs: TURN_CAP_MS, truthDirs: truthDirs.map((d) => (d.startsWith(repo) ? d.slice(repo.length + 1) : d)),
    // Latency is gated only when every build slot was held for the WHOLE run (a resume must hold them too).
    buildSlotsHeld: Boolean(slots) && startState.buildSlotsHeld === true,
    buildSlotsHeldFrom: slots?.heldFrom ?? null,
    gitSha: gitSha(), startedAt: started.toISOString(), finishedAt: finished.toISOString(),
    wallSeconds: Math.round((finished - started) / 1000), concurrency, accountId: session.accountId,
    scenarioDirs: scenarioDirs.map((d) => (resolve(repo, d).startsWith(repo) ? resolve(repo, d).slice(repo.length + 1) : resolve(repo, d))),
    scenarios: results.filter(Boolean), skippedScenarios: results.filter((r) => r === null).length, ledger: ledgerDelta(before, after), exposuresWritten: exposuresAfter - exposuresBefore,
    // A resumed run keeps its first attempt's ledger and exposure baselines (run-start.json), so spend
    // covers the whole run; wall time then includes the gap between attempts.
    resumedScenarios: resumed.size,
  };
  // The engine that answered, from the turns (null when none answered) — the flag is engineRequested.
  const answered = engineOf(recording);
  recording.engine = answered.engine;
  recording.facts = materialise(db, session.accountId, recording, scenarios);
  mkdirSync(outDir, { recursive: true });
  writeFileSync(join(outDir, "recording.json"), JSON.stringify(recording));
  const metrics = writeOutputs(outDir, recording);
  console.log(JSON.stringify(metrics.summary, null, 1));
  console.error(`askreel: wrote ${outDir}`);
  if (!metrics.summary.engineValid) {
    console.error(`askreel: INVALID — asked for ${engine}, answered by ${JSON.stringify(answered.counts)}: ${answered.mismatches.slice(0, 5).join("; ")}`);
    process.exit(1);
  }
  process.exit(0);
}

if (isMain) die("usage: askreel.mjs live|replay|check|compare …");
