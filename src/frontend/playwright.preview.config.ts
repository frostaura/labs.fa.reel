import { defineConfig, devices } from "@playwright/test";
import { tripwirePort, tripwireServer, unmockedApiReporter, viteProxyEnv } from "./scripts/e2e/tripwire";

/**
 * Specs that need the PRODUCTION build (`*.preview.spec.ts`): hashed lazy chunks, the
 * `vite:preloadError` path, the built worker. Builds once, serves `dist/` with `vite preview`, and
 * mocks every /api call at the route layer like the dev-server specs. Service workers stay
 * blocked by default, as in every spec here; the one exception is service-worker.preview.spec.ts, which
 * allows them to test the built worker itself and mocks at the server layer, through the tripwire's
 * forward mode, because WebKit's route mocks never see a worker's own fetches. `vite preview`
 * inherits the dev proxy, so it too points at the e2e tripwire and an unmocked call fails the run
 * (see playwright.config.ts). Chromium and WebKit both run every spec here (PH-12): the founder's
 * phone is an iPhone. Run with `CI=1 PW_PORT=<free> npx playwright test -c playwright.preview.config.ts`.
 */
const port = Number(process.env.PW_PORT ?? 4173);
const tripwire = tripwirePort(port);
// Where a spec hands /api to its own mock server (service-worker.preview.spec; the tripwire's
// forward mode). Set here so the workers, forked after the config loads, inherit it.
process.env.REEL_E2E_TRIPWIRE = `http://127.0.0.1:${tripwire}`;

export default defineConfig({
  testDir: "./tests-e2e",
  testMatch: "**/*.preview.spec.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], unmockedApiReporter(tripwire)],
  webServer: [
    tripwireServer(tripwire),
    {
      command: `npm run build && npx vite preview --port ${port} --strictPort`,
      url: `http://127.0.0.1:${port}`,
      env: viteProxyEnv(tripwire),
      reuseExistingServer: false,
      timeout: 300_000,
    },
  ],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    serviceWorkers: "block",
    headless: true,
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
  ],
});
