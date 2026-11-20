#!/usr/bin/env node
// The Ask Reel LLM judge and its calibration against the founder (program §4.12, WS-7 task 5).
//
//   node scripts/eval/judge.mjs items --recording <recording.json> --db <eval db> --out <labels.csv> [--n 100] [--seed 42] [--confirm <ids file>]
//   node scripts/eval/judge.mjs run   --recording <recording.json> --db <eval db> --judge-env <env file> --out <judge.json>
//                                     [--prompt ~/src/labs.fa.reel-eval-sealed/askreel/judge/prompt.md] [--concurrency 4] [--founder-key-signoff "<who, when>"]
//   node scripts/eval/judge.mjs kappa --labels <labels.csv> --judge <judge.json> [--out <kappa.json>]
//   node scripts/eval/judge.mjs score --judge <judge.json>
//   node scripts/eval/judge.mjs compare --judge <judge.json> --baseline <judge.json> [--kappa <kappa.json>]
//
// items  draws the founder's labelling set from a recording: N (scenario, turn, card) items, at most one
//        per turn first, stratified by drawing turns in a seeded order, each with the request in context
//        (the conversation so far) and the card's catalogue facts read from the eval database — never the
//        system's own why-line, which would anchor the label. --confirm appends title ids to confirm
//        (WS-3's 20 ninja films) as yes/no rows under the request they answer.
// run    asks the judge, one call per card (UMBRELA-style: one passage per call, so cards never anchor each
//        other) for "does this card satisfy the request" on 0–3, and one call per reply for "is the reply
//        grounded in the cards". PAID: it uses the judge env file's OpenRouter key, which may not be the
//        founder's without his recorded sign-off, at reasoning effort none. The prompt is read from the
//        sealed directory (§4.12: judge leakage) and only its SHA-256 is recorded.
// kappa  Cohen's κ between the founder's labels and the judge on the same items (unweighted on the 0–3
//        scale — the pre-registered gate in eval/askreel/gates.json → judge — with the quadratic-weighted
//        κ and the relevant/not-relevant κ reported beside it). The judge gates nothing below the bar.
// score  the judge's metrics for a run: mean satisfaction and relevant share (≥ 2) over each turn's top 8,
//        and ungrounded replies.
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { createHash } from "node:crypto";
import { homedir } from "node:os";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { modelKeyFor } from "./guards.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const isMain = process.argv[1] === fileURLToPath(import.meta.url);

/** The model and effort §4.12 names for the judge. */
export const JUDGE_MODEL = "openai/gpt-6-luna";
export const JUDGE_EFFORT = "none";
/** The cards per turn the judge reads: the top of the grid, where precision@8 is measured too. */
export const JUDGE_DEPTH = 8;
export const SEALED_PROMPT = join(homedir(), "src", "labs.fa.reel-eval-sealed", "askreel", "judge", "prompt.md");

// ── sampling ──────────────────────────────────────────────────────────────────────────────────────

