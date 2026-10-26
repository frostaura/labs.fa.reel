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
 * - Chromium and WebKit run the same specs (`make ci-e2e` runs both projects).
 * - **127.0.0.1, never "localhost".** Vite binds 127.0.0.1 (vite.config.ts), and on macOS
 *   "localhost" resolves to IPv6 ::1 first, so every navigation began with a refused connection.
 * - **WebKit alone gets 1 retry** (IREV3S-03). About 1 WebKit test in 75 hangs in `page.goto`
 *   "waiting until load" with not one request in its trace — not even the document — so the stall
 *   is inside the engine before the navigation reaches the network, and no app or spec change can
 *   remove it. The list reporter prints a pass-on-retry as "flaky", so it stays visible; a test that
 *   fails twice still fails the run.
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
      retries: 1,
      use: { ...devices["Desktop Safari"] },
    },
  ],
});
