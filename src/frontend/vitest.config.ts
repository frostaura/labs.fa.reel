import { defineConfig } from "vitest/config";
import path from "node:path";

// Standalone Vitest config so the app's Vite/PWA plugin chain does not load during tests.
export default defineConfig({
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "src"),
      // The PWA plugin's virtual module exists only under the app's Vite build.
      "virtual:pwa-register": path.resolve(import.meta.dirname, "src/test/pwaRegister.stub.ts"),
    },
  },
  test: {
    environment: "node",
    // Node 25+ exposes an experimental `localStorage` global that warns on first touch unless given
    // a backing file. Tests stub storage where they need it, so the experiment is switched off —
    // code under test then sees no localStorage, exactly as in a browser that blocks it.
    execArgv: ["--no-experimental-webstorage"],
    // Component tests opt into jsdom per file (`// @vitest-environment jsdom`); everything else
    // stays on node, which is faster and keeps browser globals from leaking into pure logic.
    // scripts/** holds the e2e harness (tripwire attribution); its logic is unit tested too.
    include: ["src/**/*.{test,spec}.{ts,tsx}", "scripts/**/*.test.ts"],
    coverage: {
      provider: "v8",
      // Emit coverage-summary.json for the CI threshold gate, plus human-readable output.
      reporter: ["text", "json", "json-summary"],
      reportsDirectory: "coverage",
      // Count the whole source tree in the denominator so the number reflects real coverage.
      all: true,
      include: ["src/**/*.{ts,tsx}"],
      exclude: [
        "src/**/*.{test,spec}.{ts,tsx}",
        "src/**/*.d.ts",
        "src/main.tsx",
        "src/vite-env.d.ts",
        "src/test/**",
      ],
    },
  },
});