/** A small seeded PRNG (mulberry32), so a labelling set can be redrawn exactly. */
export function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function shuffle(list, next) {
  const out = [...list];
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(next() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

/** The request a turn answers, in context: the earlier messages and the cards they were shown. */
export function requestContext(definition, recordedTurns, turnIndex) {
  const earlier = [];
  for (let i = 0; i < turnIndex; i++) {
    const shown = (recordedTurns[i]?.cards ?? []).slice(0, 5).map((c) => `${c.rank}. ${c.name}`).join("; ");
    earlier.push(`Turn ${i + 1}: "${definition.turns[i]?.query ?? ""}"${shown ? ` — shown: ${shown}` : ""}`);
  }
  return { request: definition.turns[turnIndex]?.query ?? "", earlier };
}

/**
 * N labelling items from a recording: every (scenario, turn, card in the top JUDGE_DEPTH) is a candidate;
 * turns are visited in a seeded order and one card is drawn from each, round after round, until N — so the
 * set spreads across turns (and therefore categories) before it doubles up on any.
 */
export function sampleItems(recording, definitions, { n = 100, seed = 42 } = {}) {
  const next = rng(seed);
  const byId = new Map(definitions.map((d) => [d.id, d]));
  const pools = [];
  for (const sc of recording.scenarios ?? []) {
    const def = byId.get(sc.id);
    if (!def) continue;
    sc.turns.forEach((turn, t) => {
      const cards = (turn.cards ?? []).filter((c) => c.titleId && c.rank <= JUDGE_DEPTH);
      if (cards.length) pools.push({ sc, def, t, cards: shuffle(cards, next) });
    });
  }
  const order = shuffle(pools, next);
  const items = [];
  for (let round = 0; items.length < n && order.some((p) => p.cards.length > round); round++) {
    for (const p of order) {
      if (items.length >= n) break;
      const card = p.cards[round];
      if (!card) continue;
      const { request, earlier } = requestContext(p.def, p.sc.turns, p.t);
      items.push({
        itemId: `${p.sc.id}#${p.t + 1}#${card.rank}`, scenario: p.sc.id, turn: p.t + 1, rank: card.rank,
        categories: (p.def.categories ?? []).join(" "), request, earlier: earlier.join(" | "), titleId: card.titleId,
        // What the recording itself knows about the card, for a title the eval database lacks (live
        // discovery during the recorded run can add titles to that run's own copy).
        recorded: { name: card.name, year: card.year ?? null, media: card.mediaType ?? null, genres: [], keywords: [], language: null, overview: "" },
      });
    }
  }
  return items;
}

// ── CSV ───────────────────────────────────────────────────────────────────────────────────────────

const csvCell = (v) => {
  const s = v === null || v === undefined ? "" : String(v);
  return /[",\n]/.test(s) ? `"${s.replaceAll('"', '""')}"` : s;
};

export const LABEL_COLUMNS = ["item_id", "kind", "scenario", "turn", "earlier_turns", "request", "title", "year", "media", "genres", "overview", "label", "note", "title_id"];

/** The founder's sheet: one row per item, the label column empty for him to fill (0–3, or yes/no for a confirm row). */
export function itemsCsv(items) {
  const rows = [LABEL_COLUMNS.join(",")];
  for (const i of items) {
    rows.push([i.itemId, i.kind ?? "satisfies", i.scenario, i.turn, i.earlier, i.request, i.facts?.name, i.facts?.year, i.facts?.media,
      (i.facts?.genres ?? []).join(" "), i.facts?.overview, "", "", i.titleId].map(csvCell).join(","));
  }
  return rows.join("\n") + "\n";
}

/** RFC 4180 parsing, enough for the sheet this tool writes (quoted cells, doubled quotes, embedded newlines). */
export function parseCsv(text) {
  const rows = [];
  let row = [], cell = "", quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; } else if (c === '"') quoted = false; else cell += c;
    } else if (c === '"') quoted = true;
    else if (c === ",") { row.push(cell); cell = ""; } else if (c === "\n" || c === "\r") {
      if (c === "\r" && text[i + 1] === "\n") i++;
      row.push(cell); rows.push(row); row = []; cell = "";
    } else cell += c;
  }
  if (cell.length || row.length) { row.push(cell); rows.push(row); }
  const [header, ...body] = rows.filter((r) => r.some((x) => x.trim().length));
  return body.map((r) => Object.fromEntries(header.map((h, k) => [h, r[k] ?? ""])));
}

/** The founder's labels by item id: integers 0–3 for satisfies rows; anything else is unlabelled. */
export function labelsFrom(rows) {
  const out = new Map();
  for (const r of rows) {
    if ((r.kind || "satisfies") !== "satisfies") continue;
    const v = String(r.label ?? "").trim();
    if (/^[0-3]$/.test(v)) out.set(r.item_id, Number(v));
  }
  return out;
}

// ── agreement ─────────────────────────────────────────────────────────────────────────────────────

