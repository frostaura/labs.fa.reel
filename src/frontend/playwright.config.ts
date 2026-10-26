import { defineConfig, devices } from "@playwright/test";
import { tripwirePort, tripwireServer, unmockedApiReporter, viteProxyEnv } from "./scripts/e2e/tripwire";

const isCI = !!process.env.CI;

/**
 * Playwright config for the Vite dev server. Every spec mocks its API at the route layer.
 *
 * - **No real API, ever** (trust-v1 R1-21, §7). Vite's /api and /sse proxy points at the e2e
 *   tripwire (scripts/e2e/apiTripwire.mjs), never at :5054 or any other backend. A call a spec did
 *   not mock gets the same 502 Vite gives for a dead target, and the unmocked-API reporter fails the
 *   run naming the test and the call. Known debts are listed in
 *   scripts/e2e/unmocked-api-baseline.json, which only shrinks.
 * - **Own servers only.** Both servers start fresh for every run (`reuseExistingServer: false`):
 *   reusing whatever already listens on the port could reuse a dev server proxying to a real API,
 *   or the production container whose service worker bypasses `page.route`. Pick a free port with
 *   PW_PORT (default 5173); the tripwire takes PW_PORT + 10000 unless PW_TRIPWIRE_PORT is set.
 * - Service workers stay blocked for the same reason.
 * - Chromium and WebKit run the same specs, both through `npm run test:e2e`.
 * - **127.0.0.1, never "localhost".** Vite binds 127.0.0.1 (vite.config.ts), and on macOS
 *   "localhost" resolves to IPv6 ::1 first, so every navigation began with a refused connection.
 * - **WebKit runs in shards** (IREV3S-03): `npm run test:e2e` runs Chromium as one process and
 *   WebKit through scripts/e2e/webkitShards.ts, at most 40 tests per process. One WebKit browser
 *   can create only 63 pages; the 64th never attaches and its `page.goto` hangs waiting for "load"
 *   with no request sent (the measurements are in that script's header). The WebKit project is
 *   `fullyParallel` so a shard can split by test rather than by file. Running the webkit project
 *   unsharded (`npx playwright test` with no --project) still loses about one test in 64.
 * - **No retries, WebKit included.** Both causes of the WebKit flakes are found and fixed, so a
 *   flake is news again: the page limit above, and a real app bug — a navigation away cancels the
 *   old page's in-flight lazy-chunk import, WebKit rejects it with the words a redeploy's missing
 *   chunk produces, and the one-shot "new build" reload then cancelled the navigation
 *   (lib/deployReload.ts `isLeaving`; shell-router.spec pins it).
 */
const port = Number(process.env.PW_PORT ?? 5173);
const tripwire = tripwirePort(port);

export default defineConfig({
  testDir: "./tests-e2e",
  // Production-build specs run under playwright.preview.config.ts.
  testIgnore: "**/*.preview.spec.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], unmockedApiReporter(tripwire)],
  webServer: [
    tripwireServer(tripwire),
    {
      command: `npm run dev -- --port ${port} --strictPort`,
      url: `http://127.0.0.1:${port}`,
      env: viteProxyEnv(tripwire),
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    serviceWorkers: "block",
    headless: isCI,
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 15000,
    navigationTimeout: 30000,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"], channel: undefined },
    },
    {
      name: "webkit",
      fullyParallel: true,
      use: { ...devices["Desktop Safari"] },
    },
  ],
});
