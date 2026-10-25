import { tmpdir } from "node:os";
import path from "node:path";

/**
 * Shared Playwright wiring for the e2e API tripwire (trust-v1 R1-21). Both Playwright configs use
 * it, so no config can point Vite's proxy at a real API.
 *
 * The tripwire's port defaults to the app port + 10000 (PW_TRIPWIRE_PORT overrides it). It is
 * started with `reuseExistingServer: false`, so a port that is already taken — by another lane's
 * run or by a real API — aborts the run before any test executes instead of proxying to it.
 */
export function tripwirePort(appPort: number): number {
  return Number(process.env.PW_TRIPWIRE_PORT ?? appPort + 10_000);
}

export function tripwireLogFile(port: number): string {
  return path.join(tmpdir(), `reel-e2e-tripwire-${port}.jsonl`);
}

/** The webServer entry that starts the tripwire. List it before the app server. */
export function tripwireServer(port: number) {
  return {
    command: `node scripts/e2e/apiTripwire.mjs ${port} ${JSON.stringify(tripwireLogFile(port))}`,
    port,
    reuseExistingServer: false,
    timeout: 30_000,
  };
}

/** Environment for the Vite process (dev or preview): its /api and /sse proxy target. */
export function viteProxyEnv(port: number): Record<string, string> {
  return { VITE_API_TARGET: `http://127.0.0.1:${port}` };
}

/** The reporter entry that fails the run on any unmocked call outside the baseline. */
export function unmockedApiReporter(port: number): [string, { logFile: string }] {
  return ["./scripts/e2e/unmockedApiReporter.ts", { logFile: tripwireLogFile(port) }];
}