/** Cohen's κ for paired ratings on categories 0..k-1; quadratic weights when weighted. Null when undefined. */
export function cohensKappa(pairs, { categories = 4, weighted = false } = {}) {
  const n = pairs.length;
  if (n === 0) return null;
  const k = categories;
  const confusion = Array.from({ length: k }, () => Array(k).fill(0));
  for (const [a, b] of pairs) confusion[a][b] += 1;
  const rowSum = confusion.map((r) => r.reduce((x, y) => x + y, 0));
  const colSum = confusion[0].map((_, j) => confusion.reduce((x, r) => x + r[j], 0));
  const w = (i, j) => (weighted ? ((i - j) * (i - j)) / ((k - 1) * (k - 1)) : i === j ? 0 : 1);
  let observed = 0, expected = 0;
  for (let i = 0; i < k; i++) {
    for (let j = 0; j < k; j++) {
      observed += w(i, j) * confusion[i][j] / n;
      expected += w(i, j) * (rowSum[i] * colSum[j]) / (n * n);
    }
  }
  const kappa = expected === 0 ? null : 1 - observed / expected;
  return { kappa, n, confusion };
}

/** Everything the kappa report states, from the founder's labels and the judge's verdicts on the same items. */
export function agreement(labels, judge, { kappaMin = 0.6 } = {}) {
  const verdicts = new Map((judge.cards ?? []).filter((c) => c.itemId && Number.isInteger(c.satisfies)).map((c) => [c.itemId, c.satisfies]));
  const pairs = [...labels].filter(([id]) => verdicts.has(id)).map(([id, founder]) => [founder, verdicts.get(id)]);
  const unweighted = cohensKappa(pairs);
  const quadratic = cohensKappa(pairs, { weighted: true });
  const binary = cohensKappa(pairs.map(([a, b]) => [a >= 2 ? 1 : 0, b >= 2 ? 1 : 0]), { categories: 2 });
  const missing = [...labels.keys()].filter((id) => !verdicts.has(id));
  const gates = unweighted?.kappa !== null && unweighted?.kappa !== undefined && unweighted.kappa >= kappaMin && missing.length === 0;
  return {
    n: pairs.length, labelled: labels.size, missing, kappaMin,
    kappa: unweighted?.kappa ?? null, quadraticKappa: quadratic?.kappa ?? null, binaryKappa: binary?.kappa ?? null,
    confusion: unweighted?.confusion ?? null, judgeGates: gates,
  };
}

// ── the judge ─────────────────────────────────────────────────────────────────────────────────────

/** The structured answers the judge must give. */
const SATISFIES_SCHEMA = {
  name: "satisfies", strict: true,
  schema: { type: "object", additionalProperties: false, required: ["satisfies", "reason"],
    properties: { satisfies: { type: "integer", enum: [0, 1, 2, 3] }, reason: { type: "string" } } },
};
const GROUNDED_SCHEMA = {
  name: "grounded", strict: true,
  schema: { type: "object", additionalProperties: false, required: ["grounded", "unsupported"],
    properties: { grounded: { type: "boolean" }, unsupported: { type: "array", items: { type: "string" } } } },
};

/**
 * The sealed prompt holds two sections, "## satisfies" and "## grounded", each the system message for its
 * call. Throws when either is missing, so a truncated prompt never runs.
 */
export function promptSections(text) {
  const section = (name) => {
    const m = text.match(new RegExp(`^## ${name}\\s*\\n([\\s\\S]*?)(?=^## |\\s*$(?![\\s\\S]))`, "m"));
    if (!m || !m[1].trim()) throw new Error(`the judge prompt has no "## ${name}" section`);
    return m[1].trim();
  };
  return { satisfies: section("satisfies"), grounded: section("grounded") };
}

export function satisfiesMessage(item) {
  const f = item.facts ?? {};
  return [
    item.earlier ? `Conversation so far: ${item.earlier}` : "Conversation so far: (this is the first message)",
    `Request: "${item.request}"`,
    "Card:",
    `- Title: ${f.name} (${f.year ?? "year unknown"}, ${f.media ?? "unknown medium"})`,
    `- Genres: ${(f.genres ?? []).join(", ") || "none listed"}`,
    `- Keywords: ${(f.keywords ?? []).join(", ") || "none listed"}`,
    `- Language: ${f.language ?? "unknown"}`,
    `- Overview: ${f.overview || "none"}`,
  ].join("\n");
}

