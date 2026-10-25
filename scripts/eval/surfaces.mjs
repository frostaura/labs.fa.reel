#!/usr/bin/env node
// App-wide surface harness (program WS-7 task 8). Manual dispatch, eval stack only.
//
//   node scripts/eval/surfaces.mjs --api http://127.0.0.1:5180 --db reel_eval --label <name> --api-env <env file the API runs with>
//        [--fresh] [--build-info <BUILD_INFO.json>] [--founder-key-signoff "<who, when>"] [--out .tmp/eval/<date>/<label>]
//   node scripts/eval/surfaces.mjs --replay <recording.json> [--out <dir>]
//   node scripts/eval/surfaces.mjs --compare <metrics.json> --baseline <metrics.json>
//
// --fresh  enqueues ONE Fresh feed build (POST /api/feed/rebuild) and one digest build (the BuildDigest
//          job the weekly service would enqueue; it makes no model call), waits for both, then waits for
//          EVERY job the build chained (Train, PersonaliseTitles, RefreshAvailability, a second BuildFeed…)
//          to drain before the first fetch, so nothing moves underneath the measurement. The API must run
//          with its job runner on (run-api.sh --jobs). Without --fresh it measures the snapshot as stored.
// Then it fetches every surface — the feed (tonight's picks and every row), each row's next page, each
// genre rail, the Similar rail for the first five picks, the digest, Saved and its Watch-tonight pin,
// person pages for the people credited on tonight's picks, and typeahead — recording each response's
// HTTP status, and reports per surface:
//   stub rate          why-lines in the template family a failed model call produces;
//   exclusion leaks    suggestion cards the independent eligibility truth says may not be shown;
//   cross-rail repeats one title on more than one feed row;
//   evidence share     anchored ("Because you loved X") and Similar cards that share ≥ 2 TMDB keywords
//                      with the anchor or sit closer to it than 90% of random eligible pairs;
//   hedging why-lines  explanations that argue against their own pick, and non-Latin script in copy;
//   lookup footprint   watched or rated titles still on Saved or pinned as Watch tonight (gated), and on
//                      person pages and typeahead (reported only — those show what you have seen by design);
//   coverage           a surface that errored or (suggestion surfaces) came back empty has NO data, and
//                      the run exits 1 — an error is never scored as "0 leaks";
//   copy claims        not measured yet (WS-7 task 9, the claims audit).
// Ask is not fetched here: its leaks are the golden eval's exclusionLeaks on the same _eligible.sql truth.
// Every metric is computed from data frozen into recording.json, so --replay re-scores with no API, no
// database and no paid call; --compare applies eval/surfaces/gates.json against a baseline.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { scoreSurfaces, compareSurfaces, surfaceGates } from "./surfaces-score.mjs";
import { modelKeyFor } from "./guards.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const args = {};
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  if (!argv[i].startsWith("--")) continue;
  const next = argv[i + 1];
  if (next === undefined || next.startsWith("--")) args[argv[i].slice(2)] = true;
  else { args[argv[i].slice(2)] = next; i++; }
}
const die = (m) => { console.error(`surfaces: ${m}`); process.exit(2); };

const PG = { host: process.env.EVAL_PGHOST ?? "127.0.0.1", port: process.env.EVAL_PGPORT ?? "5434", user: process.env.EVAL_PGUSER ?? "reel", password: process.env.EVAL_PGPASSWORD ?? "reel_eval" };
function guard(db) {
  if (String(PG.port) === "5433") die("refusing port 5433 — the founder's LIVE Postgres");
  if (!db || db === "reel" || !/^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*)$/.test(db)) die(`refusing database '${db}'`);
}
function sql(db, query, vars = {}) {
  guard(db);
  const v = Object.entries(vars).flatMap(([k, val]) => ["-v", `${k}=${val}`]);
  return execFileSync("psql", ["-X", "-v", "ON_ERROR_STOP=1", "-At", "-F", "\x1f", "-h", PG.host, "-p", PG.port, "-U", PG.user, "-d", db, ...v, "-f", "-"],
    { input: query, env: { ...process.env, PGPASSWORD: PG.password, PGTZ: "UTC" }, maxBuffer: 256 * 1024 * 1024 })
    .toString().split("\n").filter(Boolean).map((l) => l.split("\x1f"));
}

