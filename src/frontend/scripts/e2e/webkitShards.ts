import { spawnSync } from "node:child_process";
import { shardHarvestFile } from "./unmockedApi.ts";

/**
 * Runs the WebKit project in shards, each its own Playwright process and so its own browser
 * (IREV3S-03).
 *
 * The cause, measured 2026-10-26 (Playwright 1.60.0, webkit-2287, macOS): one WebKit browser
 * instance can create 63 pages and no more. The 64th `newPage` never attaches — `page.goto` on it
 * waits for "load" with no request ever leaving, which is the "hang on about:blank, not even the
 * document in the trace" every full run showed. It does not matter whether the pages share a
 * context, open the app or a static SVG, mock routes or not, or whether HMR is on; re-navigating
 * one page 120 times is fine, and 100 contexts with no page are fine. Playwright keeps one browser
 * per worker and restarts the worker only after a failure, which is why a full run lost one test
 * almost exactly every 64 WebKit tests (tests 438, 502, 565, 629 and 693 of one run).
 *
 * So no WebKit process may see more than 63 pages. This splits the project into shards of at most
 * {@link TESTS_PER_SHARD} tests (the project is `fullyParallel`, so a shard splits by test, not by
 * file) and runs them one after another. The headroom covers tests that open a second context
 * (a phone viewport, a second account). Chromium has no such limit and runs as one process.
 *
 * Usage: `node scripts/e2e/webkitShards.ts [playwright args…]` — the args (spec files, `-g`, …)
 * pass through to every shard. Exits non-zero when any shard fails.
 *
 * A harvest (`UNMOCKED_API_FOUND=<file>`, see unmockedApiReporter.ts) gets one file per shard,
 * `<file>.webkit-<n>.json`: every shard is its own Playwright run with its own reporter, so a single
 * path would keep only the last shard's findings — which is how a harvest silently loses most of
 * WebKit.
 */
const TESTS_PER_SHARD = 40;

const passThrough = process.argv.slice(2);
const npx = process.platform === "win32" ? "npx.cmd" : "npx";

const listed = spawnSync(npx, ["playwright", "test", "--project=webkit", "--list", "--reporter=list", ...passThrough], {
  encoding: "utf8",
});
if (listed.status !== 0) {
  process.stderr.write(listed.stderr);
  process.exit(listed.status ?? 1);
}
const tests = (listed.stdout.match(/^\s+\[webkit\] ›/gm) ?? []).length;
if (tests === 0) {
  console.log("webkit: no tests matched");
  process.exit(0);
}

const shards = Math.ceil(tests / TESTS_PER_SHARD);
console.log(`webkit: ${tests} tests in ${shards} shard(s) of at most ${TESTS_PER_SHARD}, one browser each (IREV3S-03)`);

const failed: number[] = [];
for (let shard = 1; shard <= shards; shard++) {
  const found = process.env.UNMOCKED_API_FOUND;
  const run = spawnSync(npx, ["playwright", "test", "--project=webkit", `--shard=${shard}/${shards}`, ...passThrough], {
    stdio: "inherit",
    env: found ? { ...process.env, UNMOCKED_API_FOUND: shardHarvestFile(found, shard) } : process.env,
  });
  if (run.status !== 0) failed.push(shard);
}

if (failed.length) {
  console.log(`webkit: shard(s) ${failed.join(", ")} of ${shards} failed`);
  process.exit(1);
}
console.log(`webkit: all ${shards} shard(s) passed`);