export function groundedMessage(turn, request) {
  const cards = (turn.cards ?? []).slice(0, 20).map((c) => `${c.rank}. ${c.name} (${c.year ?? "?"}, ${c.mediaType ?? "?"})`).join("\n");
  return [`Request: "${request}"`, `Reply: "${turn.reply ?? ""}"`, "Cards shown with the reply:", cards || "(none)"].join("\n");
}

/** One judge call through OpenRouter's chat completions, structured output, reasoning effort none. */
export async function callJudge({ fetchImpl = fetch, apiKey, model = JUDGE_MODEL, system, user, schema }) {
  const res = await fetchImpl("https://openrouter.ai/api/v1/chat/completions", {
    method: "POST",
    headers: { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json", "X-Title": "labs.fa.reel eval judge" },
    body: JSON.stringify({
      model, temperature: 0, reasoning: { effort: JUDGE_EFFORT },
      messages: [{ role: "system", content: system }, { role: "user", content: user }],
      response_format: { type: "json_schema", json_schema: schema },
      usage: { include: true },
    }),
  });
  if (!res.ok) {
    const err = new Error(`judge call failed: HTTP ${res.status}`);
    err.status = res.status;
    throw err;
  }
  const body = await res.json();
  const content = body.choices?.[0]?.message?.content;
  let parsed;
  try { parsed = JSON.parse(content); } catch { throw new Error("judge returned no JSON"); }
  return { parsed, finish: body.choices?.[0]?.finish_reason ?? null, usage: body.usage ?? null };
}

/** Runs `worker` over `items` with at most `limit` in flight. */
async function pool(items, limit, worker) {
  const out = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const i = next++;
      out[i] = await worker(items[i], i);
    }
  }));
  return out;
}

/**
 * Judges a recording: every card in each turn's top JUDGE_DEPTH, and every reply that came with cards.
 * Stops at the first 401/402/403 (§7 rule 4: any of them stops all live model work).
 */
export async function judgeRecording({ recording, definitions, facts, prompt, apiKey, fetchImpl = fetch, concurrency = 4, model = JUDGE_MODEL }) {
  const sections = promptSections(prompt);
  const byId = new Map(definitions.map((d) => [d.id, d]));
  const cardJobs = [];
  const replyJobs = [];
  for (const sc of recording.scenarios ?? []) {
    const def = byId.get(sc.id);
    if (!def) continue;
    sc.turns.forEach((turn, t) => {
      const { request, earlier } = requestContext(def, sc.turns, t);
      const cards = (turn.cards ?? []).filter((c) => c.titleId && c.rank <= JUDGE_DEPTH);
      for (const c of cards) {
        cardJobs.push({ itemId: `${sc.id}#${t + 1}#${c.rank}`, scenario: sc.id, turn: t + 1, rank: c.rank, titleId: c.titleId,
          request, earlier: earlier.join(" | "), facts: facts.get(c.titleId) ?? { name: c.name, year: c.year, media: c.mediaType } });
      }
      if (cards.length && turn.reply) replyJobs.push({ scenario: sc.id, turn: t + 1, request, recorded: turn });
    });
  }
  let stopped = null;
  const usage = { calls: 0, promptTokens: 0, completionTokens: 0, cost: 0, lengthStops: 0 };
  const note = (u, finish) => {
    usage.calls += 1;
    usage.promptTokens += u?.prompt_tokens ?? 0;
    usage.completionTokens += u?.completion_tokens ?? 0;
    usage.cost += u?.cost ?? 0;
    if (finish === "length") usage.lengthStops += 1;
  };
  const guarded = async (fn) => {
    if (stopped) return { error: `not run: ${stopped}` };
    try { return await fn(); } catch (err) {
      if ([401, 402, 403].includes(err.status)) stopped = err.message;
      return { error: err.message };
    }
  };
  const cards = await pool(cardJobs, concurrency, (job) => guarded(async () => {
    const { parsed, finish, usage: u } = await callJudge({ fetchImpl, apiKey, model, system: sections.satisfies, user: satisfiesMessage(job), schema: SATISFIES_SCHEMA });
    note(u, finish);
    return { satisfies: parsed.satisfies, reason: parsed.reason };
  }).then((v) => ({ itemId: job.itemId, scenario: job.scenario, turn: job.turn, rank: job.rank, titleId: job.titleId, ...v })));
  const replies = await pool(replyJobs, concurrency, (job) => guarded(async () => {
    const { parsed, finish, usage: u } = await callJudge({ fetchImpl, apiKey, model, system: sections.grounded, user: groundedMessage(job.recorded, job.request), schema: GROUNDED_SCHEMA });
    note(u, finish);
    return { grounded: parsed.grounded, unsupported: parsed.unsupported };
  }).then((v) => ({ scenario: job.scenario, turn: job.turn, ...v })));
  return {
    kind: "askreel-judge", version: 1, model, effort: JUDGE_EFFORT, depth: JUDGE_DEPTH,
    promptSha256: createHash("sha256").update(prompt).digest("hex"),
    recording: { label: recording.label, build: recording.build, snapshotId: recording.snapshotId },
    stopped, usage, cards, replies,
  };
}