const gates = JSON.parse(readFileSync(join(repo, "eval", "surfaces", "gates.json"), "utf8"));

function render(rec, m) {
  const s = m.summary;
  const fmt = (v, d = 3) => (v === null || v === undefined ? "—" : typeof v === "number" && !Number.isInteger(v) ? v.toFixed(d) : String(v));
  const L = [`# Surface harness — ${s.label}`, "",
    `- API build ${s.build ?? "unrecorded"} · snapshot \`${s.snapshotId}\` · artifact \`${s.artifact?.id ?? "unrecorded"}\`${s.artifact?.changedDuringFetch ? " (**CHANGED during the fetch**)" : ""} · ${s.fresh ? `one Fresh build (${s.buildStatus})` : "stored snapshot, no build"} · ${s.wallSeconds} s`,
    `- Jobs drained before the first fetch: ${s.jobsDrainedBeforeFetch === null ? "not recorded (recording predates the drain)" : s.jobsDrainedBeforeFetch ? "yes" : "NO"} · model key: ${s.modelKey}`,
    `- Exposures written by the run: ${s.exposuresWritten}; Trakt calls: ${s.traktCalls}; cost $${fmt(s.costUsd, 4)}`, "",
    s.complete ? "- Coverage: every surface answered with data." : `- **Coverage: ${s.surfacesWithoutData.length} surface(s) have NO data** — ${s.surfacesWithoutData.join("; ")}. Their zeros below are not measurements.`, "",
    "## Gates (task 8 targets)", "", "| Gate | Value | Target | Status |", "|---|---|---|---|",
    ...surfaceGates(m, gates).map((g) => `| ${g.label} | ${fmt(g.value)} | ${g.op} ${g.target} | ${g.status} |`), "",
    "| Metric | Value |", "|---|---|",
    `| Suggestion cards fetched | ${s.suggestionCards} |`, `| Stub rate (why-lines in the stub family) | ${fmt(s.stubRate)} |`,
    `| Exclusion leaks (suggestion surfaces) | ${s.exclusionLeaks} |`, `| Cross-rail repeats (feed page) | ${s.crossRailRepeats} |`,
    `| Evidence share (anchored + Similar, n=${s.anchoredCards}) | ${fmt(s.evidenceShare)} |`, `| Random-pair cosine p90 (evidence bar) | ${fmt(s.randomCosineP90)} |`,
    `| Why-lines hedging against their own pick | ${s.hedgingWhyLines} |`, `| Why-lines with non-Latin script | ${s.nonLatinWhyLines} |`,
    `| Watched or rated titles on Saved / Watch tonight | ${s.lookupFootprint} |`, `| False copy claims | not measured yet (task 9) |`, "",
    "## Per surface", "", "| Surface | Blocks | HTTP errors | Empty blocks | Cards | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |", "|---|---|---|---|---|---|---|---|---|---|---|---|"];
  for (const [k, v] of Object.entries(m.perSurface)) L.push(`| ${k} | ${v.blocks} | ${v.errors} | ${v.emptyBlocks} | ${v.cards} | ${v.stubWhy} | ${v.hedgingWhy} | ${v.nonLatinWhy} | ${v.leaks} | ${v.anchored} | ${v.evidencePass} | ${v.footprint} |`);
  const leaks = Object.entries(m.perSurface).flatMap(([k, v]) => v.leakNames.map((n) => `${k}: ${n}`));
  if (leaks.length) L.push("", "## Leaks (first 20 per surface)", "", ...leaks.map((x) => `- ${x}`));
  if (m.repeats.length) L.push("", "## Cross-rail repeats", "", ...m.repeats.map((r) => `- ${r.name}: ${r.rows.join(", ")}`));
  if (m.hedges.length) L.push("", "## Hedging why-lines (first 40)", "", ...m.hedges.map((h) => `- ${h}`));
  L.push("", "## Jobs that ran during the harness", "", ...(rec.jobsDuringRun ? (rec.jobsDuringRun.length ? rec.jobsDuringRun.map((j) => `- ${j.kind} ${j.status} × ${j.count}`) : ["- none"]) : ["- not recorded in this recording (it predates job recording; see provenance.md beside it if present)"]));
  L.push("", "## Ledger delta", "", "| Provider | Model | Purpose | Calls | Prompt | Completion |", "|---|---|---|---|---|---|",
    ...(m.ledger ?? []).map((r) => `| ${r.provider} | ${r.model || "—"} | ${r.purpose || "—"} | ${r.calls} | ${r.promptTokens} | ${r.completionTokens} |`));
  return L.join("\n") + "\n";
}

