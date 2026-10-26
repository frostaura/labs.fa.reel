#!/usr/bin/env node
// The exposure-free perf harness (program WS-7 task 11; §5.0 rule 4: "perf and CLS runs stub
// /api/feed/more, /lens and /api/search/ask").
//
//   node scripts/eval/perf.mjs --app http://127.0.0.1:5207 --api http://127.0.0.1:5187 --db <eval db> [--runs 7] [--path /home] [--out <dir>]
//
// --app is the BUILT frontend (`npm run build`, then `VITE_API_TARGET=<api> npx vite preview --port <port>`,
// both inside bin/build-slot), proxying /api to an eval-slot API (scripts/eval/run-api.sh) on --db. Each run
// is a cold browser context (its own Chromium, service workers blocked) signed in through dev/link, loading
// --path and measuring what a user waits for: time to the first hero card, LCP, CLS (excluding shifts
// after input), the load event, and total long-task time. p50 and p95 over the runs.
//
// Exposure-free: every endpoint that writes the append-only exposure log or spends a model call is answered
// at the route layer and never reaches the API — /api/feed/more (from the feed's own rows), the digest
// (empty), lens (no copy, the ladder's own fallback), and Ask (a finished empty turn). The harness counts
// the account's ExposureEvents before and after and FAILS (exit 1) if the count moved: the guarantee is
// measured, not assumed. GET /api/feed itself is served by the API; it writes exposures only when it
// backfills a stale pick, and the count catches that too.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const isMain = process.argv[1] === fileURLToPath(import.meta.url);

/** The p-th percentile (nearest rank) of numbers; null when there are none. */
export function percentile(values, p) {
  const v = values.filter((x) => typeof x === "number" && Number.isFinite(x)).sort((a, b) => a - b);
  if (!v.length) return null;
  return v[Math.min(v.length - 1, Math.max(0, Math.ceil((p / 100) * v.length) - 1))];
}

/** The stubbed answers, keyed by what the request asks for; pure so it can be tested without a browser. */
export function stubFor(url, method, body, feed) {
  const path = new URL(url).pathname;
  if (path === "/api/feed/more" && method === "POST") {
    const row = body?.row;
    const rows = feed?.rows ?? [];
    const match = rows.find((r) => r.kind === row && (r.anchorTitleId ?? null) === (body?.anchorTitleId ?? null)) ?? rows.find((r) => r.kind === row);
    const shown = new Set([...(body?.excludeTitleIds ?? []), ...(body?.pageExcludeTitleIds ?? [])]);
    const items = (row === "tonights-picks" || row === "hero" ? feed?.hero ?? [] : match?.items ?? [])
      .filter((c) => !shown.has(c.titleId)).slice(0, body?.limit ?? 12);
    return { status: 200, contentType: "application/json", body: JSON.stringify({ items, hasMore: false, degradedReason: null }) };
  }
  if (path === "/api/digest/current") {
    return { status: 200, contentType: "application/json", body: JSON.stringify({ weekStart: null, generatedAt: null, items: [] }) };
  }
  if (/^\/api\/titles\/[^/]+\/\d+\/lens$/.test(path)) {
    return { status: 200, contentType: "application/json", body: JSON.stringify({ source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null }) };
  }
  if (path === "/api/search/ask") {
    return { status: 200, contentType: "text/event-stream", body: `event: done\ndata: ${JSON.stringify({ results: [], streamed: 0, failed: false, reason: "perf harness: Ask is stubbed" })}\n\n` };
  }
  return null;
}

/** The report for a set of runs. */
export function summarise(runs) {
  const keys = ["firstHeroCardMs", "lcpMs", "cls", "loadMs", "longTaskMs"];
  return Object.fromEntries(keys.map((k) => [k, { p50: percentile(runs.map((r) => r[k]), 50), p95: percentile(runs.map((r) => r[k]), 95) }]));
}