/** The judge's metrics for a run; null where nothing was judged. */
export function judgeMetrics(judge) {
  const scored = (judge.cards ?? []).filter((c) => Number.isInteger(c.satisfies));
  const replies = (judge.replies ?? []).filter((r) => typeof r.grounded === "boolean");
  const errors = (judge.cards ?? []).filter((c) => c.error).length + (judge.replies ?? []).filter((r) => r.error).length;
  return {
    judgedCards: scored.length,
    satisfiesMean: scored.length ? scored.reduce((n, c) => n + c.satisfies, 0) / scored.length : null,
    relevantShare: scored.length ? scored.filter((c) => c.satisfies >= 2).length / scored.length : null,
    judgedReplies: replies.length,
    ungroundedReplies: replies.filter((r) => !r.grounded).length,
    errors,
    lengthStopRate: judge.usage?.calls ? judge.usage.lengthStops / judge.usage.calls : null,
  };
}

/**
 * The judge's regression check of a run against a baseline, under gates.json → judge. When the kappa
 * report does not clear the bar (or is absent), nothing is gated and the result says why — the SQL truth
 * stays the only gate, exactly as §4.12 says.
 */
export function compareJudge(current, baseline, kappa, judgeGates) {
  if (!kappa || kappa.judgeGates !== true) {
    return { gating: false, why: kappa ? `κ ${kappa.kappa === null ? "undefined" : kappa.kappa.toFixed(3)} is under ${judgeGates.kappaMin} (or a labelled item went unjudged)` : "no κ report: the founder's labels have not been scored", rows: [] };
  }
  if (current.promptSha256 !== baseline.promptSha256 || current.model !== baseline.model || current.promptSha256 !== kappa.promptSha256) {
    return { gating: false, refused: "the runs or the κ report used different judge prompts or models — re-judge the baseline with this judge", rows: [] };
  }
  const c = judgeMetrics(current), b = judgeMetrics(baseline);
  const rows = (judgeGates.whenGating ?? []).map((g) => {
    const key = g.metric.replace(/^judge\./, "");
    const cv = c[key], bv = b[key];
    if (bv === null || bv === undefined) return { ...g, current: cv, baseline: null, status: "no baseline data" };
    if (cv === null || cv === undefined) return { ...g, current: null, baseline: bv, status: "REGRESSION (no data)" };
    const allowed = bv + (g.higherIsBetter ? -g.tolerance : g.tolerance);
    return { ...g, current: cv, baseline: bv, allowed, status: (g.higherIsBetter ? cv >= allowed : cv <= allowed) ? "pass" : "REGRESSION" };
  });
  return { gating: true, rows };
}

// ── catalogue facts ───────────────────────────────────────────────────────────────────────────────

/** Catalogue facts for title ids from an eval database (read-only), as the judge and the founder see a card. */
export function factsFromDb(db, ids, sqlRunner) {
  const facts = new Map();
  const list = [...new Set(ids)].filter((id) => /^[0-9a-f-]{36}$/i.test(id));
  for (let i = 0; i < list.length; i += 500) {
    const chunk = list.slice(i, i + 500).map((id) => `'${id}'`).join(",");
    for (const [id, name, year, media, genres, keywords, language, overview] of sqlRunner(db,
      `select "Id", "Name", coalesce("Year"::text, ''), "MediaType", array_to_string("Genres", '|'), array_to_string("Keywords"[1:15], '|'),
              coalesce("Language", ''), left(coalesce("Overview", ''), 400) from "Titles" where "Id" in (${chunk})`)) {
      facts.set(id, { name, year: year ? Number(year) : null, media, genres: genres ? genres.split("|") : [], keywords: keywords ? keywords.split("|") : [], language: language || null, overview });
    }
  }
  return facts;
}