if (args.compare) {
  const current = JSON.parse(readFileSync(args.compare, "utf8"));
  const baseline = JSON.parse(readFileSync(args.baseline ?? die("--baseline is required"), "utf8"));
  const { refused, rows } = compareSurfaces(current, baseline, gates);
  if (refused) { console.error(`surfaces: refusing to compare — ${refused}`); process.exit(2); }
  console.log("| Metric | Baseline | Current | Allowed | Status |\n|---|---|---|---|---|");
  for (const r of rows) console.log(`| ${r.metric} | ${r.baseline ?? "—"} | ${r.current ?? "—"} | ${r.allowed ?? "—"} | ${r.status} |`);
  process.exit(rows.some((r) => r.status.startsWith("REGRESSION")) ? 1 : 0);
}

if (args.replay) {
  const rec = JSON.parse(readFileSync(args.replay, "utf8"));
  const out = args.out ?? dirname(args.replay);
  const m = scoreSurfaces(rec);
  writeFileSync(join(out, "metrics.json"), JSON.stringify(m, null, 1));
  writeFileSync(join(out, "report.md"), render(rec, m));
  console.log(JSON.stringify(m.summary, null, 1));
  process.exit(0);
}

// ── live ──────────────────────────────────────────────────────────────────────────────────────
const api = args.api ?? die("--api is required");
const db = args.db ?? die("--db is required");
guard(db);
const label = args.label ?? die("--label is required");
const modelKey = modelKeyFor(args["api-env"], typeof args["founder-key-signoff"] === "string" ? args["founder-key-signoff"] : null);
if (modelKey.refusal) die(modelKey.refusal);
const buildInfo = args["build-info"] ? JSON.parse(readFileSync(resolve(args["build-info"]), "utf8")) : null;
const outDir = resolve(args.out ?? join(repo, ".tmp", "eval", new Date().toISOString().slice(0, 10), label));
const started = Date.now();

// The access cookie lives JWT_ACCESS_TOKEN_EXPIRY_MINUTES (15 by default): link again after the build.
let cookie = null;
async function relink() {
  const res = await fetch(`${api}/api/auth/dev/link`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  if (!res.ok) die(`dev/link failed: ${res.status}`);
  cookie = res.headers.getSetCookie().map((c) => c.split(";")[0]).find((c) => c.startsWith("reel_at="));
  return (await res.json()).accountId;
}
const accountId = await relink();
// Every response keeps its HTTP status; a block records it, and the scorer treats a non-200 as no data.
const get = async (path) => { const r = await fetch(`${api}${path}`, { headers: { Cookie: cookie } }); return { status: r.status, body: r.ok ? await r.json() : null }; };
const post = async (path, body) => { const r = await fetch(`${api}${path}`, { method: "POST", headers: { Cookie: cookie, "Content-Type": "application/json" }, body: JSON.stringify(body) }); return { status: r.status, body: r.ok && r.status !== 202 && r.status !== 204 ? await r.json() : null }; };
const ok = (r) => r.status >= 200 && r.status < 300;

const day = new Date().toISOString().slice(0, 10);
const ledger = () => sql(db, `select "Provider", coalesce("Model",''), coalesce("Purpose",''), "CallCount", coalesce("PromptTokens",0), coalesce("CompletionTokens",0) from "ExternalApiUsages" where "Day" >= '${day}'`)
  .map(([provider, model, purpose, calls, pt, ct]) => ({ provider, model, purpose, calls: Number(calls), promptTokens: Number(pt), completionTokens: Number(ct) }));
const before = ledger();
const exposuresBefore = Number(sql(db, `select count(*) from "ExposureEvents"`)[0][0]);
const snapshotId = execFileSync(join(here, "snapshot.sh"), ["id", db]).toString().trim() || null;

const activeArtifact = () => sql(db, `select "Id", "Version" from "ModelArtifacts" where "AccountId" = :'account' and "Status" = 'Active'`, { account: accountId })[0] ?? [null, null];
const openJobs = () => Number(sql(db, `select count(*) from "SyncJobs" where "AccountId" = :'account' and "Status" in ('Pending', 'Running')`, { account: accountId })[0][0]);
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));

