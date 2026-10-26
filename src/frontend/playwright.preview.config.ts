import { defineConfig, devices } from "@playwright/test";
import { tripwirePort, tripwireServer, unmockedApiReporter, viteProxyEnv } from "./scripts/e2e/tripwire";

/**
 * Specs that need the PRODUCTION build (`*.preview.spec.ts`): hashed lazy chunks, the
 * `vite:preloadError` path, the built worker. Builds once, serves `dist/` with `vite preview`, and
 * mocks every /api call at the route layer like the dev-server specs. Service workers stay
 * blocked by default, as in every spec here; the one exception is service-worker.preview.spec.ts, which
 * allows them to test the built worker itself and mocks on the context (which, in Chromium, also
 * answers the worker's own fetches). `vite preview` inherits the dev proxy, so it too points at the e2e
 * tripwire and an unmocked call fails the run (see playwright.config.ts). Run with
 * `CI=1 PW_PORT=<free> npx playwright test -c playwright.preview.config.ts`.
 */
const port = Number(process.env.PW_PORT ?? 4173);
const tripwire = tripwirePort(port);

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
      url: `http://localhost:${port}`,
      env: viteProxyEnv(tripwire),
      reuseExistingServer: false,
      timeout: 300_000,
    },
  ],
  use: {
    baseURL: `http://localhost:${port}`,
    serviceWorkers: "block",
    headless: true,
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