// ── CLI ───────────────────────────────────────────────────────────────────────────────────────────

/** The OpenRouter key from an env file, read and never printed. */
export function keyFromEnvFile(file) {
  for (const raw of readFileSync(file, "utf8").split("\n")) {
    const line = raw.trim().replace(/^export\s+/, "");
    if (line.startsWith("OPENROUTER_API_KEY=")) return line.slice("OPENROUTER_API_KEY=".length).trim().replace(/^["']|["']$/g, "") || null;
  }
  return null;
}

if (isMain) {
  const [, , mode, ...rest] = process.argv;
  const args = {};
  for (let i = 0; i < rest.length; i++) {
    if (!rest[i].startsWith("--")) continue;
    const next = rest[i + 1];
    if (next === undefined || next.startsWith("--")) args[rest[i].slice(2)] = true;
    else { args[rest[i].slice(2)] = next; i++; }
  }
  const die = (m) => { console.error(`judge: ${m}`); process.exit(2); };
  const PG = { host: process.env.EVAL_PGHOST ?? "127.0.0.1", port: process.env.EVAL_PGPORT ?? "5434", user: process.env.EVAL_PGUSER ?? "reel", password: process.env.EVAL_PGPASSWORD ?? "reel_eval" };
  const sql = (db, query) => {
    if (String(PG.port) === "5433") die("refusing port 5433 — the founder's LIVE Postgres");
    if (!db || db === "reel" || !/^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*|reel_int_.*)$/.test(db)) die(`refusing database '${db}'`);
    return execFileSync("psql", ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-At", "-F", "\x1f", "-h", PG.host, "-p", PG.port, "-U", PG.user, "-d", db, "-c", query],
      { env: { ...process.env, PGPASSWORD: PG.password, PGTZ: "UTC" }, maxBuffer: 256 * 1024 * 1024 }).toString().split("\n").filter(Boolean).map((l) => l.split("\x1f"));
  };
  const definitionsFor = (recording) => {
    const dirs = (recording.scenarioDirs ?? [join(repo, "eval", "askreel", "scenarios")]).map((d) => resolve(repo, d));
    const defs = [];
    for (const dir of dirs) {
      if (!existsSync(dir)) continue;
      for (const f of ["open.json", "holdout.json"].map((n) => join(dir, n)).filter(existsSync)) defs.push(...(JSON.parse(readFileSync(f, "utf8")).scenarios ?? []));
    }
    return defs;
  };

  if (mode === "items") {
    const recording = JSON.parse(readFileSync(args.recording ?? die("--recording is required"), "utf8"));
    const db = args.db ?? die("--db is required (the card facts come from the eval database)");
    const items = sampleItems(recording, definitionsFor(recording), { n: Number(args.n ?? 100), seed: Number(args.seed ?? 42) });
    const confirm = typeof args.confirm === "string"
      ? readFileSync(args.confirm, "utf8").split("\n").map((l) => l.trim()).filter((l) => /^[0-9a-f-]{36}$/i.test(l))
      : [];
    const facts = factsFromDb(db, [...items.map((i) => i.titleId), ...confirm], sql);
    for (const i of items) i.facts = facts.get(i.titleId) ?? i.recorded;
    const confirmRows = confirm.map((id, k) => ({ itemId: `confirm#${k + 1}`, kind: "confirm (yes/no)", scenario: "", turn: "", earlier: "",
      request: typeof args["confirm-question"] === "string" ? args["confirm-question"] : "Is this a ninja film?", titleId: id, facts: facts.get(id) }));
    writeFileSync(resolve(args.out ?? die("--out is required")), itemsCsv([...items, ...confirmRows]));
    console.log(`judge: wrote ${items.length} labelling items${confirmRows.length ? ` and ${confirmRows.length} confirm rows` : ""} to ${args.out}`);
    process.exit(0);
  }

  if (mode === "run") {
    const recording = JSON.parse(readFileSync(args.recording ?? die("--recording is required"), "utf8"));
    const db = args.db ?? die("--db is required");
    const env = args["judge-env"] ?? die("--judge-env <env file> is required: the file whose OpenRouter key pays for the judge");
    const key = modelKeyFor(env, typeof args["founder-key-signoff"] === "string" ? args["founder-key-signoff"] : null);
    if (key.refusal) die(key.refusal.replace("the API's", "the judge env file's"));
    const apiKey = keyFromEnvFile(env) ?? die("the judge env file has no OPENROUTER_API_KEY");
    const promptPath = resolve(typeof args.prompt === "string" ? args.prompt : SEALED_PROMPT);
    if (promptPath.startsWith(repo)) die("the judge prompt must live outside the worktree (§4.12: judge leakage) — use the sealed directory");
    const prompt = readFileSync(promptPath, "utf8");
    const definitions = definitionsFor(recording);
    const ids = (recording.scenarios ?? []).flatMap((s) => s.turns.flatMap((t) => (t.cards ?? []).map((c) => c.titleId)));
    const result = await judgeRecording({ recording, definitions, facts: factsFromDb(db, ids, sql), prompt, apiKey, concurrency: Number(args.concurrency ?? 4) });
    result.modelKey = key.recorded;
    writeFileSync(resolve(args.out ?? die("--out is required")), JSON.stringify(result, null, 1));
    console.log(JSON.stringify({ ...judgeMetrics(result), usage: result.usage, stopped: result.stopped }, null, 1));
    process.exit(result.stopped ? 1 : 0);
  }

  if (mode === "kappa") {
    const labels = labelsFrom(parseCsv(readFileSync(args.labels ?? die("--labels is required"), "utf8")));
    const judge = JSON.parse(readFileSync(args.judge ?? die("--judge is required"), "utf8"));
    const gates = JSON.parse(readFileSync(join(repo, "eval", "askreel", "gates.json"), "utf8"));
    const result = agreement(labels, judge, { kappaMin: gates.judge?.kappaMin ?? 0.6 });
    if (typeof args.out === "string") writeFileSync(resolve(args.out), JSON.stringify({ ...result, promptSha256: judge.promptSha256, model: judge.model }, null, 1));
    console.log(JSON.stringify(result, null, 1));
    if (result.labelled === 0) die("no labelled rows (label 0–3) in the sheet");
    process.exit(result.judgeGates ? 0 : 1);
  }

  if (mode === "compare") {
    const current = JSON.parse(readFileSync(args.judge ?? die("--judge is required"), "utf8"));
    const baseline = JSON.parse(readFileSync(args.baseline ?? die("--baseline is required"), "utf8"));
    const kappa = typeof args.kappa === "string" && existsSync(args.kappa) ? JSON.parse(readFileSync(args.kappa, "utf8")) : null;
    const gates = JSON.parse(readFileSync(join(repo, "eval", "askreel", "gates.json"), "utf8"));
    const result = compareJudge(current, baseline, kappa, gates.judge);
    if (result.refused) { console.error(`judge: refusing to compare — ${result.refused}`); process.exit(2); }
    if (!result.gating) { console.log(`judge: not gating — ${result.why}; the SQL truth is the only gate`); process.exit(0); }
    console.log("| Metric | Baseline | Current | Allowed | Status |\n|---|---|---|---|---|");
    for (const r of result.rows) console.log(`| ${r.metric} | ${r.baseline ?? "—"} | ${r.current ?? "—"} | ${r.allowed ?? "—"} | ${r.status} |`);
    process.exit(result.rows.some((r) => r.status.startsWith("REGRESSION")) ? 1 : 0);
  }

  if (mode === "score") {
    console.log(JSON.stringify(judgeMetrics(JSON.parse(readFileSync(args.judge ?? die("--judge is required"), "utf8"))), null, 1));
    process.exit(0);
  }

  die("usage: judge.mjs items|run|kappa|score|compare …");
}