let buildStatus = null;
let jobsDrainedBeforeFetch = null;
if (args.fresh) {
  const enqueuedAfter = new Date(Date.now() - 1000).toISOString();
  const r = await post("/api/feed/rebuild", {});
  if (!ok(r)) die(`rebuild refused: HTTP ${r.status}`);
  // The digest is built by the weekly service, which is off on the eval stack; enqueue the same job it
  // would, so the digest surface is measured rather than empty.
  sql(db, `insert into "SyncJobs" ("Id", "AccountId", "Kind", "Priority", "Status", "AttemptCount", "EnqueuedAt", "TriggerReason")
           values (gen_random_uuid(), :'account', 'BuildDigest', 2, 'Pending', 0, now(), 'eval-surface-harness')`, { account: accountId });
  for (let i = 0; i < 180; i++) {
    const row = sql(db, `select "Status" from "SyncJobs" where "AccountId" = :'account' and "Kind" = 'BuildFeed' and "EnqueuedAt" >= '${enqueuedAfter}' order by "EnqueuedAt" asc limit 1`, { account: accountId })[0];
    buildStatus = row?.[0] ?? "Pending";
    if (buildStatus === "Succeeded" || buildStatus === "Failed") break;
    await sleep(5000);
  }
  console.error(`surfaces: Fresh build ${buildStatus}`);
  if (buildStatus !== "Succeeded") die(`the Fresh build did not succeed (${buildStatus}) — is the API running with --jobs?`);
  // Wait for everything the build chained to finish (cap 20 min), so the artifact and the rows are still.
  jobsDrainedBeforeFetch = false;
  for (let i = 0; i < 240; i++) {
    if (openJobs() === 0) { jobsDrainedBeforeFetch = true; break; }
    await sleep(5000);
  }
  console.error(`surfaces: chained jobs ${jobsDrainedBeforeFetch ? "drained" : "STILL RUNNING after 20 min"}`);
  await relink();
}
const artifactAtStart = activeArtifact();

