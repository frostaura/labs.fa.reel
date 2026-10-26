import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import type { FullConfig, FullResult, Suite, TestCase, TestResult } from "@playwright/test/reporter";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import UnmockedApiReporter from "./unmockedApiReporter";

/**
 * The reporter's wiring, as Playwright drives it: the begin message for a test reaches the reporter
 * after the worker has already started it, and the attribution has to use the worker's time.
 */
const frontendRoot = path.resolve(__dirname, "../..");

function testCase(title: string): TestCase {
  return {
    location: { file: path.join(frontendRoot, "tests-e2e/fixture.spec.ts"), line: 1, column: 1 },
    titlePath: () => ["", "chromium", "fixture.spec.ts", "suite", title],
  } as unknown as TestCase;
}

function started(at: number): TestResult {
  return { startTime: new Date(at) } as TestResult;
}

const config = { rootDir: path.join(frontendRoot, "tests-e2e"), configFile: path.join(frontendRoot, "playwright.config.ts") } as FullConfig;
const passed = { status: "passed" } as FullResult;

describe("UnmockedApiReporter", () => {
  let dir: string;
  let logFile: string;

  beforeEach(() => {
    dir = mkdtempSync(path.join(tmpdir(), "unmocked-reporter-"));
    logFile = path.join(dir, "tripwire.jsonl");
    vi.spyOn(console, "log").mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    delete process.env.UNMOCKED_API_FOUND;
    rmSync(dir, { recursive: true, force: true });
  });

  function hits(...lines: Array<[number, string, string]>): void {
    writeFileSync(logFile, lines.map(([at, method, p]) => JSON.stringify({ at, method, path: p })).join("\n") + "\n");
  }

  it("blames a call on the test the worker had started, even when the begin message arrives after the call", () => {
    // Process clock: the reporter is built at 1000 and receives each begin message 20 ms after the
    // worker started that test. The second test's first call lands at 1505: after the worker began
    // it (1500), before the reporter heard so (1520). Timed on receipt it would be blamed on "first".
    const now = vi.spyOn(Date, "now").mockReturnValue(1_000);
    const reporter = new UnmockedApiReporter({ logFile });
    reporter.onBegin(config, {} as Suite);
    now.mockReturnValue(1_120);
    reporter.onTestBegin(testCase("first"), started(1_100));
    now.mockReturnValue(1_520);
    reporter.onTestBegin(testCase("second"), started(1_500));
    hits([1_505, "GET", "/api/feed"]);
    process.env.UNMOCKED_API_FOUND = path.join(dir, "found.json");

    expect(reporter.onEnd(passed)).toEqual({ status: "failed" });
    expect(JSON.parse(readFileSync(process.env.UNMOCKED_API_FOUND, "utf8"))).toEqual({
      "fixture.spec.ts › suite › second": ["GET /api/feed"],
    });
  });

  it("passes a run whose only calls are baselined, and never fails on a baselined call it did not see", () => {
    const t0 = Date.now() + 1_000;
    const reporter = new UnmockedApiReporter({ logFile });
    reporter.onBegin(config, {} as Suite);
    // A real baseline entry: the test that is listed, with fewer calls than the baseline holds.
    const baseline = JSON.parse(readFileSync(path.join(frontendRoot, "scripts/e2e/unmocked-api-baseline.json"), "utf8")) as Record<string, string[]>;
    const [key, calls] = Object.entries(baseline).find(([, list]) => list.length > 1)!;
    const [file, ...titles] = key.split(" › ");
    const listed = {
      location: { file: path.join(frontendRoot, "tests-e2e", file), line: 1, column: 1 },
      titlePath: () => ["", "webkit", file, ...titles],
    } as unknown as TestCase;
    reporter.onTestBegin(listed, started(t0));
    const [method, route] = calls[0].split(" ");
    hits([t0 + 10, method, route.replace(":id", "42")]);

    expect(reporter.onEnd(passed)).toBeUndefined();
  });

  it("fails closed when the tripwire log is missing", () => {
    const reporter = new UnmockedApiReporter({ logFile: path.join(dir, "absent.jsonl") });
    reporter.onBegin(config, {} as Suite);
    expect(reporter.onEnd(passed)).toEqual({ status: "failed" });
  });
});
