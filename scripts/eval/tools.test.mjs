// Tests for the eval stack's guard rails: run-api.sh refusals, the ledger's cross-database arithmetic.
// Pure or local-only: no API is started, no database is touched, no paid call is made.
//   node --test 'scripts/eval/*.test.mjs'
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
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
    writeFileSync(join(dir, "nokey.env"), "TMDB_READ_ACCESS_TOKEN=t\nexport OPENROUTER_MODEL=m\n");
    const r = spawnSync("bash", [join(here, "run-api.sh"), "--dll", join(dir, "build", "Api.dll"), "--db", "reel_ws_test", "--port", "5999", "--env-file", join(dir, "nokey.env"), "--dry-run"],
      { env: { ...process.env, EVAL_FOUNDER_ENV: join(dir, "founder.env"), OPENROUTER_API_KEY: "sk-inherited-from-the-shell", STRIPE_SECRET_KEY: "sk-live-inherited", SOME_OTHER_SECRET: "x" }, encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    const names = r.stdout.trim().split("\n");
    assert.ok(names.includes("OPENROUTER_API_KEY (empty)"), "the inherited key is replaced by an empty one");
    assert.ok(names.includes("STRIPE_SECRET_KEY (empty)"));
    assert.ok(!names.some((n) => n.startsWith("SOME_OTHER_SECRET")), "nothing the env file and the script do not name is passed");
    assert.ok(names.includes("TMDB_READ_ACCESS_TOKEN") && names.includes("OPENROUTER_MODEL"));
    assert.ok(names.includes("Background__TokenRefresh") && names.includes("HTTPS_PROXY"));
    assert.equal(r.stdout.includes("sk-"), false, "dry-run prints names, never values");
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
