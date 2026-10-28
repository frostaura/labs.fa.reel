// Tests for the eval stack's guard rails: run-api.sh refusals, the ledger's cross-database arithmetic.
// Pure or local-only: no API is started, no database is touched, no paid call is made.
//   node --test 'scripts/eval/*.test.mjs'
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, realpathSync, existsSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { netOf, priced, verdicts } from "./ledger.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const runApi = (dir, dll, envFile, extraEnv = {}) => spawnSync("bash", [join(here, "run-api.sh"), "--dll", dll, "--db", "reel_ws_test", "--port", "5999", "--env-file", envFile],
  { env: { ...process.env, EVAL_FOUNDER_ENV: join(dir, "founder.env"), ...extraEnv }, encoding: "utf8" });

test("run-api.sh refuses a dll with a .env in any parent folder (the API's DotEnv would load it)", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-runapi-"));
  try {
    mkdirSync(join(dir, "checkout", "bin", "Release"), { recursive: true });
    writeFileSync(join(dir, "checkout", ".env"), "STRIPE_SECRET_KEY=live\n");
    writeFileSync(join(dir, "checkout", "bin", "Release", "Api.dll"), "");
    writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
    writeFileSync(join(dir, "eval.env"), "OPENROUTER_API_KEY=sk-eval\n");
    const r = runApi(dir, join(dir, "checkout", "bin", "Release", "Api.dll"), join(dir, "eval.env"));
    assert.equal(r.status, 2);
    assert.match(r.stderr, /\.env is above it/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("run-api.sh refuses the founder's OpenRouter key unless his sign-off is recorded", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-runapi-"));
  try {
    mkdirSync(join(dir, "build"), { recursive: true });
    writeFileSync(join(dir, "build", "Api.dll"), "");
    writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
    writeFileSync(join(dir, "lane.env"), "OPENROUTER_API_KEY=\"sk-founder\"\n");
    const r = runApi(dir, join(dir, "build", "Api.dll"), join(dir, "lane.env"));
    assert.equal(r.status, 2);
    assert.match(r.stderr, /founder's own key/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("run-api.sh gives the API an empty environment: a key exported by the caller's shell never reaches it", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-runapi-"));
  try {
    mkdirSync(join(dir, "build"), { recursive: true });
    writeFileSync(join(dir, "build", "Api.dll"), "");
    writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
    writeFileSync(join(dir, "nokey.env"), "TMDB_READ_ACCESS_TOKEN=t\nexport OPENROUTER_MODEL=m\nJWT_SECRET=local-throwaway-secret-32-characters\n");
    const r = spawnSync("bash", [join(here, "run-api.sh"), "--dll", join(dir, "build", "Api.dll"), "--db", "reel_ws_test", "--port", "5999", "--env-file", join(dir, "nokey.env"), "--dry-run"],
      { env: { ...process.env, EVAL_FOUNDER_ENV: join(dir, "founder.env"), OPENROUTER_API_KEY: "sk-inherited-from-the-shell", STRIPE_SECRET_KEY: "sk-live-inherited", SOME_OTHER_SECRET: "x" }, encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    const names = r.stdout.trim().split("\n");
    assert.ok(names.includes("OPENROUTER_API_KEY (empty)"), "the inherited key is replaced by an empty one");
    assert.ok(names.includes("STRIPE_SECRET_KEY (empty)"));
    assert.ok(!names.some((n) => n.startsWith("SOME_OTHER_SECRET")), "nothing the env file and the script do not name is passed");
    assert.ok(names.includes("TMDB_READ_ACCESS_TOKEN") && names.includes("OPENROUTER_MODEL"));
    assert.ok(names.includes("Background__TokenRefresh=false") && names.includes("HTTPS_PROXY=http://127.0.0.1:9"));
    for (const limit of ["ASK_TURNS_CONCURRENT=4", "ASK_TURNS_PER_MINUTE=120", "ASK_TURNS_PER_HOUR=5000", "ASK_TURNS_PER_DAY=5000", "ASK_TURNS_PER_DAY_FOUNDER=5000", "ASK_TURNS_PER_DAY_FREE=5000"]) {
      assert.ok(names.includes(limit), `${limit}: an eval run is never refused by a live Ask turn limit (H3-PLAT-01)`);
    }
    assert.equal(r.stdout.includes("sk-"), false, "dry-run prints names, never values");
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

// A dry run of run-api.sh against a scratch dll folder, with the env file's text and any extra args.
function dryRun(envText, { db = "reel_ws_test", withAppsettings = true } = {}) {
  const dir = realpathSync(mkdtempSync(join(tmpdir(), "ws7-runapi-")));
  mkdirSync(join(dir, "build", "net10.0"), { recursive: true });
  writeFileSync(join(dir, "build", "net10.0", "Api.dll"), "");
  if (withAppsettings) writeFileSync(join(dir, "build", "net10.0", "appsettings.json"), "{}");
  writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
  writeFileSync(join(dir, "eval.env"), envText);
  const r = spawnSync("bash", [join(here, "run-api.sh"), "--dll", join("build", "net10.0", "Api.dll"), "--db", db, "--port", "5999", "--env-file", join(dir, "eval.env"), "--dry-run"],
    { cwd: dir, env: { ...process.env, EVAL_FOUNDER_ENV: join(dir, "founder.env") }, encoding: "utf8" });
  rmSync(dir, { recursive: true, force: true });
  return { ...r, dir, lines: r.stdout.trim().split("\n") };
}
const JWT = "JWT_SECRET=local-throwaway-secret-32-characters\n";

// H3-PLAT-01: the env file's own (live-sized) Ask limits never reach an eval slot, an EVAL_* twin does, and
// the slot gets exactly the compose eval stack's allowances.
test("run-api.sh raises every Ask turn limit over the env file's, honours the EVAL_* twins, and matches the compose eval stack", () => {
  const live = "ASK_TURNS_CONCURRENT=1\nASK_TURNS_PER_MINUTE=6\nASK_TURNS_PER_HOUR=20\nASK_TURNS_PER_DAY=100\nASK_TURNS_PER_DAY_FOUNDER=300\nASK_TURNS_PER_DAY_FREE=10\n";
  const r = dryRun(`TMDB_READ_ACCESS_TOKEN=t\n${JWT}${live}`);
  assert.equal(r.status, 0, r.stderr);
  const limits = Object.fromEntries(r.lines.filter((l) => l.startsWith("ASK_TURNS_")).map((l) => l.split("=")));
  const compose = Object.fromEntries([...readFileSync(join(here, "..", "..", "docker-compose.eval.yml"), "utf8")
    .matchAll(/^\s+(ASK_TURNS_[A-Z_]+): \$\{EVAL_\1:-(\d+)\}$/gm)].map((m) => [m[1], m[2]]));
  assert.deepEqual(Object.keys(compose).sort(), ["ASK_TURNS_CONCURRENT", "ASK_TURNS_PER_DAY", "ASK_TURNS_PER_DAY_FOUNDER", "ASK_TURNS_PER_DAY_FREE", "ASK_TURNS_PER_HOUR", "ASK_TURNS_PER_MINUTE"],
    "every limit AskTurnLimitOptions reads is set on the compose eval stack by its EVAL_* twin, never by the live name");
  assert.deepEqual(limits, compose, "a run-api slot and the compose eval stack give the eval account the same allowances");
  assert.ok(Number(limits.ASK_TURNS_CONCURRENT) >= 4, "askreel.mjs runs up to 4 scenarios at once on one session");
  assert.ok(Number(limits.ASK_TURNS_PER_HOUR) >= 400 && Number(limits.ASK_TURNS_PER_DAY_FOUNDER) >= 400, "a ~200-turn golden run twice in one hour or day");
  // An EVAL_* twin is the one way to set a slot's limit.
  const dir = realpathSync(mkdtempSync(join(tmpdir(), "ws7-runapi-")));
  try {
    mkdirSync(join(dir, "build"), { recursive: true });
    writeFileSync(join(dir, "build", "Api.dll"), "");
    writeFileSync(join(dir, "build", "appsettings.json"), "{}");
    writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
    writeFileSync(join(dir, "eval.env"), `${JWT}${live}`);
    const twin = spawnSync("bash", [join(here, "run-api.sh"), "--dll", join(dir, "build", "Api.dll"), "--db", "reel_ws_test", "--port", "5999", "--env-file", join(dir, "eval.env"), "--dry-run"],
      { env: { ...process.env, EVAL_FOUNDER_ENV: join(dir, "founder.env"), EVAL_ASK_TURNS_CONCURRENT: "2", ASK_TURNS_PER_HOUR: "7" }, encoding: "utf8" });
    assert.equal(twin.status, 0, twin.stderr);
    const out = twin.stdout.trim().split("\n");
    assert.ok(out.includes("ASK_TURNS_CONCURRENT=2"));
    assert.ok(out.includes("ASK_TURNS_PER_HOUR=5000"), "a live-named variable in the caller's shell never sets a slot's limit");
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("run-api.sh refuses an env file with no JWT_SECRET, or a blank one, by name and before the API starts", () => {
  for (const text of ["TMDB_READ_ACCESS_TOKEN=t\n", "TMDB_READ_ACCESS_TOKEN=t\nJWT_SECRET=\n", "JWT_SECRET=\"   \"\n"]) {
    const r = dryRun(text);
    assert.equal(r.status, 2, text);
    assert.match(r.stderr, /sets no JWT_SECRET, and the API will not start without one/, text);
  }
  // A secret the caller's shell exported does not count: the API never sees the caller's environment.
  const dir = mkdtempSync(join(tmpdir(), "ws7-runapi-"));
  try {
    mkdirSync(join(dir, "build"), { recursive: true });
    writeFileSync(join(dir, "build", "Api.dll"), "");
    writeFileSync(join(dir, "founder.env"), "OPENROUTER_API_KEY=sk-founder\n");
    writeFileSync(join(dir, "eval.env"), "TMDB_READ_ACCESS_TOKEN=t\n");
    const r = runApi(dir, join(dir, "build", "Api.dll"), join(dir, "eval.env"), { JWT_SECRET: "inherited-from-the-shell-32-characters" });
    assert.equal(r.status, 2);
    assert.match(r.stderr, /sets no JWT_SECRET/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("run-api.sh boots an eval copy migrated, from the dll's folder, with repairs, purge, dump handshake and Trakt off — whatever the env file says", () => {
  const r = dryRun(JWT + "Background__DataRepairs=true\nBackground__TrustPurge=true\nDatabase__AutoMigrateOnStartup=false\nBACKUP_REQUEST_DIR=/backups\nTRAKT_CLIENT_ID=real-client\nTRAKT_ACCESS_TOKEN=at\nTRAKT_REFRESH_TOKEN=rt\n");
  assert.equal(r.status, 0, r.stderr);
  for (const want of ["Database__AutoMigrateOnStartup=true", "Background__DataRepairs=false", "Background__TrustPurge=false", "BACKUP_REQUEST_DIR=", "TRAKT_CLIENT_ID=eval-no-trakt", "TRAKT_ACCESS_TOKEN (empty)", "TRAKT_REFRESH_TOKEN (empty)", "Background__JobRunner=false"]) {
    assert.ok(r.lines.includes(want), `${want} — got:\n${r.stdout}`);
  }
  // The dll was named by a RELATIVE path; the content root is still its own absolute folder.
  assert.ok(r.stderr.includes(`content root ${r.dir}/build/net10.0\n`), r.stderr);
  assert.doesNotMatch(r.stderr, /no appsettings\.json/);
  assert.match(dryRun(JWT, { withAppsettings: false }).stderr, /no appsettings\.json beside/, "a dll folder without its appsettings is called out");
});

test("run-api.sh never runs on a template database (every slot is cloned from it)", () => {
  for (const db of ["reel_eval_template", "reel_ws7_template"]) {
    const r = dryRun(JWT, { db });
    assert.equal(r.status, 2, db);
    assert.match(r.stderr, /a template is cloned, never run/, db);
  }
  assert.equal(dryRun(JWT, { db: "reel_eval" }).status, 0, "the eval database itself is still allowed");
});

test("surfaces.mjs --replay creates a missing --out directory (recursively) and writes beside the recording without one", () => {
  const dir = mkdtempSync(join(tmpdir(), "ws7-replay-"));
  try {
    const recording = { kind: "surface-recording", version: 3, label: "t", snapshotId: "s", blocks: [{ surface: "genre-rail", key: "g", component: "card", status: 200, cards: [{ titleId: "a", name: "A", whyThis: "Fits.", lensClause: null, hook: null, reason: null }] }],
      names: { a: "A" }, ledger: [], facts: { eligible: ["a"], seen: [], evidence: {}, randomCosineP90: null } };
    writeFileSync(join(dir, "recording.json"), JSON.stringify(recording));
    const out = join(dir, "not", "there", "yet");
    const r = spawnSync(process.execPath, [join(here, "surfaces.mjs"), "--replay", join(dir, "recording.json"), "--out", out], { encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    assert.ok(existsSync(join(out, "metrics.json")) && existsSync(join(out, "report.md")));
    const beside = spawnSync(process.execPath, [join(here, "surfaces.mjs"), "--replay", join(dir, "recording.json")], { encoding: "utf8" });
    assert.equal(beside.status, 0, beside.stderr);
    assert.ok(existsSync(join(dir, "metrics.json")));
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("ledger: a database's own spend is its rows net of what it inherited from the snapshot template", () => {
  const row = (purpose, calls, pt) => ({ provider: "OpenRouter", model: "openai/gpt-6-luna", purpose, day: "2026-10-24", calls, promptTokens: pt, completionTokens: 0, tokensUsed: 0 });
  const inherited = [row("rerank-feed", 10, 1000)];
  assert.deepEqual(netOf([row("rerank-feed", 12, 1500), row("rerank-ask", 3, 300)], inherited).map((r) => [r.purpose, r.calls, r.promptTokens]), [["rerank-feed", 2, 500], ["rerank-ask", 3, 300]]);
  assert.deepEqual(netOf([row("rerank-feed", 10, 1000)], inherited), [], "a fresh clone has spent nothing");
});

test("ledger: §7 verdicts apply to the total across databases, and any eval Trakt call is an isolation failure", () => {
  const p = priced([{ provider: "OpenRouter", model: "m", purpose: "x", calls: 1, promptTokens: 1e6, completionTokens: 0, tokensUsed: 0 }], { m: { inputPerMillion: 6 } });
  assert.equal(p.total, 6);
  assert.match(verdicts(p.total, 0, false)[0], /ABOVE \$5/);
  assert.match(verdicts(8.5, 0, false)[0], /ABOVE \$8/);
  assert.match(verdicts(0, 1, false)[0], /ISOLATION FAILURE/);
  assert.deepEqual(verdicts(0, 3, true), [], "Trakt calls on the live ledger are the live stack's business");
});