const card = (c) => ({ titleId: c.titleId, name: c.name, why: c.whyThis ?? c.why ?? null });
const blocks = [];
const push = (surface, key, res, items, extra = {}) => blocks.push({ surface, key, status: res.status, ...extra, cards: ok(res) ? (items(res.body) ?? []).map(card) : [] });
const feedRes = await get("/api/feed");
if (!ok(feedRes)) die(`GET /api/feed: HTTP ${feedRes.status}`);
const feed = feedRes.body;
push("feed-hero", "tonights-picks", feedRes, (b) => b.hero);
for (const row of feed.rows ?? []) {
  const key = row.kind + (row.anchorName ? `:${row.anchorName}` : "");
  push("feed-row", key, feedRes, () => row.items, { anchorTitleId: row.anchorTitleId ?? null });
  const shown = (row.items ?? []).map((c) => c.titleId);
  const more = await post("/api/feed/more", { row: row.kind, anchorTitleId: row.anchorTitleId ?? null, excludeTitleIds: shown, limit: 12 });
  push("more", `${key} (page 2)`, more, (b) => b.items, { anchorTitleId: row.anchorTitleId ?? null });
}
const heroMore = await post("/api/feed/more", { row: "tonights-picks", excludeTitleIds: (feed.hero ?? []).map((c) => c.titleId), limit: 12 });
push("more", "tonights-picks (page 2)", heroMore, (b) => b.items);
for (const slug of ["new-for-you", "deep-cuts"]) {
  if ((feed.rows ?? []).some((r) => r.kind === slug)) continue;
  push("more", slug, await post("/api/feed/more", { row: slug, excludeTitleIds: [], limit: 12 }), (b) => b.items);
}
const genresRes = await get("/api/feed/genres");
if (!ok(genresRes)) blocks.push({ surface: "genre-rail", key: "genres", status: genresRes.status, cards: [] });
for (const g of genresRes.body?.genres ?? []) {
  push("genre-rail", `genre:${g.slug}`, await post("/api/feed/more", { row: "genre", genre: g.slug, excludeTitleIds: [], limit: 12 }), (b) => b.items);
}
for (const h of (feed.hero ?? []).slice(0, 5)) {
  push("similar", `similar:${h.name}`, await post("/api/feed/more", { row: "similar", anchorTitleId: h.titleId, excludeTitleIds: [], limit: 12 }), (b) => b.items, { anchorTitleId: h.titleId });
}
push("digest", "digest", await get("/api/digest/current"), (b) => b.items);
const savedRes = await get("/api/saved");
push("saved", "saved", savedRes, (b) => b.items);
push("watch-tonight", "watch-tonight", savedRes, (b) => (b.items ?? []).filter((x) => x.watchTonight));
// Person pages: the top-billed people on tonight's picks (cast order, then crew), five at most.
const heroIds = (feed.hero ?? []).map((c) => c.titleId).filter(Boolean);
const people = heroIds.length ? sql(db, `select "PersonId" from (select distinct on ("PersonId") "PersonId", coalesce("CastOrder", 99) o
    from "TitleCredits" where "TitleId" in (${heroIds.map((x) => `'${x}'`).join(",")}) order by "PersonId", o) p order by o, "PersonId" limit 5`).map((r) => r[0]) : [];
for (const pid of people) {
  push("person", `person:${pid}`, await get(`/api/people/${pid}`), (b) => b.filmography);
}
for (const q of ["matrix", "nolan", "ninja", "comedy", "the", "blade runner", "romance", "anime", "severance", "dune"]) {
  push("typeahead", `typeahead:${q}`, await get(`/api/search/typeahead?q=${encodeURIComponent(q)}`), (b) => b.titles);
}
const artifactAtEnd = activeArtifact();

const finished = Date.now();
await new Promise((res) => setTimeout(res, 20000)); // ledger flush window
const after = ledger();
const exposuresAfter = Number(sql(db, `select count(*) from "ExposureEvents"`)[0][0]);
const key = (r) => [r.provider, r.model, r.purpose].join("|");
const b = new Map(before.map((r) => [key(r), r]));
const ledgerDelta = after.map((r) => { const p = b.get(key(r)) ?? { calls: 0, promptTokens: 0, completionTokens: 0 }; return { ...r, calls: r.calls - p.calls, promptTokens: r.promptTokens - p.promptTokens, completionTokens: r.completionTokens - p.completionTokens }; })
  .filter((r) => r.calls || r.promptTokens || r.completionTokens);
const prices = JSON.parse(readFileSync(join(repo, "eval", "askreel", "prices.json"), "utf8")).models;
const costUsd = ledgerDelta.reduce((n, r) => n + (prices[r.model] ? (r.promptTokens * prices[r.model].inputPerMillion + r.completionTokens * (prices[r.model].outputPerMillion ?? 0)) / 1e6 : 0), 0);

