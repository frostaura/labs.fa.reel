import { defineConfig, devices } from "@playwright/test";

const isCI = !!process.env.CI;

/**
 * Playwright config pointed at the vite dev server (proxying /api → :5000).
 *
 * The port defaults to 5173 (what CI expects, where the port is free) but can be overridden with
 * PW_PORT for local runs where 5173 is occupied by the live docker stack — e.g.
 * `PW_PORT=5199 npx playwright test`. Overriding matters beyond the port clash: with
 * `reuseExistingServer` on, pointing at a busy 5173 silently reuses whatever is listening there.
 * If that is the *production* container, its service worker takes control shortly after first
 * paint (skipWaiting + clientsClaim) and later fetches (like typeahead) are issued by the worker,
 * bypassing `page.route` — mocks appear to "randomly" stop intercepting. `serviceWorkers: "block"`
 * below closes that hole for good measure.
 *
 * In CI we run headless and let Playwright start the frontend dev server itself (`webServer`
 * below) so the smoke gate has no external dependency. Locally we default to headed and reuse
 * an already-running dev server. The CI gate (`npm run test:e2e`) runs only the frontend-only
 * shell smoke (smoke.spec.ts); full-stack journeys need a running backend + linked profile and
 * run via `npm run test:e2e:full` against a locally-managed stack.
 */
const port = Number(process.env.PW_PORT ?? 5173);

export default defineConfig({
  testDir: "./tests-e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  webServer: {
    command: `npm run dev -- --port ${port} --strictPort`,
    url: `http://localhost:${port}`,
    reuseExistingServer: !isCI,
    timeout: 120_000,
  },
  use: {
    baseURL: `http://localhost:${port}`,
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
  ],
});
