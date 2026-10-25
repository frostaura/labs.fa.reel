import { defineConfig, devices } from "@playwright/test";

/**
 * Specs that need the PRODUCTION build (`*.preview.spec.ts`): hashed lazy chunks, the
 * `vite:preloadError` path, the built worker. Builds once, serves `dist/` with `vite preview`, and
 * mocks every /api call at the route layer like the dev-server specs. Service workers stay
 * blocked, as in every spec here. Run with
 * `CI=1 PW_PORT=<free> npx playwright test -c playwright.preview.config.ts`.
 */
const port = Number(process.env.PW_PORT ?? 4173);

export default defineConfig({
  testDir: "./tests-e2e",
  testMatch: "**/*.preview.spec.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  webServer: {
    command: `npm run build && npx vite preview --port ${port} --strictPort`,
    url: `http://localhost:${port}`,
    reuseExistingServer: false,
    timeout: 300_000,
  },
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