// Facts frozen for replay: eligibility, anchor evidence, the random-pair bar, names.
const ids = [...new Set(blocks.flatMap((bl) => bl.cards.map((c) => c.titleId)).filter(Boolean))];
const inIds = ids.length ? ids.map((x) => `'${x}'`).join(",") : "NULL";
const eligibleSql = readFileSync(join(repo, "eval", "askreel", "truth", "_eligible.sql"), "utf8");
const eligible = sql(db, `with e as (${eligibleSql}) select "Id" from e where "Id" in (${inIds})`, { account: accountId }).map((r) => r[0]);
const seen = sql(db, `select distinct "TitleId" from (select "TitleId" from "WatchedTitles" where "AccountId" = :'account' union select "TitleId" from "UserRatings" where "AccountId" = :'account') x where "TitleId" in (${inIds})`, { account: accountId }).map((r) => r[0]);
const pairs = blocks.filter((bl) => bl.anchorTitleId).flatMap((bl) => bl.cards.map((c) => [bl.anchorTitleId, c.titleId]));
const evidence = {};
if (pairs.length) {
  const values = pairs.map(([a, c]) => `('${a}'::uuid,'${c}'::uuid)`).join(",");
  for (const [a, c, shared, cos] of sql(db, `
      with p(a, c) as (values ${values})
      select p.a, p.c,
             (select count(*) from unnest(ta."Keywords") k where k = any (tc."Keywords")),
             coalesce((1 - (ea."Embedding" <=> ec."Embedding"))::text, '')
        from p join "Titles" ta on ta."Id" = p.a join "Titles" tc on tc."Id" = p.c
        left join "TitleEmbeddings" ea on ea."TitleId" = p.a left join "TitleEmbeddings" ec on ec."TitleId" = p.c`)) {
    evidence[`${a}|${c}`] = { sharedKeywords: Number(shared), cosine: cos === "" ? null : Number(cos) };
  }
}
// The evidence bar: cosine of 2,000 random eligible pairs on this snapshot, p90. Seeded so it is stable.
const p90 = sql(db, `select setseed(0.4242); with e as (${eligibleSql}), x as (select e."Id" from e join "TitleEmbeddings" te on te."TitleId" = e."Id" order by random() limit 4000),
  n as (select "Id", row_number() over () rn from x)
  select percentile_disc(0.9) within group (order by 1 - (ea."Embedding" <=> eb."Embedding"))
    from n a join n b on b.rn = a.rn + 2000 join "TitleEmbeddings" ea on ea."TitleId" = a."Id" join "TitleEmbeddings" eb on eb."TitleId" = b."Id"`, { account: accountId }).at(-1)?.[0];
// Every job that ran on the eval database during the harness (a Fresh build can chain retrains and
// enrichment), so a surprising number can be traced to what else moved underneath it.
const jobsDuringRun = sql(db, `select "Kind", "Status", count(*) from "SyncJobs" where "EnqueuedAt" >= '${new Date(started).toISOString()}' group by 1, 2 order by 1`)
  .map(([kind, status, n]) => ({ kind, status, count: Number(n) }));
const names = Object.fromEntries(blocks.flatMap((bl) => bl.cards.map((c) => [c.titleId, c.name])));

const rec = {
  kind: "surface-recording", version: 2, label, build: buildInfo?.commit ?? null, buildInfo, modelKey: modelKey.recorded, api, db, snapshotId, accountId, fresh: Boolean(args.fresh), buildStatus, jobsDrainedBeforeFetch,
  artifact: { id: artifactAtStart[0], version: artifactAtStart[1] ? Number(artifactAtStart[1]) : null, changedDuringFetch: artifactAtStart[0] !== artifactAtEnd[0] },
  startedAt: new Date(started).toISOString(), wallSeconds: Math.round((finished - started) / 1000), blocks, names,
  ledger: ledgerDelta, costUsd, exposuresWritten: exposuresAfter - exposuresBefore, jobsDuringRun,
  facts: { eligible, seen, evidence, randomCosineP90: p90 === undefined || p90 === "" ? null : Number(p90) },
};
mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, "recording.json"), JSON.stringify(rec));
const m = scoreSurfaces(rec);
writeFileSync(join(outDir, "metrics.json"), JSON.stringify(m, null, 1));
writeFileSync(join(outDir, "report.md"), render(rec, m));
console.log(JSON.stringify(m.summary, null, 1));
console.error(`surfaces: wrote ${outDir}`);
if (!m.summary.complete || rec.artifact.changedDuringFetch) {
  console.error(`surfaces: INCOMPLETE — ${[...m.summary.surfacesWithoutData, rec.artifact.changedDuringFetch ? "the model artifact changed during the fetch" : null].filter(Boolean).join("; ")}`);
  process.exit(1);
}