export function renderPerf(summary, meta) {
  const fmt = (v, k) => (v === null ? "—" : k === "cls" ? v.toFixed(3) : `${Math.round(v)} ms`);
  return [`# Perf — ${meta.path} (${meta.runs} cold runs)`, "",
    `- App ${meta.app} → API ${meta.api} on \`${meta.db}\`; build ${meta.build ?? "unrecorded"}.`,
    `- Exposure-free: the account's ExposureEvents went ${meta.exposuresBefore} → ${meta.exposuresAfter}${meta.exposuresAfter === meta.exposuresBefore ? " (unchanged)" : " (**CHANGED — the run is void**)"}; stubbed requests: ${meta.stubbed}.`,
    "", "| Measure | p50 | p95 |", "|---|---|---|",
    ...Object.entries(summary).map(([k, v]) => `| ${k} | ${fmt(v.p50, k)} | ${fmt(v.p95, k)} |`), ""].join("\n");
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
  const die = (m) => { console.error(`perf: ${m}`); process.exit(2); };
  const app = args.app ?? die("--app is required (the built frontend, proxying /api to the eval API)");
  const api = args.api ?? die("--api is required (the eval API, for the dev-link session)");
  const db = args.db ?? die("--db is required (the database the API runs on, to prove no exposure was written)");
  const PG = { host: process.env.EVAL_PGHOST ?? "127.0.0.1", port: process.env.EVAL_PGPORT ?? "5434", user: process.env.EVAL_PGUSER ?? "reel", password: process.env.EVAL_PGPASSWORD ?? "reel_eval" };
  if (String(PG.port) === "5433") die("refusing port 5433 — the founder's LIVE Postgres");
  if (db === "reel" || !/^(reel_eval(_.*)?|reel_ws.*|reel_test_.*|reel_baseline.*|reel_int_.*)$/.test(db)) die(`refusing database '${db}'`);
  if (/:5054\b/.test(api)) die("refusing the founder's live dev API on :5054");
  const count = (accountId) => Number(execFileSync("psql", ["-X", "-At", "-h", PG.host, "-p", PG.port, "-U", PG.user, "-d", db, "-c",
    `select count(*) from "ExposureEvents" where "AccountId" = '${accountId}'`], { env: { ...process.env, PGPASSWORD: PG.password } }).toString().trim());

  const require = createRequire(join(repo, "src", "frontend", "package.json"));
  const { chromium } = require("playwright");
  const runs = Number(args.runs ?? 7);
  const path = typeof args.path === "string" ? args.path : "/home";

  const link = await fetch(`${api}/api/auth/dev/link`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ noCascade: true }) });
  if (!link.ok) die(`dev/link failed: HTTP ${link.status} (link once without noCascade on this database first)`);
  const setCookies = link.headers.getSetCookie().map((c) => c.split(";")[0]);
  const { accountId } = await link.json();
  const feedRes = await fetch(`${api}/api/feed`, { headers: { Cookie: setCookies.join("; ") } });
  const feed = feedRes.ok ? await feedRes.json() : null;

  const before = count(accountId);
  const browser = await chromium.launch({ headless: true });
  const results = [];
  let stubbed = 0;
  try {
    for (let i = 0; i < runs; i++) {
      const context = await browser.newContext({ serviceWorkers: "block", viewport: { width: 1440, height: 900 } });
      const host = new URL(app).hostname;
      await context.addCookies(setCookies.map((c) => { const [name, ...v] = c.split("="); return { name, value: v.join("="), domain: host, path: "/" }; }));
      await context.route("**/api/**", async (route) => {
        const req = route.request();
        let body = null;
        try { body = req.postDataJSON(); } catch { /* not JSON */ }
        const stub = stubFor(req.url(), req.method(), body, feed);
        if (stub) { stubbed += 1; return route.fulfill(stub); }
        return route.continue();
      });
      await context.addInitScript(() => {
        window.__perf = { lcp: null, cls: 0, longTask: 0 };
        new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__perf.lcp = e.startTime; }).observe({ type: "largest-contentful-paint", buffered: true });
        new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__perf.cls += e.value; }).observe({ type: "layout-shift", buffered: true });
        new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__perf.longTask += e.duration; }).observe({ type: "longtask", buffered: true });
      });
      const page = await context.newPage();
      const t0 = Date.now();
      await page.goto(new URL(path, app).toString(), { waitUntil: "load" });
      let firstHeroCardMs = null;
      try {
        await page.locator('[data-testid="hero-card"]').first().waitFor({ state: "visible", timeout: 30_000 });
        firstHeroCardMs = Date.now() - t0;
      } catch { /* no hero card: recorded as null */ }
      await page.waitForTimeout(2_000);
      const m = await page.evaluate(() => ({ ...window.__perf, load: performance.getEntriesByType("navigation")[0]?.loadEventEnd ?? null }));
      results.push({ firstHeroCardMs, lcpMs: m.lcp, cls: m.cls, loadMs: m.load, longTaskMs: m.longTask });
      console.error(`perf: run ${i + 1}/${runs} — first hero card ${firstHeroCardMs ?? "never"} ms, LCP ${m.lcp === null ? "none" : Math.round(m.lcp)} ms, CLS ${m.cls.toFixed(3)}`);
      await context.close();
    }
  } finally {
    await browser.close();
  }
  const after = count(accountId);
  const summary = summarise(results);
  const meta = { app, api, db, path, runs, build: typeof args.build === "string" ? args.build : null, exposuresBefore: before, exposuresAfter: after, stubbed };
  const report = renderPerf(summary, meta);
  console.log(report);
  const out = resolve(typeof args.out === "string" ? args.out : join(repo, ".tmp", "eval", new Date().toISOString().slice(0, 10), "perf"));
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, "perf.json"), JSON.stringify({ meta, summary, runs: results }, null, 1));
  writeFileSync(join(out, "report.md"), report);
  process.exit(after === before ? 0 : 1);
}
